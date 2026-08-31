using System.Net;
using System.Runtime.InteropServices;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Network;

/// <summary>One transport flow Windows told us about, while it was alive.</summary>
/// <remarks>
/// Held in memory for the length of one discovery pass and never written anywhere. The
/// addresses and ports here identify what a user is playing and who with, which is not
/// something a latency feature has any business persisting.
/// </remarks>
public sealed record ObservedFlow
{
    public required uint ProcessId { get; init; }

    public required IPEndPoint Local { get; init; }

    public required IPEndPoint Remote { get; init; }

    public required LatencyProtocol Protocol { get; init; }

    public required DateTimeOffset EstablishedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    public bool IsOpen => DeletedAt is null;

    public TimeSpan LifetimeAt(DateTimeOffset now) => (DeletedAt ?? now) - EstablishedAt;

    /// <summary>The five-tuple, for de-duplication inside one pass.</summary>
    public string Key => $"{ProcessId}|{Protocol}|{Local}|{Remote}";
}

/// <summary>Watches new transport flows as they are created, and never touches them.</summary>
public interface IProcessFlowObserver : IAsyncDisposable
{
    /// <summary>Whether the observer is currently receiving events.</summary>
    bool IsRunning { get; }

    /// <summary>Why the observer could not start, when it could not.</summary>
    string? Unavailable { get; }

    /// <summary>When the observer opened, so callers can say what it cannot have seen.</summary>
    DateTimeOffset? StartedAt { get; }

    Task<bool> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Every flow seen since the observer started, open or closed.</summary>
    IReadOnlyList<ObservedFlow> Flows();

    Task StopAsync();
}

/// <summary>
/// Discovers a running game's real endpoint, including UDP, from WinDivert's FLOW layer.
/// </summary>
/// <remarks>
/// <para>
/// Windows' UDP table reports the local address and port of a socket and nothing else,
/// because a UDP socket has no connection to describe. That is why an earlier build could
/// only ever find TCP endpoints and had to tell the user to type the server address in by
/// hand for anything that plays over UDP - which is most things.
/// </para>
/// <para>
/// WinDivert's FLOW layer answers exactly this question. It reports
/// <c>WINDIVERT_EVENT_FLOW_ESTABLISHED</c> and <c>WINDIVERT_EVENT_FLOW_DELETED</c> with
/// the process id and the full five-tuple, for TCP and UDP alike. The handle is opened
/// with <c>SNIFF | RECV_ONLY</c>, which the documentation requires for this layer and
/// which means the observer cannot block, modify or inject anything: it is a read of
/// events the stack was going to raise anyway, and the packet path is untouched.
/// </para>
/// <para>
/// The documented limitation is that the layer "cannot capture flow events that occurred
/// before the handle was opened", so a game already connected is invisible until it
/// reconnects. That is surfaced to the user rather than worked around, because the
/// alternative - guessing from a stale table - is how a measurement ends up pointing at
/// the wrong server.
/// </para>
/// </remarks>
public sealed class WinDivertFlowObserver : IProcessFlowObserver
{
    /// <summary>Well below the engine's filters, and on a different layer besides.</summary>
    private const short FlowPriority = 100;

    private const byte TcpProtocol = 6;
    private const byte UdpProtocol = 17;

    /// <summary>A pass cannot grow without bound, however chatty the machine is.</summary>
    private const int MaximumFlows = 4096;

    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ObservedFlow> _flows = [];

    private WinDivertHandle? _handle;
    private Thread? _pump;
    private volatile bool _stopping;

    public WinDivertFlowObserver(Action<string>? log = null) => _log = log;

    public bool IsRunning => _handle is { IsOpen: true } && !_stopping;

    public string? Unavailable { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsRunning)
        {
            return Task.FromResult(true);
        }

        if (!OperatingSystem.IsWindows())
        {
            Unavailable = "Akış gözlemi yalnız Windows üzerinde çalışır.";
            return Task.FromResult(false);
        }

        try
        {
            // "true" rather than a protocol filter: the FLOW layer's field set is smaller
            // than the network layer's, and a filter the driver rejects would take the
            // whole feature out. Flow events are rare enough to sort in managed code.
            _handle = WinDivertHandle.Open("true", WinDivertLayer.Flow, FlowPriority, WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
        }
        catch (WinDivertException ex)
        {
            Unavailable = $"Akış gözlemi başlatılamadı: {ex.Message}";
            _log?.Invoke($"latency.flow: {Unavailable}");
            return Task.FromResult(false);
        }
        catch (DllNotFoundException)
        {
            Unavailable = "WinDivert sürücüsü bulunamadı; UDP uç noktası keşfi kullanılamıyor.";
            return Task.FromResult(false);
        }

        _stopping = false;
        StartedAt = DateTimeOffset.UtcNow;
        Unavailable = null;

        lock (_gate)
        {
            _flows.Clear();
        }

        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "dpibypass-flow-observer",
        };

