using System.Globalization;

namespace DpiBypass.Core.Network;

/// <summary>What the user is asking the send-rate limit to optimise for.</summary>
public enum TrafficGuardMode
{
    /// <summary>Keep as much throughput as possible while removing the queue.</summary>
    Balanced = 0,

    /// <summary>Accept a slower transfer for the lowest loaded tail this link can give.</summary>
    LowestLatency = 1,
}

/// <summary>One cap that was actually applied and measured.</summary>
public sealed record TrafficGuardCapTrial
{
    public required ulong BitsPerSecond { get; init; }

    /// <summary>The share of measured capacity this cap represents.</summary>
    public required double Share { get; init; }

    public required LoadExperimentResult Result { get; init; }

    /// <summary>
    /// Whether the traffic actually respected the cap.
    /// </summary>
    /// <remarks>
    /// A policy that exists and is read back correctly can still be doing nothing - it may
    /// not have matched the flow, or the throttle may apply somewhere other than where it
    /// was expected. The measured byte rate is the only thing that settles it, so a trial
    /// whose transfer ran faster than the cap is not counted as a data point about that
    /// cap at all.
    /// </remarks>
    public required bool RateHonoured { get; init; }

    public double? QueueingMs => Result.QueueingMs;

    public double LoadedP95Ms => Result.Loaded?.P95RttMs ?? double.PositiveInfinity;

    public double LoadedP99Ms => Result.Loaded?.P99RttMs ?? double.PositiveInfinity;

    public double JitterMs => Result.Loaded?.JitterMs ?? double.PositiveInfinity;

    public double LossPercent => Result.Loaded?.PacketLossPercent ?? 100;

    public double ThroughputKbps => Result.ThroughputKbps;
}

/// <summary>The cap the search settled on, and what it costs.</summary>
public sealed record TrafficGuardCapChoice
{
    public required TrafficGuardCapTrial Trial { get; init; }

    public required string Why { get; init; }

    /// <summary>Share of the unthrottled rate this cap keeps, as measured.</summary>
    public required double RetainedThroughputShare { get; init; }

    public ulong BitsPerSecond => Trial.BitsPerSecond;

    public string Describe() => string.Create(
        CultureInfo.CurrentCulture,
        $"{BitsPerSecond / 1_000_000d:F1} Mbit/s · throughput'un %{RetainedThroughputShare * 100:F0}'i korunuyor");
}

/// <summary>
/// Chooses a send-rate cap by measuring several, instead of assuming one.
/// </summary>
/// <remarks>
/// <para>
/// A fixed 85 percent of capacity cannot be right for every link. Where the queue sits, how
/// deep it is and how much headroom the drain rate needs are properties of the equipment
/// between this machine and the operator, and none of them are knowable from here. So a
/// few caps are applied and measured, and the one that actually produced the best trade is
/// kept.
/// </para>
/// <para>
/// The ordering is deliberate and matches what a player feels: the loaded tail first,
/// because a stutter is the worst one percent of packets and not the average; then how
/// much queueing was actually removed; then jitter and loss; and only then how much of the
/// transfer speed survived. Balanced mode reverses the last step among caps that are
/// already close on the tail, because a cap that is 1 ms better and halves the transfer
/// rate is not a better cap.
/// </para>
/// </remarks>
public static class TrafficGuardCapPlanner
{
    /// <summary>Caps to try, as shares of the measured ceiling, in the order tried.</summary>
    /// <remarks>
    /// Descending, so the least disruptive cap is measured first and a link that is fixed
    /// by barely throttling at all never has the harsher ones applied to it.
    /// </remarks>
    public static IReadOnlyList<double> SharesFor(TrafficGuardMode mode) => mode switch
    {
        TrafficGuardMode.LowestLatency => [0.80, 0.65, 0.50],
        _ => [0.92, 0.80, 0.68],
    };

