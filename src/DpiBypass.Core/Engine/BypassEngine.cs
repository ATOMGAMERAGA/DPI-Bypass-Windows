using System.Buffers;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Net;

namespace DpiBypass.Core.Engine;

/// <summary>
/// The packet path.
/// </summary>
/// <remarks>
/// <para>
/// Only the very first data packet of a TCP connection is ever touched, and only
/// when it is a TLS ClientHello or a plaintext HTTP request head. Everything else -
/// every ACK, every byte of payload after the handshake, all UDP, all ICMP - is
/// never diverted in the first place, because the kernel filter excludes it. That
/// is deliberate: ping, voice traffic and download throughput are untouched
/// because they never enter this process.
/// </para>
/// <para>
/// No per-connection state is kept either. A ClientHello is self-identifying, so
/// there is nothing to look up, nothing to expire and nothing to grow.
/// </para>
/// </remarks>
public sealed class BypassEngine : IDisposable
{
    /// <summary>
    /// The kernel side filter, matched on the payload so only a handshake is ever
    /// diverted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single most important line in the engine for how the machine
    /// feels. The obvious filter - every outbound packet with a payload on 443 or 80
    /// - is what the app used to install, and it does not mean "the first packet of
    /// each connection": it means <i>every</i> byte anybody uploads over HTTPS. Each
    /// one is taken out of the network stack, copied to user mode, parsed by managed
    /// code, and re-injected, on four worker threads. Uploading a file, sending video
    /// in a call, or pushing a commit all ran through that path, and when the workers
    /// could not keep up the driver's queue filled and started dropping packets -
    /// which is a connection that stalls and retransmits, i.e. exactly the "it broke
    /// my internet" the user sees.
    /// </para>
    /// <para>
    /// The conditions below are the same ones
    /// <see cref="TlsClientHello.IsClientHello"/> and
    /// <see cref="HttpRequestHead.IsRequest"/> apply in user mode, moved into the
    /// kernel where they cost nothing: a TLS record whose type is handshake and whose
    /// first handshake byte is ClientHello, or a payload starting with the first
    /// letter of an HTTP method. Application data records (0x17) - all of the bulk
    /// traffic - never leave the kernel now.
    /// </para>
    /// </remarks>
    private const string HandshakeFilter =
        "outbound and tcp and ("
        + "(tcp.DstPort == 443 and tcp.PayloadLength >= 6 and tcp.Payload[0] == 0x16 and tcp.Payload[5] == 0x01)"
        + " or (tcp.DstPort == 80 and tcp.PayloadLength >= 5 and ("
        + "tcp.Payload[0] == 0x47 or tcp.Payload[0] == 0x50 or tcp.Payload[0] == 0x48 or tcp.Payload[0] == 0x44"
        + " or tcp.Payload[0] == 0x4F or tcp.Payload[0] == 0x54 or tcp.Payload[0] == 0x43))"
        + ")";

    /// <summary>Used if the driver rejects payload indexing in a TCP filter.</summary>
    private const string TcpFilter =
        "outbound and ip and tcp and (tcp.DstPort == 443 or tcp.DstPort == 80) and tcp.PayloadLength > 0";

    private const string TcpFilterV6 =
        "outbound and tcp and (tcp.DstPort == 443 or tcp.DstPort == 80) and tcp.PayloadLength > 0";

    /// <summary>
    /// Only QUIC long header packets with an Initial type reach user mode. Filtering
    /// on the first payload byte in the kernel means an established QUIC session (a
    /// short header, first byte 0x40-0x7F) is never diverted at all, so watching a
    /// video over QUIC costs nothing even when suppression is switched on.
    /// </summary>
    private const string QuicFilter =
        "outbound and udp and udp.DstPort == 443 and udp.PayloadLength > 32 "
        + "and udp.Payload[0] >= 0xC0 and udp.Payload[0] <= 0xCF";

