namespace DpiBypass.Core.Network;

/// <summary>One baseline/candidate pair, measured back to back under the same conditions.</summary>
public sealed record LatencyPair
{
    public required LatencyMeasurement Baseline { get; init; }

    public required LatencyMeasurement Candidate { get; init; }

    /// <summary>
    /// Whether the two halves are worth subtracting: same target, both answered, and the
    /// link was equally busy for both.
    /// </summary>
    public bool IsComparable =>
        Baseline.HasRemoteConnectivity
        && Candidate.HasRemoteConnectivity
        && string.Equals(Baseline.RemoteEndpoint, Candidate.RemoteEndpoint, StringComparison.Ordinal)
        && Baseline.Load.ComparableWith(Candidate.Load);

    /// <summary>Improvement in this pair. Positive means the candidate was faster.</summary>
    public LatencyDelta Delta => LatencyDelta.Between(Baseline, Candidate);
}

/// <summary>How much better (positive) or worse (negative) one measurement is than another.</summary>
public sealed record LatencyDelta
{
    public required double MedianMs { get; init; }

    public required double P95Ms { get; init; }

    public required double P99Ms { get; init; }

    public required double JitterMs { get; init; }

    /// <summary>Percentage points of packet loss removed. Negative means loss was added.</summary>
    public required double LossPercent { get; init; }

    public static readonly LatencyDelta Zero = new()
    {
        MedianMs = 0,
        P95Ms = 0,
        P99Ms = 0,
        JitterMs = 0,
        LossPercent = 0,
    };

    public static LatencyDelta Between(LatencyMeasurement before, LatencyMeasurement after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return new LatencyDelta
        {
            MedianMs = before.MedianRttMs - after.MedianRttMs,
            P95Ms = before.P95RttMs - after.P95RttMs,
            P99Ms = before.P99RttMs - after.P99RttMs,
            JitterMs = before.JitterMs - after.JitterMs,
            LossPercent = before.PacketLossPercent - after.PacketLossPercent,
        };
    }

    /// <summary>
    /// Adds up the verified gains of changes that were applied one on top of the other.
    /// </summary>
    /// <remarks>
    /// Each candidate is measured against the machine with the previously accepted
    /// changes already on it, so the per-candidate gains stack rather than compete, and
    /// the total is what the user actually ends up with.
    /// </remarks>
    public static LatencyDelta Sum(IReadOnlyList<LatencyDelta> deltas)
    {
        ArgumentNullException.ThrowIfNull(deltas);

        if (deltas.Count == 0)
        {
            return Zero;
        }

        return new LatencyDelta
        {
            MedianMs = deltas.Sum(delta => delta.MedianMs),
            P95Ms = deltas.Sum(delta => delta.P95Ms),
            P99Ms = deltas.Sum(delta => delta.P99Ms),
            JitterMs = deltas.Sum(delta => delta.JitterMs),
            LossPercent = deltas.Sum(delta => delta.LossPercent),
        };
    }

    public static LatencyDelta Mean(IReadOnlyList<LatencyDelta> deltas)
    {
        ArgumentNullException.ThrowIfNull(deltas);

        if (deltas.Count == 0)
        {
            return Zero;
        }

        return new LatencyDelta
        {
            MedianMs = LatencyStatistics.Mean(deltas.Select(delta => delta.MedianMs)),
            P95Ms = LatencyStatistics.Mean(deltas.Select(delta => delta.P95Ms)),
            P99Ms = LatencyStatistics.Mean(deltas.Select(delta => delta.P99Ms)),
            JitterMs = LatencyStatistics.Mean(deltas.Select(delta => delta.JitterMs)),
            LossPercent = LatencyStatistics.Mean(deltas.Select(delta => delta.LossPercent)),
        };
    }
}

public enum LatencyVerdictOutcome
{
    /// <summary>The gain is real and repeatable; keep the change.</summary>
    Accepted,

    /// <summary>No gain, or a regression. Put the original value back.</summary>
    Rejected,

    /// <summary>Something is there but the noise is as big as it is. Measure again.</summary>
    Inconclusive,
}

/// <summary>What the paired benchmark concluded about one candidate.</summary>
public sealed record LatencyVerdict
{
    public required LatencyVerdictOutcome Outcome { get; init; }

    public required string PropertyName { get; init; }

    public required string Description { get; init; }

    public required string Reason { get; init; }

    public required int Cycles { get; init; }

    public LatencyDelta Delta { get; init; } = LatencyDelta.Zero;

    /// <summary>Spread of the per-cycle median deltas: the noise the gain had to clear.</summary>
    public double MedianNoiseMs { get; init; }

    public bool Accepted => Outcome == LatencyVerdictOutcome.Accepted;
}

