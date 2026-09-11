using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace DpiBypass.Core.Dns;

/// <summary>
/// A loopback DNS server that answers from cache or forwards over DoH.
/// </summary>
/// <remarks>
/// Windows can only be pointed at a plain DNS server, so to get the whole machine
/// onto encrypted DNS we listen on 127.0.0.1:53 and do the HTTPS part ourselves.
/// Answers are cached, which is why switching to this does not cost latency: a
/// warm lookup is a loopback round trip.
/// </remarks>
public sealed class DnsProxyServer : IAsyncDisposable
{
    private const int MaxUdpResponse = 4096;
    private const int MaxCacheEntries = 4096;
    private const int MaxConcurrentQueries = 256;

    /// <summary>
    /// How far past its own TTL an answer may still be handed out when nothing upstream
    /// is answering.
    /// </summary>
    /// <remarks>
    /// RFC 8767's whole point: while the resolvers are unreachable, a slightly old address
    /// is worth far more than a failure, because the failure is what the user experiences
    /// as the internet having stopped. Five minutes was short enough that a DoH outage of
    /// any real length - an operator throttling 443, a hotel portal, a link that flaps for
    /// a quarter of an hour - took name resolution off the machine with it. This only ever
    /// applies after every endpoint in the chain has already failed.
    /// </remarks>
    private static readonly TimeSpan MaxStale = TimeSpan.FromHours(1);

    /// <summary>How long one query on a TCP connection may take from prefix to answer.</summary>
    private static readonly TimeSpan TcpQueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a TCP connection may sit between queries before it is closed.
    /// </summary>
    /// <remarks>
    /// RFC 7766 §6.2.1 asks a server to keep the connection open for further queries and
    /// to set its own idle timeout; each held connection is one of the proxy's request
    /// slots, so a client that connects and goes quiet must give the slot back.
    /// </remarks>
    private static readonly TimeSpan TcpIdleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The most queries one TCP connection may ask before it is closed.
    /// </summary>
    /// <remarks>
    /// A ceiling on the work a single connection can claim, not a limit any real resolver
    /// will reach: Windows opens a TCP connection for one truncated answer and closes it.
    /// </remarks>
    private const int MaxQueriesPerConnection = 64;

    private readonly DohResolver _resolver;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly Lock _cacheGate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _workers = [];
    private readonly SemaphoreSlim _capacity = new(MaxConcurrentQueries, MaxConcurrentQueries);
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private readonly Action<string>? _log;
    private long _nextRequestId;

    private Socket? _udp4;
    private Socket? _udp6;
    private Socket? _tcp4;
    private Socket? _tcp6;
    private long _served;
    private long _cacheHits;
    private long _truncated;
    private long _partialSends;
    private long _crossNetworkDrops;
    private long _sinkholed;
    private long _overCapacity;
    private long _staleAnswers;

    public DnsProxyServer(DohResolver resolver, Action<string>? log = null)
    {
        _resolver = resolver;
        _log = log;
    }

    /// <summary>
    /// Where the cache reads the time from.
    /// </summary>
    /// <remarks>
    /// Only the tests replace it. Whether an answer is fresh, stale-but-usable or too old
    /// to hand out at all is entirely a question of elapsed time, and the alternative to a
    /// seam here is a test that sits still for an hour to find out.
    /// </remarks>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// True while every listener this proxy bound is still being served.
    /// </summary>
    /// <remarks>
    /// <see cref="IsRunning"/> only says the proxy was started and not yet disposed. If a
    /// listener loop ends on its own - a socket torn down under it by an adapter reset, an
    /// exception nobody predicted - the port stays bound and every query aimed at it goes
    /// unanswered, which on a machine whose resolvers point at 127.0.0.1 is the whole of
    /// name resolution. The watchdog rebuilds the proxy on this.
    /// </remarks>
    public bool IsHealthy
    {
        get
        {
            if (!IsRunning)
            {
                return false;
            }

            foreach (var worker in _workers)
            {
                if (worker.IsCompleted)
                {
                    return false;
                }
            }

            return _workers.Count > 0;
        }
    }

    public Func<bool>? SuppressIPv6Answers { get; init; }

    /// <summary>
    /// Names to answer with an unroutable address instead of resolving them.
    /// </summary>
    /// <remarks>
    /// Asked once per query, before anything else looks at it, and expected to be a set
    /// membership test - which is why the advertisement block costs nothing measurable:
    /// a blocked name never reaches the cache, the resolver or the network, and a name
    /// that is not blocked has paid one hash lookup for the privilege.
    /// </remarks>
    public Func<string, bool>? Sinkhole { get; init; }