    /// <summary>Used if the driver rejects payload indexing in a filter.</summary>
    private const string QuicFilterFallback = "outbound and udp and udp.DstPort == 443 and udp.PayloadLength > 32";

    private const int MaxPacket = 65535;

    private readonly TargetMatcher _matcher;
    private readonly ProcessPortMap? _portMap;
    private readonly Action<string>? _log;
    private readonly List<Thread> _workers = [];

    /// <summary>Guards opening and closing the QUIC handle against the toggle.</summary>
    private readonly Lock _quicGate = new();

    private CancellationTokenSource _stopping = new();
    private WinDivertHandle? _tcpHandle;
    private WinDivertHandle? _quicHandle;
    private volatile bool _blockQuic = true;
    private volatile BypassStrategy _strategy = StrategyLibrary.Default;

    public BypassEngine(TargetMatcher matcher, ProcessPortMap? portMap = null, Action<string>? log = null)
    {
        _matcher = matcher;
        _portMap = portMap;
        _log = log;
    }

    public EngineStatistics Stats { get; } = new();

    public bool IsRunning => _tcpHandle?.IsOpen == true;

    /// <summary>The recipe in force. Swapping it is atomic and takes effect on the next connection.</summary>
    public BypassStrategy Strategy
    {
        get => _strategy;
        set => _strategy = value;
    }

    /// <summary>
    /// Drop new QUIC handshakes for protected processes so they fall back to TCP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// QUIC hides its ClientHello behind handshake encryption, so the desync tricks
    /// here cannot reach it. Refusing the handshake makes browsers retry over TCP
    /// within a few hundred milliseconds, where the bypass does work. Only Initial
    /// packets are dropped, so QUIC sessions already running are left alone.
    /// </para>
    /// <para>
    /// Switching this off closes the QUIC handle rather than leaving it open and
    /// forwarding everything it catches. A diversion handle is not free just because
    /// the loop behind it does nothing: the driver still lifts each matching packet
    /// out of the stack, and it still has to come back through user mode to be put
    /// back. With the feature off there is no reason to pay for any of that.
    /// </para>
    /// </remarks>
    public bool BlockQuicHandshakes
    {
        get => _blockQuic;
        set
        {
            if (_blockQuic == value)
            {
                return;
            }

            _blockQuic = value;
            SyncQuicHandle();
        }
    }

    /// <summary>Raised the first time each hostname is rewritten. Used for the activity list.</summary>
    public event Action<string, string>? HostRewritten;

    public void Start(int workerCount = 0)
    {
        if (IsRunning)
        {
            return;
        }

        if (_stopping.IsCancellationRequested)
        {
            _stopping.Dispose();
            _stopping = new CancellationTokenSource();
        }

        // WinDivert rejects an IPv4-only filter clause on machines without IPv4, and
        // some builds are pickier than others about the "ip and" prefix, so fall back.
        _tcpHandle = OpenWithFallback();

        // Deeper queues make bursts (a browser opening thirty connections at once)
        // safe without us having to keep up in real time.
        _tcpHandle.SetParam(WinDivertParam.QueueLength, 8192);
        _tcpHandle.SetParam(WinDivertParam.QueueTime, 2000);
        _tcpHandle.SetParam(WinDivertParam.QueueSize, 16 * 1024 * 1024);

        var tcp = _tcpHandle;
        var tcpGroup = new WorkerGroup(tcp);
        var threads = workerCount > 0 ? workerCount : Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        for (var i = 0; i < threads; i++)
        {
            StartWorker($"DpiBypass.Tcp{i}", () => TcpLoop(tcp), tcpGroup);
        }

        SyncQuicHandle();

        Stats.StartedAt = DateTimeOffset.UtcNow;
        _log?.Invoke($"Engine running with {threads} worker thread(s); strategy '{_strategy.Id}'.");
    }

