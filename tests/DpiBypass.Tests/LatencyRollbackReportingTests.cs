using System.Net;
using DpiBypass.Core.Network;
using DpiBypass.Core.Network.Latency;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// What the card is allowed to say after a candidate was rejected and taken back off.
/// </summary>
/// <remarks>
/// These cover the reported "about +5% ms, disabled" result. The run itself was right - the
/// candidate genuinely measured worse, it was rejected, and it was correctly rolled back -
/// but the reading taken while it was applied was carried into the result as the "after"
/// value, and the status view rendered that as the user's current idle ping. So the card
/// reported the increase that caused the rollback as the state the user had been left in,
/// directly under a headline saying the change had been undone.
/// </remarks>
public sealed class LatencyRollbackReportingTests
{
    private const double BaselineMedian = 48;

    /// <summary>The candidate's reading: five percent worse, which is what gets rejected.</summary>
    private const double WorseMedian = 50.4;

    /// <summary>
    /// What the link reads once the candidate is off again.
    /// </summary>
    /// <remarks>
    /// Deliberately neither the baseline nor the candidate's, so a fresh reading is
    /// distinguishable from either of the two numbers the old code might have copied
    /// forward.
    /// </remarks>
    private const double SettledMedian = 48.3;

    /// <summary>The settle wait, which is how these fixtures know a rollback has happened.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task ACandidateThatMeasuresWorseIsRejectedAndRolledBack()
    {
        using var run = Worsening();

        var result = await run.Optimizer.StartAsync(CancellationToken.None);

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Empty(result.AppliedChanges);
        Assert.False(result.RestoreFailed);

        // Nothing left on the adapter.
        Assert.Empty(run.Controller.Live);
        Assert.Contains(result.Verdicts, verdict => !verdict.Accepted);
    }

    [Fact]
    public async Task FivePercentWorseIsExactlyOnTheBoundaryAndReadsAsNoGain()
    {
        // Worth pinning, because it is the reported case and it is not an accident. The
        // median regression threshold is max(1 ms, 5% of the baseline) and the test is
        // strictly greater, so 48 ms -> 50.4 ms misses being called a regression by
        // nothing at all: 2.4 > 2.4 is false. The candidate is still rejected, for want of
        // a gain rather than for harm, and still taken back off.
        //
        // That is why the old card said "about +5%, disabled" rather than naming a
        // regression: the run's own conclusion was "no gain", and the number beside it was
        // the candidate's reading leaking through as the current ping.
        using var run = Worsening();

        var result = await run.Optimizer.StartAsync(CancellationToken.None);

        var verdict = Assert.Single(result.Verdicts);
        Assert.False(verdict.Accepted);
        Assert.Equal(LatencyOutcomeCause.MeasuredNoGain, verdict.Cause);
        Assert.Equal(-(WorseMedian - BaselineMedian), verdict.Delta.MedianMs, 3);

        // Rejected on this side of the boundary is still rejected, and still rolled back.
        Assert.Empty(run.Controller.Live);
        Assert.Empty(result.AppliedChanges);
    }

    [Fact]
    public async Task AClearlyHarmfulCandidateIsNamedAsARegression()
    {
        // Past the boundary, the same machinery calls it what it is, and the card shows the
        // rolled-back situation rather than the no-difference one.
        using var run = Worsening(worseMedian: 58);

        var result = await run.Optimizer.StartAsync(CancellationToken.None);

        Assert.Contains(result.Verdicts, verdict =>
            !verdict.Accepted && verdict.Cause == LatencyOutcomeCause.MeasuredRegression);

        var status = LatencyStatusView.From(modeEnabled: true, result);
        Assert.Equal(LatencySituation.RolledBack, status.Situation);

        // Still no after, and still a fresh reading rather than the harmful one.
        Assert.Null(status.IdleAfter);
        Assert.Equal(SettledMedian, status.Idle!.MedianRttMs, 3);
    }