    public long QueriesServed => Interlocked.Read(ref _served);

    public long CacheHits => Interlocked.Read(ref _cacheHits);

    /// <summary>Answers sent back with TC set because they did not fit the client's buffer.</summary>
    public long TruncatedAnswers => Interlocked.Read(ref _truncated);

    /// <summary>TCP answers the client stopped reading half way through.</summary>
    public long AbandonedTcpAnswers => Interlocked.Read(ref _partialSends);

    /// <summary>Answers that came back after a network change and were not cached.</summary>
    public long CrossNetworkDrops => Interlocked.Read(ref _crossNetworkDrops);

    /// <summary>Questions answered locally because the name is on the block list.</summary>
    public long SinkholedAnswers => Interlocked.Read(ref _sinkholed);

    /// <summary>Queries answered without a request slot, from cache or as SERVFAIL.</summary>
    public long OverCapacityAnswers => Interlocked.Read(ref _overCapacity);

    /// <summary>Answers handed out past their TTL because nothing upstream would answer.</summary>
    public long StaleAnswers => Interlocked.Read(ref _staleAnswers);

    public int Port { get; private set; } = 53;

    /// <summary>True when the IPv6 loopback listeners came up too.</summary>
    public bool HasIPv6 { get; private set; }

    /// <summary>
    /// Raised with each distinct hostname the machine looks up. The discovery pass
    /// uses it to notice sites it has not measured yet.
    /// </summary>
    public event Action<string>? NameResolved;

    /// <summary>Binds the loopback listeners. Returns false when port 53 is already taken.</summary>
    public bool TryStart(int port = 53)
    {
        if (IsRunning)
        {
            return true;
        }

        Port = port;
        HasIPv6 = false;

        try
        {
            _udp4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IgnoreConnectionResets(_udp4);
            _udp4.Bind(new IPEndPoint(IPAddress.Loopback, port));

            _tcp4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _tcp4.Bind(new IPEndPoint(IPAddress.Loopback, port));
            _tcp4.Listen(64);
        }
        catch (SocketException ex)
        {
            _log?.Invoke($"DNS proxy could not bind 127.0.0.1:{port} ({ex.SocketErrorCode}).");
            Cleanup();
            return false;
        }

        // IPv6 loopback is best effort: plenty of machines have it disabled.
        try
        {
            _udp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            IgnoreConnectionResets(_udp6);
            _udp6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));

