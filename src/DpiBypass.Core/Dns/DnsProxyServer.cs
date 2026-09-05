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
    private const int MaxConcurrentQueries = 128;
    private static readonly TimeSpan MaxStale = TimeSpan.FromMinutes(5);

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

    public DnsProxyServer(DohResolver resolver, Action<string>? log = null)
    {
        _resolver = resolver;
        _log = log;
    }

    public bool IsRunning { get; private set; }

    public long QueriesServed => Interlocked.Read(ref _served);

    public long CacheHits => Interlocked.Read(ref _cacheHits);

    /// <summary>Answers sent back with TC set because they did not fit the client's buffer.</summary>
    public long TruncatedAnswers => Interlocked.Read(ref _truncated);

    /// <summary>TCP answers the client stopped reading half way through.</summary>
    public long AbandonedTcpAnswers => Interlocked.Read(ref _partialSends);

    /// <summary>Answers that came back after a network change and were not cached.</summary>
    public long CrossNetworkDrops => Interlocked.Read(ref _crossNetworkDrops);

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
                    var failure = BuildServerFailure(query);
                    await socket.SendToAsync(failure, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
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
        if (hasCached && cached!.Expires > DateTimeOffset.UtcNow)
        {
            Interlocked.Increment(ref _cacheHits);
            cached!.Touch();
            var reply = DnsMessage.AgeResponseTtls(cached.Response, DateTimeOffset.UtcNow - cached.StoredAt);
            DnsMessage.SetId(reply, id);
            return reply;
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
            if (hasCached && DateTimeOffset.UtcNow - cached!.Expires <= MaxStale)
            {
                cached!.Touch();
                var stale = DnsMessage.AgeResponseTtls(cached.Response, DateTimeOffset.UtcNow - cached.StoredAt);
                DnsMessage.SetId(stale, id);
                return stale;
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
            var now = DateTimeOffset.UtcNow;
            lock (_cacheGate)
            {
                _cache[key] = new CacheEntry(response.ToArray(), now.AddSeconds(ttl), now);
                PruneIfLarge();
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

    private void PruneIfLarge()
    {
        if (_cache.Count < MaxCacheEntries)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
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

        public void Touch() => Interlocked.Exchange(ref _lastAccess, DateTimeOffset.UtcNow.UtcTicks);
    }
}
