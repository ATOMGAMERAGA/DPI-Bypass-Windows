using DpiBypass.Core.Diagnostics;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// What the automatic profile choice is allowed to trade for what.
/// </summary>
/// <remarks>
/// The behaviours here are the ones a user would notice and could not diagnose: a fast
/// but flaky profile winning, a profile that halves their download winning by two
/// milliseconds, the app changing profile every few minutes because two candidates are
/// inside each other's noise, or a decision made from three probes being presented as
/// though it were measured.
/// </remarks>
public sealed class StrategySelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<ProbeTarget> Targets = StrategyTargets.Default;

    /// <summary>A candidate that reached everything, with the connect times given.</summary>
    private static StrategyMeasurement Healthy(
        string id,
        double[] connectMs,
        double? downMbps = null,
        int failures = 0,
        bool reachAllRequired = true)
    {
        var samples = new List<ProbeSample>();
        var hosts = Targets.Where(target => target.Required).Select(target => target.Host).ToArray();

        for (var i = 0; i < connectMs.Length; i++)
        {
            var host = reachAllRequired ? hosts[i % hosts.Length] : hosts[0];
            samples.Add(new ProbeSample(
                host,
                ProbeMethod.TcpConnect,
                Success: true,
                ProbeOutcome.Reachable,
                Dns: TimeSpan.FromMilliseconds(4),
                Connect: TimeSpan.FromMilliseconds(connectMs[i]),
                Handshake: TimeSpan.FromMilliseconds(connectMs[i] * 3)));
        }

        for (var i = 0; i < failures; i++)
        {
            samples.Add(new ProbeSample(
                hosts[0],
                ProbeMethod.TcpConnect,
                Success: false,
                ProbeOutcome.HandshakeReset,
                Dns: null,
                Connect: null,
                Handshake: null));
        }

        return new StrategyMeasurement
        {
            StrategyId = id,
            StrategyName = id,
            Samples = samples,
            MeasuredAt = Now,
            Throughput = downMbps is { } mbps
                ? new ThroughputResult(mbps, null, null, 8_000_000, TimeSpan.FromSeconds(5))
                : null,
        };
    }

    private static StrategySelection Choose(
        IReadOnlyList<StrategyMeasurement> measurements,
        string? incumbent = null,
        DateTimeOffset? incumbentSince = null,
        SelectionThresholds? thresholds = null)
        => StrategySelectionPolicy.Choose(measurements, Targets, incumbent, incumbentSince, Now, thresholds);

    [Fact]
    public void TheLowestConnectTimeWinsAmongHealthyCandidates()
    {
        var selection = Choose(
        [
            Healthy("slow", [40, 42, 41, 43]),
            Healthy("fast", [20, 21, 19, 22]),
        ]);

        Assert.Equal("fast", selection.StrategyId);
        Assert.True(selection.Changed);
        Assert.Contains("fast", selection.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Speed comes before latency. A profile that costs a fifth of the download is not
    /// worth having however low its connect time is.
    /// </summary>
    [Fact]
    public void ACandidateThatCostsRealThroughputIsEliminatedEvenWhenItIsTheFastestToConnect()
    {
        var selection = Choose(
        [
            Healthy("quick-but-slow-link", [18, 19, 18, 20], downMbps: 30),
            Healthy("balanced", [26, 27, 25, 28], downMbps: 92),
        ]);

        Assert.Equal("balanced", selection.StrategyId);
        Assert.Equal(SelectionConfidence.WithThroughput, selection.Confidence);
        Assert.Contains(
            selection.Eliminated,
            rejection => rejection.StrategyId == "quick-but-slow-link"
                && rejection.Reason.Contains("Hızı", StringComparison.Ordinal));
    }

    /// <summary>
    /// And a candidate inside the speed floor keeps its latency advantage: the rule is
    /// "no meaningful loss", not "the fastest link wins".
    /// </summary>
    [Fact]
    public void ACandidateWithinTheSpeedFloorStillWinsOnLatency()
    {
        var selection = Choose(
        [
            Healthy("slightly-slower-link", [18, 19, 18, 20], downMbps: 96),
            Healthy("faster-link", [30, 31, 29, 32], downMbps: 100),
        ]);

        Assert.Equal("slightly-slower-link", selection.StrategyId);
    }

    /// <summary>A profile that gets through four times in five is not a profile that works.</summary>
    [Fact]
    public void AFastButUnstableCandidateIsEliminated()
    {
        var selection = Choose(
        [
            Healthy("flaky", [10, 11, 10], failures: 5),
            Healthy("steady", [30, 31, 29, 30]),
        ]);

        Assert.Equal("steady", selection.StrategyId);
        Assert.Contains(
            selection.Eliminated,
            rejection => rejection.StrategyId == "flaky" && rejection.Reason.Contains("Kararsız", StringComparison.Ordinal));
    }

    /// <summary>
    /// "One target answered" is not "the network is open". A candidate that never reached
    /// the gateway is not a candidate, whatever discord.com did.
    /// </summary>
    [Fact]
    public void ACandidateThatMissedARequiredTargetIsNotEligible()
    {
        var partial = Healthy("partial", [8, 9, 8, 9], reachAllRequired: false);
        var complete = Healthy("complete", [40, 41, 39, 42]);

        var selection = Choose([partial, complete]);

        Assert.Equal("complete", selection.StrategyId);
        Assert.Contains(
            selection.Eliminated,
            rejection => rejection.StrategyId == "partial"
                && rejection.Reason.Contains("gateway.discord.gg", StringComparison.Ordinal));
    }

    [Fact]
    public void TooFewSamplesIsNotAMeasurement()
    {
        var selection = Choose([Healthy("barely-tried", [12])], incumbent: "current");

        Assert.Equal("current", selection.StrategyId);
        Assert.False(selection.Changed);
        Assert.Equal(SelectionConfidence.Insufficient, selection.Confidence);
        Assert.Contains(
            selection.Eliminated,
            rejection => rejection.Reason.Contains("Yeterli örnek", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenNothingWasMeasuredTheCurrentProfileIsKept()
    {
        var selection = Choose([], incumbent: "current");

        Assert.Equal("current", selection.StrategyId);
        Assert.False(selection.Changed);
        Assert.Equal(SelectionConfidence.Insufficient, selection.Confidence);
    }

    [Fact]
    public void WhenNothingReachedTheTargetsTheCurrentProfileIsKept()
    {
        var selection = Choose(
        [
            Healthy("a", [10, 11, 10], reachAllRequired: false),
            Healthy("b", [12, 13, 12], reachAllRequired: false),
        ],
        incumbent: "current");

        Assert.Equal("current", selection.StrategyId);
        Assert.False(selection.Changed);
        Assert.Contains("korunuyor", selection.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The anti-thrash rule. Two profiles inside each other's noise must not take turns.
    /// </summary>
    [Fact]
    public void AnInsignificantImprovementDoesNotChangeTheProfile()
    {
        var selection = Choose(
        [
            Healthy("incumbent", [30, 31, 30, 31]),
            Healthy("challenger", [28, 29, 28, 29]),
        ],
        incumbent: "incumbent",
        incumbentSince: Now - TimeSpan.FromHours(3));

        Assert.Equal("incumbent", selection.StrategyId);
        Assert.False(selection.Changed);
        Assert.Contains("ölçüm belirsizliğinin içinde", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeaningfulImprovementDoesChangeTheProfile()
    {
        var selection = Choose(
        [
            Healthy("incumbent", [60, 61, 60, 61]),
            Healthy("challenger", [22, 23, 22, 23]),
        ],
        incumbent: "incumbent",
        incumbentSince: Now - TimeSpan.FromHours(3));

        Assert.Equal("challenger", selection.StrategyId);
        Assert.True(selection.Changed);
    }

    /// <summary>
    /// A working profile is left alone for a while, so a network whose two best candidates
    /// keep swapping places does not reconnect the user every sweep.
    /// </summary>
    [Fact]
    public void AProfileThatHasJustBeenInstalledIsLeftAlone()
    {
        var selection = Choose(
        [
            Healthy("incumbent", [60, 61, 60, 61]),
            Healthy("challenger", [22, 23, 22, 23]),
        ],
        incumbent: "incumbent",
        incumbentSince: Now - TimeSpan.FromMinutes(2));

        Assert.Equal("incumbent", selection.StrategyId);
        Assert.False(selection.Changed);
        Assert.Contains("dakika daha korunacak", selection.Reason, StringComparison.Ordinal);
    }

    /// <summary>But a profile that has stopped working is replaced immediately.</summary>
    [Fact]
    public void AnIncumbentThatNoLongerReachesTheTargetsIsReplacedWithoutWaiting()
    {
        var selection = Choose(
        [
            Healthy("incumbent", [10, 11, 10], reachAllRequired: false),
            Healthy("challenger", [40, 41, 39, 42]),
        ],
        incumbent: "incumbent",
        incumbentSince: Now - TimeSpan.FromSeconds(30));

        Assert.Equal("challenger", selection.StrategyId);
        Assert.True(selection.Changed);
    }

    /// <summary>
    /// With no speed measurement the result says so, in the rationale a user reads.
    /// </summary>
    [Fact]
    public void AChoiceMadeWithoutASpeedTestSaysSo()
    {
        var selection = Choose([Healthy("a", [20, 21, 20, 21]), Healthy("b", [50, 51, 50, 51])]);

        Assert.Equal(SelectionConfidence.LatencyOnly, selection.Confidence);
        Assert.Contains("hız testi yapılmadı", selection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A candidate nobody measured the speed of is not eliminated by the speed rule.
    /// Unknown is not the same as bad.
    /// </summary>
    [Fact]
    public void ACandidateWithNoThroughputMeasurementSurvivesTheSpeedFloor()
    {
        var selection = Choose(
        [
            Healthy("unmeasured", [18, 19, 18, 20]),
            Healthy("fast-link", [40, 41, 39, 42], downMbps: 100),
            Healthy("slow-link", [20, 21, 20, 21], downMbps: 40),
        ]);

        Assert.Equal("unmeasured", selection.StrategyId);
        Assert.DoesNotContain(selection.Eliminated, rejection => rejection.StrategyId == "unmeasured");
    }

    /// <summary>Every decision explains itself, and every rejection says why.</summary>
    [Fact]
    public void EveryOutcomeCarriesAReason()
    {
        var cases = new[]
        {
            Choose([]),
            Choose([Healthy("a", [12])]),
            Choose([Healthy("a", [20, 21, 20, 21]), Healthy("b", [50, 51, 50, 51])]),
            Choose([Healthy("a", [20, 21, 20, 21], downMbps: 90), Healthy("b", [22, 23, 22, 23], downMbps: 30)]),
        };

        foreach (var selection in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(selection.Reason));
            Assert.All(selection.Eliminated, rejection => Assert.False(string.IsNullOrWhiteSpace(rejection.Reason)));
        }
    }

    /// <summary>
    /// The thresholds are the app's starting points and have to be changeable as one
    /// number each, which is the only reason they are a parameter at all.
    /// </summary>
    [Fact]
    public void TheSpeedFloorIsATunableNumberRatherThanABuiltInComparison()
    {
        var measurements = new[]
        {
            Healthy("quick", [18, 19, 18, 20], downMbps: 80),
            Healthy("full-speed", [26, 27, 25, 28], downMbps: 100),
        };

        // At the default 0.95 floor the 80 Mb/s candidate is out.
        Assert.Equal("full-speed", Choose(measurements).StrategyId);

        // Relax the floor to 0.7 and it is back in, and wins on latency.
        var relaxed = SelectionThresholds.Default with { SpeedFloorRatio = 0.7 };
        Assert.Equal("quick", Choose(measurements, thresholds: relaxed).StrategyId);
    }

    [Fact]
    public void TheDefaultThresholdsAreTheOnesTheReportDocuments()
    {
        var defaults = SelectionThresholds.Default;

        Assert.Equal(3, defaults.MinimumSamplesPerCandidate);
        Assert.Equal(0.2, defaults.MaximumFailureRate);
        Assert.Equal(0.95, defaults.SpeedFloorRatio);
        Assert.Equal(3, defaults.MinimumImprovementMs);
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.MinimumDwell);
    }

    /// <summary>
    /// Jitter is the tie-break, because two profiles with the same median are not the same
    /// profile to a voice call.
    /// </summary>
    [Fact]
    public void EqualMediansAreSeparatedByJitter()
    {
        var steady = Healthy("steady", [30, 30, 30, 30, 30]);
        var jumpy = Healthy("jumpy", [10, 50, 30, 50, 10]);

        var selection = Choose([jumpy, steady]);

        Assert.Equal("steady", selection.StrategyId);
    }

    /// <summary>
    /// The measurement keeps the handshake and the connect apart, so nothing downstream can
    /// show a TLS handshake as a round trip time.
    /// </summary>
    [Fact]
    public void TheHandshakeTimeIsNeverTheConnectTime()
    {
        var measurement = Healthy("a", [20, 20, 20, 20]);

        Assert.Equal(20, measurement.MedianConnectMs!.Value, 1);
        Assert.Equal(60, measurement.MedianHandshakeMs!.Value, 1);
    }

    /// <summary>A failed handshake is a failure rate, not a packet loss figure.</summary>
    [Fact]
    public void FailuresAreCountedAsFailuresAndNeverAsPacketLoss()
    {
        var measurement = Healthy("a", [20, 20, 20], failures: 1);

        Assert.Equal(0.25, measurement.FailureRate, 3);

        // There is no loss field on a strategy measurement at all: a refused TLS handshake
        // is a filter acting on a connection, not a packet dropped on the wire.
        Assert.DoesNotContain(
            typeof(StrategyMeasurement).GetProperties(),
            property => property.Name.Contains("Loss", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Two points do not make a distribution, so jitter is null below three.</summary>
    [Fact]
    public void JitterIsNotReportedFromTwoSamples()
    {
        Assert.Null(Healthy("a", [20, 24]).JitterMs);
        Assert.NotNull(Healthy("a", [20, 24, 22]).JitterMs);
    }
}
