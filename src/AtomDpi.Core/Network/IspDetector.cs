using System.Net;
using System.Text;
using AtomDpi.Core.Dns;

namespace AtomDpi.Core.Network;

public sealed record IspDetection(
    IspProfile Profile,
    IPAddress? PublicAddress,
    string? ReverseDns,
    int? Asn,
    string? AsnOrganisation,
    bool WasAutomatic)
{
    public string Summary
    {
        get
        {
            var parts = new List<string> { Profile.DisplayName };
            if (Asn is not null)
            {
                parts.Add($"AS{Asn}");
            }

            if (PublicAddress is not null)
            {
                parts.Add(PublicAddress.ToString());
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Works out which operator we are behind.
/// </summary>
/// <remarks>
/// Everything here rides on the DoH resolver or on an IP-literal HTTPS request, so
/// detection keeps working on a connection where plain DNS is poisoned - which is
/// precisely the situation the app exists for. ASN lookup uses Team Cymru's DNS
/// interface rather than a REST geolocation service, so there is no third party
/// API key and no locale-dependent output to parse.
/// </remarks>
public sealed class IspDetector
{
    private const string CloudflareTraceUrl = "https://1.1.1.1/cdn-cgi/trace";

    private readonly DohResolver _resolver;
    private readonly Action<string>? _log;

    public IspDetector(DohResolver resolver, Action<string>? log = null)
    {
        _resolver = resolver;
        _log = log;
    }

    public async Task<IspDetection> DetectAsync(NetworkFingerprint fingerprint, CancellationToken cancellationToken = default)
    {
        var publicAddress = await GetPublicAddressAsync(cancellationToken).ConfigureAwait(false);

        string? reverseDns = null;
        int? asn = null;
        string? organisation = null;

        if (publicAddress is not null)
        {
            reverseDns = await SafeReverseAsync(publicAddress, cancellationToken).ConfigureAwait(false);
            (asn, organisation) = await LookupAsnAsync(publicAddress, cancellationToken).ConfigureAwait(false);
        }

        // The ASN organisation string is another place the operator name shows up,
        // so feed it into matching alongside reverse DNS.
        var haystack = string.Join(' ', new[] { reverseDns, organisation }.Where(s => !string.IsNullOrEmpty(s)));
        var profile = IspCatalog.Match(haystack, asn, fingerprint.Ssid, fingerprint.GuessAccessKind());

        _log?.Invoke($"Operator detection: {profile.DisplayName} (ptr='{reverseDns ?? "-"}', asn={asn?.ToString() ?? "-"}, ssid='{fingerprint.Ssid ?? "-"}').");
        return new IspDetection(profile, publicAddress, reverseDns, asn, organisation, WasAutomatic: true);
    }

    private async Task<IPAddress?> GetPublicAddressAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            var body = await client.GetStringAsync(CloudflareTraceUrl, cancellationToken).ConfigureAwait(false);

            foreach (var line in body.Split('\n'))
            {
                if (line.StartsWith("ip=", StringComparison.Ordinal)
                    && IPAddress.TryParse(line[3..].Trim(), out var address))
                {
                    return address;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not determine the public address: {ex.Message}");
        }

        return null;
    }

    private async Task<string?> SafeReverseAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            return await _resolver.ReverseAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Team Cymru answers "which AS announces this address" over plain DNS TXT, which
    /// means it works through our DoH transport with no extra dependency.
    /// </summary>
    private async Task<(int? Asn, string? Organisation)> LookupAsnAsync(IPAddress address, CancellationToken cancellationToken)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return (null, null);
        }

        try
        {
            var octets = address.GetAddressBytes();
            var name = $"{octets[3]}.{octets[2]}.{octets[1]}.{octets[0]}.origin.asn.cymru.com";
            var query = DnsMessage.BuildQuery((ushort)Random.Shared.Next(1, ushort.MaxValue), name, DnsRecordType.Txt);
            var response = await _resolver.QueryAsync(query, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return (null, null);
            }

            var text = ReadFirstText(response);
            if (text is null)
            {
                return (null, null);
            }

            // "9121 | 88.240.0.0/13 | TR | ripencc | 2001-01-30"
            var fields = text.Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length == 0 || !int.TryParse(fields[0].Split(' ')[0], out var asn))
            {
                return (null, null);
            }

            var organisation = await LookupAsnNameAsync(asn, cancellationToken).ConfigureAwait(false);
            return (asn, organisation);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private async Task<string?> LookupAsnNameAsync(int asn, CancellationToken cancellationToken)
    {
        try
        {
            var query = DnsMessage.BuildQuery(
                (ushort)Random.Shared.Next(1, ushort.MaxValue),
                $"AS{asn}.asn.cymru.com",
                DnsRecordType.Txt);

            var response = await _resolver.QueryAsync(query, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return null;
            }

            // "9121 | TR | ripencc | 2001-01-30 | TTNET, TR"
            var text = ReadFirstText(response);
            var fields = text?.Split('|', StringSplitOptions.TrimEntries);
            return fields is { Length: >= 5 } ? fields[^1] : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>TXT record data is a sequence of length-prefixed strings.</summary>
    private static string? ReadFirstText(byte[] response)
    {
        foreach (var record in DnsMessage.ReadAnswers(response))
        {
            if (record.Type != DnsRecordType.Txt || record.Data.Length < 2)
            {
                continue;
            }

            var builder = new StringBuilder();
            var pos = 0;
            while (pos < record.Data.Length)
            {
                var length = record.Data[pos];
                if (pos + 1 + length > record.Data.Length)
                {
                    break;
                }

                builder.Append(Encoding.ASCII.GetString(record.Data, pos + 1, length));
                pos += 1 + length;
            }

            if (builder.Length > 0)
            {
                return builder.ToString();
            }
        }

        return null;
    }
}