    /// <summary>
    /// Brings the QUIC handle into line with <see cref="BlockQuicHandshakes"/>.
    /// </summary>
    /// <remarks>
    /// Called from start-up and from the toggle, which is the UI thread, so it is
    /// guarded: two callers arriving at once must not each open a handle, and the one
    /// that closes must not leave the worker reading a handle the other just replaced.
    /// </remarks>
    private void SyncQuicHandle()
    {
        lock (_quicGate)
        {
            // Nothing to sync until the engine is up; Start calls this itself.
            if (_tcpHandle is not { IsOpen: true })
            {
                return;
            }

            if (_blockQuic)
            {
                if (_quicHandle is { IsOpen: true })
                {
                    return;
                }

                // The local, not the field: the worker outlives this method and the
                // field is cleared the moment the toggle goes the other way.
                var opened = TryOpenQuicHandle();
                _quicHandle = opened;

                if (opened is not null)
                {
                    StartWorker("DpiBypass.Quic", () => QuicLoop(opened), new WorkerGroup(opened));
                }

                return;
            }

            var handle = _quicHandle;
            _quicHandle = null;

            if (handle is null)
            {
                return;
            }

            // Shut down first: the worker is parked inside Receive and comes out of it
            // when the handle stops delivering, at which point it disposes the handle
            // through its own group.
            handle.Shutdown();
            _log?.Invoke("QUIC suppression off; the driver is no longer diverting handshakes.");
        }
    }

    private WinDivertHandle? TryOpenQuicHandle()
    {
        foreach (var filter in new[] { QuicFilter, QuicFilterFallback })
        {
            WinDivertHandle? handle = null;

            try
            {
                handle = WinDivertHandle.Open(filter, WinDivertLayer.Network, 1001, WinDivertFlags.None);
                handle.SetParam(WinDivertParam.QueueLength, 2048);
                return handle;
            }
            catch (WinDivertException ex) when (ex.NativeErrorCode == 87)
            {
                // Opened but rejected on the parameter: abandoning it here would leave
                // the driver diverting QUIC into a queue nobody reads.
                handle?.Dispose();
                _log?.Invoke("QUIC filter rejected by the driver; trying a simpler one.");
            }
            catch (WinDivertException ex)
            {
                handle?.Dispose();
                _log?.Invoke($"QUIC suppression unavailable: {ex.Message}");
                return null;
            }
        }

        _log?.Invoke("QUIC suppression unavailable: no usable filter.");
        return null;
    }

    /// <summary>The filters to try, best first. Public so the tests can pin the order.</summary>
    internal static IReadOnlyList<string> TcpFilterLadder => [HandshakeFilter, TcpFilter, TcpFilterV6];

    /// <summary>
    /// Opens the TCP handle on the narrowest filter this driver will accept.
    /// </summary>
    /// <remarks>
    /// Each candidate is compiled before it is opened. Compiling asks the same parser
    /// the driver uses but touches nothing, so a filter this build of WinDivert does
    /// not understand - payload indexing is the one in question - costs a rejected
    /// parse rather than an opened handle that has to be closed again. An opened
    /// handle is not free to abandon: between the open and the close the driver is
    /// already pulling matching packets out of the stack for a queue nobody reads.
    /// </remarks>
    private WinDivertHandle OpenWithFallback()
    {
        WinDivertException? last = null;

        foreach (var filter in TcpFilterLadder)
        {
            if (!Compiles(filter))
            {
                _log?.Invoke("Filter rejected by the driver's parser; trying a simpler one.");
                continue;
            }

            try
            {
                var handle = WinDivertHandle.Open(filter, WinDivertLayer.Network, 1000, WinDivertFlags.None);
                _log?.Invoke(filter == HandshakeFilter
                    ? "Kernel filter: handshakes only."
                    : "Kernel filter: all HTTP/HTTPS payload packets (the driver would not take the narrow one).");
                return handle;
            }
            catch (WinDivertException ex) when (ex.NativeErrorCode == 87)
            {
                // Compiled but refused. Keep going; the next one is simpler.
                last = ex;
                _log?.Invoke("Primary filter rejected; retrying with a simpler one.");
            }
        }

        // Every candidate was refused. The last real error is the useful one, and if
        // the parser rejected all of them the broadest filter still has to be tried
        // so the failure the caller reports comes from the driver rather than from us.
        throw last ?? new WinDivertException(87, "No packet filter was accepted by the driver.");
    }

