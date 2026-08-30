using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The rule that decides whether a change is kept. Everything here is arithmetic on
/// measurements, so it can be pinned exactly.
/// </summary>
public sealed class LatencyComparisonTests
{
    private const int MinimumCycles = 2;
    private const int MaximumCycles = 3;

    private static LatencyVerdict Evaluate(IReadOnlyList<LatencyPair> pairs, bool cpuSensitive = false)
        => LatencyComparison.Evaluate(Fake.Candidate(cpuSensitive: cpuSensitive), pairs, MinimumCycles, MaximumCycles);

    // --- acceptance -----------------------------------------------------------------

    [Fact]
    public void ARepeatableFiveMillisecondGainIsAccepted()
    {
        var verdict = Evaluate(Fake.Pairs((42, 37), (41.4, 36.6)));

        Assert.Equal(LatencyVerdictOutcome.Accepted, verdict.Outcome);
        Assert.Equal(4.9, verdict.Delta.MedianMs, precision: 1);
        Assert.Equal(2, verdict.Cycles);
    }

    [Fact]
    public void OneCycleIsNeverEnoughToAccept()
    {
        var verdict = Evaluate(Fake.Pairs((42, 37)));

        Assert.Equal(LatencyVerdictOutcome.Inconclusive, verdict.Outcome);
    }

    [Fact]
    public void NoCyclesAtAllIsInconclusiveRatherThanAnAcceptance()
        => Assert.Equal(LatencyVerdictOutcome.Inconclusive, Evaluate([]).Outcome);

    /// <summary>
    /// A tail-only win still counts: p99 is what a game stutters on, and a candidate that
    /// leaves the median alone while halving the worst percentile is worth keeping.
    /// </summary>
    [Fact]
    public void ATailOnlyImprovementIsAccepted()
    {
        var pairs = new[]
        {
            Pair(baseline: Fake.Measurement(30, p95: 62, p99: 90), candidate: Fake.Measurement(29.9, p95: 40, p99: 48)),
            Pair(baseline: Fake.Measurement(30.1, p95: 60, p99: 88), candidate: Fake.Measurement(30, p95: 41, p99: 50)),
        };

        var verdict = Evaluate(pairs);

        Assert.Equal(LatencyVerdictOutcome.Accepted, verdict.Outcome);
        Assert.True(
            verdict.Reason.Contains("p95", StringComparison.Ordinal) || verdict.Reason.Contains("p99", StringComparison.Ordinal),
            $"a tail win should be reported as a tail win, not '{verdict.Reason}'");
    }

    [Fact]
    public void AJitterOnlyImprovementIsAccepted()
    {
        var pairs = new[]
        {
            Pair(Fake.Measurement(30, jitter: 8), Fake.Measurement(29.9, jitter: 2)),
            Pair(Fake.Measurement(30.1, jitter: 7.5), Fake.Measurement(30, jitter: 2.2)),
        };

        Assert.Equal(LatencyVerdictOutcome.Accepted, Evaluate(pairs).Outcome);
    }

    [Fact]
    public void AConsistentP95GainIsAccepted()
        => Assert.Equal(LatencyVerdictOutcome.Accepted, Evaluate(P95Pairs(8, 7, 9)).Outcome);

    [Fact]
    public void AConsistentP99GainIsAccepted()
        => Assert.Equal(LatencyVerdictOutcome.Accepted, Evaluate(P99Pairs(10, 9, 11)).Outcome);

    [Fact]
    public void AConsistentJitterGainIsAccepted()
        => Assert.Equal(LatencyVerdictOutcome.Accepted, Evaluate(JitterPairs(3, 2.8, 3.2)).Outcome);

    // --- rejection ------------------------------------------------------------------

