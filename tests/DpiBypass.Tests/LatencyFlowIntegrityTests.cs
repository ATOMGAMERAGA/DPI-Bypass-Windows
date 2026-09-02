using System.Net;
using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Which endpoint a run measures, and what it may substitute when that one is silent.
/// </summary>
public sealed class EndpointSelectionTests
{
    /// <summary>
    /// The general reference list falls through to whichever address answers.
    /// </summary>
    /// <remarks>
    /// Any of those addresses is a statement about the route, which is what the general
    /// target means, so moving down the list changes nothing about what the number is.
    /// </remarks>
    [Fact]
    public async Task TheGeneralReferenceFallsThroughToAnAddressThatAnswers()
    {
        var silent = LatencyEndpoint.Icmp(IPAddress.Parse("1.1.1.1"), "birinci");
        var answering = LatencyEndpoint.Icmp(IPAddress.Parse("9.9.9.9"), "ikinci");

        var choice = await LatencyEndpointSelector.ChooseAsync(
            new LatencyTargetResolution { Endpoints = [silent, answering] },
            (endpoint, _) => Task.FromResult(endpoint.Address.Equals(answering.Address)
                ? Fake.Measurement(24, endpoint: "9.9.9.9")
                : Unreachable("1.1.1.1")));

        Assert.Equal(answering.Address, choice.Endpoint.Address);
        Assert.True(choice.Responded);
        Assert.Null(choice.Notice);
    }

    /// <summary>
    /// A game server that does not answer is not quietly replaced by another machine.
    /// </summary>
    /// <remarks>
    /// The number would be perfectly real and would be about somebody else's server. A
    /// user who asked for their own game's ping has to be told it could not be measured.
    /// </remarks>
    [Fact]
    public async Task AUserTargetThatDoesNotAnswerIsNotReplacedByAnotherServer()
    {
        var game = new LatencyEndpoint
        {
            Address = IPAddress.Parse("203.0.113.10"),
            Protocol = LatencyProtocol.Tcp,
            Port = 25565,
            Kind = LatencyTargetKind.Custom,
            Label = "oyun sunucum",
        };

        var reference = LatencyEndpoint.Icmp(IPAddress.Parse("1.1.1.1"), "genel referans");

        var choice = await LatencyEndpointSelector.ChooseAsync(
            new LatencyTargetResolution { Endpoints = [game, reference] },
            (endpoint, _) => Task.FromResult(Unreachable(endpoint.Address.ToString())));

        Assert.Equal(game.Address, choice.Endpoint.Address);
        Assert.False(choice.Responded);
        Assert.NotNull(choice.Notice);
        Assert.Contains("oyun sunucum", choice.Notice!, StringComparison.Ordinal);
    }

    /// <summary>Every address of one user target is still a valid alternative.</summary>
    [Fact]
    public async Task TheAddressesOfOneUserTargetAreStillAlternativesForEachOther()
    {
        var first = new LatencyEndpoint
        {
            Address = IPAddress.Parse("203.0.113.10"),
            Protocol = LatencyProtocol.Tcp,
            Port = 25565,
            Kind = LatencyTargetKind.Custom,
            Label = "oyun sunucum",
        };

        var second = first with { Address = IPAddress.Parse("203.0.113.11") };

        var choice = await LatencyEndpointSelector.ChooseAsync(
            new LatencyTargetResolution { Endpoints = [first, second] },
            (endpoint, _) => Task.FromResult(endpoint.Address.Equals(second.Address)
                ? Fake.Measurement(40, endpoint: "203.0.113.11")
                : Unreachable("203.0.113.10")));

        Assert.Equal(second.Address, choice.Endpoint.Address);
        Assert.Null(choice.Notice);
    }

    private static LatencyMeasurement Unreachable(string endpoint)
        => LatencyMeasurement.Create(endpoint, "ICMP", [], 9, [], 3);
}

