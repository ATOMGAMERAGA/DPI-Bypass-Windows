namespace DpiBypass.Core.Network;

/// <summary>Which half of a cycle ran first.</summary>
/// <remarks>
/// Always measuring the baseline first would let anything that drifts with time - a
/// machine warming up, a link quietening down after the user stopped typing - land
/// entirely on one arm. Alternating the order across cycles turns that drift into noise
/// instead of into a result.
/// </remarks>
public enum LatencyCycleOrder
{
    BaselineFirst = 0,
    CandidateFirst = 1,
}

/// <summary>One baseline/candidate pair, measured back to back under the same conditions.</summary>
public sealed record LatencyPair
{
    public required LatencyMeasurement Baseline { get; init; }

    public required LatencyMeasurement Candidate { get; init; }

    /// <summary>Which arm this cycle measured first.</summary>
    public LatencyCycleOrder Order { get; init; } = LatencyCycleOrder.BaselineFirst;

    /// <summary>Machine and radio state during the baseline arm, when it was sampled.</summary>
    public LatencyEnvironment? BaselineEnvironment { get; init; }

    public LatencyEnvironment? CandidateEnvironment { get; init; }

    /// <summary>
    /// Whether the two halves are worth subtracting: same target, both answered, the link
    /// was equally busy for both, and the machine underneath did not change.
    /// </summary>
    public bool IsComparable =>
        HasSameMeasurementPath
        && Baseline.Load.ComparableWith(Candidate.Load)
        && HasComparableEnvironment;

    /// <summary>
    /// Whether CPU load, power source, route and radio stayed alike across the pair.
    /// </summary>
    /// <remarks>
    /// True when nothing was sampled: a machine that cannot report its own state has not
    /// told us the halves differed, and treating silence as a mismatch would reject every
    /// pair on hardware that simply has no counters.
    /// </remarks>
    public bool HasComparableEnvironment => BaselineEnvironment is null
        || CandidateEnvironment is null
        || BaselineEnvironment.ComparableWith(CandidateEnvironment);

    /// <summary>Everything except load counters proves both halves measured the same path.</summary>
    public bool HasSameMeasurementPath =>
        Baseline.HasRemoteConnectivity
        && Candidate.HasRemoteConnectivity
        && string.Equals(Baseline.RemoteEndpoint, Candidate.RemoteEndpoint, StringComparison.Ordinal)
        && string.Equals(Baseline.Protocol, Candidate.Protocol, StringComparison.Ordinal);

    /// <summary>
    /// Both counter readings failed. This is not directly comparable, but repeated cycles
    /// can be considered by the evaluator with a higher effect-size requirement.
    /// </summary>
    public bool HasUnknownLoad => Baseline.Load.State == LatencyLoadState.Unknown
        && Candidate.Load.State == LatencyLoadState.Unknown;

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
    /// Adds per-candidate deltas for diagnostic comparison only.
    /// </summary>
    /// <remarks>
    /// Candidate effects can interact and measurement noise also adds up. This arithmetic
    /// total must not be presented as the observed end-to-end result; use
    /// <see cref="Between(LatencyMeasurement, LatencyMeasurement)"/> on the original and
    /// final measurements for that.
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

    /// <summary>Robust spread of the winning metric's per-cycle deltas.</summary>
    public double MetricNoiseMs { get; init; }

    /// <summary>Which metric carried the decision, when one did.</summary>
    public string? WinningMetric { get; init; }

    /// <summary>Resampled interval for the winning metric's mean paired difference.</summary>
    public double? ConfidenceLowerMs { get; init; }

    public double? ConfidenceUpperMs { get; init; }

    /// <summary>Paired sign-flip p-value for the winning metric, reported not gated.</summary>
    public double? PValue { get; init; }

    /// <summary>The intervention this verdict is about.</summary>
    public string InterventionId { get; init; } = string.Empty;

    public bool Accepted => Outcome == LatencyVerdictOutcome.Accepted;
}