    [Fact]
    public async Task TheRejectedCandidatesReadingIsNeverTheCurrentPing()
    {
        using var run = Worsening();

        var result = await run.Optimizer.StartAsync(CancellationToken.None);

        // Nothing was kept, so there is no "after" to show.
        Assert.Null(result.After);

        // What the connection reads now is a reading of its own, taken once the settings
        // were back and the link had settled.
        Assert.Equal(LatencyRemeasureState.Completed, result.Remeasure);
        Assert.NotNull(result.Current);
        Assert.Equal(SettledMedian, result.Current!.MedianRttMs, 3);
        Assert.Equal(LatencyMeasurementRole.PostRollback, result.Current.Role);

        // The two numbers that used to reach the card in its place.
        Assert.NotEqual(WorseMedian, result.Current.MedianRttMs, 3);
        Assert.NotEqual(BaselineMedian, result.Current.MedianRttMs, 3);
    }

    [Fact]
    public async Task TheCardShowsTheReMeasuredPingAndNoBeforeAfterPair()
    {
        using var run = Worsening();
        var result = await run.Optimizer.StartAsync(CancellationToken.None);

        var status = LatencyStatusView.From(modeEnabled: true, result);

        // A candidate rejected for want of a gain rather than for harm leaves the machine
        // as it was found, which is the no-difference situation and not the rolled-back
        // one. Either way the numbers below have to describe the machine as it is now.
        Assert.Equal(LatencySituation.NoDifference, status.Situation);

        // The idle ping is the post-rollback reading.
        Assert.NotNull(status.Idle);
        Assert.Equal(SettledMedian, status.Idle!.MedianRttMs, 3);

        // There is no after, so there is no pair to render as a change - which is what
        // produced "48 ms -> 50 ms" under a rolled-back headline.
        Assert.Null(status.IdleAfter);
        Assert.NotNull(status.IdleBefore);

        // And no gain is claimed.
        Assert.Equal(LatencyGainKind.None, status.GainKind);
    }

    [Fact]
    public async Task AFailedReMeasurementSaysSoRatherThanReusingANumber()
    {
        // The link stops answering once the candidate is taken back off, so the run rolls
        // back and then cannot measure.
        using var run = Worsening(reachableAfterRollback: false);

        var result = await run.Optimizer.StartAsync(CancellationToken.None);

        Assert.Equal(LatencyRemeasureState.Failed, result.Remeasure);
        Assert.Null(result.Current);
        Assert.Null(result.After);

        var status = LatencyStatusView.From(modeEnabled: true, result);
        Assert.Equal(LatencyRemeasureState.Failed, status.Remeasure);

        // No after value invented from the reading that is lying around.
        Assert.Null(status.IdleAfter);
    }

    [Fact]
    public async Task AVerifiedGainKeepsItsAfterBecauseItIsStillApplied()
    {
        // The other side of the same rule: when something genuinely was kept, the reading
        // taken with it applied is both the after value and the current one.
        var scenario = LatencyScenario.WithImprovement(gain: 6);

        var result = await scenario.Optimizer.StartAsync(CancellationToken.None);

        Assert.Equal(LatencyOptimizationStatus.Active, result.Status);
        Assert.NotEmpty(result.AppliedChanges);
        Assert.NotNull(result.After);
        Assert.Equal(LatencyMeasurementRole.Verification, result.After!.Role);
        Assert.Same(result.After, result.Current);
        Assert.Equal(LatencyRemeasureState.NotNeeded, result.Remeasure);

        var status = LatencyStatusView.From(modeEnabled: true, result);
        Assert.NotNull(status.IdleAfter);
        Assert.NotNull(status.IdleBefore);
        Assert.Equal(LatencyGainKind.Median, status.GainKind);
    }