            // Windows retries over TCP whenever an answer comes back truncated, so a
            // UDP-only [::1] listener would look alive and then time out on big replies.
            _tcp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            _tcp6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
            _tcp6.Listen(64);
        }
        catch (SocketException)
        {
            _udp6?.Dispose();
            _udp6 = null;
            _tcp6?.Dispose();
            _tcp6 = null;
        }

        _workers.Add(Task.Run(() => ServeUdpAsync(_udp4!, _stopping.Token)));
        if (_udp6 is not null)
        {
            _workers.Add(Task.Run(() => ServeUdpAsync(_udp6, _stopping.Token)));
        }

        _workers.Add(Task.Run(() => ServeTcpAsync(_tcp4!, _stopping.Token)));
        if (_tcp6 is not null)
        {
            _workers.Add(Task.Run(() => ServeTcpAsync(_tcp6, _stopping.Token)));
        }

        HasIPv6 = _udp6 is not null && _tcp6 is not null;
        IsRunning = true;
        _log?.Invoke($"DNS proxy listening on 127.0.0.1:{port} (UDP + TCP).");
        return true;
    }

    /// <summary>SIO_UDP_CONNRESET, which Windows leaves on for datagram sockets.</summary>
    private const int SioUdpConnectionReset = -1744830452;

    /// <summary>
    /// Stops a client that walked away from breaking the next read.
    /// </summary>
    /// <remarks>
    /// Windows turns the ICMP port-unreachable that comes back from an abandoned client
    /// into a WSAECONNRESET on the server's *next* receive - an error about a datagram
    /// nobody is waiting for any more, raised against the socket the whole machine
    /// resolves names through. A resolver that gives up on a query and closes its socket
    /// is completely ordinary, so this is not an edge case; it is the default behaviour
    /// every Windows UDP server has to switch off.
    /// </remarks>
    private static void IgnoreConnectionResets(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            socket.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null);
        }
        catch (Exception)
        {
            // Not supported here; the receive loop absorbs the resets instead.
        }
    }

    private async Task ServeUdpAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxUdpResponse];
        var remote = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var query = buffer[..received.ReceivedBytes];
            var sender = received.RemoteEndPoint;
            var clientLimit = DnsMessage.GetClientUdpPayloadSize(query);

            // Acquire capacity before creating work so a burst cannot build an
            // unbounded queue of tasks behind the resolver semaphore.
            if (!_capacity.Wait(0))
            {
                try
                {
                    // A burst is exactly when the machine is least able to afford a
                    // failure, and an answer already in hand costs nothing to hand out.
                    // Only a name that has never been resolved here falls through to
                    // SERVFAIL, which is the honest answer for one nobody can look up.
                    var answer = AnswerFromCache(query, allowStale: true) ?? BuildServerFailure(query);
                    if (answer.Length > clientLimit)
                    {
                        answer = DnsMessage.BuildTruncatedResponse(answer);
                        Interlocked.Increment(ref _truncated);
                    }

                    Interlocked.Increment(ref _overCapacity);
                    await socket.SendToAsync(answer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutting down, or the datagram sender vanished.
                }

                continue;
            }

            TrackRequest(HandleUdpQueryAsync(socket, query, sender, clientLimit, cancellationToken));
        }
    }

    private async Task HandleUdpQueryAsync(
        Socket socket,
        byte[] query,
        EndPoint sender,
        int clientPayloadLimit,
        CancellationToken cancellationToken)
    {
        try
        {
            var answer = await ResolveAsync(query, cancellationToken).ConfigureAwait(false);
            if (answer is not null)
            {
                // Over the size this client said it can take, the answer goes back as a
                // header with TC set rather than as its own first N bytes: the client
                // reads that and asks the same question over TCP, which is a listener
                // this proxy also runs. Cutting the datagram short instead would hand the
                // resolver a message whose record counts promise sections that are not
                // there - a malformed answer, which is worse than a large one.
                if (answer.Length > clientPayloadLimit)
                {
                    _log?.Invoke(
                        $"DNS answer of {answer.Length} bytes exceeds the client's {clientPayloadLimit} byte "
                        + "buffer; replying truncated so it retries over TCP.");
                    answer = DnsMessage.BuildTruncatedResponse(answer);
                    Interlocked.Increment(ref _truncated);
                }

                await socket.SendToAsync(answer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A malformed query or abandoned sender affects only this datagram.
        }
        finally
        {
            _capacity.Release();
        }
    }

    private async Task ServeTcpAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // One client resetting mid-accept must not take the listener down for
                // good: the port would stay bound while every later query times out.
                continue;
            }

            if (!_capacity.Wait(0))
            {
                client.Dispose();
                continue;
            }

            TrackRequest(HandleTcpClientAsync(client, cancellationToken));
        }
    }

    /// <summary>
    /// Serves one TCP client until it goes away, its budget runs out, or we shut down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Successive queries on one connection are answered rather than the connection being
    /// closed after the first, which is what RFC 7766 §6.2.1 asks of a server and what a
    /// resolver retrying a truncated answer expects. Both bounds it needs come with it: a
    /// per-query deadline, and an idle timeout so a connection that stops asking gives its
    /// request slot back.
    /// </para>
    /// <para>
    /// The answer goes out through <see cref="DnsStreamTransport.SendAllAsync"/>, which is
    /// the actual fix here: the length prefix and the message used to be handed to one
    /// <c>SendAsync</c> whose return value was dropped, so a partial send produced a reply
    /// shorter than its own prefix and a client that waited for the rest until it timed out.
    /// </para>
    /// </remarks>
    private async Task HandleTcpClientAsync(Socket client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var sink = new DnsStreamTransport.SocketSink(client);
            var lengthPrefix = new byte[2];

            try
            {
                for (var served = 0; served < MaxQueriesPerConnection; served++)
                {
                    // A fresh deadline per query: the first one gets the connection
                    // timeout, and a client that keeps asking keeps being served, but
                    // neither the wait for the next question nor one answer can run long.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idle.CancelAfter(served == 0 ? TcpQueryTimeout : TcpIdleTimeout);

                    if (!await ReadExactAsync(client, lengthPrefix, idle.Token).ConfigureAwait(false))
                    {
                        return;
                    }

                    using var query = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    query.CancelAfter(TcpQueryTimeout);
                    var token = query.Token;

                    var length = BinaryPrimitives.ReadUInt16BigEndian(lengthPrefix);
                    if (length is 0 or > MaxUdpResponse)
                    {
                        return;
                    }

                    var message = new byte[length];
                    if (!await ReadExactAsync(client, message, token).ConfigureAwait(false))
                    {
                        return;
                    }

                    var answer = await ResolveAsync(message, token).ConfigureAwait(false);
                    if (answer is null)
                    {
                        return;
                    }

                    // No size limit on this leg: TCP is where a client is sent when an
                    // answer will not fit in a datagram, so truncating here would be a
                    // loop with no way out of it.
                    if (!await DnsStreamTransport
                        .SendAllAsync(sink, DnsStreamTransport.Frame(answer), token)
                        .ConfigureAwait(false))
                    {
                        // The client stopped taking bytes half way through an answer.
                        // Nothing useful can follow on this connection.
                        Interlocked.Increment(ref _partialSends);
                        return;
                    }
                }

                _log?.Invoke($"DNS TCP client reached {MaxQueriesPerConnection} queries; closing the connection.");
            }
            catch (OperationCanceledException)
            {
                // Client deadline, idle timeout, or normal shutdown.
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Malformed, stalled or abandoned TCP query; drop it.
            }
            finally
            {
                _capacity.Release();
            }
        }
    }

    private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private void TrackRequest(Task task)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        _requests[id] = task;
        _ = task.ContinueWith(
            _task =>
            {
                _requests.TryRemove(id, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Answers one query from the cache, from a stale entry, or from upstream.
    /// </summary>
    /// <remarks>Internal so the tests can drive it without binding port 53.</remarks>
    internal async Task<byte[]?> ResolveAsync(byte[] query, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _served);

        if (!DnsMessage.TryReadQuestion(query, out var question))
        {
            return null;
        }

        // First, and before the hotspot rule, so a blocked name is refused identically
        // whether or not Vodafone Sınırsız Modu is suppressing IPv6 under it. Nothing
        // downstream sees the name: it is not cached, the discovery pass is not told
        // about it, and no packet leaves the machine on its account.
        if (Sinkhole is { } blocked && blocked(question.Name)
            && DnsMessage.BuildSinkholeResponse(query) is { } refusal)
        {
            Interlocked.Increment(ref _sinkholed);
            return refusal;
        }

        // Evaluate before the cache: the hotspot rule can make a previously cached
        // IPv6 address unusable. A/SRV lookups must continue to reach upstream.
        if (question.Type == DnsRecordType.Aaaa && question.Class == 1
            && SuppressIPv6Answers?.Invoke() == true)
        {
            var empty = DnsMessage.BuildQuery(DnsMessage.GetId(query), question.Name, question.Type,
                recursionDesired: (query[2] & 1) != 0);
            empty[2] |= 0x80;
            empty[3] = 0x80; // NODATA with recursion available, not NXDOMAIN.
            return empty;
        }

        var id = DnsMessage.GetId(query);
        if (!DnsMessage.TryBuildCacheKey(query, out var key))
        {
            return null;
        }

        // Address lookups only: a PTR or TXT query says nothing about a site the user
        // is trying to reach, and our own ASN lookups run over TXT.
        if (question.Type is DnsRecordType.A or DnsRecordType.Aaaa && NameResolved is { } observer)
        {
            try
            {
                observer(question.Name);
            }
            catch (Exception)
            {
                // A misbehaving observer must not break name resolution.
            }
        }

        var hasCached = _cache.TryGetValue(key, out var cached);
        if (hasCached && cached!.Expires > Clock.GetUtcNow())
        {
            Interlocked.Increment(ref _cacheHits);
            return Serve(cached, id, Clock.GetUtcNow());
        }

        // Read before the query and compared after it. A lookup that started on the
        // cafe's resolver and came back after the laptop joined the home network is an
        // answer about a link nobody is on: it goes to the client that asked for it, and
        // it stays out of the new network's cache.
        var epoch = _resolver.Epoch;

        var response = await _resolver.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            // Serve a stale answer rather than nothing - a slightly old IP beats a
            // dead name lookup while the operator is throttling us.
            var now = Clock.GetUtcNow();
            if (hasCached && now - cached!.Expires <= MaxStale)
            {
                Interlocked.Increment(ref _staleAnswers);
                return Serve(cached, id, now);
            }

            return BuildServerFailure(query);
        }

        if (!DnsMessage.IsResponseForQuery(query, response))
        {
            return BuildServerFailure(query);
        }

        if (DnsMessage.GetResponseCode(response) == 0 && _resolver.Epoch == epoch)
        {
            var ttl = DnsMessage.GetMinimumTtl(response);
            var storedAt = Clock.GetUtcNow();
            lock (_cacheGate)
            {
                _cache[key] = new CacheEntry(response.ToArray(), storedAt.AddSeconds(ttl), storedAt);
                PruneIfLarge(storedAt);
            }
        }
        else if (_resolver.Epoch != epoch)
        {
            Interlocked.Increment(ref _crossNetworkDrops);
            _log?.Invoke("DNS answer arrived after a network change; not cached for the new link.");
        }

        DnsMessage.SetId(response, id);
        return response;
    }

    /// <summary>
    /// The best answer the cache can give for a query, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Used where there is no slot to resolve the query properly. A stale entry is offered
    /// only because the alternative on that path is a failure, never in place of a lookup
    /// that could have been made.
    /// </remarks>
    private byte[]? AnswerFromCache(byte[] query, bool allowStale)
    {
        if (!DnsMessage.TryBuildCacheKey(query, out var key) || !_cache.TryGetValue(key, out var entry))
        {
            return null;
        }

        var now = Clock.GetUtcNow();
        if (entry.Expires > now)
        {
            Interlocked.Increment(ref _cacheHits);
            return Serve(entry, DnsMessage.GetId(query), now);
        }

        if (!allowStale || now - entry.Expires > MaxStale)
        {
            return null;
        }

        Interlocked.Increment(ref _staleAnswers);
        return Serve(entry, DnsMessage.GetId(query), now);
    }

    /// <summary>Copies a cached answer out for one client, aged and stamped with its ID.</summary>
    private static byte[] Serve(CacheEntry entry, ushort id, DateTimeOffset now)
    {
        entry.Touch(now);
        var reply = DnsMessage.AgeResponseTtls(entry.Response, now - entry.StoredAt);
        DnsMessage.SetId(reply, id);
        return reply;
    }

    private static byte[] BuildServerFailure(byte[] query)
    {
        var response = new byte[Math.Min(query.Length, 512)];
        query.AsSpan(0, response.Length).CopyTo(response);
        if (response.Length >= 4)
        {
            response[2] = (byte)(response[2] | 0x80); // QR = response
            response[3] = (byte)((response[3] & 0xF0) | 2); // RCODE = SERVFAIL
        }

        return response;
    }

    private void PruneIfLarge(DateTimeOffset now)
    {
        if (_cache.Count < MaxCacheEntries)
        {
            return;
        }

        foreach (var (key, entry) in _cache)
        {
            if (entry.Expires <= now)
            {
                _cache.TryRemove(key, out _);
            }
        }

        if (_cache.Count < MaxCacheEntries)
        {
            return;
        }

        foreach (var key in _cache
            .OrderBy(pair => pair.Value.LastAccess)
            .Take(Math.Max(1, _cache.Count - MaxCacheEntries + 1))
            .Select(pair => pair.Key)
            .ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }

    public void ClearCache()
    {
        lock (_cacheGate)
        {
            _cache.Clear();
        }
    }

    /// <summary>
    /// The machine moved: everything cached describes the resolver of a different link.
    /// </summary>
    /// <remarks>
    /// Split-horizon names, a captive portal's answers and a home router's own records
    /// are all correct where they were learned and wrong everywhere else, so the cache is
    /// emptied rather than aged out. The resolver's epoch moves with it, which is what
    /// keeps a lookup still in flight from putting the old link's answer straight back.
    /// </remarks>
    public void OnNetworkChanged()
    {
        ClearCache();
        _log?.Invoke("DNS cache cleared after a network change.");
    }

    private void Cleanup()
    {
        _udp4?.Dispose();
        _udp6?.Dispose();
        _tcp4?.Dispose();
        _tcp6?.Dispose();
        _udp4 = _udp6 = _tcp4 = _tcp6 = null;
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        await _stopping.CancelAsync().ConfigureAwait(false);
        Cleanup();

        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Workers are aborted along with their sockets; nothing to report.
        }

        try
        {
            await Task.WhenAll(_requests.Values).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Request handlers share the same cancellation token and closed sockets.
        }

        _stopping.Dispose();
        if (_requests.IsEmpty)
        {
            _capacity.Dispose();
        }
    }

    private sealed class CacheEntry(byte[] response, DateTimeOffset expires, DateTimeOffset storedAt)
    {
        private long _lastAccess = storedAt.UtcTicks;

        public byte[] Response { get; } = response;

        public DateTimeOffset Expires { get; } = expires;

        public DateTimeOffset StoredAt { get; } = storedAt;

        public DateTimeOffset LastAccess => new(Interlocked.Read(ref _lastAccess), TimeSpan.Zero);

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastAccess, now.UtcTicks);
    }
}
