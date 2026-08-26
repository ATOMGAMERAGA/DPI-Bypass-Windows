using System.Buffers;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Net;

namespace DpiBypass.Core.Vodafone;

/// <summary>
/// Rewrites the TTL of packets leaving one adapter, so a tethered connection is
/// not counted against a separate hotspot allowance.
/// </summary>
/// <remarks>
/// <para>
/// Operators spot tethering from the TTL: traffic the phone itself originates
/// arrives with 64, while a packet from a laptop has been routed once by the phone
/// and arrives with 63. Sending at <see cref="TtlFixSettings.TimeToLive"/> (65 by
/// default) means the phone's decrement leaves exactly 64 on the wire.
/// </para>
/// <para>
/// <b>The guard is the important part.</b> The desync strategies deliberately emit
/// decoy packets with a TTL of 3-8 so that an inspector sees them but the server
/// never does - that expiry <i>is</i> the mechanism. Rewriting those would silently
/// break every fake-packet recipe in the library. So only packets already above
/// <see cref="TtlFixSettings.Guard"/> are touched, both in the kernel filter and
/// again in the loop, and a test pins the guard above the highest TTL any strategy
/// uses.
/// </para>
/// <para>
/// The rule is scoped to a single adapter index. Moving to home Wi-Fi therefore
/// stops it by itself rather than quietly rewriting TTLs on a network where it
/// makes no sense.
/// </para>
/// </remarks>
public sealed class HotspotTtlFix : IDisposable
{
    private const int MaxPacket = 65535;

    /// <summary>Below the engine's handles, so this sees the final form of every packet.</summary>
    private const short FilterPriority = 500;

    private readonly Action<string>? _log;
    private readonly Lock _gate = new();

    private WinDivertHandle? _handle;
    private CancellationTokenSource _stopping = new();
    private Thread? _worker;
    private long _rewritten;
    private long _ipv6Dropped;

    public HotspotTtlFix(Action<string>? log = null) => _log = log;

    public bool IsActive { get; private set; }

    public int InterfaceIndex { get; private set; }

    public TtlFixSettings Settings { get; private set; } = TtlFixSettings.Default;

    /// <summary>Packets whose TTL we actually rewrote. Proof the rule is doing something.</summary>
    public long RewrittenPackets => Interlocked.Read(ref _rewritten);

    public long DroppedIPv6Packets => Interlocked.Read(ref _ipv6Dropped);

    /// <summary>
    /// Starts rewriting on one adapter, replacing any rule already in place.
    /// </summary>
    /// <exception cref="TtlFixException">The driver refused the filter.</exception>
    public void Apply(int interfaceIndex, TtlFixSettings settings)
    {
        settings.Validate();

        if (interfaceIndex <= 0)
        {
            throw new TtlFixException("Ağ bağdaştırıcısı belirlenemedi; şu anda bir ağa bağlı değilsiniz.");
        }

        lock (_gate)
        {
            Clear();

            var filter = BuildFilter(interfaceIndex, settings);

            try
            {
                _handle = WinDivertHandle.Open(filter, WinDivertLayer.Network, FilterPriority, WinDivertFlags.None);
            }
            catch (WinDivertException ex)
            {
                throw new TtlFixException($"TTL kuralı kurulamadı: {ex.Message}", ex);
            }

            _handle.SetParam(WinDivertParam.QueueLength, 4096);
            _handle.SetParam(WinDivertParam.QueueTime, 2000);

            _stopping = new CancellationTokenSource();
            Settings = settings;
            InterfaceIndex = interfaceIndex;
            IsActive = true;

            Interlocked.Exchange(ref _rewritten, 0);
            Interlocked.Exchange(ref _ipv6Dropped, 0);

            // Handed over rather than read from the fields later, so the worker stays
            // pinned to the rule and the stop signal it was started for even once
            // Clear() has replaced both.
            var handle = _handle;
            var stopping = _stopping.Token;

            _worker = new Thread(() => Loop(handle, stopping))
            {
                IsBackground = true,
                Name = "DpiBypass.TtlFix",
                Priority = ThreadPriority.AboveNormal,
            };

            _worker.Start();
            _log?.Invoke($"Hotspot TTL fix active on adapter {interfaceIndex} (ttl={settings.TimeToLive}, guard={settings.Guard}).");
        }
    }

    /// <summary>Removes the rule. Safe to call when nothing is active.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_handle is null)
            {
                IsActive = false;
                return;
            }

            _stopping.Cancel();
            _handle.Shutdown();