/// <summary>What the deep test does when a precondition it needs is not met.</summary>
public sealed class LoadedLaneCompletionTests
{
    /// <summary>
    /// A link that never goes quiet stops the run rather than producing a baseline.
    /// </summary>
    /// <remarks>
    /// Every queueing number in the run is a loaded window minus this baseline. Measuring
    /// the baseline over somebody's download counts the queue into both halves and
    /// reports the difference - near zero - as "no queueing found".
    /// </remarks>
    [Fact]
    public async Task ALinkThatNeverGoesQuietEndsAsAnIncompleteMeasurement()
    {
        var lane = new LoadedLatencyLane(
            probe: FakeProbe.Flat(new FakeController()),
            targets: new FakeTargetResolver(),
            load: new FakeLoadExperiment { QuietLink = false },
            qos: new FakeQosController(),
            snapshots: new FakeSnapshotStore(),
            capture: () => Fake.Network("busy"),
            log: _ => { },
            applications: new FakeApplicationResolver());

        var result = await lane.RunAsync(new LoadedLaneRequest());

        // Not "no gain": nothing was measured, and the user is told what is missing.
        Assert.Equal(LatencyOptimizationStatus.NeedsDeepTest, result.Status);
        Assert.NotEqual(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Contains("boşalmadı", result.StatusLine, StringComparison.Ordinal);

        var lane_ = Assert.Single(result.Lanes, entry => entry.Lane == LatencyLane.LoadedLatency);
        Assert.Equal(LatencyLaneState.Incomplete, lane_.State);
    }

    /// <summary>
    /// A rollback that could not remove its policies never reports a tidy cancellation.
    /// </summary>
    /// <remarks>
    /// The sweep used to swallow the failure and return zero, which is the same value it
    /// returns when there was nothing to remove - so a machine left with a rate limit on
    /// its uplink printed "nothing was changed".
    /// </remarks>
    [Fact]
    public async Task AFailedPolicySweepIsNotReportedAsACleanCancellation()
    {
        var lane = new LoadedLatencyLane(
            probe: FakeProbe.Flat(new FakeController()),
            targets: new FakeTargetResolver(),
            load: new FakeLoadExperiment(),
            qos: new FakeQosController { RefuseRemoveAllReason = "ilke deposu yanıt vermedi" },
            snapshots: new FakeSnapshotStore(),
            capture: () => Fake.Network("sweep"),
            log: _ => { },
            applications: new FakeApplicationResolver());

        var sweep = await lane.SweepOwnedPoliciesAsync();

        Assert.False(sweep.Succeeded);
        Assert.False(sweep.IsClean);
        Assert.Equal("ilke deposu yanıt vermedi", sweep.Failure);
        Assert.Contains("kaldırılamadı", sweep.Describe(), StringComparison.Ordinal);

        // And the two clean outcomes still read as different from each other.
        var nothing = LoadedLatencyLane.QosSweepResult.Nothing;
        Assert.True(nothing.IsClean);
        Assert.Contains("yoktu", nothing.Describe(), StringComparison.Ordinal);
    }
}

/// <summary>
/// One latency operation at a time, and no result filed under the wrong target.
/// </summary>
public sealed class LatencyRunLifecycleTests
{
    /// <summary>
    /// Turning the mode on takes the same gate every other latency operation takes.
    /// </summary>
    /// <remarks>
    /// It used to take none, so a quick test running at the same moment measured a link
    /// whose adapter settings were being rewritten underneath it, and the two runs'
    /// snapshots could undo each other.
    /// </remarks>
    [Fact]
    public async Task TurningTheModeOnSerialisesAgainstTheDeepTest()
    {
        using var directory = new TempDirectory("dpibypass-latency-gate");

        // One counter both lanes touch. The optimizer raises it while it is writing to
        // the adapter, and the deep test raises it while it is measuring; a reading above
        // one means the two ran at the same time on the same link.
        var active = 0;
        var overlaps = 0;

        void Enter()
        {
            if (Interlocked.Increment(ref active) > 1)
            {
                Interlocked.Increment(ref overlaps);
            }
        }

        void Leave() => Interlocked.Decrement(ref active);

        var controller = new FakeController { ApplyDelay = TimeSpan.FromMilliseconds(25) };
        var watched = new WatchedController(controller, Enter, Leave);

        var optimizer = new LatencyOptimizer(
            watched,
            FakeProbe.Flat(controller),
            new FakeSnapshotStore(),
            profiles: new FakeProfileStore(),
            targets: new FakeTargetResolver(),
            environmentSampler: new FakeEnvironmentSampler(),
            resourceRestorers: [],
            delay: (_, _) => Task.CompletedTask);

        var load = new FakeLoadExperiment();
        load.OnRun = _ =>
        {
            Enter();
            Thread.Sleep(25);
            Leave();
        };

        await using var service = new ProtectionService(
            new ConfigStore(directory.File("settings.json"), directory.File("networks.json")),
            new LearnedDomainStore(directory.File("learned.json")),
            optimizer,
            loadedLatency: new LoadedLatencyLane(
                probe: FakeProbe.Flat(controller),
                targets: new FakeTargetResolver(),
                load: load,
                qos: new FakeQosController(),
                snapshots: new FakeSnapshotStore(),
                capture: () => Fake.Network("gate"),
                log: _ => { },
                applications: new FakeApplicationResolver()),
            flowObserver: new FakeFlowObserver());

        var modeOn = service.SetLowLatencyModeAsync(true);
        var deepTest = service.RunLoadedLatencyTestAsync();

        await Task.WhenAll(modeOn, deepTest);

        Assert.Equal(0, overlaps);
        Assert.True(controller.Applied.Count > 0, "the optimizer never reached the adapter");
        Assert.True(load.Calls > 0, "the deep test never ran");
    }

