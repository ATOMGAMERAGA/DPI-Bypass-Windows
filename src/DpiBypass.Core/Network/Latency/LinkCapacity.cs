using System.Globalization;

namespace DpiBypass.Core.Network;

/// <summary>How much is actually known about a link's capacity in one direction.</summary>
/// <remarks>
/// The distinction that matters is between <see cref="Weak"/> and <see cref="Measured"/>.
/// A single busy window says the link can carry at least that much; it says nothing about
/// where the ceiling is. Deciding that a link is saturated needs the ceiling, so a weak
/// estimate produces "not measured" rather than a verdict.
/// </remarks>
public enum LinkCapacityConfidence
{
    /// <summary>Nothing worth calling a transfer has been seen.</summary>
    None = 0,

    /// <summary>Traffic was seen, but the rate never stopped climbing.</summary>
    Weak = 1,

    /// <summary>A ramp that rose and then flattened: the plateau is the capacity.</summary>
    Measured = 2,

    /// <summary>The user told us, and they know their line better than we can guess it.</summary>
    UserSupplied = 3,
}

/// <summary>
/// What a window of traffic means relative to what the link can carry.
/// </summary>
/// <remarks>
/// The three busy states used to be one. "Something is transferring" is not "the link is
/// full", and only the second can produce a standing queue - so only the second is
/// allowed to support a bufferbloat conclusion.
/// </remarks>
public enum LinkLoadClassification
{
    /// <summary>The counters could not be read, or capacity is not known well enough.</summary>
    Unknown = 0,

    /// <summary>Background drip: name resolution, clock sync, a notification poll.</summary>
    Quiet = 1,

    /// <summary>A real transfer, but nowhere near the ceiling.</summary>
    Traffic = 2,

    /// <summary>Busy enough to matter, not yet busy enough to prove a queue.</summary>
    HighUtilisation = 3,

    /// <summary>At or beyond the measured ceiling: this is where queues form.</summary>
    Saturated = 4,
}

/// <summary>
/// What this link has been observed to carry, per direction, and how sure we are.
/// </summary>
/// <remarks>
/// <para>
/// An earlier build called a window "loaded" at a quarter of whatever rate it had ever
/// seen, and learned that rate from a single window. Both halves of that are wrong in the
/// same direction: a machine whose counters have only ever seen 2 Mbit/s decides 500
/// kbit/s is saturation, measures the queueing that is not there, and confirms its own
/// mistake the next time round.
/// </para>
/// <para>
/// So capacity is only ever learned from a ramp that flattened - see
/// <see cref="LinkCapacityRamp"/> - and saturation means at least
/// <see cref="SaturationShare"/> of a capacity that was learned that way. Anything less
/// confident is reported as not measured.
/// </para>
/// </remarks>
public sealed record LinkCapacityEstimate
{
    public static readonly LinkCapacityEstimate Unknown = new();

    /// <summary>Share of a measured capacity at which the link counts as saturated.</summary>
    /// <remarks>
    /// High on purpose. Below this the sender is not outrunning the drain rate, so
    /// whatever delay is measured is not a standing queue of this machine's making.
    /// </remarks>
    public const double SaturationShare = 0.85;

    /// <summary>Share above which the link is busy enough to be worth reporting.</summary>
    public const double HighUtilisationShare = 0.60;

    public double? UplinkKbps { get; init; }

    public double? DownlinkKbps { get; init; }

    public LinkCapacityConfidence UplinkConfidence { get; init; } = LinkCapacityConfidence.None;

    public LinkCapacityConfidence DownlinkConfidence { get; init; } = LinkCapacityConfidence.None;

    public DateTimeOffset? UplinkObservedAt { get; init; }

    public DateTimeOffset? DownlinkObservedAt { get; init; }

    /// <summary>How many windows the uplink figure rests on.</summary>
    public int UplinkWindows { get; init; }

    public int DownlinkWindows { get; init; }

    public bool HasUplink => UplinkKbps is > 0;

    public bool HasDownlink => DownlinkKbps is > 0;

    /// <summary>Whether a direction is known well enough to support a saturation claim.</summary>
    public bool IsConfident(LoadDirection direction) => ConfidenceFor(direction)
        is LinkCapacityConfidence.Measured or LinkCapacityConfidence.UserSupplied;