/// <summary>How strict one evaluation should be.</summary>
/// <remarks>
/// Split out so the production path can require things a focused unit test does not: a
/// balanced cycle order, a resampled interval that excludes zero, and enough replies
/// before a tail percentile is allowed to decide anything.
/// </remarks>
public sealed record LatencyEvaluationOptions
{
    public static readonly LatencyEvaluationOptions Default = new();

    /// <summary>What a production run uses: every guard on.</summary>
    public static readonly LatencyEvaluationOptions Strict = new()
    {
        RequireBalancedOrder = true,
        RequireConfidenceInterval = true,
    };

    public int MinimumCycles { get; init; } = 2;

    public int MaximumCycles { get; init; } = 4;

    /// <summary>
    /// Whether at least one cycle must have run each way round.
    /// </summary>
    /// <remarks>
    /// Without this a two-cycle run could be A→B twice, and anything drifting over those
    /// two minutes would be credited to the candidate both times.
    /// </remarks>
    public bool RequireBalancedOrder { get; init; }

    /// <summary>Whether the resampled interval for the winning metric must exclude zero.</summary>
    public bool RequireConfidenceInterval { get; init; }

    /// <summary>
    /// Replies needed in both halves before p99 may decide anything.
    /// </summary>
    /// <remarks>
    /// A p99 computed from forty samples is the maximum wearing a percentile's name.
    /// Below this the tail is still reported, but it cannot accept a candidate on its own.
    /// </remarks>
    public int MinimumRepliesForP99 { get; init; } = 100;

    public double ConfidenceLevel { get; init; } = 0.90;

    public int BootstrapIterations { get; init; } = 2000;

    public int Seed { get; init; } = 0x5EED;
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
/// Rejection is deliberately cheaper than acceptance. A repeated regression ends the
/// candidate as soon as it is established; a contradictory single cycle gets additional
/// measurement but can never be averaged away at the maximum. A user whose link is left
/// exactly as the driver shipped it has lost nothing, and one whose tail latency sometimes
/// doubles has lost the thing the feature exists to protect.
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

    private const double JitterRegressionFloorMs = 1.0;
    private const double JitterRegressionShare = 0.25;

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

    /// <summary>Unreadable load counters require a clearer effect and every allowed cycle.</summary>
    private const double UnknownLoadMultiplier = 1.5;

    public static LatencyVerdict Evaluate(
        LatencyOptimizationCandidate candidate,
        IReadOnlyList<LatencyPair> pairs,
        int minimumCycles,
        int maximumCycles)
        => Evaluate(
            candidate,
            pairs,
            LatencyEvaluationOptions.Default with { MinimumCycles = minimumCycles, MaximumCycles = maximumCycles });

    public static LatencyVerdict Evaluate(
        LatencyOptimizationCandidate candidate,
        IReadOnlyList<LatencyPair> pairs,
        LatencyEvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinimumCycles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumCycles, options.MinimumCycles);

        var minimumCycles = options.MinimumCycles;
        var maximumCycles = options.MaximumCycles;