    /// <summary>Whether the driver's own parser accepts a filter, without opening it.</summary>
    private static bool Compiles(string filter)
    {
        try
        {
            return WinDivertHandle.TryCompileFilter(filter, WinDivertLayer.Network, out _);
        }
        catch (Exception)
        {
            // The helper is missing from this build of the DLL. Let the open decide.
            return true;
        }
    }

    /// <summary>
    /// One divert handle and the count of threads still reading from it.
    /// </summary>
    /// <remarks>
    /// A diversion handle with nobody receiving from it is worse than no handle at
    /// all: the driver still takes the matched packets out of the network stack,
    /// queues them, and drops them once the queue fills. An unexpectedly dead worker
    /// would therefore black-hole every new connection the filter matches - all HTTP
    /// and HTTPS, or all QUIC - while the UI still reported protection as running.
    /// Counting readers per handle lets the last one out close it, which downgrades
    /// that into traffic flowing unprotected: worse protection, working internet.
    /// </remarks>
    private sealed class WorkerGroup(WinDivertHandle handle)
    {
        public WinDivertHandle Handle { get; } = handle;

        public int Live;
    }

    /// <summary>
    /// Starts one reader. Safe to call while the engine is running: the QUIC toggle
    /// does exactly that.
    /// </summary>
    private void StartWorker(string name, Action body, WorkerGroup group)
    {
        Interlocked.Increment(ref group.Live);

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Stats.AddError();
                _log?.Invoke($"{name} stopped: {ex.Message}");
            }
            finally
            {
                if (Interlocked.Decrement(ref group.Live) == 0 && !_stopping.IsCancellationRequested)
                {
                    _log?.Invoke($"{name} was the last reader on its filter; releasing it so traffic flows again.");

                    try
                    {
                        group.Handle.Dispose();
                    }
                    catch (Exception)
                    {
                        // Already gone; the point was only to stop diverting.
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = name,
            // Above normal keeps the desync latency down without starving anything.
            Priority = ThreadPriority.AboveNormal,
        };

        lock (_quicGate)
        {
            // Toggling QUIC suppression on and off adds a worker each time, so the
            // ones that have already finished are dropped rather than joined again.
            _workers.RemoveAll(worker => !worker.IsAlive);
            _workers.Add(thread);
        }

        thread.Start();
    }

    private void TcpLoop(WinDivertHandle handle)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxPacket);
        var scratch = ArrayPool<byte>.Shared.Rent(MaxPacket);

        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var address = default(WinDivertAddress);
                if (!handle.Receive(buffer.AsSpan(0, MaxPacket), out var length, ref address))
                {
                    return;
                }

                if (length == 0)
                {
                    continue;
                }

                var packet = buffer.AsSpan(0, length);