/// <summary>
/// The rule that decides whether a NIC change earned its place.
/// </summary>
/// <remarks>
/// <para>
/// A single before/after pair cannot tell a 2 ms improvement from a 2 ms mood swing on a
/// Wi-Fi link, so nothing here looks at one pair. Each candidate is measured as repeated
/// A/B cycles - baseline, candidate, baseline, candidate - against the same target with
/// the same probe count while the link is equally busy, and the change survives only if
/// the same metric improves in most cycles by more than the cycles disagree with each
/// other.
/// </para>
/// <para>
/// Rejection is deliberately cheaper than acceptance. Any loss increase, any meaningful
/// regression in median, p95 or p99, or a candidate that cannot be measured at all ends
/// the candidate immediately: a user whose link is left exactly as the driver shipped it
/// has lost nothing, and one whose tail latency doubled has lost the thing the feature
/// exists to protect.
/// </para>
/// </remarks>
public static class LatencyComparison
{
    /// <summary>Median may not get worse by more than this.</summary>
    private const double MedianRegressionFloorMs = 1.0;
    private const double MedianRegressionShare = 0.05;

    private const double P95RegressionFloorMs = 2.0;
    private const double P95RegressionShare = 0.08;

    private const double P99RegressionFloorMs = 3.0;
    private const double P99RegressionShare = 0.10;

    /// <summary>Smallest gain worth calling a gain, before the noise test.</summary>
    private const double MedianGainFloorMs = 0.8;
    private const double MedianGainShare = 0.04;

    private const double P95GainFloorMs = 1.5;
    private const double P95GainShare = 0.05;

    private const double P99GainFloorMs = 2.0;
    private const double P99GainShare = 0.05;

    private const double JitterGainFloorMs = 0.5;
    private const double JitterGainShare = 0.15;

    /// <summary>A change that costs CPU has to win by this much more to be worth it.</summary>
    private const double CpuSensitiveMultiplier = 2.0;

