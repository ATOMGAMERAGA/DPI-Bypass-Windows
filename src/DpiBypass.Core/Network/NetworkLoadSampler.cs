using System.Net.NetworkInformation;

namespace DpiBypass.Core.Network;

/// <summary>How busy the link was while a measurement ran.</summary>
public enum LatencyLoadState
{
    /// <summary>The adapter's counters could not be read, so nothing is claimed.</summary>
    Unknown = 0,

    Idle = 1,

    UplinkLoaded = 2,

    DownlinkLoaded = 3,

    BidirectionalLoaded = 4,
}

/// <summary>One reading of an adapter's cumulative byte counters.</summary>
public readonly record struct NetworkCounters(long BytesSent, long BytesReceived, DateTimeOffset At);

/// <summary>What the link was doing across one measurement window.</summary>
public sealed record NetworkLoadSample
{
    /// <summary>Anything at or above this in either direction counts as loaded.</summary>
    /// <remarks>
    /// 256 kbit/s is well above the drip of a machine sitting idle - name resolution,
    /// clock sync, a notification poll - and well below anything that would put a
    /// consumer access link into its queue.
    /// </remarks>
    public const double LoadedKbps = 256;

    public static readonly NetworkLoadSample Unknown = new()
    {
        State = LatencyLoadState.Unknown,
        UplinkKbps = 0,
        DownlinkKbps = 0,
    };

    public required LatencyLoadState State { get; init; }

    public required double UplinkKbps { get; init; }

    public required double DownlinkKbps { get; init; }

    public bool IsLoaded => State is LatencyLoadState.UplinkLoaded
        or LatencyLoadState.DownlinkLoaded
        or LatencyLoadState.BidirectionalLoaded;

    /// <summary>
    /// Whether two windows were busy enough alike for their latencies to be compared.
    /// </summary>
    /// <remarks>
    /// A candidate measured while a download was running against a baseline measured on
    /// an idle link is not a measurement of the candidate; it is a measurement of the
    /// download. Comparing only same-state windows is what keeps that out of a verdict.
    /// </remarks>
    public bool ComparableWith(NetworkLoadSample other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // Nothing is known about at least one side, so there is nothing to disagree
        // about either; the rest of the checks still have to hold the result up.
        if (State == LatencyLoadState.Unknown || other.State == LatencyLoadState.Unknown)
        {
            return true;
        }

        return State == other.State;
    }

    public static NetworkLoadSample Between(NetworkCounters? start, NetworkCounters? end)
    {
        if (start is not { } from || end is not { } to)
        {
            return Unknown;
        }

        var seconds = (to.At - from.At).TotalSeconds;
        if (seconds <= 0.05)
        {
            return Unknown;
        }

        // Counters are unsigned on the wire and can be reset by the driver; a negative
        // delta means the window is not measurable rather than that traffic ran backwards.
        var sent = to.BytesSent - from.BytesSent;
        var received = to.BytesReceived - from.BytesReceived;
        if (sent < 0 || received < 0)
        {
            return Unknown;
        }

        var uplink = sent * 8 / seconds / 1000;
        var downlink = received * 8 / seconds / 1000;

        return new NetworkLoadSample
        {
            State = (uplink >= LoadedKbps, downlink >= LoadedKbps) switch
            {
                (true, true) => LatencyLoadState.BidirectionalLoaded,
                (true, false) => LatencyLoadState.UplinkLoaded,
                (false, true) => LatencyLoadState.DownlinkLoaded,
                _ => LatencyLoadState.Idle,
            },
            UplinkKbps = uplink,
            DownlinkKbps = downlink,
        };
    }
}

public interface INetworkLoadSampler
{
    /// <summary>The adapter's cumulative counters, or null when they cannot be read.</summary>
    NetworkCounters? Read(NetworkFingerprint network);
}

/// <summary>
/// Reads what the adapter has already carried. It never sends anything.
/// </summary>
/// <remarks>
/// Loaded latency matters far more than idle latency for games - most of what people
/// call lag is their own uplink queueing - but generating that load would mean pushing
/// traffic at somebody else's server to answer a question about this machine. So the
/// load is observed rather than created: the optimizer compares only windows the link
/// was equally busy in, and reports a queueing delay when the user's own traffic
/// happens to provide the loaded window.
/// </remarks>
public sealed class NetworkLoadSampler : INetworkLoadSampler
{
    private readonly Action<string>? _log;

    public NetworkLoadSampler(Action<string>? log = null) => _log = log;

    public NetworkCounters? Read(NetworkFingerprint network)
    {
        ArgumentNullException.ThrowIfNull(network);

        if (string.IsNullOrWhiteSpace(network.AdapterId))
        {
            return null;
        }

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(adapter.Id, network.AdapterId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var statistics = adapter.GetIPStatistics();
                return new NetworkCounters(
                    statistics.BytesSent,
                    statistics.BytesReceived,
                    DateTimeOffset.UtcNow);
            }
        }
        catch (NetworkInformationException ex)
        {
            // The adapter can be pulled out from under this between enumerating and
            // reading. Load classification is an input to the verdict, not the verdict.
            _log?.Invoke($"latency.load: bağdaştırıcı sayaçları okunamadı ({ex.Message}).");
        }
        catch (PlatformNotSupportedException ex)
        {
            _log?.Invoke($"latency.load: bağdaştırıcı sayaçları bu platformda yok ({ex.Message}).");
        }

        return null;
    }
}
