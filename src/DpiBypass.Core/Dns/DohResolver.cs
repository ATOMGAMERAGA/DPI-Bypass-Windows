using System.Collections.Concurrent;
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
/// How one encrypted DNS endpoint is doing, for the diagnostics report.
/// </summary>
/// <remarks>
/// "Not measured" is a distinct answer from "failing": an endpoint further down the
/// chain than anything the session needed has never been asked, and reporting that as
/// healthy or as broken would both be inventions.
/// </remarks>
public sealed record DohEndpointStatus(
    string Provider,
    bool Healthy,
    long? LastLatencyMs,
    string? LastFailure,
    TimeSpan? PenaltyRemaining);

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
    private const int MaxDnsMessageBytes = 65535;

    /// <summary>How many distinct upstream queries may be in flight at once.</summary>
    private const int MaxUpstreamJobs = 128;

    /// <summary>How many clients may share one upstream query.</summary>
    private const int MaxWaitersPerJob = 256;

    /// <summary>A penalty above this demotes an endpoint to the end of the chain.</summary>
    private const long PenaltyThresholdMs = 5000;

    /// <summary>
    /// How long a demotion lasts.
    /// </summary>
    /// <remarks>
    /// The penalty used to have no end at all: one timeout put an endpoint last for the
    /// rest of the process, and because a demoted endpoint is only reached when every
    /// other one has failed, the machine's preferred resolver could be retired for a
    /// session by a single blip on a train. It expires now, which is also what makes
    /// "when is it retried" an answerable question.
    /// </remarks>
    private static readonly TimeSpan PenaltyWindow = TimeSpan.FromSeconds(60);

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
    private readonly TimeSpan _overallTimeout;
    private readonly Dictionary<string, EndpointHealth> _health = new(StringComparer.Ordinal);
    private readonly Lock _latencyGate = new();

    /// <summary>Upstream queries in flight, keyed by the question they are asking.</summary>
    private readonly Dictionary<string, UpstreamJob> _jobs = new(StringComparer.Ordinal);
    private readonly Lock _jobGate = new();

    /// <summary>Cancels every shared job when the resolver is disposed.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    private long _epoch;
    private long _coalesced;
    private long _upstreamQueries;

    /// <param name="transport">
    /// Replaces the HTTPS stack. Only the tests pass one: sharing, penalties and buffer
    /// ownership are decided entirely by the sequence of answers, and pinning them
    /// against a scripted transport is the difference between a test that proves the
    /// behaviour and one that needs the internet and three third party resolvers.
    /// </param>
    public DohResolver(
        IReadOnlyList<DohEndpoint>? chain = null,
        TimeSpan? perEndpointTimeout = null,
        TimeSpan? overallTimeout = null,
        HttpMessageHandler? transport = null)
    {
        _chain = chain is { Count: > 0 } ? chain : DefaultChain;
        _perEndpointTimeout = perEndpointTimeout ?? TimeSpan.FromSeconds(4);
        _overallTimeout = overallTimeout ?? TimeSpan.FromSeconds(10);

        if (transport is not null)
        {
            _http = BuildClient(transport);
            return;
        }

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

        _http = BuildClient(handler);
    }

    private static HttpClient BuildClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DpiBypass/1.0");
        return client;
    }

    /// <summary>
    /// The provider that answered the most recent query.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same thing as <see cref="VerifiedProvider"/>. This one says
    /// who happened to answer last, which after a failover is the fallback rather than
    /// the user's preferred resolver - and reporting that as "healthy" is how a card ends
    /// up claiming Cloudflare is fine while every query is really going to Quad9.
    /// </remarks>
    public string? ActiveProvider { get; private set; }

    /// <summary>
    /// The provider whose last answer passed validation and which is not being penalised.
    /// </summary>
    /// <remarks>
    /// Null while nothing has answered correctly yet, or while the endpoint that last did
    /// is still inside its penalty window. This is what the UI should show when it wants
    /// to say encrypted DNS is genuinely working, rather than merely that something replied.
    /// </remarks>
    public string? VerifiedProvider
    {
        get
        {
            lock (_latencyGate)
            {
                if (_verifiedUrl is null || !_health.TryGetValue(_verifiedUrl, out var health))
                {
                    return null;
                }

                return IsPenalised(health, DateTimeOffset.UtcNow) ? null : _verifiedProvider;
            }
        }
    }

    private string? _verifiedProvider;
    private string? _verifiedUrl;

    /// <summary>
    /// Which network generation the answers belong to.
    /// </summary>
    /// <remarks>
    /// Read before a query and again after it: a different value means the machine moved
    /// while the query was in flight, and the answer describes a resolver on a link we are
    /// no longer on. The proxy uses it to keep such an answer out of the new network's cache.
    /// </remarks>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>Queries that were answered by joining an upstream request already running.</summary>
    public long CoalescedQueries => Interlocked.Read(ref _coalesced);

    /// <summary>Queries that actually went to a provider.</summary>
    public long UpstreamQueries => Interlocked.Read(ref _upstreamQueries);

    /// <summary>
    /// The machine is on a different network: forget which endpoints were unreachable.
    /// </summary>
    /// <remarks>
    /// Reachability is a property of the link, not of the endpoint. A resolver blocked on
    /// a hotel network is usually the fastest one at home, and carrying the penalty across
    /// the transition means arriving home on the fallback for the next minute.
    /// </remarks>
    public void OnNetworkChanged()
    {
        Interlocked.Increment(ref _epoch);

        lock (_latencyGate)
        {
            _health.Clear();
            _verifiedProvider = null;
            _verifiedUrl = null;
        }
    }

    /// <summary>
    /// Sends a query through the chain, sharing the work with anyone asking the same thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cold cache and a browser opening a page is fifty simultaneous lookups of a
    /// handful of names, and each one used to become its own HTTPS request. Identical
    /// questions now ride one upstream request: the key is the whole query with only the
    /// transaction ID cleared, so the DNS flags, the record type and class, and any
    /// EDNS/DNSSEC options all still tell two different questions apart.
    /// </para>
    /// <para>
    /// Every waiter gets its own copy of the answer carrying its own transaction ID, so
    /// nobody is handed a buffer another client is about to write into. A waiter that
    /// gives up leaves; it does not cancel the request the others are still waiting for.
    /// The shared request is dropped once the last waiter has gone.
    /// </para>
    /// </remarks>
    public async Task<byte[]?> QueryAsync(byte[] query, CancellationToken cancellationToken)
    {
        if (!DnsMessage.TryBuildCacheKey(query, out var key))
        {
            // Nothing to key on, so nothing to share. It still gets asked.
            return await SendThroughChainAsync(query, cancellationToken).ConfigureAwait(false);
        }

        UpstreamJob job;
        var owner = false;

        lock (_jobGate)
        {
            if (_jobs.TryGetValue(key, out var existing) && existing.Waiters < MaxWaitersPerJob)
            {
                existing.Waiters++;
                job = existing;
                Interlocked.Increment(ref _coalesced);
            }
            else if (_jobs.Count >= MaxUpstreamJobs)
            {
                // Refused rather than queued: an unbounded backlog of upstream requests
                // is how a resolver turns a burst into a stall for everything after it.
                return null;
            }
            else
            {
                job = new UpstreamJob(CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
                _jobs[key] = job;
                owner = true;
            }
        }

        if (owner)
        {
            // The shared request carries transaction ID 0 - which is also what RFC 8484
            // §4.1 recommends for DoH - and each waiter stamps its own ID on its own copy.
            var shared = query.ToArray();
            DnsMessage.SetId(shared, 0);
            Interlocked.Increment(ref _upstreamQueries);
            job.Start(SendThroughChainAsync(shared, job.Cancellation.Token));
        }

        try
        {
            // WaitAsync, not a linked token: this waiter walking away must not take the
            // answer from the others still waiting for it.
            var answer = await job.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (answer is null)
            {
                return null;
            }

            var mine = answer.ToArray();
            DnsMessage.SetId(mine, DnsMessage.GetId(query));
            return mine;
        }
        finally
        {
            Release(key, job);
        }
    }

    /// <summary>
    /// Drops a waiter, and the shared request with it once the last one has gone.
    /// </summary>
    private void Release(string key, UpstreamJob job)
    {
        var abandoned = false;

        lock (_jobGate)
        {
            job.Waiters--;
            if (job.Waiters <= 0)
            {
                if (_jobs.TryGetValue(key, out var current) && ReferenceEquals(current, job))
                {
                    _jobs.Remove(key);
                }

                abandoned = true;
            }
        }

        if (abandoned)
        {
            job.Dispose();
        }
    }

    private async Task<byte[]?> SendThroughChainAsync(byte[] query, CancellationToken cancellationToken)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(_overallTimeout);

        foreach (var endpoint in OrderedChain())
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                linked.CancelAfter(_perEndpointTimeout);

                using var content = new ByteArrayContent(query);
                content.Headers.ContentType = DnsMediaType;

                using var response = await _http
                    .PostAsync(endpoint.Url, content, linked.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    RecordFailure(endpoint, $"HTTP {(int)response.StatusCode}");
                    continue;
                }

                if (!string.Equals(
                        response.Content.Headers.ContentType?.MediaType,
                        "application/dns-message",
                        StringComparison.OrdinalIgnoreCase)
                    || response.Content.Headers.ContentLength > MaxDnsMessageBytes)
                {
                    RecordFailure(endpoint, "beklenmeyen içerik türü");
                    continue;
                }

                await response.Content.LoadIntoBufferAsync(MaxDnsMessageBytes, linked.Token).ConfigureAwait(false);
                var payload = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
                if (payload.Length < DnsMessage.HeaderLength || !DnsMessage.IsResponseForQuery(query, payload))
                {
                    // An answer to a question nobody asked. This used to move on without
                    // recording anything, so an endpoint that reliably answered wrongly
                    // kept its place at the head of the chain and was tried first, every
                    // time, for as long as it went on being wrong.
                    RecordFailure(endpoint, "yanıt sorguyla eşleşmedi");
                    continue;
                }

                RecordSuccess(endpoint, stopwatch.Elapsed);
                return payload;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (overall.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                // Any failure just means "try the next provider". Cloudflare being
                // unreachable is exactly why Google and Quad9 are in the list.
                RecordFailure(endpoint, ex.GetType().Name);
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
    /// <remarks>
    /// The demotion expires. Every endpoint is still in the list either way - a demoted
    /// one is tried last rather than dropped - but once its window has passed it goes
    /// back to its configured position and is tried first again, which is what stops one
    /// bad minute from choosing the user's resolver for the rest of the session.
    /// </remarks>
    private IEnumerable<DohEndpoint> OrderedChain()
    {
        var now = DateTimeOffset.UtcNow;

        lock (_latencyGate)
        {
            return _chain
                .Select((endpoint, index) => (
                    endpoint,
                    index,
                    demoted: _health.TryGetValue(endpoint.Url, out var health) && IsPenalised(health, now)))
                .OrderBy(x => x.demoted ? 1 : 0)
                .ThenBy(x => x.index)
                .Select(x => x.endpoint)
                .ToList();
        }
    }

    private static bool IsPenalised(EndpointHealth health, DateTimeOffset now)
        => health.PenaltyMs > PenaltyThresholdMs && now - health.RecordedAt < PenaltyWindow;

    /// <summary>How each endpoint is doing right now, for the diagnostics report.</summary>
    public IReadOnlyList<DohEndpointStatus> EndpointStatus()
    {
        var now = DateTimeOffset.UtcNow;

        lock (_latencyGate)
        {
            return
            [
                .. _chain.Select(endpoint =>
                {
                    var known = _health.TryGetValue(endpoint.Url, out var health);
                    return new DohEndpointStatus(
                        endpoint.Provider,
                        known && !IsPenalised(health, now) && health.LastFailure is null,
                        known ? health.PenaltyMs : null,
                        known ? health.LastFailure : "ölçülmedi",
                        known && IsPenalised(health, now)
                            ? PenaltyWindow - (now - health.RecordedAt)
                            : null);
                }),
            ];
        }
    }

    private void RecordSuccess(DohEndpoint endpoint, TimeSpan elapsed)
    {
        lock (_latencyGate)
        {
            _health[endpoint.Url] = new EndpointHealth((long)elapsed.TotalMilliseconds, DateTimeOffset.UtcNow, null);
            _verifiedProvider = endpoint.Provider;
            _verifiedUrl = endpoint.Url;
        }

        ActiveProvider = endpoint.Provider;
    }

    private void RecordFailure(DohEndpoint endpoint, string reason)
    {
        lock (_latencyGate)
        {
            _health[endpoint.Url] = new EndpointHealth(
                (long)TimeSpan.FromSeconds(30).TotalMilliseconds,
                DateTimeOffset.UtcNow,
                reason);
        }
    }

    /// <summary>What is known about one endpoint, and when its penalty runs out.</summary>
    private readonly record struct EndpointHealth(long PenaltyMs, DateTimeOffset RecordedAt, string? LastFailure);

    /// <summary>
    /// One shared upstream request and the clients waiting on it.
    /// </summary>
    /// <remarks>
    /// The completion is handed out to every waiter, and the cancellation belongs to the
    /// request rather than to any one of them - which is what lets a client walk away
    /// without taking the answer from everybody else waiting for the same name.
    /// </remarks>
    private sealed class UpstreamJob : IDisposable
    {
        private readonly TaskCompletionSource<byte[]?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UpstreamJob(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
            Waiters = 1;
        }

        public CancellationTokenSource Cancellation { get; }

        public int Waiters { get; set; }

        public Task<byte[]?> Completion => _completion.Task;

        public void Start(Task<byte[]?> work)
            => _ = work.ContinueWith(
                finished =>
                {
                    if (finished.IsFaulted)
                    {
                        // Not surfaced to the waiters as a fault: every failure in the
                        // chain already means "no answer", and a caller told the DNS
                        // lookup failed is exactly what a null says.
                        _ = finished.Exception;
                        _completion.TrySetResult(null);
                    }
                    else if (finished.IsCanceled)
                    {
                        _completion.TrySetResult(null);
                    }
                    else
                    {
                        _completion.TrySetResult(finished.Result);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        public void Dispose()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _completion.TrySetResult(null);
            Cancellation.Dispose();
        }
    }

    private static ushort NextId() => (ushort)Random.Shared.Next(1, ushort.MaxValue);

    public void Dispose()
    {
        // Shared requests first: they hold HTTP responses, and a waiter still parked on
        // one after the client is gone would wait for its own timeout to fire.
        UpstreamJob[] jobs;
        lock (_jobGate)
        {
            jobs = [.. _jobs.Values];
            _jobs.Clear();
        }

        foreach (var job in jobs)
        {
            job.Dispose();
        }

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _shutdown.Dispose();
        _http.Dispose();
    }
}