        _pump.Start();
        _log?.Invoke("latency.flow.started: yeni bağlantılar dinleniyor (yalnız gözlem).");
        return Task.FromResult(true);
    }

    public IReadOnlyList<ObservedFlow> Flows()
    {
        lock (_gate)
        {
            return [.. _flows.Values];
        }
    }

    public Task StopAsync()
    {
        _stopping = true;

        var handle = _handle;
        _handle = null;

        if (handle is not null)
        {
            handle.Shutdown();
            handle.Dispose();
        }

        var pump = _pump;
        _pump = null;
        pump?.Join(TimeSpan.FromSeconds(2));

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _flows.Clear();
        }
    }

    private void Pump()
    {
        // A flow event carries no payload, so the buffer only exists because Recv wants
        // one. The address is the whole message.
        var buffer = new byte[1];

        while (!_stopping && _handle is { IsOpen: true } handle)
        {
            var address = default(WinDivertAddress);

            try
            {
                if (!handle.Receive(buffer, out _, ref address))
                {
                    return;
                }
            }
            catch (WinDivertException ex)
            {
                _log?.Invoke($"latency.flow: dinleme durdu ({ex.Message}).");
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Record(address);
        }
    }

    private void Record(in WinDivertAddress address)
    {
        if (address.Layer != WinDivertLayer.Flow)
        {
            return;
        }

        var protocol = address.Protocol switch
        {
            TcpProtocol => LatencyProtocol.Tcp,
            UdpProtocol => LatencyProtocol.Udp,
            _ => (LatencyProtocol?)null,
        };

        if (protocol is not { } transport)
        {
            return;
        }

        var local = ToEndPoint(address.LocalAddr0, address.LocalAddr1, address.LocalAddr2, address.LocalAddr3, address.LocalPort, address.IPv6);
        var remote = ToEndPoint(address.RemoteAddr0, address.RemoteAddr1, address.RemoteAddr2, address.RemoteAddr3, address.RemotePort, address.IPv6);

        if (local is null || remote is null || IsUninteresting(remote.Address))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var key = $"{address.ProcessId}|{transport}|{local}|{remote}";

        lock (_gate)
        {
            if (address.Event == WinDivertEvent.FlowDeleted)
            {
                if (_flows.TryGetValue(key, out var existing))
                {
                    _flows[key] = existing with { DeletedAt = now };
                }

                return;
            }

            if (address.Event != WinDivertEvent.FlowEstablished || _flows.Count >= MaximumFlows)
            {
                return;
            }

            _flows[key] = new ObservedFlow
            {
                ProcessId = address.ProcessId,
                Local = local,
                Remote = remote,
                Protocol = transport,
                EstablishedAt = now,
            };
        }
    }

    /// <summary>
    /// Turns WinDivert's four address words into an endpoint.
    /// </summary>
    /// <remarks>
    /// The layer always stores addresses in IPv6 form, with IPv4 appearing as an
    /// IPv4-mapped address, so the family comes from the address flag rather than from
    /// guessing at the contents.
    /// </remarks>
    private static IPEndPoint? ToEndPoint(uint word0, uint word1, uint word2, uint word3, ushort port, bool ipv6)
    {
        try
        {
            if (!ipv6)
            {
                // IPv4 sits in the first word, in host order on this architecture.
                return new IPEndPoint(new IPAddress(BitConverter.GetBytes(word0)), port);
            }

            Span<byte> bytes = stackalloc byte[16];
            MemoryMarshal.Write(bytes[..4], in word0);
            MemoryMarshal.Write(bytes.Slice(4, 4), in word1);
            MemoryMarshal.Write(bytes.Slice(8, 4), in word2);
            MemoryMarshal.Write(bytes.Slice(12, 4), in word3);

            // WinDivert stores the words in reverse order for IPv6.
            bytes.Reverse();
            return new IPEndPoint(new IPAddress(bytes), port);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsUninteresting(IPAddress address) =>
        IPAddress.IsLoopback(address)
        || address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.IPv6Any)
        || address.IsIPv6LinkLocal
        || IsPrivateV4(address);

    private static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> octets = stackalloc byte[4];
        if (!address.TryWriteBytes(octets, out _))
        {
            return false;
        }

        return octets[0] switch
        {
            10 => true,
            127 => true,
            169 when octets[1] == 254 => true,
            172 when octets[1] is >= 16 and <= 31 => true,
            192 when octets[1] == 168 => true,
            _ => false,
        };
    }
}
