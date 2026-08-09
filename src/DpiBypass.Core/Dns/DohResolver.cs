using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;

namespace DpiBypass.Core.Dns;

public sealed record DohEndpoint(string Provider, string Url, IPAddress PlainAddress)
{
    public override string ToString() => $"{Provider} ({PlainAddress})";
}

/// <summary>
/// DNS-over-HTTPS client.
/// </summary>
/// <remarks>
/// Every endpoint is addressed by IP literal on purpose. TLS forbids putting an IP
/// address in SNI, so these connections carry no hostname at all - which means an
/// SNI filter has nothing to match on and resolution keeps working even where
/// plain DNS is poisoned. The certificates for 1.1.1.1, 8.8.8.8 and 9.9.9.9 all
/// carry IP SANs, so ordinary chain validation still applies.
/// </remarks>
public sealed class DohResolver : IDisposable
{
    public static readonly DohEndpoint Cloudflare = new("Cloudflare", "https://1.1.1.1/dns-query", IPAddress.Parse("1.1.1.1"));
    public static readonly DohEndpoint CloudflareSecondary = new("Cloudflare", "https://1.0.0.1/dns-query", IPAddress.Parse("1.0.0.1"));
    public static readonly DohEndpoint Google = new("Google", "https://8.8.8.8/dns-query", IPAddress.Parse("8.8.8.8"));
    public static readonly DohEndpoint GoogleSecondary = new("Google", "https://8.8.4.4/dns-query", IPAddress.Parse("8.8.4.4"));
    public static readonly DohEndpoint Quad9 = new("Quad9", "https://9.9.9.9/dns-query", IPAddress.Parse("9.9.9.9"));
    public static readonly DohEndpoint Quad9Secondary = new("Quad9", "https://149.112.112.112/dns-query", IPAddress.Parse("149.112.112.112"));

    /// <summary>Cloudflare first, then Google, then Quad9 - each with its own second address.</summary>
    public static readonly IReadOnlyList<DohEndpoint> DefaultChain =
    [
        Cloudflare, CloudflareSecondary, Google, GoogleSecondary, Quad9, Quad9Secondary,
    ];

    private static readonly MediaTypeHeaderValue DnsMediaType = new("application/dns-message");

    private readonly HttpClient _http;
    private readonly IReadOnlyList<DohEndpoint> _chain;
    private readonly TimeSpan _perEndpointTimeout;
    private readonly Dictionary<string, long> _latency = new();
    private readonly Lock _latencyGate = new();

    public DohResolver(IReadOnlyList<DohEndpoint>? chain = null, TimeSpan? perEndpointTimeout = null)
    {
        _chain = chain is { Count: > 0 } ? chain : DefaultChain;
        _perEndpointTimeout = perEndpointTimeout ?? TimeSpan.FromSeconds(4);

        var handler = new SocketsHttpHandler
        {
            // Long lived pooled HTTP/2 connections keep the added latency at zero
            // after the first query - a resolve is one round trip on a warm socket.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = false,
            AllowAutoRedirect = false,
            EnableMultipleHttp2Connections = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                    | System.Security.Authentication.SslProtocols.Tls13,
            },
        };

        _http = new HttpClient(handler)
        {
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DpiBypass/1.0");
    }

    /// <summary>The endpoint that answered fastest so far, for display purposes.</summary>
    public string? ActiveProvider { get; private set; }

    /// <summary>Sends a raw wire-format query through the chain and returns the first good answer.</summary>
    public async Task<byte[]?> QueryAsync(byte[] query, CancellationToken cancellationToken)
    {
        foreach (var endpoint in OrderedChain())
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(_perEndpointTimeout);

                using var content = new ByteArrayContent(query);
                content.Headers.ContentType = DnsMediaType;

                using var response = await _http
                    .PostAsync(endpoint.Url, content, linked.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    RecordLatency(endpoint, TimeSpan.FromSeconds(30));
                    continue;
                }

                var payload = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
                if (payload.Length < DnsMessage.HeaderLength)
                {
                    continue;
                }

                RecordLatency(endpoint, stopwatch.Elapsed);
                ActiveProvider = endpoint.Provider;
                return payload;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Any failure just means "try the next provider". Cloudflare being
                // unreachable is exactly why Google and Quad9 are in the list.
                RecordLatency(endpoint, TimeSpan.FromSeconds(30));
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, bool includeIPv6, CancellationToken cancellationToken)
    {
        var results = new List<IPAddress>();

        var v4 = await QueryAsync(DnsMessage.BuildQuery(NextId(), host, DnsRecordType.A), cancellationToken).ConfigureAwait(false);
        if (v4 is not null)
        {
            results.AddRange(DnsMessage.ReadAddresses(v4));
        }

        if (includeIPv6)
        {
            var v6 = await QueryAsync(DnsMessage.BuildQuery(NextId(), host, DnsRecordType.Aaaa), cancellationToken).ConfigureAwait(false);
            if (v6 is not null)
            {
                results.AddRange(DnsMessage.ReadAddresses(v6));
            }
        }

        return results;
    }

    public async Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var query = DnsMessage.BuildQuery(NextId(), DnsMessage.ToReverseName(address), DnsRecordType.Ptr);
        var response = await QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return response is null ? null : DnsMessage.ReadFirstPointer(response);
    }

    /// <summary>Cheap reachability probe used by the UI and by the startup self-check.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        var response = await QueryAsync(DnsMessage.BuildQuery(NextId(), "discord.com", DnsRecordType.A), cancellationToken)
            .ConfigureAwait(false);
        return response is not null && DnsMessage.ReadAddresses(response).Any();
    }

    /// <summary>
    /// Preserves the configured provider preference but demotes endpoints that have
    /// been slow or failing, so a dead resolver is not retried first every time.
    /// </summary>
    private IEnumerable<DohEndpoint> OrderedChain()
    {
        lock (_latencyGate)
        {
            return _chain
                .Select((endpoint, index) => (endpoint, index, penalty: _latency.GetValueOrDefault(endpoint.Url, 0L)))
                .OrderBy(x => x.penalty > 5000 ? 1 : 0)
                .ThenBy(x => x.index)
                .Select(x => x.endpoint)
                .ToList();
        }
    }

    private void RecordLatency(DohEndpoint endpoint, TimeSpan elapsed)
    {
        lock (_latencyGate)
        {
            _latency[endpoint.Url] = (long)elapsed.TotalMilliseconds;
        }
    }

    private static ushort NextId() => (ushort)Random.Shared.Next(1, ushort.MaxValue);

    public void Dispose() => _http.Dispose();
}