        LatencyVerdict Verdict(LatencyVerdictOutcome outcome, string reason, LatencyDelta? delta = null, double noise = 0) => new()
        {
            Outcome = outcome,
            PropertyName = candidate.PropertyName,
            Description = candidate.Description,
            InterventionId = candidate.Descriptor.Id,
            Reason = reason,
            Cycles = pairs.Count,
            Delta = delta ?? LatencyDelta.Zero,
            MetricNoiseMs = noise,
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

        if (pairs.Any(pair =>
            !HasValidStatistics(pair.Baseline)
            || !HasValidStatistics(pair.Candidate)))
        {
            return Verdict(LatencyVerdictOutcome.Rejected, "ölçüm geçerli gecikme istatistikleri üretmedi");
        }

        var directlyComparable = pairs.Where(pair => pair.IsComparable).ToArray();
        var unknownLoad = pairs.Where(pair =>
            pair.HasSameMeasurementPath
            && pair.HasUnknownLoad
            && HasValidStatistics(pair.Baseline)
            && HasValidStatistics(pair.Candidate)).ToArray();

        var usingUnknownLoad = directlyComparable.Length == 0 && unknownLoad.Length > 0;
        var usable = usingUnknownLoad ? unknownLoad : directlyComparable;
        if (usable.Length == 0)
        {
            return Verdict(
                pairs.Count >= maximumCycles ? LatencyVerdictOutcome.Rejected : LatencyVerdictOutcome.Inconclusive,
                "karşılaştırılabilir ölçüm çifti elde edilemedi");
        }

        if (usingUnknownLoad && pairs.Count < maximumCycles)
        {
            return Verdict(
                LatencyVerdictOutcome.Inconclusive,
                "yük sayaçları okunamadı; ek ölçüm gerekiyor");
        }

        // Every usable cycle having run the same way round means anything drifting over
        // the run landed entirely on one arm, and no amount of repetition separates that
        // from an effect.
        if (options.RequireBalancedOrder
            && usable.Length >= 2
            && usable.DistinctBy(pair => pair.Order).Count() == 1)
        {
            return Verdict(
                pairs.Count >= maximumCycles ? LatencyVerdictOutcome.Rejected : LatencyVerdictOutcome.Inconclusive,
                "turların tümü aynı sırada ölçüldü; sıra dengelenmeden karar verilmez");
        }

        var deltas = usable.Select(pair => pair.Delta).ToArray();
        var mean = LatencyDelta.Mean(deltas);
        var baselineMean = MeanBaseline(usable);

        LatencyVerdict RegressionVerdict(int affectedCycles, string reason) => Verdict(
            usable.Length >= minimumCycles
                && (affectedCycles == usable.Length || pairs.Count >= maximumCycles)
                    ? LatencyVerdictOutcome.Rejected
                    : LatencyVerdictOutcome.Inconclusive,
            reason,
            mean);

        // --- regressions, checked before any gain is considered ---------------------

        // One probe of a batch may go missing on any link. Two in even one candidate
        // window is an intermittent reliability regression, not something gains in
        // other cycles should be allowed to average away.
        var lossRegressions = usable
            .Where(pair => -pair.Delta.LossPercent > Math.Max(1.0, pair.Candidate.LossQuantumPercent))
            .Select(pair => -pair.Delta.LossPercent)
            .ToArray();
        if (lossRegressions.Length > 0)
        {
            return RegressionVerdict(
                lossRegressions.Length,
                $"bir turda paket kaybı %{lossRegressions.Max():F1} arttı");
        }

        if (Regressions(usable, delta => delta.MedianMs, baseline => baseline.MedianRttMs,
                MedianRegressionFloorMs, MedianRegressionShare) is { Count: > 0 } medianRegression)
        {
            return RegressionVerdict(
                medianRegression.Count,
                $"bir turda median {medianRegression.Worst:F1} ms kötüleşti");
        }

        if (Regressions(usable, delta => delta.P95Ms, baseline => baseline.P95RttMs,
                P95RegressionFloorMs, P95RegressionShare) is { Count: > 0 } p95Regression)
        {
            return RegressionVerdict(
                p95Regression.Count,
                $"bir turda p95 {p95Regression.Worst:F1} ms kötüleşti");
        }

        if (Regressions(usable, delta => delta.P99Ms, baseline => baseline.P99RttMs,
                P99RegressionFloorMs, P99RegressionShare) is { Count: > 0 } p99Regression)
        {
            return RegressionVerdict(
                p99Regression.Count,
                $"bir turda p99 {p99Regression.Worst:F1} ms kötüleşti");
        }

        if (Regressions(usable, delta => delta.JitterMs, baseline => baseline.JitterMs,
                JitterRegressionFloorMs, JitterRegressionShare) is { Count: > 0 } jitterRegression)
        {
            return RegressionVerdict(
                jitterRegression.Count,
                $"bir turda jitter {jitterRegression.Worst:F1} ms kötüleşti");
        }

        // --- is there a gain at all, and in which metric ----------------------------

        var scale = (candidate.CpuSensitive ? CpuSensitiveMultiplier : 1.0)
            * (usingUnknownLoad ? UnknownLoadMultiplier : 1.0);

        // A p99 is only a percentile once there are enough replies for the hundredth of
        // them to mean something. Below that it is the worst sample, and the worst sample
        // is not allowed to accept a candidate on its own.
        var repliesPerArm = usable.Min(pair => Math.Min(pair.Baseline.RemoteReplies, pair.Candidate.RemoteReplies));
        var tailIsDecisive = repliesPerArm >= options.MinimumRepliesForP99;

        // Nothing smaller than the instrument can resolve is a result. ICMP replies are
        // whole milliseconds, so on an ICMP experiment a "0.8 ms" gain is a rounding
        // artefact however consistently it appears; on a stopwatch-timed transport the
        // floor is far below any threshold here and this changes nothing.
        var resolution = usable.Max(pair =>
            Math.Max(pair.Baseline.ClockResolutionMs, pair.Candidate.ClockResolutionMs));

        var gains = new (string Name, double Value, double Threshold, Func<LatencyDelta, double> Select)[]
        {
            ("median", mean.MedianMs, Floor(Limit(baselineMean.MedianRttMs, MedianGainFloorMs, MedianGainShare) * scale, resolution), delta => delta.MedianMs),
            ("p95", mean.P95Ms, Floor(Limit(baselineMean.P95RttMs, P95GainFloorMs, P95GainShare) * scale, resolution), delta => delta.P95Ms),
            ("p99", mean.P99Ms, Floor(Limit(baselineMean.P99RttMs, P99GainFloorMs, P99GainShare) * scale, resolution), delta => delta.P99Ms),
            ("jitter", mean.JitterMs, Floor(Limit(baselineMean.JitterMs, JitterGainFloorMs, JitterGainShare) * scale, resolution), delta => delta.JitterMs),
        };

        var winner = gains
            .Where(gain => tailIsDecisive || gain.Name != "p99")
            .Where(gain => gain.Value >= gain.Threshold)
            .OrderByDescending(gain => gain.Value / Math.Max(gain.Threshold, 0.001))
            .Select(gain => (gain.Name, gain.Value, gain.Threshold, gain.Select))
            .FirstOrDefault();

        if (winner.Name is null)
        {
            return usable.Length >= minimumCycles
                ? Verdict(
                    LatencyVerdictOutcome.Rejected,
                    $"ölçülebilir bir kazanç yok (saat çözünürlüğü {resolution:F1} ms)",
                    mean)
                : Verdict(LatencyVerdictOutcome.Inconclusive, "kazanç eşiğin altında", mean);
        }

        // --- is the gain repeatable, or is it the link being moody ------------------

        if (usable.Length < minimumCycles)
        {
            return Verdict(LatencyVerdictOutcome.Inconclusive, "tekrarlanması bekleniyor", mean);
        }

        var winningDeltas = deltas.Select(winner.Select).ToArray();
        var noise = LatencyStatistics.MedianAbsoluteDeviation(winningDeltas);
        var typicalGain = LatencyStatistics.Median(winningDeltas);

        // The same metric has to clear its meaningful-effect threshold in most cycles,
        // not merely move by a positive fraction of a millisecond.
        var required = (usable.Length / 2) + 1;
        var improvedCycles = winningDeltas.Count(value => value >= winner.Threshold);
        if (improvedCycles < required)
        {
            return usable.Length >= maximumCycles
                ? Verdict(LatencyVerdictOutcome.Rejected, $"{winner.Name} kazancı {usable.Length} turun {improvedCycles} tanesinde tekrarlandı", mean, noise)
                : Verdict(LatencyVerdictOutcome.Inconclusive, $"{winner.Name} kazancı tutarsız", mean, noise);
        }

        // With only a handful of cycles, one full-size opposite result is meaningful
        // contradiction rather than something a majority vote should hide.
        if (winningDeltas.Any(value => value <= -winner.Threshold))
        {
            return usable.Length >= maximumCycles
                ? Verdict(LatencyVerdictOutcome.Rejected, $"{winner.Name} bazı turlarda anlamlı biçimde kötüleşti", mean, noise)
                : Verdict(LatencyVerdictOutcome.Inconclusive, $"{winner.Name} turları birbiriyle çelişiyor", mean, noise);
        }

        // A gain smaller than the robust spread of that same winning metric is
        // indistinguishable from cycle-to-cycle disagreement.
        if (typicalGain <= noise)
        {
            return usable.Length >= maximumCycles
                ? Verdict(LatencyVerdictOutcome.Rejected, $"tipik {winner.Name} kazancı ({typicalGain:F1} ms) ölçüm gürültüsünün ({noise:F1} ms) altında", mean, noise)
                : Verdict(LatencyVerdictOutcome.Inconclusive, "kazanç gürültü seviyesinde", mean, noise);
        }

        var (lower, upper) = LatencyStatistics.PairedMeanInterval(
            winningDeltas,
            options.ConfidenceLevel,
            options.BootstrapIterations,
            options.Seed);
        var pValue = LatencyStatistics.PairedSignFlipPValue(winningDeltas, seed: options.Seed);

        LatencyVerdict Enriched(LatencyVerdictOutcome outcome, string reason) => Verdict(outcome, reason, mean, noise) with
        {
            WinningMetric = winner.Name,
            ConfidenceLowerMs = lower,
            ConfidenceUpperMs = upper,
            PValue = pValue,
        };

        // The resampled interval asks the one question the repeatability rules cannot:
        // given how much these cycles disagree, is "no difference" still on the table?
        if (options.RequireConfidenceInterval && lower <= 0)
        {
            return usable.Length >= maximumCycles
                ? Enriched(
                    LatencyVerdictOutcome.Rejected,
                    $"{winner.Name} güven aralığı sıfırı içeriyor ({lower:F1} … {upper:F1} ms)")
                : Enriched(LatencyVerdictOutcome.Inconclusive, "güven aralığı henüz sıfırı dışlamıyor");
        }

        return Enriched(
            LatencyVerdictOutcome.Accepted,
            $"{winner.Name} {winner.Value:F1} ms iyileşti · {usable.Length} turun {improvedCycles} tanesinde tekrarlandı"
                + (options.RequireConfidenceInterval ? $" · %{options.ConfidenceLevel * 100:F0} aralık {lower:F1}…{upper:F1} ms" : string.Empty));
    }

