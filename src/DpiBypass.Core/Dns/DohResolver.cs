using System.Collections.Concurrent;
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
/// <para>
/// Every endpoint is addressed by IP literal on purpose. TLS forbids putting an IP
/// address in SNI, so these connections carry no hostname at all - which means an
/// SNI filter has nothing to match on and resolution keeps working even where
/// plain DNS is poisoned. The certificates for 1.1.1.1, 8.8.8.8 and 9.9.9.9 all
/// carry IP SANs, so ordinary chain validation still applies.
/// </para>
/// <para>
/// The chain is raced rather than walked. Walking it - try Cloudflare, wait for it
/// to time out, try Google, wait again - is fine when one provider is down and
/// ruinous when the operator is dropping traffic to all of them, which is exactly
/// the network this app exists for: six endpoints at four seconds each is
/// twenty-four seconds for a single name, with the machine's resolvers already
/// pointed at us. Every program on the box stops working and the app looks like
/// what broke the internet. So a query starts at the head of the chain, brings the
/// next endpoint in alongside it after a short delay rather than instead of it,
/// takes the first good answer, and gives up on the lot within a fixed budget so
/// the caller can fall back to something that does work.
/// </para>
/// <para>
/// Endpoints that fail are also taken out of the rotation for a while. Without
/// that, an operator blocking 1.1.1.1 costs every single query the hedge delay
/// before the endpoint that does work is even contacted.
/// </para>
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

    /// <summary>How long a query may take in total, however many endpoints it tries.</summary>
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the head of the chain gets on its own before the next endpoint is
    /// started alongside it. Long enough that a healthy resolver is never raced,
    /// short enough that a black hole costs a fraction of a second.
    /// </summary>
    private static readonly TimeSpan HedgeDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>Failures before an endpoint is rested, and for how long.</summary>
    private const int FailuresBeforeCooldown = 2;

    private static readonly TimeSpan MinimumCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaximumCooldown = TimeSpan.FromMinutes(5);

    private static readonly MediaTypeHeaderValue DnsMediaType = new("application/dns-message");

    private readonly HttpClient _http;
    private readonly IReadOnlyList<DohEndpoint> _chain;
    private readonly TimeSpan _perEndpointTimeout;
    private readonly TimeSpan _budget;
    private readonly ConcurrentDictionary<string, Health> _health = new(StringComparer.Ordinal);

    public DohResolver(
        IReadOnlyList<DohEndpoint>? chain = null,
        TimeSpan? perEndpointTimeout = null,
        TimeSpan? totalBudget = null)
    {
        _chain = chain is { Count: > 0 } ? chain : DefaultChain;
        _perEndpointTimeout = perEndpointTimeout ?? TimeSpan.FromSeconds(2.5);
        _budget = totalBudget ?? DefaultBudget;

        var handler = new SocketsHttpHandler
        {
            // Long lived pooled HTTP/2 connections keep the added latency at zero
            // after the first query - a resolve is one round trip on a warm socket.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3),
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
        var endpoints = OrderedChain();
        if (endpoints.Count == 0)
        {
            return null;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_budget);

        var attempts = new List<Task<byte[]?>>();
        var next = 0;

        try
        {
            while (true)
            {
                if (next < endpoints.Count)
                {
                    attempts.Add(AttemptAsync(endpoints[next++], query, budget.Token));
                }

                if (attempts.Count == 0)
                {
                    return null;
                }

                Task finished;

                if (next < endpoints.Count)
                {
                    // No token on the hedge: cancelling it in the finally below would
                    // fault a task nobody is left to observe.
                    var hedge = Task.Delay(HedgeDelay);
                    var waiting = new List<Task>(attempts.Count + 1);
                    waiting.AddRange(attempts);
                    waiting.Add(hedge);

                    finished = await Task.WhenAny(waiting).ConfigureAwait(false);

                    if (ReferenceEquals(finished, hedge))
                    {
                        // Still waiting on the ones in flight - bring the next endpoint
                        // in alongside them rather than in place of them.
                        continue;
                    }
                }
                else
                {
                    finished = await Task.WhenAny(attempts).ConfigureAwait(false);
                }

                var attempt = (Task<byte[]?>)finished;
                attempts.Remove(attempt);

                // AttemptAsync answers with null rather than throwing, so this await
                // is reading a result, not risking one.
                var answer = await attempt.ConfigureAwait(false);
                if (answer is not null)
                {
                    return answer;
                }

                if (attempts.Count == 0 && next >= endpoints.Count)
                {
                    return null;
                }

                if (budget.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }
            }
        }
        finally
        {
            // Whatever is still in flight has nobody waiting for it. Cancelling is
            // what stops a dead endpoint holding a socket for the rest of its timeout.
            try
            {
                await budget.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already cancelled, or disposed by the using above on the way out.
            }
        }
    }

    /// <summary>One endpoint, once. Never throws.</summary>
    /// <param name="raceToken">
    /// Cancelled when the query is over - because another endpoint answered, or
    /// because the budget ran out. An endpoint that was still in flight at that point
    /// did not fail, it was simply not needed, and recording it as a failure would
    /// eventually rest every endpoint in the chain.
    /// </param>
    private async Task<byte[]?> AttemptAsync(DohEndpoint endpoint, byte[] query, CancellationToken raceToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(raceToken);
            linked.CancelAfter(_perEndpointTimeout);

            using var content = new ByteArrayContent(query);
            content.Headers.ContentType = DnsMediaType;

            using var response = await _http
                .PostAsync(endpoint.Url, content, linked.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                RecordFailure(endpoint, raceToken);
                return null;
            }

            var payload = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            if (payload.Length < DnsMessage.HeaderLength)
            {
                RecordFailure(endpoint, raceToken);
                return null;
            }

            RecordSuccess(endpoint);
            ActiveProvider = endpoint.Provider;
            return payload;
        }
        catch (Exception)
        {
            // Any failure just means "this endpoint did not answer". Cloudflare being
            // unreachable is exactly why Google and Quad9 are in the list.
            RecordFailure(endpoint, raceToken);
            return null;
        }
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
    /// Preserves the configured provider preference but moves endpoints that are
    /// resting to the back, so a blocked resolver is not raced first every time.
    /// </summary>
    /// <remarks>
    /// Resting endpoints are kept rather than dropped: a machine where every resolver
    /// has failed once still has to try something, and an empty chain would mean no
    /// name resolution at all until the cooldowns expired.
    /// </remarks>
    internal IReadOnlyList<DohEndpoint> OrderedChain()
    {
        var now = DateTime.UtcNow;

        return
        [
            .. _chain
                .Select((endpoint, index) => (endpoint, index, health: _health.GetValueOrDefault(endpoint.Url)))
                .OrderBy(x => x.health?.IsResting(now) == true ? 1 : 0)
                .ThenBy(x => x.index)
                .Select(x => x.endpoint),
        ];
    }

    private void RecordSuccess(DohEndpoint endpoint)
        => _health.GetOrAdd(endpoint.Url, _ => new Health()).Succeeded();

    private void RecordFailure(DohEndpoint endpoint, CancellationToken raceToken)
    {
        if (raceToken.IsCancellationRequested)
        {
            return;
        }

        _health.GetOrAdd(endpoint.Url, _ => new Health()).Failed();
    }

    private static ushort NextId() => (ushort)Random.Shared.Next(1, ushort.MaxValue);

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// One endpoint's recent record. Mutated from several query tasks at once, so
    /// every field is written under its own lock rather than interlocked piecemeal.
    /// </summary>
    private sealed class Health
    {
        private readonly Lock _gate = new();
        private int _consecutiveFailures;
        private DateTime _restingUntil = DateTime.MinValue;

        public bool IsResting(DateTime now)
        {
            lock (_gate)
            {
                return _restingUntil > now;
            }
        }

        public void Succeeded()
        {
            lock (_gate)
            {
                _consecutiveFailures = 0;
                _restingUntil = DateTime.MinValue;
            }
        }

        public void Failed()
        {
            lock (_gate)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures < FailuresBeforeCooldown)
                {
                    return;
                }

                // Backs off as the failures pile up, so a resolver that is blocked
                // rather than briefly busy stops being asked several times a minute.
                var multiplier = Math.Min(1 << Math.Min(_consecutiveFailures - FailuresBeforeCooldown, 8), 16);
                var cooldown = TimeSpan.FromTicks(Math.Min(MinimumCooldown.Ticks * multiplier, MaximumCooldown.Ticks));
                _restingUntil = DateTime.UtcNow.Add(cooldown);
            }
        }
    }
}