    public double? CapacityFor(LoadDirection direction)
        => direction == LoadDirection.Upload ? UplinkKbps : DownlinkKbps;

    public LinkCapacityConfidence ConfidenceFor(LoadDirection direction)
        => direction == LoadDirection.Upload ? UplinkConfidence : DownlinkConfidence;

    public DateTimeOffset? ObservedAt(LoadDirection direction)
        => direction == LoadDirection.Upload ? UplinkObservedAt : DownlinkObservedAt;

    /// <summary>Figures the user typed in, which outrank anything we could observe.</summary>
    public static LinkCapacityEstimate FromUser(double? uplinkKbps, double? downlinkKbps)
    {
        if (uplinkKbps is not > 0 && downlinkKbps is not > 0)
        {
            return Unknown;
        }

        return new LinkCapacityEstimate
        {
            UplinkKbps = uplinkKbps is > 0 ? uplinkKbps : null,
            DownlinkKbps = downlinkKbps is > 0 ? downlinkKbps : null,
            UplinkConfidence = uplinkKbps is > 0 ? LinkCapacityConfidence.UserSupplied : LinkCapacityConfidence.None,
            DownlinkConfidence = downlinkKbps is > 0 ? LinkCapacityConfidence.UserSupplied : LinkCapacityConfidence.None,
        };
    }

    /// <summary>
    /// Records what a completed ramp established for one direction.
    /// </summary>
    /// <remarks>
    /// A user-supplied figure is never overwritten by an observation: a measurement taken
    /// through whatever the user happened to be uploading is a lower bound on their line,
    /// not a correction to what they told us it is.
    /// </remarks>
    public LinkCapacityEstimate With(LoadDirection direction, LinkCapacityRamp.Result result, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (ConfidenceFor(direction) == LinkCapacityConfidence.UserSupplied
            || result.Confidence == LinkCapacityConfidence.None)
        {
            return this;
        }

        return direction == LoadDirection.Upload
            ? this with
            {
                UplinkKbps = result.Kbps,
                UplinkConfidence = result.Confidence,
                UplinkObservedAt = at,
                UplinkWindows = result.Windows,
            }
            : this with
            {
                DownlinkKbps = result.Kbps,
                DownlinkConfidence = result.Confidence,
                DownlinkObservedAt = at,
                DownlinkWindows = result.Windows,
            };
    }

    /// <summary>
    /// What one measured window means for this direction.
    /// </summary>
    /// <remarks>
    /// Never returns <see cref="LinkLoadClassification.Saturated"/> without a capacity of
    /// at least <see cref="LinkCapacityConfidence.Measured"/>. That is the whole rule: a
    /// link whose ceiling is unknown cannot be shown to be at it.
    /// </remarks>
    public LinkLoadClassification Classify(NetworkLoadSample sample, LoadDirection direction)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (sample.State == LatencyLoadState.Unknown)
        {
            return LinkLoadClassification.Unknown;
        }

        var rate = direction == LoadDirection.Upload ? sample.UplinkKbps : sample.DownlinkKbps;
        if (!double.IsFinite(rate) || rate < NetworkLoadSample.LoadedKbps)
        {
            return LinkLoadClassification.Quiet;
        }

        if (!IsConfident(direction) || CapacityFor(direction) is not { } capacity || capacity <= 0)
        {
            // Something is transferring. Where the ceiling is remains unknown, and
            // guessing it is exactly the mistake this type exists to prevent.
            return LinkLoadClassification.Traffic;
        }