    public static LatencyVerdict Evaluate(
        LatencyOptimizationCandidate candidate,
        IReadOnlyList<LatencyPair> pairs,
        int minimumCycles,
        int maximumCycles)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumCycles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCycles, minimumCycles);

        LatencyVerdict Verdict(LatencyVerdictOutcome outcome, string reason, LatencyDelta? delta = null, double noise = 0) => new()
        {
            Outcome = outcome,
            PropertyName = candidate.PropertyName,
            Description = candidate.Description,
            Reason = reason,
            Cycles = pairs.Count,
            Delta = delta ?? LatencyDelta.Zero,
            MedianNoiseMs = noise,
        };

        if (pairs.Count == 0)
        {
            return Verdict(LatencyVerdictOutcome.Inconclusive, "henüz ölçüm yok");
        }

        // Any cycle where the candidate could not be reached at all is fatal; a setting
        // that sometimes takes the link down is not a latency improvement.
        if (pairs.Any(pair => !pair.Candidate.HasRemoteConnectivity))
        {
            return Verdict(LatencyVerdictOutcome.Rejected, "aday uygulandığında uzak uç yanıt vermedi");
        }

        var usable = pairs.Where(pair => pair.IsComparable).ToArray();
        if (usable.Length == 0)
        {
            return Verdict(
                pairs.Count >= maximumCycles ? LatencyVerdictOutcome.Rejected : LatencyVerdictOutcome.Inconclusive,
                "karşılaştırılabilir ölçüm çifti elde edilemedi");
        }

        var deltas = usable.Select(pair => pair.Delta).ToArray();
        var mean = LatencyDelta.Mean(deltas);
        var baselineMean = MeanBaseline(usable);
        var noise = LatencyStatistics.StandardDeviation([.. deltas.Select(delta => delta.MedianMs)]);

        // --- regressions, checked before any gain is considered ---------------------

        // One probe of the batch may go missing on any link; two consistently does not.
        var lossTolerance = Math.Max(1.0, usable.Max(pair => pair.Candidate.LossQuantumPercent));
        if (-mean.LossPercent > lossTolerance)
        {
            return Verdict(LatencyVerdictOutcome.Rejected, $"paket kaybı %{-mean.LossPercent:F1} arttı", mean, noise);
        }

        if (-mean.MedianMs > Limit(baselineMean.MedianRttMs, MedianRegressionFloorMs, MedianRegressionShare))
        {
            return Verdict(LatencyVerdictOutcome.Rejected, $"median {-mean.MedianMs:F1} ms kötüleşti", mean, noise);
        }

        if (-mean.P95Ms > Limit(baselineMean.P95RttMs, P95RegressionFloorMs, P95RegressionShare))
        {
            return Verdict(LatencyVerdictOutcome.Rejected, $"p95 {-mean.P95Ms:F1} ms kötüleşti", mean, noise);
        }

        if (-mean.P99Ms > Limit(baselineMean.P99RttMs, P99RegressionFloorMs, P99RegressionShare))
        {
            return Verdict(LatencyVerdictOutcome.Rejected, $"p99 {-mean.P99Ms:F1} ms kötüleşti", mean, noise);
        }

        // --- is there a gain at all, and in which metric ----------------------------

        var scale = candidate.CpuSensitive ? CpuSensitiveMultiplier : 1.0;

        var gains = new (string Name, double Value, double Threshold, Func<LatencyDelta, double> Select)[]
        {
            ("median", mean.MedianMs, Limit(baselineMean.MedianRttMs, MedianGainFloorMs, MedianGainShare) * scale, delta => delta.MedianMs),
            ("p95", mean.P95Ms, Limit(baselineMean.P95RttMs, P95GainFloorMs, P95GainShare) * scale, delta => delta.P95Ms),
            ("p99", mean.P99Ms, Limit(baselineMean.P99RttMs, P99GainFloorMs, P99GainShare) * scale, delta => delta.P99Ms),
            ("jitter", mean.JitterMs, Limit(baselineMean.JitterMs, JitterGainFloorMs, JitterGainShare) * scale, delta => delta.JitterMs),
        };

        var winner = gains
            .Where(gain => gain.Value >= gain.Threshold)
            .OrderByDescending(gain => gain.Value / Math.Max(gain.Threshold, 0.001))
            .Select(gain => (gain.Name, gain.Value, gain.Select))
            .FirstOrDefault();

        if (winner.Name is null)
        {
            return usable.Length >= minimumCycles
                ? Verdict(LatencyVerdictOutcome.Rejected, "ölçülebilir bir kazanç yok", mean, noise)
                : Verdict(LatencyVerdictOutcome.Inconclusive, "kazanç eşiğin altında", mean, noise);
        }

        // --- is the gain repeatable, or is it the link being moody ------------------

        if (usable.Length < minimumCycles)
        {
            return Verdict(LatencyVerdictOutcome.Inconclusive, "tekrarlanması bekleniyor", mean, noise);
        }

        // The same metric has to move the right way in most cycles, not once by a lot.
        var required = (usable.Length / 2) + 1;
        var improvedCycles = deltas.Count(delta => winner.Select(delta) > 0);
        if (improvedCycles < required)
        {
            return usable.Length >= maximumCycles
                ? Verdict(LatencyVerdictOutcome.Rejected, $"{winner.Name} kazancı {usable.Length} turun {improvedCycles} tanesinde tekrarlandı", mean, noise)
                : Verdict(LatencyVerdictOutcome.Inconclusive, $"{winner.Name} kazancı tutarsız", mean, noise);
        }

        // A median gain smaller than how much the cycles disagree with each other is
        // indistinguishable from the disagreement itself.
        var medianNoiseFloor = noise;
        if (winner.Name == "median" && mean.MedianMs <= medianNoiseFloor)
        {
            return usable.Length >= maximumCycles
                ? Verdict(LatencyVerdictOutcome.Rejected, $"kazanç ({mean.MedianMs:F1} ms) ölçüm gürültüsünün ({medianNoiseFloor:F1} ms) altında", mean, noise)
                : Verdict(LatencyVerdictOutcome.Inconclusive, "kazanç gürültü seviyesinde", mean, noise);
        }

        return Verdict(
            LatencyVerdictOutcome.Accepted,
            $"{winner.Name} {winner.Value:F1} ms iyileşti · {usable.Length} turun {improvedCycles} tanesinde tekrarlandı",
            mean,
            noise);
    }

    /// <summary>
    /// The same rule applied to a single before/after pair, used for the one final check
    /// that the machine really is where the run said it would leave it.
    /// </summary>
    public static bool ConfirmsImprovement(LatencyMeasurement before, LatencyMeasurement after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (!before.HasRemoteConnectivity || !after.HasRemoteConnectivity)
        {
            return false;
        }

        var delta = LatencyDelta.Between(before, after);

        if (-delta.LossPercent > Math.Max(1.0, after.LossQuantumPercent))
        {
            return false;
        }

        if (-delta.MedianMs > Limit(before.MedianRttMs, MedianRegressionFloorMs, MedianRegressionShare))
        {
            return false;
        }

        if (-delta.P95Ms > Limit(before.P95RttMs, P95RegressionFloorMs, P95RegressionShare))
        {
            return false;
        }

        return -delta.P99Ms <= Limit(before.P99RttMs, P99RegressionFloorMs, P99RegressionShare);
    }

    private static double Limit(double baseline, double floor, double share) => Math.Max(floor, baseline * share);

    private static LatencyMeasurement MeanBaseline(IReadOnlyList<LatencyPair> pairs)
    {
        var first = pairs[0].Baseline;

        return first with
        {
            MedianRttMs = LatencyStatistics.Mean(pairs.Select(pair => pair.Baseline.MedianRttMs)),
            P95RttMs = LatencyStatistics.Mean(pairs.Select(pair => pair.Baseline.P95RttMs)),
            P99RttMs = LatencyStatistics.Mean(pairs.Select(pair => pair.Baseline.P99RttMs)),
            JitterMs = LatencyStatistics.Mean(pairs.Select(pair => pair.Baseline.JitterMs)),
            PacketLossPercent = LatencyStatistics.Mean(pairs.Select(pair => pair.Baseline.PacketLossPercent)),
        };
    }
}
