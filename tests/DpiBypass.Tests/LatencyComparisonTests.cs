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
            Pair(Fake.Measurement(30, p95: 36, p99: 40), Fake.Measurement(24, p95: 70, p99: 42)),
            Pair(Fake.Measurement(30, p95: 36, p99: 40), Fake.Measurement(24, p95: 70, p99: 42)),
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
    /// Counters that cannot be read leave the load unknown. That is a reason to lean on
    /// the other checks, not a reason to refuse to measure at all.
    /// </summary>
    [Fact]
    public void AnUnreadableLoadCounterDoesNotBlockAComparison()
    {
        var pair = Pair(
            Fake.Measurement(42, load: LatencyLoadState.Unknown),
            Fake.Measurement(36, load: LatencyLoadState.Idle));

        Assert.True(pair.IsComparable);
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

    // --- the final single-sample gate -------------------------------------------------

    [Fact]
    public void TheFinalCheckPassesWhenNothingRegressed()
        => Assert.True(LatencyComparison.ConfirmsImprovement(Fake.Measurement(42), Fake.Measurement(37)));

    [Fact]
    public void TheFinalCheckFailsOnAMedianRegression()
        => Assert.False(LatencyComparison.ConfirmsImprovement(Fake.Measurement(25), Fake.Measurement(31)));

    [Fact]
    public void TheFinalCheckFailsWhenTheRemoteEndStoppedAnswering()
        => Assert.False(LatencyComparison.ConfirmsImprovement(
            Fake.Measurement(25),
            LatencyMeasurement.Create("1.1.1.1", "ICMP", [], 24, [], 8)));

    [Fact]
    public void TheFinalCheckFailsOnAP99Regression()
        => Assert.False(LatencyComparison.ConfirmsImprovement(
            Fake.Measurement(25, p95: 30, p99: 34),
            Fake.Measurement(24, p95: 29, p99: 90)));

    // --- aggregation -------------------------------------------------------------------

    [Fact]
    public void StackedGainsAreAddedAndAveragedGainsAreNot()
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
}