                try
                {
                    if (!TryRewrite(handle, packet, scratch, ref address))
                    {
                        handle.Send(packet, ref address);
                    }
                }
                catch (Exception ex)
                {
                    Stats.AddError();
                    _log?.Invoke($"Packet rewrite failed, forwarding unchanged: {ex.Message}");
                    handle.Send(packet, ref address);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Returns true when the packet was replaced (and must not be forwarded as-is).</summary>
    private bool TryRewrite(WinDivertHandle handle, Span<byte> packet, byte[] scratch, ref WinDivertAddress address)
    {
        // Packets we injected come back around; forward them untouched or we loop forever.
        if (address.Impostor)
        {
            return false;
        }

        var parsed = TcpIpPacket.Parse(packet);
        if (!parsed.IsValid || parsed.PayloadLength <= 0)
        {
            return false;
        }

        var payload = packet.Slice(parsed.PayloadOffset, parsed.PayloadLength);
        var isTls = parsed.DestinationPort == 443 && TlsClientHello.IsClientHello(payload);
        var isHttp = parsed.DestinationPort == 80 && HttpRequestHead.IsRequest(payload);

        if (!isTls && !isHttp)
        {
            // Mid-stream data. Not interesting, and not counted, so the numbers on
            // the status page mean "handshakes seen" rather than "packets seen".
            return false;
        }

        Stats.AddInspected();

        string? hostName = null;
        if (isTls)
        {
            if (TlsClientHello.TryParse(payload, out var hello))
            {
                hostName = hello.ServerName;
            }
        }
        else if (HttpRequestHead.TryParse(payload, out var head))
        {
            hostName = head.Host;
        }

        var imagePath = _portMap?.GetImagePath(parsed.SourcePort);

        if (!_matcher.ShouldProtect(hostName, imagePath))
        {
            Stats.AddPassedThrough();
            return false;
        }

        var strategy = _strategy;
        if (strategy.IsPassthrough)
        {
            Stats.AddPassedThrough();
            return false;
        }

        var plan = DesyncPlanner.Plan(strategy, payload, isTls, hostName);
        if (plan.IsNoOp)
        {
            Stats.AddPassedThrough();
            return false;
        }

        var baseSequence = parsed.SequenceNumber;
        var headerLength = parsed.PayloadOffset;
        var sent = 0;
        var decoys = 0;

        foreach (var segment in plan.Segments)
        {
            var segmentPayload = segment.FakePayload is not null
                ? segment.FakePayload.AsSpan()
                : plan.Payload.AsSpan(segment.Offset, segment.Length);

            if (segmentPayload.Length == 0)
            {
                continue;
            }

            if (headerLength + segmentPayload.Length > MaxPacket)
            {
                // Should not happen for a ClientHello, but never build past the buffer.
                continue;
            }

            packet[..headerLength].CopyTo(scratch);
            segmentPayload.CopyTo(scratch.AsSpan(headerLength));

            var total = headerLength + segmentPayload.Length;
            var view = scratch.AsSpan(0, total);

            TcpIpPacket.SetTotalLength(view, parsed.IsIPv6, total);
            TcpIpPacket.SetIdentification(view, parsed.IsIPv6, (ushort)Random.Shared.Next(1, ushort.MaxValue));

            var sequence = unchecked(baseSequence + segment.SequenceOffset + (uint)segment.SequenceSkew);
            TcpIpPacket.SetSequenceNumber(view, parsed.TcpHeaderOffset, sequence);

            var flags = parsed.Flags;
            if (segment.Urgent)
            {
                flags |= TcpFlags.Urg;
                TcpIpPacket.SetUrgentPointer(view, parsed.TcpHeaderOffset, (ushort)segmentPayload.Length);
            }
            else
            {
                flags &= ~TcpFlags.Urg;
                TcpIpPacket.SetUrgentPointer(view, parsed.TcpHeaderOffset, 0);
            }

            TcpIpPacket.SetFlags(view, parsed.TcpHeaderOffset, flags);

            if (segment.TimeToLive is { } ttl)
            {
                TcpIpPacket.SetTimeToLive(view, parsed.IsIPv6, ttl);
            }
            else
            {
                TcpIpPacket.SetTimeToLive(view, parsed.IsIPv6, parsed.TimeToLive);
            }

            // Zero the checksum fields so the helper recomputes rather than adjusts.
            TcpIpPacket.SetChecksum(view, parsed.TcpHeaderOffset, 0);
            var outgoing = address;
            WinDivertHandle.CalculateChecksums(view, ref outgoing);

            if (segment.CorruptChecksum)
            {
                // Flip the checksum after the helper has done its work: the inspector
                // usually accepts the segment, the server's stack always discards it.
                TcpIpPacket.SetChecksum(view, parsed.TcpHeaderOffset, 0xBAD1);
            }

            if (handle.Send(view, ref outgoing))
            {
                sent++;
                if (segment.IsDecoy)
                {
                    decoys++;
                }
            }
        }

        if (sent == 0)
        {
            // Nothing made it out - let the original through so the user is never
            // worse off than with the app uninstalled.
            Stats.AddError();
            return false;
        }

        Stats.AddRewritten();
        Stats.AddSegments(sent);
        Stats.AddDecoys(decoys);

        if (hostName is not null && Stats.LastHost != hostName)
        {
            Stats.LastHost = hostName;
            HostRewritten?.Invoke(hostName, strategy.Id);
        }

        return true;
    }

    private void QuicLoop(WinDivertHandle handle)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxPacket);

        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var address = default(WinDivertAddress);
                if (!handle.Receive(buffer.AsSpan(0, MaxPacket), out var length, ref address))
                {
                    return;
                }

