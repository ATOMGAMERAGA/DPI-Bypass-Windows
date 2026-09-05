using System.Net;
using System.Text.RegularExpressions;

namespace DpiBypass.Core.Diagnostics;

/// <summary>What kind of thing a masked value was, so the report still reads sensibly.</summary>
public enum RedactionKind
{
    Network = 0,
    Bssid = 1,
    Mac = 2,
    Address = 3,
    Host = 4,
    Path = 5,
    User = 6,
    Adapter = 7,
}

/// <summary>
/// Replaces the identifying parts of a diagnostics report with stable stand-ins.
/// </summary>
/// <remarks>
/// <para>
/// A report exists to be sent to somebody, so the default has to be that it carries no
/// SSID, BSSID, MAC, address, custom target, custom domain, account name or user profile
/// path. What it does carry is a consistent alias per value - "ag-1" wherever one network
/// appears, "adres-2" wherever one address does - because a report where every occurrence
/// is the same black box cannot be reasoned about at all.
/// </para>
/// <para>
/// The aliases are ordinals in order of first appearance, deliberately not hashes.
/// A hash of an SSID is not anonymisation: the space of plausible SSIDs and MAC prefixes
/// is small enough to enumerate, so anyone holding the report can simply hash their
/// candidates and compare. An ordinal carries nothing to attack.
/// </para>
/// <para>
/// Free text goes through the same pass. Exception messages and log lines are where
/// identifying values actually leak - "could not reach 192.0.2.14" and
/// "C:\Users\ayse\AppData\..." are both things this app really writes - so registered
/// values are substituted there too, and the patterns below catch the ones nothing
/// registered.
/// </para>
/// </remarks>
public sealed partial class DiagnosticRedactor
{
    /// <summary>
    /// Addresses that identify the app's own configuration rather than the user.
    /// </summary>
    /// <remarks>
    /// Loopback and the unspecified address say where our own proxy listens; the public
    /// resolvers are constants compiled into this build. Masking them would remove the
    /// part of the report that explains what the app was doing without protecting anybody.
    /// </remarks>
    private static readonly HashSet<string> PublicKnowledge = new(StringComparer.OrdinalIgnoreCase)
    {
        "127.0.0.1", "::1", "0.0.0.0", "::", "localhost",
        "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112",
        "2606:4700:4700::1111", "2001:4860:4860::8888", "2620:fe::fe",
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RedactionKind, int> _counters = [];

    /// <summary>How many distinct values were masked, for the report's own footer.</summary>
    public int MaskedValues
    {
        get
        {
            lock (_gate)
            {
                return _aliases.Count;
            }
        }
    }

    /// <summary>
    /// The stand-in for one value, creating it on first sight.
    /// </summary>
    /// <returns>Null for a null or blank input, so "not measured" stays distinguishable.</returns>
    public string? Alias(RedactionKind kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (PublicKnowledge.Contains(trimmed))
        {
            return trimmed;
        }

        lock (_gate)
        {
            if (_aliases.TryGetValue(trimmed, out var existing))
            {
                return existing;
            }

            var next = _counters.GetValueOrDefault(kind) + 1;
            _counters[kind] = next;
            var alias = $"{Prefix(kind)}-{next}";
            _aliases[trimmed] = alias;
            return alias;
        }
    }

    /// <summary>
    /// Masks free text: registered values first, then anything that still looks personal.
    /// </summary>
    /// <remarks>
    /// Registered values are substituted longest first, so a value that contains another -
    /// an adapter name inside a longer description, an address inside an endpoint - cannot
    /// be half replaced and leave the rest readable.
    /// </remarks>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        KeyValuePair<string, string>[] known;
        lock (_gate)
        {
            known = [.. _aliases.OrderByDescending(pair => pair.Key.Length)];
        }

        var result = text;
        foreach (var (secret, alias) in known)
        {
            if (secret.Length >= 3)
            {
                result = result.Replace(secret, alias, StringComparison.OrdinalIgnoreCase);
            }
        }

        result = UserProfilePath().Replace(result, match => $"{match.Groups[1].Value}<kullanıcı>");
        result = UnixHomePath().Replace(result, "/home/<kullanıcı>");
        result = MacAddress().Replace(result, match => Alias(RedactionKind.Mac, match.Value)!);
        result = IPv4().Replace(result, match => MaskAddress(match.Value));
        result = IPv6Candidate().Replace(result, match => MaskAddress(match.Value));

        var user = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3)
        {
            result = result.Replace(user, "<kullanıcı>", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Registers the values a snapshot is known to contain, before any text is masked.
    /// </summary>
    /// <remarks>
    /// Order matters for readability rather than for safety: registering the network's own
    /// identifiers first means it is "ag-1" in the summary as well as in every log line
    /// that happens to mention it.
    /// </remarks>
    public void Register(RedactionKind kind, params string?[] values)
    {
        foreach (var value in values)
        {
            _ = Alias(kind, value);
        }
    }

    /// <summary>
    /// Masks a candidate only once it really parses as an address.
    /// </summary>
    /// <remarks>
    /// The pattern that finds IPv6 candidates also matches the start of a log timestamp -
    /// "14:59:54" is three colon-separated hex-looking groups - and turning every log
    /// line's clock into "adres-7" would make the report unreadable while protecting
    /// nothing. The parser is the arbiter: it rejects a group count that is not an
    /// address, so anything it accepts is worth masking and anything it refuses is left
    /// exactly as written.
    /// </remarks>
    private string MaskAddress(string candidate)
        => IPAddress.TryParse(candidate, out _) ? Alias(RedactionKind.Address, candidate)! : candidate;

    private static string Prefix(RedactionKind kind) => kind switch
    {
        RedactionKind.Network => "ag",
        RedactionKind.Bssid => "erisim-noktasi",
        RedactionKind.Mac => "donanim-adresi",
        RedactionKind.Address => "adres",
        RedactionKind.Host => "hedef",
        RedactionKind.Path => "yol",
        RedactionKind.User => "kullanici",
        _ => "baglanti-noktasi",
    };

    // Keeps the drive and "Users\" so the shape of the path stays legible, and takes the
    // account name, which is the identifying part.
    [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)[^\\\r\n""']+", RegexOptions.CultureInvariant)]
    private static partial Regex UserProfilePath();

    [GeneratedRegex(@"/home/[^/\s""']+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixHomePath();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddress();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b", RegexOptions.CultureInvariant)]
    private static partial Regex IPv4();

    // Broad on purpose - MaskAddress decides. Anything narrow enough to exclude a log
    // timestamp by pattern alone also excludes real addresses.
    [GeneratedRegex(@"(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6Candidate();
}