            try
            {
                _worker?.Join(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Coming down anyway.
            }

            _worker = null;
            _handle.Dispose();
            _handle = null;
            _stopping.Dispose();
            _stopping = new CancellationTokenSource();

            IsActive = false;
            InterfaceIndex = 0;
            _log?.Invoke("Hotspot TTL fix removed.");
        }
    }

    /// <summary>
    /// The kernel filter. Doing the guard here as well as in the loop means the
    /// low-TTL decoy packets are never even copied to user mode.
    /// </summary>
    internal static string BuildFilter(int interfaceIndex, TtlFixSettings settings)
    {
        var ipv4 = $"(ip and ip.TTL >= {settings.Guard})";

        // Tethering hands the laptop its own global IPv6 address, so the operator can
        // see two distinct sources from one subscriber no matter what the hop limit
        // says. Dropping outbound IPv6 on this adapter closes that hole and, unlike
        // unbinding the protocol, leaves nothing behind when the rule goes away.
        var ipv6 = settings.DropIPv6
            ? "ipv6"
            : $"(ipv6 and ipv6.HopLimit >= {settings.Guard})";

        return $"outbound and ifIdx == {interfaceIndex} and ({ipv4} or {ipv6})";
    }

    private void Loop(WinDivertHandle handle, CancellationToken stopping)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxPacket);

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var address = default(WinDivertAddress);
                if (!handle.Receive(buffer.AsSpan(0, MaxPacket), out var length, ref address))
                {
                    if (!stopping.IsCancellationRequested)
                    {
                        _log?.Invoke("TTL fix worker stopped: the driver closed the handle.");
                    }

                    return;
                }

                if (length == 0)
                {
                    continue;
                }

                var packet = buffer.AsSpan(0, length);

                if (!TryFix(packet, out var changed))
                {
                    Interlocked.Increment(ref _ipv6Dropped);
                    continue;
                }

                if (changed)
                {
                    WinDivertHandle.CalculateChecksums(packet, ref address);
                    Interlocked.Increment(ref _rewritten);
                }

                handle.Send(packet, ref address);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"TTL fix worker stopped: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            if (!stopping.IsCancellationRequested)
            {
                // The kernel rule outlives the worker: WinDivert keeps diverting matched
                // packets into a queue nobody drains and the driver drops them once it
                // fills, which black-holes the adapter while IsActive still claims all is
                // well. Clear() joins this very thread, so it has to run somewhere else.
                _log?.Invoke("Removing the TTL rule so the adapter is not left black-holed.");

                // Task.Run, not QueueUserWorkItem: an exception escaping a work item
                // is unhandled on a thread pool thread and ends the process, and the
                // teardown below touches a cancellation source a concurrent stop may
                // already have disposed. A faulted task is merely observed and logged.
                _ = Task.Run(() => TearDownAfterFailure(handle))
                    .ContinueWith(
                        t => _log?.Invoke($"TTL rule teardown failed: {t.Exception?.GetBaseException().Message}"),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    /// <summary>Takes down a rule whose worker died on its own.</summary>
    private void TearDownAfterFailure(WinDivertHandle handle)
    {
        lock (_gate)
        {
            // A stop or a re-apply may have replaced the rule while this was queued;
            // tearing down the one that is running now would be a fresh outage.
            if (!ReferenceEquals(_handle, handle))
            {
                return;
            }

            Clear();
        }
    }

    /// <summary>Returns false when the packet should be dropped rather than forwarded.</summary>
    private bool TryFix(Span<byte> packet, out bool changed)
    {
        changed = false;

        if (packet.Length < 20)
        {
            return true;
        }

        var version = packet[0] >> 4;
        var settings = Settings;

        if (version == 6)
        {
            if (settings.DropIPv6)
            {
                return false;
            }

            if (packet.Length < 40)
            {
                return true;
            }

            return Rewrite(packet, offset: 7, settings, out changed);
        }

        if (version != 4)
        {
            return true;
        }

        return Rewrite(packet, offset: 8, settings, out changed);
    }

    private static bool Rewrite(Span<byte> packet, int offset, TtlFixSettings settings, out bool changed)
    {
        changed = false;
        var current = packet[offset];

        // Second half of the guard: a strategy's low-TTL decoy must reach the wire
        // exactly as the engine built it.
        if (current < settings.Guard)
        {
            return true;
        }

        // Already ours. Re-injected packets can come back around, and rewriting a
        // value to itself forever would be a loop rather than a fix.
        if (current == settings.TimeToLive)
        {
            return true;
        }

        packet[offset] = settings.TimeToLive;
        changed = true;
        return true;
    }

    public void Dispose() => Clear();
}
