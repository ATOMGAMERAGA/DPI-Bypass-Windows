using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.Network;
using DpiBypass.Core.Network.Latency;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// What the one switch has to do, including when it is flicked faster than a run finishes.
/// </summary>
/// <remarks>
/// The card is a single control now, so everything that used to be spread across a mode
/// picker, a test button and an apply step happens behind this one property. That makes its
/// edges worth pinning: off has to stop the run rather than wait for it, a run that was
/// abandoned must not write its result over a newer one, and none of it may touch the
/// bypass connection.
/// </remarks>
public sealed class LatencyToggleTests
{
    [Fact]
    public async Task TurningItOffStopsTheRunInsteadOfQueueingBehindIt()
    {
        // A paired benchmark takes minutes. Before this, turning the switch off waited for
        // all of them before putting anything back, which reads as the switch doing
        // nothing. The gate is held by the run in flight, so if off did not cancel first,
        // this test would deadlock on the probe that never returns.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new FakeController();
        var probe = new BlockingProbe(controller, started);

        using var directory = new TempDirectory();
        await using var service = NewService(directory, controller, probe);

        var turningOn = service.SetLowLatencyModeAsync(enabled: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Off, while the benchmark is still measuring.
        var turningOff = await service
            .SetLowLatencyModeAsync(enabled: false)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(service.Settings.LowLatencyMode);
        Assert.Empty(turningOff.AppliedChanges);

        // The abandoned run also put back everything it had applied.
        await turningOn.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(controller.Live);
    }

    [Fact]
    public async Task AnAbandonedRunDoesNotWriteItsResultOverTheNewerOne()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new FakeController();
        var probe = new BlockingProbe(controller, started);

        using var directory = new TempDirectory();
        await using var service = NewService(directory, controller, probe);

        var turningOn = service.SetLowLatencyModeAsync(enabled: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await service.SetLowLatencyModeAsync(enabled: false).WaitAsync(TimeSpan.FromSeconds(10));
        var afterOff = service.LatencyResult;

        // Let the cancelled run finish unwinding, then check it did not publish.
        await turningOn.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(afterOff, service.LatencyResult);
        Assert.False(service.Settings.LowLatencyMode);
    }

    [Fact]
    public async Task TheSwitchAndAnAppliedImprovementAreTwoDifferentStates()
    {
        // "On" is what the user asked for. Whether anything was applied is what the
        // measurements found. A run that found nothing leaves the switch on and says so,
        // rather than turning itself off or implying a gain nobody measured.
        using var directory = new TempDirectory();
        var controller = new FakeController();
        await using var service = NewService(directory, controller, FakeProbe.Flat(controller));

        await service.SetLowLatencyModeAsync(enabled: true);

        Assert.True(service.Settings.LowLatencyMode);
        Assert.Empty(service.LatencyResult.AppliedChanges);

        var status = service.LatencyStatus;
        Assert.True(status.ModeEnabled);
        Assert.Empty(status.Applied);

        var card = LatencyHeadline.From(status, DateTimeOffset.UtcNow);
        Assert.False(card.ShowsReduction);
        Assert.Equal(LatencyHeadline.NotMeasured, card.Gain);
        Assert.Contains("korundu", card.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeitherDirectionTouchesTheBypassConnection()
    {
        // The latency subsystem has no WinDivert handle by construction, and this pins the
        // consequence: flicking the switch either way must not start or stop protection.
        using var directory = new TempDirectory();
        var controller = new FakeController();
        await using var service = NewService(directory, controller, FakeProbe.Flat(controller));

        var stateBefore = service.State;

        await service.SetLowLatencyModeAsync(enabled: true);
        Assert.Equal(stateBefore, service.State);

        await service.SetLowLatencyModeAsync(enabled: false);
        Assert.Equal(stateBefore, service.State);
    }

    private static ProtectionService NewService(TempDirectory directory, FakeController controller, ILatencyProbe probe)
        => new(
            new ConfigStore(directory.File("settings.json"), directory.File("networks.json")),
            new LearnedDomainStore(directory.File("learned.json")),
            new LatencyOptimizer(
                controller,
                probe,
                new FakeSnapshotStore(),
                profiles: new FakeProfileStore(),
                options: new LatencyOptimizerOptions { MinimumCycles = 2, MaximumCycles = 3 },
                targets: new FakeTargetResolver(),
                environmentSampler: new FakeEnvironmentSampler(),
                resourceRestorers: [],
                delay: (_, _) => Task.CompletedTask,
                captureRoute: Fake.NoRoute),
            flowObserver: new FakeFlowObserver());

    /// <summary>
    /// A probe that blocks on its second measurement until the run is cancelled.
    /// </summary>
    /// <remarks>
    /// The first one lets the baseline through so the run reaches the candidate stage and
    /// has something applied to put back; the rest hold until the token is cancelled, which
    /// is how a real benchmark behaves from the switch's point of view.
    /// </remarks>
    private sealed class BlockingProbe : ILatencyProbe
    {
        private readonly FakeController _controller;
        private readonly TaskCompletionSource _reachedTheSlowPart;
        private int _measurements;

        public BlockingProbe(FakeController controller, TaskCompletionSource reachedTheSlowPart)
        {
            _controller = controller;
            _reachedTheSlowPart = reachedTheSlowPart;
        }

        public async Task<LatencyMeasurement> MeasureAsync(
            NetworkFingerprint network,
            LatencyProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.Increment(ref _measurements) > 1)
            {
                _reachedTheSlowPart.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return Fake.Measurement(_controller.Live.Contains(Fake.DefaultKeyword) ? 30 : 32);
        }

        public Task<LatencyConnectivity> CheckConnectivityAsync(
            NetworkFingerprint network,
            string remoteEndpoint,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LatencyConnectivity(true, true));
    }
}