    /// <summary>
    /// Confirms the whole accepted bundle, from paired cycles rather than one final read.
    /// </summary>
    /// <remarks>
    /// Accepting settings one at a time proves each one on its own; it does not prove the
    /// machine ends up better with all of them on. Two changes can each help a little and
    /// fight each other, and the first baseline was taken minutes and possibly a different
    /// network condition ago. So the bundle is re-measured the same way a candidate is:
    /// alternating original and optimised, and judged on the paired differences.
    /// </remarks>
    public static bool ConfirmsBundle(
        IReadOnlyList<LatencyPair> pairs,
        bool cpuSensitive,
        LatencyEvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var settings = options ?? LatencyEvaluationOptions.Strict;
        var bundle = new LatencyOptimizationCandidate
        {
            Kind = LatencySettingKind.AdvancedProperty,
            PropertyName = "bundle",
            Description = "tüm kabul edilen değişiklikler",
            CpuSensitive = cpuSensitive,
        };

        return Evaluate(bundle, pairs, settings).Accepted;
    }

    /// <summary>
    /// Whether the second measurement preserves connectivity and stays inside every
    /// operational regression guard. This does not, by itself, claim an improvement.
    /// </summary>
    public static bool HasNoMaterialRegression(LatencyMeasurement before, LatencyMeasurement after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (!before.HasRemoteConnectivity
            || !after.HasRemoteConnectivity
            || !HasValidStatistics(before)
            || !HasValidStatistics(after)
            || !string.Equals(before.RemoteEndpoint, after.RemoteEndpoint, StringComparison.Ordinal)
            || !string.Equals(before.Protocol, after.Protocol, StringComparison.Ordinal))
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

        if (-delta.P99Ms > Limit(before.P99RttMs, P99RegressionFloorMs, P99RegressionShare))
        {
            return false;
        }

        return -delta.JitterMs <= Limit(before.JitterMs, JitterRegressionFloorMs, JitterRegressionShare);
    }