    /// <summary>
    /// The longest ladder any mode defines, so a trial budget cannot silently truncate it.
    /// </summary>
    /// <remarks>
    /// The budget used to be a literal two against a ladder of three, and
    /// <c>Take(MaximumTrials)</c> meant the last share was dead code in normal operation:
    /// on a link that needed a real cap, the one cap that would have worked was never
    /// applied. Reading the length from the ladder keeps the two in step.
    /// </remarks>
    public static int MaximumShares { get; } = Enum.GetValues<TrafficGuardMode>()
        .Max(mode => SharesFor(mode).Count);

    /// <summary>Queueing has to fall by at least this for a cap to qualify.</summary>
    public const double MinimumQueueingReductionMs = 10;

    /// <summary>And by at least this share of what it was.</summary>
    public const double MinimumQueueingReductionShare = 0.25;

    /// <summary>Throughput floor for balanced mode, as a share of the unthrottled rate.</summary>
    public const double BalancedThroughputFloor = 0.70;

    /// <summary>The lower floor lowest-latency mode is allowed, which the user is shown.</summary>
    public const double LowestLatencyThroughputFloor = 0.40;

    /// <summary>How close on the tail two caps have to be before throughput decides.</summary>
    public const double BalancedTailToleranceMs = 3.0;

    /// <summary>How far over the cap measured traffic may run before the cap is not real.</summary>
    /// <remarks>
    /// The adapter's byte counters include headers the policy's rate action does not, and
    /// the sampling window is not aligned to the pacer's, so a working throttle can read a
    /// little high. Ten percent covers that and still separates a throttle that is working
    /// from one that matched nothing and left the transfer at line rate.
    /// </remarks>
    public const double RateToleranceShare = 1.10;

    /// <summary>Never cap below this, whatever the shares say.</summary>
    public const ulong MinimumCapBitsPerSecond = 256_000;

    public static ulong CapFor(double capacityKbps, double share)
        => (ulong)Math.Max(MinimumCapBitsPerSecond, capacityKbps * share * 1000);

    /// <summary>Whether the measured traffic can be said to have obeyed a cap.</summary>
    public static bool RateHonoured(ulong capBitsPerSecond, double measuredKbps)
        => measuredKbps <= capBitsPerSecond / 1000d * RateToleranceShare;

    public static double ThroughputFloor(TrafficGuardMode mode) => mode == TrafficGuardMode.LowestLatency
        ? LowestLatencyThroughputFloor
        : BalancedThroughputFloor;

    /// <summary>
    /// Picks the cap worth keeping, or nothing when none of them earned their place.
    /// </summary>
    public static TrafficGuardCapChoice? Choose(
        IReadOnlyList<TrafficGuardCapTrial> trials,
        LoadExperimentResult baseline,
        TrafficGuardMode mode)
    {
        ArgumentNullException.ThrowIfNull(trials);
        ArgumentNullException.ThrowIfNull(baseline);

        if (baseline.QueueingMs is not { } queueingBefore || baseline.ThroughputKbps <= 0)
        {
            return null;
        }

        var floor = ThroughputFloor(mode);
        var qualifying = trials
            .Where(trial => trial.RateHonoured && trial.Result.ProvesQueueing)
            .Where(trial => Removed(queueingBefore, trial) >= MinimumQueueingReductionMs)
            .Where(trial => Removed(queueingBefore, trial) >= queueingBefore * MinimumQueueingReductionShare)
            .Where(trial => trial.ThroughputKbps >= baseline.ThroughputKbps * floor)
            .Where(trial => !AddsLoss(baseline, trial))
            .ToArray();

        if (qualifying.Length == 0)
        {
            return null;
        }

        var ranked = qualifying
            .OrderBy(trial => trial.LoadedP95Ms)
            .ThenBy(trial => trial.LoadedP99Ms)
            .ThenBy(trial => trial.JitterMs)
            .ThenBy(trial => trial.LossPercent)
            .ThenByDescending(trial => trial.ThroughputKbps)
            .ToArray();

        var winner = ranked[0];
        var why = "yük altında en düşük p95";

        if (mode == TrafficGuardMode.Balanced)
        {
            // Among caps that are within a few milliseconds of the best tail, the one that
            // costs the least speed wins: past that point the extra throttling is buying
            // nothing anybody can feel.
            var best = ranked[0].LoadedP95Ms;
            var close = ranked
                .Where(trial => trial.LoadedP95Ms <= best + Math.Max(BalancedTailToleranceMs, best * 0.10))
                .OrderByDescending(trial => trial.ThroughputKbps)
                .ToArray();

            if (close.Length > 0 && !ReferenceEquals(close[0], winner))
            {
                winner = close[0];
                why = "p95 en iyiye yakın kalırken en yüksek throughput";
            }
            else
            {
                why = "hem en düşük p95 hem en yüksek korunan throughput";
            }
        }

        return new TrafficGuardCapChoice
        {
            Trial = winner,
            Why = why,
            RetainedThroughputShare = winner.ThroughputKbps / baseline.ThroughputKbps,
        };
    }