    /// <summary>
    /// A result measured against one target is not published under another.
    /// </summary>
    /// <remarks>
    /// The measurement is real; the heading it would appear under is not. The run is
    /// still returned to whoever awaited it, which is the only caller that knows what it
    /// asked for.
    /// </remarks>
    [Fact]
    public async Task AResultForTheOldTargetIsNotPublishedUnderTheNewOne()
    {
        using var directory = new TempDirectory("dpibypass-latency-stale");
        var controller = new FakeController();
        var gate = new TaskCompletionSource();

        var optimizer = new LatencyOptimizer(
            controller,
            new GatedProbe(gate, FakeProbe.Flat(controller)),
            new FakeSnapshotStore(),
            profiles: new FakeProfileStore(),
            targets: new FakeTargetResolver(),
            environmentSampler: new FakeEnvironmentSampler(),
            resourceRestorers: [],
            delay: (_, _) => Task.CompletedTask);

        await using var service = new ProtectionService(
            new ConfigStore(directory.File("settings.json"), directory.File("networks.json")),
            new LearnedDomainStore(directory.File("learned.json")),
            optimizer,
            flowObserver: new FakeFlowObserver());

        var run = service.TestLatencyAsync();

        // The user picks a different target while the first run is still measuring.
        service.SetLatencyPreferences(service.Settings.Latency with
        {
            TargetKind = LatencyTargetKind.Custom,
            TargetHost = "example.invalid",
        });

        gate.SetResult();
        var result = await run;

        // The caller still gets what it awaited...
        Assert.NotNull(result);

        // ...but the card is not showing it under the new target's name.
        Assert.NotSame(result, service.LatencyResult);
    }

    /// <summary>
    /// Wraps a controller so a test can see exactly when a write to the adapter is in
    /// flight, which is the window the deep test must not measure inside.
    /// </summary>
    private sealed class WatchedController : ILatencyAdapterController
    {
        private readonly ILatencyAdapterController _inner;
        private readonly Action _enter;
        private readonly Action _leave;

        public WatchedController(ILatencyAdapterController inner, Action enter, Action leave)
        {
            _inner = inner;
            _enter = enter;
            _leave = leave;
        }

        public Task<AdapterLatencyCapability?> DetectAsync(
            NetworkFingerprint network,
            CancellationToken cancellationToken = default)
            => _inner.DetectAsync(network, cancellationToken);

