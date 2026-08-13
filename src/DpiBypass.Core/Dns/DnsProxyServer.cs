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

    private readonly DohResolver _resolver;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _workers = [];
    private readonly Action<string>? _log;

    private Socket? _udp4;
    private Socket? _udp6;
    private Socket? _tcp4;
    private Socket? _tcp6;
    private long _served;
    private long _cacheHits;

    public DnsProxyServer(DohResolver resolver, Action<string>? log = null)
    {
        _resolver = resolver;
        _log = log;
    }

    public bool IsRunning { get; private set; }

    public long QueriesServed => Interlocked.Read(ref _served);

    public long CacheHits => Interlocked.Read(ref _cacheHits);

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

            // Do not let one slow upstream lookup stall the whole listener. The whole
            // body is guarded: a lookup cancelled mid-flight by shutdown would otherwise
            // fault a task nobody awaits, once per query still in flight.
            _ = Task.Run(async () =>
            {
                try
                {
                    var answer = await ResolveAsync(query, cancellationToken).ConfigureAwait(false);
                    if (answer is null)
                    {
                        return;
                    }

                    await socket.SendToAsync(answer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutting down, or the client vanished before we answered.
                }
            }, cancellationToken);
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

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var lengthPrefix = new byte[2];
                        if (!await ReadExactAsync(client, lengthPrefix, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        var length = BinaryPrimitives.ReadUInt16BigEndian(lengthPrefix);
                        if (length is 0 or > MaxUdpResponse)
                        {
                            return;
                        }

                        var query = new byte[length];
                        if (!await ReadExactAsync(client, query, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        var answer = await ResolveAsync(query, cancellationToken).ConfigureAwait(false);
                        if (answer is null)
                        {
                            return;
                        }

                        var framed = new byte[answer.Length + 2];
                        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)answer.Length);
                        answer.CopyTo(framed, 2);
                        await client.SendAsync(framed, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Malformed or abandoned TCP query; drop it.
                    }
                }
            }, cancellationToken);
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

    private async Task<byte[]?> ResolveAsync(byte[] query, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _served);

        if (!DnsMessage.TryReadQuestion(query, out var question))
        {
            return null;
        }

        var id = DnsMessage.GetId(query);
        var key = question.CacheKey;

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

        if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            Interlocked.Increment(ref _cacheHits);
            var reply = (byte[])cached.Response.Clone();
            DnsMessage.SetId(reply, id);
            return reply;
        }

        var response = await _resolver.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            // Serve a stale answer rather than nothing - a slightly old IP beats a
            // dead name lookup while the operator is throttling us.
            if (cached.Response is not null)
            {
                var stale = (byte[])cached.Response.Clone();
                DnsMessage.SetId(stale, id);
                return stale;
            }

            return BuildServerFailure(query);
        }

        if (DnsMessage.GetResponseCode(response) == 0)
        {
            var ttl = DnsMessage.GetMinimumTtl(response);
            _cache[key] = new CacheEntry(response, DateTimeOffset.UtcNow.AddSeconds(ttl));
            PruneIfLarge();
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
        if (_cache.Count < 4096)
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
    }

    public void ClearCache() => _cache.Clear();

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

        _stopping.Dispose();
    }

    private readonly record struct CacheEntry(byte[] Response, DateTimeOffset Expires);
}