                if (length == 0)
                {
                    continue;
                }

                var packet = buffer.AsSpan(0, length);

                if (address.Impostor || !ShouldDropQuic(packet))
                {
                    handle.Send(packet, ref address);
                    continue;
                }

                Stats.AddQuicBlocked();
                // Dropped by simply not forwarding it.
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool ShouldDropQuic(ReadOnlySpan<byte> packet)
    {
        if (!BlockQuicHandshakes)
        {
            return false;
        }

        var version = packet[0] >> 4;
        var ipHeaderLength = version == 4 ? (packet[0] & 0x0F) * 4 : 40;
        if (version == 4)
        {
            if (packet.Length < ipHeaderLength + 8 || packet[9] != TcpIpPacket.ProtocolUdp)
            {
                return false;
            }
        }
        else if (version == 6)
        {
            if (packet.Length < 48 || packet[6] != TcpIpPacket.ProtocolUdp)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        var udpPayloadOffset = ipHeaderLength + 8;
        if (packet.Length <= udpPayloadOffset)
        {
            return false;
        }

        // QUIC long header, Initial packet: 0b11xx_xxxx with type bits 00.
        var first = packet[udpPayloadOffset];
        var isInitial = (first & 0xF0) == 0xC0;
        if (!isInitial)
        {
            return false;
        }

        var sourcePort = (ushort)((packet[ipHeaderLength] << 8) | packet[ipHeaderLength + 1]);
        var imagePath = _portMap?.GetImagePath(sourcePort);

        // The hostname is inside the encrypted handshake, so the decision can only be
        // made on the owning process. In system-wide mode that means leaving QUIC
        // alone - dropping every handshake on the box would be a bad trade.
        return _matcher.Scope switch
        {
            ProtectionScope.DiscordOnly => TargetMatcher.IsDiscord(imagePath),
            ProtectionScope.DiscordAndBrowsers => TargetMatcher.IsDiscord(imagePath) || TargetMatcher.IsBrowser(imagePath),
            _ => false,
        };
    }

    public void Stop()
    {
        if (!IsRunning && _quicHandle is null)
        {
            return;
        }

        _stopping.Cancel();
        _tcpHandle?.Shutdown();

        Thread[] workers;
        lock (_quicGate)
        {
            // Under the lock, because the QUIC toggle owns this handle too and is
            // driven from the UI thread.
            _quicHandle?.Shutdown();

            // Copied out: the joins below take seconds that the lock has no business
            // holding.
            workers = [.. _workers];
            _workers.Clear();
        }

        foreach (var worker in workers)
        {
            worker.Join(TimeSpan.FromSeconds(2));
        }

        _tcpHandle?.Dispose();
        _tcpHandle = null;
        _quicHandle?.Dispose();
        _quicHandle = null;
        Stats.StartedAt = null;
        _log?.Invoke("Engine stopped.");
    }

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
    }
}