    /// <summary>
    /// Confirms that an independent final measurement is safe and shows a genuinely
    /// useful effect, rather than merely failing to regress.
    /// </summary>
    /// <remarks>
    /// Statistical acceptance still comes from repeated paired A/B cycles. This final
    /// gate checks the end-to-end state against the original baseline and requires an
    /// effect above the same operational floors. A p99-only claim needs at least 100
    /// replies; with the normal 24-probe benchmark, p95 must corroborate a tail win.
    /// </remarks>
    public static bool ConfirmsMeaningfulImprovement(
        LatencyMeasurement before,
        LatencyMeasurement after,
        bool cpuSensitive = false)
    {
        if (!HasNoMaterialRegression(before, after))
        {
            return false;
        }

        var unknownLoad = before.Load.State == LatencyLoadState.Unknown
            && after.Load.State == LatencyLoadState.Unknown;
        if (!before.Load.ComparableWith(after.Load) && !unknownLoad)
        {
            return false;
        }

        var scale = (cpuSensitive ? CpuSensitiveMultiplier : 1.0)
            * (unknownLoad ? UnknownLoadMultiplier : 1.0);
        var delta = LatencyDelta.Between(before, after);
        var replies = Math.Min(before.RemoteReplies, after.RemoteReplies);

        return delta.MedianMs >= Limit(before.MedianRttMs, MedianGainFloorMs, MedianGainShare) * scale
            || (replies >= 20
                && delta.P95Ms >= Limit(before.P95RttMs, P95GainFloorMs, P95GainShare) * scale)
            || (replies >= 100
                && delta.P99Ms >= Limit(before.P99RttMs, P99GainFloorMs, P99GainShare) * scale)
            || (replies >= 12
                && delta.JitterMs >= Limit(before.JitterMs, JitterGainFloorMs, JitterGainShare) * scale);
    }