        var share = rate / capacity;
        return share switch
        {
            >= SaturationShare => LinkLoadClassification.Saturated,
            >= HighUtilisationShare => LinkLoadClassification.HighUtilisation,
            _ => LinkLoadClassification.Traffic,
        };
    }

    /// <summary>How close a window came to the ceiling, when the ceiling is known.</summary>
    public double? ShareOfCapacity(NetworkLoadSample sample, LoadDirection direction)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (!IsConfident(direction) || CapacityFor(direction) is not { } capacity || capacity <= 0)
        {
            return null;
        }

        var rate = direction == LoadDirection.Upload ? sample.UplinkKbps : sample.DownlinkKbps;
        return double.IsFinite(rate) ? Math.Max(0, rate / capacity) : null;
    }

    public string Describe(LoadDirection direction)
    {
        var capacity = CapacityFor(direction);
        if (capacity is not > 0)
        {
            return "kapasite ölçülmedi";
        }

        var qualifier = ConfidenceFor(direction) switch
        {
            LinkCapacityConfidence.UserSupplied => "kullanıcı girdisi",
            LinkCapacityConfidence.Measured => "ölçüldü",
            _ => "alt sınır",
        };

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{capacity.Value / 1000:F1} Mbit/s ({qualifier})");
    }

    public string Describe() => $"gönderim {Describe(LoadDirection.Upload)} · indirme {Describe(LoadDirection.Download)}";
}

/// <summary>
/// Learns one direction's capacity from a ramp that rises and then flattens.
/// </summary>
/// <remarks>
/// <para>
/// A transfer does not start at line rate: TCP has to open its window, a radio has to
/// pick a rate, and a disk has to keep up. So the shape of a real capacity measurement is
/// a climb followed by a plateau, and the plateau is the answer. Taking the maximum of
/// whatever has been seen instead would mean a short transfer that never got going
/// becomes this link's ceiling for the rest of the session.
/// </para>
/// <para>
/// Nothing here sends anything. It is fed windows the sampler already took from the
/// adapter's own counters while the user's own transfer was running.
/// </para>
/// </remarks>
public sealed class LinkCapacityRamp
{
    /// <summary>Consecutive windows that must sit together before it is a plateau.</summary>
    public const int PlateauWindows = 3;

    /// <summary>Widest spread inside the plateau, as a ratio of fastest to slowest.</summary>
    public const double PlateauTolerance = 1.15;

    /// <summary>How close to the fastest window the plateau has to sit.</summary>
    /// <remarks>
    /// Without this a ramp that climbed, plateaued, and then dropped away as the transfer
    /// ended would call the tail-off a plateau and report a capacity far below the line.
    /// </remarks>
    public const double PlateauShareOfPeak = 0.9;

    /// <summary>Windows above the floor needed before any figure is offered at all.</summary>
    public const int MinimumWindows = 4;

    private readonly List<double> _samples = [];

    /// <summary>What the ramp has established so far.</summary>
    public sealed record Result(double? Kbps, LinkCapacityConfidence Confidence, int Windows)
    {
        public static readonly Result Nothing = new(null, LinkCapacityConfidence.None, 0);

        public bool IsMeasured => Confidence == LinkCapacityConfidence.Measured;
    }

    public int Count => _samples.Count;

    /// <summary>Adds one measured window. Quiet windows are not part of a ramp.</summary>
    public void Add(double kbps)
    {
        if (!double.IsFinite(kbps) || kbps < NetworkLoadSample.LoadedKbps)
        {
            // A gap in the transfer ends the current ramp rather than shortening it: the
            // windows either side of a pause are not consecutive in any useful sense.
            _samples.Clear();
            return;
        }

        _samples.Add(kbps);
    }

    /// <summary>Adds the window's rate for one direction.</summary>
    public void Add(NetworkLoadSample sample, LoadDirection direction)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (sample.State == LatencyLoadState.Unknown)
        {
            _samples.Clear();
            return;
        }

        Add(direction == LoadDirection.Upload ? sample.UplinkKbps : sample.DownlinkKbps);
    }

    public Result Evaluate()
    {
        if (_samples.Count < MinimumWindows)
        {
            return Result.Nothing;
        }

        var peak = _samples.Max();
        var tail = _samples.Skip(_samples.Count - PlateauWindows).ToArray();
        var high = tail.Max();
        var low = tail.Min();

        // Flattened: the most recent windows sit together, and near the fastest one seen.
        if (low > 0 && high / low <= PlateauTolerance && low >= peak * PlateauShareOfPeak)
        {
            return new Result(LatencyStatistics.Median(tail), LinkCapacityConfidence.Measured, _samples.Count);
        }

        // Still climbing, or falling away. The peak is a lower bound on the line and is
        // reported as exactly that, which is not enough to call anything saturated.
        return new Result(peak, LinkCapacityConfidence.Weak, _samples.Count);
    }

    public void Reset() => _samples.Clear();
}