    /// <summary>Why no cap was kept, in a form the card can show.</summary>
    public static string ExplainRejection(
        IReadOnlyList<TrafficGuardCapTrial> trials,
        LoadExperimentResult baseline,
        TrafficGuardMode mode)
    {
        ArgumentNullException.ThrowIfNull(trials);
        ArgumentNullException.ThrowIfNull(baseline);

        if (trials.Count == 0)
        {
            return "hiçbir sınır ölçülemedi";
        }

        if (trials.All(trial => !trial.RateHonoured))
        {
            return "oluşturulan ilke ölçülen bayt hızını sınırlamadı; QoS bu trafiğe uygulanmıyor";
        }

        if (trials.All(trial => !trial.Result.ProvesQueueing))
        {
            return "sınır altındaki turlarda hat doygunluğa ulaşmadı; karşılaştırma yapılamadı";
        }

        var queueingBefore = baseline.QueueingMs ?? 0;
        var best = trials
            .Where(trial => trial.RateHonoured && trial.Result.ProvesQueueing)
            .OrderByDescending(trial => Removed(queueingBefore, trial))
            .FirstOrDefault();

        if (best is null)
        {
            return "hiçbir sınır ölçülebilir bir tur üretmedi";
        }

        var removed = Removed(queueingBefore, best);
        if (removed < MinimumQueueingReductionMs || removed < queueingBefore * MinimumQueueingReductionShare)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"en iyi sınırda bile kuyruklanma {queueingBefore:F0} ms → {best.QueueingMs ?? 0:F0} ms, anlamlı bir azalma değil");
        }

        if (best.ThroughputKbps < baseline.ThroughputKbps * ThroughputFloor(mode))
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"kuyruklanma azaldı fakat gönderim hızı {baseline.ThroughputKbps / 1000:F1} → {best.ThroughputKbps / 1000:F1} Mbit/s ile fazla düştü");
        }

        return "sınır kabul ölçütlerini geçemedi";
    }

    private static double Removed(double queueingBefore, TrafficGuardCapTrial trial)
        => queueingBefore - (trial.QueueingMs ?? queueingBefore);

    private static bool AddsLoss(LoadExperimentResult baseline, TrafficGuardCapTrial trial)
    {
        if (baseline.Loaded is not { } before || trial.Result.Loaded is not { } after)
        {
            return false;
        }

        // A cap can only be blamed for loss both windows actually counted. When either
        // instrument does not measure loss there is no comparison to make, and inventing
        // one would reject a working cap on evidence that was never collected.
        if (before.PacketLossPercent is not { } lossBefore || after.PacketLossPercent is not { } lossAfter)
        {
            return false;
        }

        return lossAfter - lossBefore > Math.Max(1.0, after.LossQuantumPercent ?? 0);
    }
}