        public Task<AdapterOperationalState> ReadOperationalStateAsync(
            string adapterId,
            CancellationToken cancellationToken = default)
            => _inner.ReadOperationalStateAsync(adapterId, cancellationToken);

        public async Task<LatencyApplyResult> ApplyAsync(
            AdapterLatencyCapability adapter,
            LatencyOptimizationCandidate candidate,
            AdapterRestartPolicy restart,
            CancellationToken cancellationToken = default)
        {
            _enter();

            try
            {
                return await _inner.ApplyAsync(adapter, candidate, restart, cancellationToken);
            }
            finally
            {
                _leave();
            }
        }

        public async Task<LatencyRestoreOutcome> RestoreAsync(
            LatencySettingSnapshot setting,
            CancellationToken cancellationToken = default)
        {
            _enter();

            try
            {
                return await _inner.RestoreAsync(setting, cancellationToken);
            }
            finally
            {
                _leave();
            }
        }
    }

    /// <summary>A probe that waits for a gate before its first measurement.</summary>
    private sealed class GatedProbe : ILatencyProbe
    {
        private readonly TaskCompletionSource _gate;
        private readonly ILatencyProbe _inner;
        private bool _waited;

        public GatedProbe(TaskCompletionSource gate, ILatencyProbe inner)
        {
            _gate = gate;
            _inner = inner;
        }

        public async Task<LatencyMeasurement> MeasureAsync(
            NetworkFingerprint network,
            LatencyProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!_waited)
            {
                _waited = true;
                await _gate.Task.WaitAsync(cancellationToken);
            }

            return await _inner.MeasureAsync(network, request, cancellationToken);
        }

        public Task<LatencyConnectivity> CheckConnectivityAsync(
            NetworkFingerprint network,
            string remoteEndpoint,
            CancellationToken cancellationToken = default)
            => _inner.CheckConnectivityAsync(network, remoteEndpoint, cancellationToken);
    }
}

/// <summary>
/// The card cannot start a measurement against a target the screen is not showing.
/// </summary>
/// <remarks>
/// Checked in the view model's source because loading it here would drag WPF into the
/// test project. What is being pinned is that the commands are gated on the target being
/// valid, not only on the card being idle - which is the difference between the error
/// message being advisory and it being enforced.
/// </remarks>
public sealed class LatencyTargetGateTests
{
    [Fact]
    public void EveryMeasuringCommandIsGatedOnTheTargetBeingValid()
    {
        var viewModel = File.ReadAllText(RepoFiles.MainViewModel);

        foreach (var command in new[]
        {
            "LatencyTestCommand",
            "LatencyDeepTestCommand",
            "LatencyRetestCommand",
            "LatencyPrimaryCommand",
        })
        {
            Assert.Contains($"{command} = new AsyncRelayCommand(", viewModel, StringComparison.Ordinal);
        }

        // The gate itself, and the fact that it is what the commands use.
        Assert.Contains(
            "private bool CanRunLatency() => !_isLatencyBusy && _isLatencyTargetValid;",
            viewModel,
            StringComparison.Ordinal);

        Assert.Equal(4, CountOccurrences(viewModel, ", CanRunLatency)"));

        // An invalid target marks itself invalid rather than only writing an error and
        // leaving the previous target live behind the buttons.
        Assert.Contains("InvalidTarget(", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsLatencyTargetValid = false;", viewModel, StringComparison.Ordinal);
    }

    /// <summary>
    /// The custom target is validated as it is typed, so Enter cannot beat the check.
    /// </summary>
    /// <remarks>
    /// With <c>LostFocus</c> the value only reached the view model when focus moved, so
    /// typing a bad address and pressing Enter ran the command while the model still held
    /// the previous target.
    /// </remarks>
    [Fact]
    public void TheCustomTargetIsValidatedAsItIsTyped()
    {
        var markup = File.ReadAllText(RepoFiles.MainWindowXaml);

        Assert.Contains(
            "{Binding LatencyCustomTarget, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
            markup,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