    [Fact]
    public void NoiseAroundZeroIsNotAGain()
    {
        var verdict = Evaluate(Fake.Pairs((25, 24.9), (24.8, 25.0)));

        Assert.Equal(LatencyVerdictOutcome.Rejected, verdict.Outcome);
        Assert.Contains("kazanç", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One huge win and one equal loss average out to a gain that the cycles themselves
    /// disagree about. A rule that only looked at the mean would keep this.
    /// </summary>
    [Fact]
    public void AGainThatOnlyHappensInOneCycleIsRejected()
    {
        var verdict = Evaluate(Fake.Pairs((40, 30), (40, 41), (40, 40.5)));

        Assert.Equal(LatencyVerdictOutcome.Rejected, verdict.Outcome);
    }

    [Fact]
    public void AGainSmallerThanTheDisagreementBetweenCyclesIsRejected()
    {
        // Mean median gain 3 ms, but the cycles differ by far more than that.
        var verdict = Evaluate(Fake.Pairs((40, 28), (40, 46), (40, 37)));

        Assert.Equal(LatencyVerdictOutcome.Rejected, verdict.Outcome);
    }

    [Fact]
    public void ANoisyP95OnlyApparentGainIsRejected()
        => Assert.Equal(LatencyVerdictOutcome.Rejected, Evaluate(P95Pairs(12, 6, 1)).Outcome);

    [Fact]
    public void ANoisyP99OnlyApparentGainIsRejected()
        => Assert.Equal(LatencyVerdictOutcome.Rejected, Evaluate(P99Pairs(18, 8, 1)).Outcome);

    [Fact]
    public void ANoisyJitterOnlyApparentGainIsRejected()
        => Assert.Equal(LatencyVerdictOutcome.Rejected, Evaluate(JitterPairs(6, 2, 0.1)).Outcome);

    [Fact]
    public void ContradictoryCyclesAreRejected()
        => Assert.Equal(LatencyVerdictOutcome.Rejected, Evaluate(P95Pairs(12, -8, 12)).Outcome);

    [Fact]
    public void ATypicalGainSmallerThanMeasurementNoiseIsRejected()
        => Assert.Equal(LatencyVerdictOutcome.Rejected, Evaluate(Fake.Pairs((40, 32), (40, 37), (40, 39.5))).Outcome);

    [Fact]
    public void AMedianRegressionIsRejectedEvenWhenTheTailImproves()
    {
        var pairs = new[]
        {
            Pair(Fake.Measurement(25, p95: 60, p99: 80), Fake.Measurement(31, p95: 40, p99: 50)),
            Pair(Fake.Measurement(25, p95: 60, p99: 80), Fake.Measurement(31, p95: 40, p99: 50)),
        };

        var verdict = Evaluate(pairs);

        Assert.Equal(LatencyVerdictOutcome.Rejected, verdict.Outcome);
        Assert.Contains("median", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AP95RegressionIsRejectedEvenWhenTheMedianImproves()
    {
        var pairs = new[]
        {
            Pair(Fake.Measurement(30, p95: 36, p99: 40), Fake.Measurement(24, p95: 70, p99: 72)),
            Pair(Fake.Measurement(30, p95: 36, p99: 40), Fake.Measurement(24, p95: 70, p99: 72)),
        };

        var verdict = Evaluate(pairs);

        Assert.Equal(LatencyVerdictOutcome.Rejected, verdict.Outcome);
        Assert.Contains("p95", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AP99RegressionIsRejectedEvenWhenTheMedianAndP95Improve()
    {
        var pairs = new[]
        {
            Pair(Fake.Measurement(30, p95: 40, p99: 44), Fake.Measurement(24, p95: 34, p99: 120)),
            Pair(Fake.Measurement(30, p95: 40, p99: 44), Fake.Measurement(24, p95: 34, p99: 120)),
        };

        var verdict = Evaluate(pairs);

        Assert.Equal(LatencyVerdictOutcome.Rejected, verdict.Outcome);
        Assert.Contains("p99", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APacketLossIncreaseIsRejectedHoweverGoodTheLatencyLooks()
    {
        var pairs = new[]
        {
            Pair(Fake.Measurement(42), Fake.Measurement(30, loss: 8)),
            Pair(Fake.Measurement(42), Fake.Measurement(30, loss: 8)),
        };

        var verdict = Evaluate(pairs);

        Assert.Equal(LatencyVerdictOutcome.Rejected, verdict.Outcome);
        Assert.Contains("paket kaybı", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntermittentPacketLossCannotBeAveragedAwayByCleanCycles()
    {
        var pairs = new[]
        {
            Pair(Fake.Measurement(42), Fake.Measurement(30, loss: 12.5)),
            Pair(Fake.Measurement(42), Fake.Measurement(30)),
            Pair(Fake.Measurement(42), Fake.Measurement(30)),
        };

        Assert.Equal(LatencyVerdictOutcome.Rejected, Evaluate(pairs).Outcome);
    }

    [Fact]
    public void AnIntermittentTailRegressionCannotBeAveragedAwayByOtherCycles()
    {
        var pairs = new[]
        {
            Pair(Fake.Measurement(40, p95: 50, p99: 60), Fake.Measurement(35, p95: 45, p99: 80)),
            Pair(Fake.Measurement(40, p95: 50, p99: 60), Fake.Measurement(35, p95: 45, p99: 45)),
            Pair(Fake.Measurement(40, p95: 50, p99: 60), Fake.Measurement(35, p95: 45, p99: 45)),
        };

        var verdict = Evaluate(pairs);

        Assert.Equal(LatencyVerdictOutcome.Rejected, verdict.Outcome);
        Assert.Contains("p99", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>A single probe of a 24-probe batch going missing is normal, not a regression.</summary>
    [Fact]
    public void OneMissingProbeIsWithinTolerance()
    {
        var pairs = new[]
        {
            Pair(Fake.Measurement(42), Fake.Measurement(36, loss: 4.1666)),
            Pair(Fake.Measurement(42), Fake.Measurement(36, loss: 4.1666)),
        };

        Assert.Equal(LatencyVerdictOutcome.Accepted, Evaluate(pairs).Outcome);
    }

    [Fact]
    public void ACandidateThatLosesTheRemoteEndIsRejectedImmediately()
    {
        var pairs = new[]
        {
            Pair(Fake.Measurement(42), LatencyMeasurement.Create("1.1.1.1", "ICMP", [], 24, [], 8)),
        };

        var verdict = Evaluate(pairs);

        Assert.Equal(LatencyVerdictOutcome.Rejected, verdict.Outcome);
        Assert.Contains("yanıt vermedi", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFiniteOrImpossibleStatisticsAreRejected()
    {
        var invalidBaseline = Fake.Measurement(30) with { MedianRttMs = double.NaN };
        var invalidOrdering = Fake.Measurement(25, p95: 24, p99: 30);

        Assert.Equal(
            LatencyVerdictOutcome.Rejected,
            Evaluate([Pair(invalidBaseline, Fake.Measurement(20))]).Outcome);
        Assert.False(LatencyComparison.ConfirmsMeaningfulImprovement(
            Fake.Measurement(30),
            invalidOrdering));
    }

    // --- comparability ---------------------------------------------------------------

    [Fact]
    public void PairsMeasuredAgainstDifferentTargetsAreNotComparable()
    {
        var pair = Pair(Fake.Measurement(42, endpoint: "1.1.1.1"), Fake.Measurement(20, endpoint: "8.8.8.8"));

        Assert.False(pair.IsComparable);
    }

    [Fact]
    public void APairWhereOnlyOneHalfRanOnABusyLinkIsNotComparable()
    {
        var pair = Pair(
            Fake.Measurement(42, load: LatencyLoadState.Idle),
            Fake.Measurement(20, load: LatencyLoadState.DownlinkLoaded));

        Assert.False(pair.IsComparable);
    }

    [Fact]
    public void TwoEquallyBusyHalvesAreComparable()
    {
        var pair = Pair(
            Fake.Measurement(80, load: LatencyLoadState.DownlinkLoaded),
            Fake.Measurement(70, load: LatencyLoadState.DownlinkLoaded));

        Assert.True(pair.IsComparable);
    }

    /// <summary>
    /// One unreadable counter cannot establish that the two windows had the same load.
    /// </summary>
    [Fact]
    public void AnUnreadableLoadCounterIsNotDirectlyComparable()
    {
        var pair = Pair(
            Fake.Measurement(42, load: LatencyLoadState.Unknown),
            Fake.Measurement(36, load: LatencyLoadState.Idle));

        Assert.False(pair.IsComparable);
    }

    [Fact]
    public void TwoUnknownLoadsNeedEveryCycleAndAStrongerGain()
    {
        var pairs = Enumerable.Range(0, MaximumCycles)
            .Select(_ => Pair(
                Fake.Measurement(42, load: LatencyLoadState.Unknown),
                Fake.Measurement(36, load: LatencyLoadState.Unknown)))
            .ToArray();

        Assert.Equal(LatencyVerdictOutcome.Inconclusive, Evaluate(pairs[..^1]).Outcome);
        Assert.Equal(LatencyVerdictOutcome.Accepted, Evaluate(pairs).Outcome);
    }

    [Fact]
    public void OnlyComparablePairsCountTowardsAVerdict()
    {
        var pairs = new[]
        {
            Pair(Fake.Measurement(42, load: LatencyLoadState.Idle), Fake.Measurement(20, load: LatencyLoadState.DownlinkLoaded)),
            Pair(Fake.Measurement(42, load: LatencyLoadState.Idle), Fake.Measurement(20, load: LatencyLoadState.DownlinkLoaded)),
            Pair(Fake.Measurement(42, load: LatencyLoadState.Idle), Fake.Measurement(20, load: LatencyLoadState.DownlinkLoaded)),
        };

        // Three enormous "gains", none of which measured the same conditions twice.
        Assert.Equal(LatencyVerdictOutcome.Rejected, Evaluate(pairs).Outcome);
    }

    // --- cost ------------------------------------------------------------------------

    /// <summary>
    /// Interrupt moderation off trades CPU for latency. The same 1.5 ms that is worth
    /// keeping for a free change is not worth an interrupt per packet.
    /// </summary>
    [Fact]
    public void ACpuSensitiveCandidateNeedsABiggerWin()
    {
        var pairs = Fake.Pairs((30, 28.5), (30, 28.5));

        Assert.Equal(LatencyVerdictOutcome.Accepted, Evaluate(pairs).Outcome);
        Assert.Equal(LatencyVerdictOutcome.Rejected, Evaluate(pairs, cpuSensitive: true).Outcome);
    }

    [Fact]
    public void ACpuSensitiveCandidateWithALargeWinIsStillAccepted()
        => Assert.Equal(
            LatencyVerdictOutcome.Accepted,
            Evaluate(Fake.Pairs((40, 30), (40, 30.4)), cpuSensitive: true).Outcome);

    // --- independent final verification ---------------------------------------------

    [Fact]
    public void IdenticalMeasurementsAreSafeButNotAConfirmedImprovement()
    {
        var before = Fake.Measurement(30);
        var after = Fake.Measurement(30);

        Assert.True(LatencyComparison.HasNoMaterialRegression(before, after));
        Assert.False(LatencyComparison.ConfirmsMeaningfulImprovement(before, after));
    }

    [Fact]
    public void ATinyMeaninglessImprovementIsNotConfirmed()
        => Assert.False(LatencyComparison.ConfirmsMeaningfulImprovement(
            Fake.Measurement(30),
            Fake.Measurement(29.8)));

    [Fact]
    public void ASmallAllowedRegressionIsNotAConfirmedImprovement()
    {
        var before = Fake.Measurement(30);
        var after = Fake.Measurement(30.8);

        Assert.True(LatencyComparison.HasNoMaterialRegression(before, after));
        Assert.False(LatencyComparison.ConfirmsMeaningfulImprovement(before, after));
    }

    [Fact]
    public void AMeaningfulMedianImprovementIsConfirmed()
        => Assert.True(LatencyComparison.ConfirmsMeaningfulImprovement(
            Fake.Measurement(42),
            Fake.Measurement(37)));

    [Fact]
    public void AMeaningfulP95ImprovementWithEnoughRepliesIsConfirmed()
        => Assert.True(LatencyComparison.ConfirmsMeaningfulImprovement(
            Fake.Measurement(30, p95: 60, p99: 90),
            Fake.Measurement(30, p95: 48, p99: 90)));

    [Fact]
    public void AP99OnlyImprovementNeedsEnoughRepliesToRepresentThePercentile()
    {
        var before = Fake.Measurement(30, p95: 60, p99: 100);
        var after = Fake.Measurement(30, p95: 60, p99: 80);

        Assert.False(LatencyComparison.ConfirmsMeaningfulImprovement(before, after));
        Assert.True(LatencyComparison.ConfirmsMeaningfulImprovement(
            before with { RemoteAttempts = 100, RemoteReplies = 100 },
            after with { RemoteAttempts = 100, RemoteReplies = 100 }));
    }

    [Fact]
    public void TheFinalCheckFailsOnAMedianRegression()
        => Assert.False(LatencyComparison.HasNoMaterialRegression(Fake.Measurement(25), Fake.Measurement(31)));

    [Fact]
    public void TheFinalCheckFailsWhenTheRemoteEndStoppedAnswering()
        => Assert.False(LatencyComparison.ConfirmsMeaningfulImprovement(
            Fake.Measurement(25),
            LatencyMeasurement.Create("1.1.1.1", "ICMP", [], 24, [], 8)));

    [Fact]
    public void TheFinalCheckFailsOnAP99Regression()
        => Assert.False(LatencyComparison.HasNoMaterialRegression(
            Fake.Measurement(25, p95: 30, p99: 34),
            Fake.Measurement(24, p95: 29, p99: 90)));

    [Fact]
    public void TheFinalCheckFailsOnPacketLossRegression()
        => Assert.False(LatencyComparison.ConfirmsMeaningfulImprovement(
            Fake.Measurement(30),
            Fake.Measurement(20, loss: 12)));

    // --- aggregation -------------------------------------------------------------------

    [Fact]
    public void CandidateDeltasCanBeSummedForDiagnosticsAndAveraged()
    {
        LatencyDelta[] deltas =
        [
            new() { MedianMs = 2, P95Ms = 4, P99Ms = 6, JitterMs = 1, LossPercent = 0 },
            new() { MedianMs = 3, P95Ms = 5, P99Ms = 7, JitterMs = 0.5, LossPercent = 0 },
        ];

        Assert.Equal(5, LatencyDelta.Sum(deltas).MedianMs);
        Assert.Equal(2.5, LatencyDelta.Mean(deltas).MedianMs);
        Assert.Equal(0, LatencyDelta.Sum([]).MedianMs);
        Assert.Equal(0, LatencyDelta.Mean([]).MedianMs);
    }

    private static LatencyPair Pair(LatencyMeasurement baseline, LatencyMeasurement candidate)
        => new() { Baseline = baseline, Candidate = candidate };

    private static IReadOnlyList<LatencyPair> P95Pairs(params double[] gains) =>
    [
        .. gains.Select(gain => Pair(
            Fake.Measurement(30, p95: 60, p99: 90),
            Fake.Measurement(30, p95: 60 - gain, p99: 90))),
    ];

    private static IReadOnlyList<LatencyPair> P99Pairs(params double[] gains) =>
    [
        .. gains.Select(gain => Pair(
            Fake.Measurement(30, p95: 60, p99: 100),
            Fake.Measurement(30, p95: 60, p99: 100 - gain))),
    ];

    private static IReadOnlyList<LatencyPair> JitterPairs(params double[] gains) =>
    [
        .. gains.Select(gain => Pair(
            Fake.Measurement(30, jitter: 8),
            Fake.Measurement(30, jitter: 8 - gain))),
    ];
}