    [Fact]
    public void AMeasurementTakenUnderACandidateNeverDescribesTheCurrentState()
    {
        // The rule the view relies on, stated directly.
        Assert.False(Role(LatencyMeasurementRole.Candidate).DescribesCurrentState);
        Assert.False(Role(LatencyMeasurementRole.Unspecified).DescribesCurrentState);

        Assert.True(Role(LatencyMeasurementRole.Baseline).DescribesCurrentState);
        Assert.True(Role(LatencyMeasurementRole.Verification).DescribesCurrentState);
        Assert.True(Role(LatencyMeasurementRole.PostRollback).DescribesCurrentState);
        Assert.True(Role(LatencyMeasurementRole.Reference).DescribesCurrentState);

        static LatencyMeasurement Role(LatencyMeasurementRole role)
            => Fake.Measurement(30) with { Role = role };
    }

    /// <summary>
    /// A run whose single candidate makes the median five percent worse.
    /// </summary>
    /// <remarks>
    /// Built here rather than through <see cref="LatencyScenario"/> because these tests
    /// need to know when the rollback happened. The settle wait is the signal: the
    /// optimizer awaits it once, on the post-rollback path, with a span nothing else uses.
    /// </remarks>
    private static Run Worsening(bool reachableAfterRollback = true, double worseMedian = WorseMedian)
    {
        var controller = new FakeController();
        var rolledBack = false;

        var probe = new FakeProbe(
            controller,
            (live, _) => live.Contains(Fake.DefaultKeyword)
                ? Fake.Measurement(worseMedian, jitter: 3.6, p95: worseMedian + 9)
                : Fake.Measurement(
                    rolledBack ? SettledMedian : BaselineMedian,
                    jitter: 3.4,
                    p95: (rolledBack ? SettledMedian : BaselineMedian) + 9))
        {
            Connectivity = new LatencyConnectivity(true, true),
        };

        var optimizer = new LatencyOptimizer(
            controller,
            reachableAfterRollback ? probe : new UnreachableOnceRolledBack(probe, () => rolledBack),
            new FakeSnapshotStore(),
            profiles: new FakeProfileStore(),
            options: new LatencyOptimizerOptions
            {
                MinimumCycles = 2,
                MaximumCycles = 3,
                RollbackSettleDelay = SettleDelay,
            },
            targets: new FakeTargetResolver(),
            environmentSampler: new FakeEnvironmentSampler(),
            resourceRestorers: [],

            // Settling pauses are real seconds on a real driver and nothing at all here.
            // The span identifies which wait this is, and the rollback wait is the one that
            // separates "measured under the candidate" from "measured after it went back".
            delay: (span, _) =>
            {
                if (span == SettleDelay)
                {
                    rolledBack = true;
                }

                return Task.CompletedTask;
            },
            captureRoute: Fake.NoRoute);

        return new Run(optimizer, controller);
    }

    private sealed record Run(LatencyOptimizer Optimizer, FakeController Controller) : IDisposable
    {
        public void Dispose() => Optimizer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>A link that stops answering once the settings have gone back.</summary>
    private sealed class UnreachableOnceRolledBack : ILatencyProbe
    {
        private readonly ILatencyProbe _inner;
        private readonly Func<bool> _rolledBack;

        public UnreachableOnceRolledBack(ILatencyProbe inner, Func<bool> rolledBack)
        {
            _inner = inner;
            _rolledBack = rolledBack;
        }

        public Task<LatencyMeasurement> MeasureAsync(
            NetworkFingerprint network,
            LatencyProbeRequest request,
            CancellationToken cancellationToken = default)
            => _inner.MeasureAsync(network, request, cancellationToken);

        public Task<LatencyConnectivity> CheckConnectivityAsync(
            NetworkFingerprint network,
            string remoteEndpoint,
            CancellationToken cancellationToken = default)
            => _rolledBack()
                ? Task.FromResult(new LatencyConnectivity(false, false))
                : _inner.CheckConnectivityAsync(network, remoteEndpoint, cancellationToken);
    }
}
