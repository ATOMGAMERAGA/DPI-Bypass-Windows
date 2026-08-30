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
    /// The preferred kernel filter. It mirrors the user-mode handshake checks so
    /// established HTTP/HTTPS payload does not cross the user-mode packet path.
    /// </summary>
    private const string HandshakeFilter =
        "outbound and tcp and ("
        + "(tcp.DstPort == 443 and tcp.PayloadLength >= 6 and tcp.Payload[0] == 0x16 and tcp.Payload[5] == 0x01)"
        + " or (tcp.DstPort == 80 and tcp.PayloadLength >= 5 and ("
        + "tcp.Payload[0] == 0x47 or tcp.Payload[0] == 0x50 or tcp.Payload[0] == 0x48 or tcp.Payload[0] == 0x44"
        + " or tcp.Payload[0] == 0x4F or tcp.Payload[0] == 0x54 or tcp.Payload[0] == 0x43))"
        + ")";

    /// <summary>Fallbacks for WinDivert builds that reject payload indexing.</summary>
    private const string TcpFilter =
        "outbound and ip and tcp and (tcp.DstPort == 443 or tcp.DstPort == 80) and tcp.PayloadLength > 0";

    private const string TcpFilterV6 =
        "outbound and tcp and (tcp.DstPort == 443 or tcp.DstPort == 80) and tcp.PayloadLength > 0";

    internal static IReadOnlyList<string> TcpFilterLadder => [HandshakeFilter, TcpFilter, TcpFilterV6];

    /// <summary>
    /// Only QUIC long header packets with an Initial type reach user mode. Filtering
    /// on the first payload byte in the kernel means an established QUIC session (a
    /// short header, first byte 0x40-0x7F) is never diverted at all, so watching a
    /// video over QUIC costs nothing even when suppression is switched on.
    /// </summary>
    private const string QuicFilter =
        "outbound and udp and udp.DstPort == 443 and udp.PayloadLength > 32 "
        + "and ((udp.Payload[0] >= 0xC0 and udp.Payload[0] <= 0xCF) "
        + "or (udp.Payload[0] >= 0xD0 and udp.Payload[0] <= 0xDF))";

    /// <summary>Used if the driver rejects payload indexing in a filter.</summary>
    private const string QuicFilterFallback = "outbound and udp and udp.DstPort == 443 and udp.PayloadLength > 32";

    /// <summary>
    /// Exposed so the fast-path tests can pin how narrow the UDP side stays.
    /// </summary>
    /// <remarks>
    /// Ordinary UDP is what games and voice use. Everything here is destination port 443
    /// and, on the preferred filter, a QUIC long header Initial byte - so an established
    /// QUIC session, and every game protocol, is never copied to user mode at all.
    /// </remarks>
    internal static IReadOnlyList<string> QuicFilterLadder => [QuicFilter, QuicFilterFallback];

    private const int MaxPacket = 65535;

    private readonly TargetMatcher _matcher;
    private readonly ProcessPortMap? _portMap;
    private readonly Action<string>? _log;
    private readonly List<Thread> _workers = [];

    private CancellationTokenSource _stopping = new();
    private WinDivertHandle? _tcpHandle;
    private WinDivertHandle? _quicHandle;
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
    /// QUIC hides its ClientHello behind handshake encryption, so the desync tricks
    /// here cannot reach it. Refusing the handshake makes browsers retry over TCP
    /// within a few hundred milliseconds, where the bypass does work. Only Initial
    /// packets are dropped, so QUIC sessions already running are left alone.
    /// </remarks>
    public bool BlockQuicHandshakes { get; set; } = true;

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

        // Opened regardless of the current setting so the toggle takes effect
        // immediately instead of waiting for a restart. With the narrow filter above
        // this costs a handful of packets per new QUIC connection.
        _quicHandle = TryOpenQuicHandle();

        var tcpGroup = new WorkerGroup(_tcpHandle);
        var threads = workerCount > 0 ? workerCount : Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        for (var i = 0; i < threads; i++)
        {
            StartWorker($"DpiBypass.Tcp{i}", () => TcpLoop(_tcpHandle), tcpGroup);
        }

        if (_quicHandle is not null)
        {
            StartWorker("DpiBypass.Quic", () => QuicLoop(_quicHandle), new WorkerGroup(_quicHandle));
        }

        Stats.StartedAt = DateTimeOffset.UtcNow;
        _log?.Invoke($"Engine running with {threads} worker thread(s); strategy '{_strategy.Id}'.");
    }

    private WinDivertHandle? TryOpenQuicHandle()
    {
        foreach (var filter in QuicFilterLadder)
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

    private WinDivertHandle OpenWithFallback()
    {
        WinDivertException? last = null;

        foreach (var filter in TcpFilterLadder)
        {
            if (!Compiles(filter))
            {
                _log?.Invoke("Filter rejected by the WinDivert parser; trying a simpler fallback.");
                continue;
            }

            try
            {
                var handle = WinDivertHandle.Open(filter, WinDivertLayer.Network, 1000, WinDivertFlags.None);
                _log?.Invoke(filter == HandshakeFilter
                    ? "Kernel filter: handshakes only."
                    : "Kernel filter fallback: all HTTP/HTTPS payload packets.");
                return handle;
            }
            catch (WinDivertException ex) when (ex.NativeErrorCode == 87)
            {
                last = ex;
                _log?.Invoke("Packet filter rejected; trying a simpler fallback.");
            }
        }

        throw last ?? new WinDivertException(87, "No TCP packet filter was accepted by WinDivert.");
    }

    private static bool Compiles(string filter)
    {
        try
        {
            return WinDivertHandle.TryCompileFilter(filter, WinDivertLayer.Network, out _);
        }
        catch (Exception)
        {
            // Older or incomplete DLL payloads may not expose the helper. Let Open
            // produce the authoritative failure instead of blocking startup here.
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

        _workers.Add(thread);
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
                        if (!TryForward(handle, packet, ref address))
                        {
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Stats.AddError();
                    _log?.Invoke($"Packet rewrite failed; stopping the filter to avoid an unsafe replay: {ex.Message}");
                    handle.Shutdown();
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private bool TryForward(WinDivertHandle handle, ReadOnlySpan<byte> packet, ref WinDivertAddress address)
    {
        if (handle.Send(packet, ref address))
        {
            return true;
        }

        Stats.AddError();
        _log?.Invoke("WinDivert could not re-inject a pass-through packet; stopping the filter.");
        handle.Shutdown();
        return false;
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
        var sentRealSegments = 0;
        var injectionFailed = false;

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
            if (!WinDivertHandle.CalculateChecksums(view, ref outgoing))
            {
                Stats.AddError();
                injectionFailed = true;
                break;
            }

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
                else
                {
                    sentRealSegments++;
                }
            }
            else
            {
                Stats.AddError();
                injectionFailed = true;
                if (!segment.IsDecoy)
                {
                    break;
                }
            }
        }

        var requiredRealSegments = plan.Segments.Count(segment => !segment.IsDecoy && segment.Length > 0);
        if (sentRealSegments == 0)
        {
            // Nothing made it out - let the original through so the user is never
            // worse off than with the app uninstalled.
            if (!injectionFailed)
            {
                Stats.AddError();
            }

            return false;
        }

        if (sentRealSegments < requiredRealSegments)
        {
            // Some sequence-space bytes were already injected. Re-sending the whole
            // original here would create overlap/duplicates, so contain the damage to
            // this connection and keep the rest of the engine alive.
            if (!injectionFailed)
            {
                Stats.AddError();
            }

            return true;
        }

        Stats.AddRewritten();
        Stats.AddSegments(sent);
        Stats.AddDecoys(decoys);

        if (hostName is not null && Stats.LastHost != hostName)
        {
            Stats.LastHost = hostName;
            try
            {
                HostRewritten?.Invoke(hostName, strategy.Id);
            }
            catch (Exception ex)
            {
                Stats.AddError();
                _log?.Invoke($"Host rewrite observer failed: {ex.Message}");
            }
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
                    if (!TryForward(handle, packet, ref address))
                    {
                        return;
                    }

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

        if (!TcpIpPacket.TryLocateTransport(
                packet,
                TcpIpPacket.ProtocolUdp,
                out _,
                out var udpOffset,
                out var usableLength)
            || udpOffset + 8 > usableLength)
        {
            return false;
        }

        var udpLength = (packet[udpOffset + 4] << 8) | packet[udpOffset + 5];
        if (udpLength < 8 || udpOffset + udpLength > usableLength)
        {
            return false;
        }

        var udpPayload = packet.Slice(udpOffset + 8, udpLength - 8);
        if (!QuicPacket.IsInitial(udpPayload))
        {
            return false;
        }

        var sourcePort = (ushort)((packet[udpOffset] << 8) | packet[udpOffset + 1]);
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
        _quicHandle?.Shutdown();

        foreach (var worker in _workers)
        {
            worker.Join(TimeSpan.FromSeconds(2));
        }

        _workers.Clear();

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