    /// <summary>Raises a threshold to whatever the instrument can actually resolve.</summary>
    private static double Floor(double threshold, double resolutionMs)
        => Math.Max(threshold, double.IsFinite(resolutionMs) ? Math.Max(0, resolutionMs) : 0);

    private static double Limit(double baseline, double floor, double share) => Math.Max(floor, baseline * share);

    private static bool HasValidStatistics(LatencyMeasurement measurement) =>
        measurement.RemoteAttempts > 0
        && measurement.RemoteReplies > 0
        && measurement.RemoteReplies <= measurement.RemoteAttempts
        && IsValidMetric(measurement.MedianRttMs)
        && IsValidMetric(measurement.P95RttMs)
        && IsValidMetric(measurement.P99RttMs)
        && measurement.P95RttMs >= measurement.MedianRttMs
        && measurement.P99RttMs >= measurement.P95RttMs
        && IsValidMetric(measurement.JitterMs)
        && double.IsFinite(measurement.PacketLossPercent)
        && measurement.PacketLossPercent is >= 0 and <= 100;

    private static bool IsValidMetric(double value) => double.IsFinite(value) && value >= 0;

    private static (int Count, double Worst) Regressions(
        IReadOnlyList<LatencyPair> pairs,
        Func<LatencyDelta, double> selectDelta,
        Func<LatencyMeasurement, double> selectBaseline,
        double floor,
        double share)
    {
        var count = 0;
        var worst = 0d;

        foreach (var pair in pairs)
        {
            var regression = -selectDelta(pair.Delta);
            if (regression > Limit(selectBaseline(pair.Baseline), floor, share))
            {
                count++;
                worst = Math.Max(worst, regression);
            }
        }

        return (count, worst);
    }

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
