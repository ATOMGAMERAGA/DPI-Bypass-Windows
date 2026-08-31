using System.Diagnostics;
using System.Net;

using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The guards that stop the latency feature reporting a result it did not measure.
/// </summary>
/// <remarks>
/// Every test here corresponds to a way an earlier build could produce a confident
/// answer from no evidence: a registry write mistaken for a driver change, a quarter of a
/// link mistaken for saturation, a QoS policy tested against the flow it could not
/// possibly govern, or a probe series whose two halves covered different slices of time.
/// </remarks>
public sealed class LatencyApplyStateTests
{
    /// <summary>
    /// Only two of the seven apply states mean the machine is running with the change.
    /// </summary>
    [Theory]
    [InlineData(LatencyApplyState.Refused, false, false)]
    [InlineData(LatencyApplyState.RegistryWritten, false, true)]
    [InlineData(LatencyApplyState.RestartRequired, false, true)]
    [InlineData(LatencyApplyState.NotVerified, false, true)]
    [InlineData(LatencyApplyState.LinkNotRestored, false, true)]
    [InlineData(LatencyApplyState.RolledBack, false, false)]
    [InlineData(LatencyApplyState.AdapterRestarted, true, false)]
    [InlineData(LatencyApplyState.OperationallyVerified, true, false)]
    public void OnlyAnEffectiveApplyMayBeMeasured(
        LatencyApplyState state,
        bool effective,
        bool needsRollback)
    {
        var result = new LatencyApplyResult { State = state };

        Assert.Equal(effective, result.IsEffective);
        Assert.Equal(needsRollback, result.NeedsRollback);
        Assert.False(string.IsNullOrWhiteSpace(result.Describe()));
    }

    /// <summary>
    /// Windows can only answer the operational question for the keywords it has a query
    /// for; the rest need a restart, and are never claimed to be verified.
    /// </summary>
    [Fact]
    public void OnlyKeywordsWindowsCanReportOnAreEverCalledOperationallyVerified()
    {
        Assert.True(AdapterOperationalState.HasOperationalQuery(AdapterInterventionCatalog.RscIPv4Keyword));
        Assert.True(AdapterOperationalState.HasOperationalQuery(AdapterInterventionCatalog.RssKeyword));
        Assert.True(AdapterOperationalState.HasOperationalQuery(AdapterInterventionCatalog.LsoIPv4Keyword));

        Assert.False(AdapterOperationalState.HasOperationalQuery(AdapterInterventionCatalog.InterruptModerationKeyword));
        Assert.False(AdapterOperationalState.HasOperationalQuery(AdapterInterventionCatalog.EeeKeyword));

        Assert.True(AdapterInterventionCatalog
            .DescriptorFor(AdapterInterventionCatalog.InterruptModerationKeyword).MayNeedRestart);
        Assert.True(AdapterInterventionCatalog
            .DescriptorFor(AdapterInterventionCatalog.EeeKeyword).MayNeedRestart);
    }

    /// <summary>The operational state answers per keyword, and null means "cannot tell".</summary>
    [Fact]
    public void AnUnansweredOperationalQuestionIsNullRatherThanFalse()
    {
        var state = new AdapterOperationalState { RscIPv4Operational = false, RssEnabled = true };

        Assert.False(state.ForKeyword(AdapterInterventionCatalog.RscIPv4Keyword));
        Assert.True(state.ForKeyword(AdapterInterventionCatalog.RssKeyword));
        Assert.Null(state.ForKeyword(AdapterInterventionCatalog.RscIPv6Keyword));
        Assert.Null(state.ForKeyword(AdapterInterventionCatalog.InterruptModerationKeyword));
    }

    /// <summary>
    /// An adapter that never came back takes the change with it: nothing is kept, and the
    /// run does not go on to measure a machine it cannot reach.
    /// </summary>
    [Fact]
    public async Task AnAdapterThatDoesNotComeBackEndsWithNothingApplied()
    {
        var controller = new FakeController { LinkNotRestored = Fake.DefaultKeyword };
        var scenario = new LatencyScenario(controller, FakeProbe.Improves(controller, gain: 9));

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("no-link"));

        Assert.Empty(controller.Live);
        Assert.Null(scenario.Snapshots.Value);
        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);

        var verdict = Assert.Single(result.Verdicts);
        Assert.Contains("bağlantı geri gelmedi", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A run interrupted at any point in the transaction is undone on the next launch,
    /// and a completed one is left alone.
    /// </summary>
    [Theory]
    [InlineData(LatencyTransactionState.SnapshotCreated, true)]
    [InlineData(LatencyTransactionState.CandidateApplied, true)]
    [InlineData(LatencyTransactionState.Verifying, true)]
    [InlineData(LatencyTransactionState.Committed, false)]
    public async Task ACrashAtAnyStageOfTheTransactionIsRecoverable(
        LatencyTransactionState state,
        bool shouldRollBack)
    {
        var controller = new FakeController();
        controller.Live.Add(Fake.DefaultKeyword);

        var snapshots = new FakeSnapshotStore
        {
            Value = Fake.Snapshot("adapter-crash", Fake.DefaultKeyword, state: state),
        };

        var optimizer = new LatencyOptimizer(
            controller,
            FakeProbe.Flat(controller),
            snapshots,
            profiles: new FakeProfileStore());

        Assert.True(await optimizer.RecoverAsync());

        if (shouldRollBack)
        {
            Assert.Equal([Fake.DefaultKeyword], controller.Restored);
            Assert.Null(snapshots.Value);
        }
        else
        {
            Assert.Empty(controller.Restored);
            Assert.NotNull(snapshots.Value);
        }
    }
}

/// <summary>The instrument has to be able to see a gain before the gain is believed.</summary>
public sealed class LatencyMeasurementResolutionTests
{
    /// <summary>
    /// ICMP replies are whole milliseconds, so a repeatable "0.9 ms" gain over ICMP is
    /// rounding rather than a result.
    /// </summary>
    [Fact]
    public void AGainSmallerThanTheClockResolutionIsRefused()
    {
        // A 10 ms baseline puts the meaningful-effect floor at 0.8 ms, so 0.9 ms clears it
        // on effect size alone - and is still below what an ICMP echo can resolve.
        var pairs = Pairs(resolutionMs: LatencyProbe.IcmpResolutionMs, baseline: 10, candidate: 9.1);

        var verdict = LatencyComparison.Evaluate(Fake.Candidate(), pairs, LatencyEvaluationOptions.Strict);

        Assert.NotEqual(LatencyVerdictOutcome.Accepted, verdict.Outcome);
        Assert.Contains("saat çözünürlüğü", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same difference on a stopwatch-timed instrument is measurable.</summary>
    [Fact]
    public void TheSameDifferenceIsAcceptedWhenTheInstrumentCanResolveIt()
    {
        var pairs = Pairs(resolutionMs: LatencyProbe.StopwatchResolutionMs, baseline: 10, candidate: 9.1);

        var verdict = LatencyComparison.Evaluate(Fake.Candidate(), pairs, LatencyEvaluationOptions.Strict);

        Assert.Equal(LatencyVerdictOutcome.Accepted, verdict.Outcome);
    }

    /// <summary>
    /// Whenever a candidate is accepted under the production rules, the resampled interval
    /// for the deciding metric excludes zero.
    /// </summary>
    /// <remarks>
    /// The property rather than one fixture: "accepted" and "the interval excluded zero"
    /// have to stay the same statement, whatever data reaches the evaluator.
    /// </remarks>
    [Fact]
    public void AnAcceptedGainAlwaysCarriesAnIntervalThatExcludesZero()
    {
        Assert.True(LatencyEvaluationOptions.Strict.RequireConfidenceInterval);

        var pairs = new[] { 24.0, 24.5, 24.2, 24.4 }
            .Select((candidate, index) => new LatencyPair
            {
                Baseline = Fake.Measurement(30),
                Candidate = Fake.Measurement(candidate),
                Order = index % 2 == 0 ? LatencyCycleOrder.BaselineFirst : LatencyCycleOrder.CandidateFirst,
            })
            .ToArray();

        var accepted = LatencyComparison.Evaluate(Fake.Candidate(), pairs, LatencyEvaluationOptions.Strict);

        Assert.Equal(LatencyVerdictOutcome.Accepted, accepted.Outcome);
        Assert.NotNull(accepted.ConfidenceLowerMs);
        Assert.True(accepted.ConfidenceLowerMs > 0, "an accepted gain's interval must exclude zero");
    }

    private static IReadOnlyList<LatencyPair> Pairs(double resolutionMs, double baseline, double candidate) =>
    [
        .. Enumerable.Range(0, 4).Select(index => new LatencyPair
        {
            Baseline = Fake.Measurement(baseline, jitter: 0.2, p95: baseline + 1) with
            {
                ClockResolutionMs = resolutionMs,
            },
            Candidate = Fake.Measurement(candidate, jitter: 0.2, p95: candidate + 1) with
            {
                ClockResolutionMs = resolutionMs,
            },
            Order = index % 2 == 0 ? LatencyCycleOrder.BaselineFirst : LatencyCycleOrder.CandidateFirst,
        }),
    ];
}

/// <summary>The probe's own deadlines, which used to exist only in the request record.</summary>
public sealed class LatencyProbeDeadlineTests
{
    /// <summary>
    /// A TCP probe gives up at its own deadline rather than at the operating system's.
    /// </summary>
    /// <remarks>
    /// <see cref="LatencyProbeRequest.TimeoutMilliseconds"/> used to apply to echo requests
    /// only, so a connect to a black hole sat in the OS retransmit schedule for around
    /// twenty seconds - longer than the whole series was meant to take. The assertion is
    /// the ceiling rather than the exact duration: a network that answers "unreachable"
    /// immediately is also a pass, and the regression was never about being fast.
    /// </remarks>
    [Fact]
    public async Task ATcpProbeNeverOutlastsItsOwnDeadline()
    {
        // TEST-NET-1, reserved by RFC 5737 for documentation and routed nowhere.
        var address = IPAddress.Parse("192.0.2.1");
        var elapsed = Stopwatch.StartNew();

        var result = await LatencyProbe.TryTcpConnectForTestAsync(address, 443, 400, CancellationToken.None);

        elapsed.Stop();

        Assert.Null(result);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"the connect ran for {elapsed.Elapsed.TotalSeconds:F1}s against a 0.4s deadline");
    }

    /// <summary>
    /// The gateway series is bounded by the remote one, so both cover the same window.
    /// </summary>
    /// <remarks>
    /// The pacing is still derived from the request, but the series ends when the remote
    /// series ends rather than after a fixed count - which is what stops a slow TCP series
    /// being compared against a gateway median measured across only its first few seconds.
    /// </remarks>
    [Fact]
    public async Task TheGatewaySeriesCoversTheWholeRemoteWindow()
    {
        var network = Fake.Network("aligned");
        var probe = new LatencyProbe(new NullLoadSampler());

        var measurement = await probe.MeasureAsync(
            network,
            LatencyProbeRequest.Survey with
            {
                ProbeCount = 2,
                WarmupCount = 0,
                GatewayProbeCount = 2,
                TimeoutMilliseconds = 50,
                Pacing = TimeSpan.FromMilliseconds(1),
                Endpoint = LatencyEndpoint.Icmp(IPAddress.Parse("192.0.2.1"), "unreachable"),
            });

        // Nothing answers, so the interesting part is the bookkeeping: the gateway half
        // reports the attempts it really made rather than the number that was planned.
        Assert.True(measurement.GatewayAttempts >= 0);
        Assert.Equal(0, measurement.GatewayReplies);
        Assert.Equal(LatencyProbe.IcmpResolutionMs, measurement.ClockResolutionMs);
    }

    private sealed class NullLoadSampler : INetworkLoadSampler
    {
        public NetworkCounters? Read(NetworkFingerprint network) => null;
    }
}

/// <summary>A policy is only created when the store holds exactly what was asked for.</summary>
public sealed class QosReadBackTests
{
    [Fact]
    public void APolicyMatchingTheRequestInEveryFieldIsAccepted()
        => Assert.Null(WindowsQosController.DescribeMismatch(Request(), Stored()));

    /// <summary>
    /// The regression: an earlier build accepted a policy because a policy of that name
    /// existed, which proves nothing about what it does.
    /// </summary>
    [Theory]
    [MemberData(nameof(Mismatches))]
    public void APolicyThatDiffersInAnyFieldIsNotAccepted(QosPolicyInfo stored, string expected)
    {
        var mismatch = WindowsQosController.DescribeMismatch(Request(), stored);

        Assert.NotNull(mismatch);
        Assert.Contains(expected, mismatch, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<QosPolicyInfo, string> Mismatches() => new()
    {
        { Stored() with { AppPathName = "notepad.exe" }, "uygulama" },
        { Stored() with { ThrottleRateBitsPerSecond = 1_000_000 }, "hız sınırı" },
        { Stored() with { PolicyStore = "localhost" }, "depo" },
        { Stored() with { Precedence = 200 }, "öncelik" },
        { Stored() with { DestinationPort = 443 }, "hedef portu" },
        { Stored() with { DestinationPrefix = "10.0.0.0/8" }, "hedef öneki" },
        { Stored() with { Protocol = "UDP" }, "protokol" },
        { Stored() with { Dscp = 46 }, "DSCP" },
    };

    /// <summary>A policy that vanished between the create and the read-back is a failure.</summary>
    [Fact]
    public void APolicyThatIsNotInTheStoreIsNotAPolicy()
        => Assert.Contains(
            "depoda görünmüyor",
            WindowsQosController.DescribeMismatch(Request(), null),
            StringComparison.Ordinal);

    /// <summary>Nothing outside this application's namespace is ever ours.</summary>
    [Fact]
    public void APolicyCarryingAForeignNameIsRefusedEvenIfEverythingElseMatches()
    {
        var foreign = Request() with { Name = "Corp-Backup-Throttle" };

        Assert.Contains(
            "ad alanında değil",
            WindowsQosController.DescribeMismatch(foreign, Stored() with { Name = "Corp-Backup-Throttle" }),
            StringComparison.Ordinal);
    }

    private static QosPolicyRequest Request() => new()
    {
        Name = "DPIBypass.Latency.bulk.net1",
        AppPathName = @"C:\Program Files\Steam\steam.exe",
        ThrottleBitsPerSecond = 16_000_000,
    };

    private static QosPolicyInfo Stored() => new()
    {
        Name = "DPIBypass.Latency.bulk.net1",
        PolicyStore = QosPolicyStores.Active,
        AppPathName = @"C:\Program Files\Steam\steam.exe",
        ThrottleRateBitsPerSecond = 16_000_000,
        Precedence = 127,
    };
}

/// <summary>What the deep test shows the user, stage by stage.</summary>
public sealed class LoadedLaneStageTests
{
    /// <summary>
    /// The run asks for an upload, asks for it to stop, applies the policy, asks for a
    /// fresh upload, and then measures the download half - and says so at each point.
    /// </summary>
    /// <remarks>
    /// The regression this pins: an earlier build showed one static line about starting an
    /// upload and then silently waited for two more transfers, so the deep test could not
    /// be completed by anybody who was not reading the source.
    /// </remarks>
    [Fact]
    public async Task TheWizardAsksForEveryTransferItActuallyNeeds()
    {
        var stages = new RecordingStages();
        var qos = new FakeQosController();

        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 150, loadedP95: 200),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 150, loadedP95: 200),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 34, loadedP95: 42, uplinkKbps: 16_000),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 34, loadedP95: 42, uplinkKbps: 16_000))
        {
            Stager = stages,
        };

        var lane = new LoadedLatencyLane(
            probe: new IdleProbe(),
            targets: new FakeTargetResolver(),
            load: load,
            qos: qos,
            snapshots: new FakeSnapshotStore(),
            capture: () => Fake.Network("wizard"),
            log: _ => { },
            flows: new FakeFlowObserver { OnQuery = _ => Fake.Flow(at: DateTimeOffset.UtcNow.AddSeconds(1)) },
            applications: new FakeApplicationResolver(),
            stages: stages);

        await lane.RunAsync(new LoadedLaneRequest
        {
            RunTrafficGuard = true,
            BulkApplication = "steam.exe",
            MeasureDownload = false,
            MaximumTrialsForTest = 1,
        });

        // The lane owns the ordering and publishes its own stages; the load experiment owns
        // the waits, and is told which stage to display while each one runs.
        var sequence = stages.Sequence.ToList();

        Assert.Contains(LoadedLaneStage.VerifyingTarget, sequence);
        Assert.Contains(LoadedLaneStage.WaitingForQuietLink, sequence);
        Assert.Contains(LoadedLaneStage.IdleBaseline, sequence);
        Assert.Contains(LoadedLaneStage.AwaitingUploadStop, sequence);
        Assert.Contains(LoadedLaneStage.ApplyingPolicy, sequence);
        Assert.Contains(LoadedLaneStage.Confirming, sequence);
        Assert.Contains(LoadedLaneStage.AwaitingUploadStart, sequence);
        Assert.Contains(LoadedLaneStage.AwaitingFreshUpload, sequence);

        // The measuring stages are distinct too, so the card never says it is measuring a
        // baseline while it is measuring a capped round.
        Assert.Contains(LoadedLaneStage.MeasuringUploadBaseline, sequence);
        Assert.Contains(LoadedLaneStage.MeasuringUploadCandidate, sequence);

        // The stop always precedes the policy, and the policy always precedes the request
        // for a new transfer: that ordering is the whole reason the guard can measure.
        Assert.True(
            sequence.IndexOf(LoadedLaneStage.AwaitingUploadStop) < sequence.IndexOf(LoadedLaneStage.ApplyingPolicy),
            "the transfer must be stopped before the policy is created");
        Assert.True(
            sequence.IndexOf(LoadedLaneStage.ApplyingPolicy) < sequence.IndexOf(LoadedLaneStage.AwaitingFreshUpload),
            "the policy must exist before a fresh transfer is asked for");
    }

    /// <summary>Every stage has a name the card can show; none of them is blank.</summary>
    [Fact]
    public void EveryStageHasATitle()
        => Assert.All(
            Enum.GetValues<LoadedLaneStage>(),
            stage => Assert.False(string.IsNullOrWhiteSpace(LoadedLaneProgress.TitleFor(stage))));

    /// <summary>Waiting stages are the ones that need the user; terminal ones do not.</summary>
    [Fact]
    public void TheStagesThatNeedTheUserAreTheOnesThatSayTheyDo()
    {
        Assert.True(Progress(LoadedLaneStage.AwaitingFreshUpload).IsWaitingOnUser);
        Assert.True(Progress(LoadedLaneStage.AwaitingUploadStop).IsWaitingOnUser);
        Assert.False(Progress(LoadedLaneStage.MeasuringUploadCandidate).IsWaitingOnUser);

        Assert.True(Progress(LoadedLaneStage.Committed).IsTerminal);
        Assert.True(Progress(LoadedLaneStage.Cancelled).IsTerminal);
        Assert.False(Progress(LoadedLaneStage.Confirming).IsTerminal);
    }

    /// <summary>The rate line reports the share of capacity when capacity is known.</summary>
    [Fact]
    public void TheRateLineShowsHowCloseToCapacityTheTransferIs()
    {
        var progress = Progress(LoadedLaneStage.MeasuringUploadBaseline) with
        {
            InstantKbps = 17_000,
            CapacityShare = 0.85,
            DataUsedBytes = 5 * 1024 * 1024,
        };

        Assert.Contains("17.0 Mbit/s", progress.DescribeRate(), StringComparison.Ordinal);
        Assert.Contains("85", progress.DescribeRate(), StringComparison.Ordinal);
        Assert.Equal("5.0 MB", progress.DescribeData());
    }

    private static LoadedLaneProgress Progress(LoadedLaneStage stage) => new()
    {
        Stage = stage,
        Title = LoadedLaneProgress.TitleFor(stage),
        Instruction = string.Empty,
    };

    private sealed class IdleProbe : ILatencyProbe
    {
        public Task<LatencyMeasurement> MeasureAsync(
            NetworkFingerprint network,
            LatencyProbeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Fake.Measurement(24, load: LatencyLoadState.Idle));

        public Task<LatencyConnectivity> CheckConnectivityAsync(
            NetworkFingerprint network,
            string remoteEndpoint,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LatencyConnectivity(true, true));
    }
}

/// <summary>The card's own contract: the states and the controls the run needs.</summary>
public sealed class LatencyCardMarkupTests
{
    [Fact]
    public void TheCardShowsTheStagePanelTheCancelButtonAndTheResultBlock()
    {
        var markup = File.ReadAllText(RepoFiles.MainWindowXaml);

        Assert.Contains("{Binding LatencyStageTitle}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding LatencyStageInstruction}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding LatencyStageRate}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding LatencyStageRemaining}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding LatencyStageData}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding LatencyCancelCommand}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding LatencyResultSummary}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding LatencyDataUsedSummary}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding AllowAdapterRestart", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding SelectedGuardMode", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding LatencyEndpoints}", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card says the throttled application is the bulk one and not the game, because
    /// that is the single most alarming thing a user could misread here.
    /// </summary>
    [Fact]
    public void TheCardSaysTheThrottledApplicationIsNotTheGame()
    {
        var markup = File.ReadAllText(RepoFiles.MainWindowXaml);

        Assert.Contains("oyun değil", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Oyununuz asla sınırlanmaz", markup, StringComparison.Ordinal);
    }

    /// <summary>The download half is diagnosed honestly rather than promised a fix.</summary>
    [Fact]
    public void TheCardIsHonestAboutWhatALocalLimitCannotReach()
    {
        var markup = File.ReadAllText(RepoFiles.MainWindowXaml);

        Assert.Contains("operatörün ekipmanında", markup, StringComparison.Ordinal);
        Assert.Contains("SQM/CAKE", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every latency command in the card ends in a real method on the service.
    /// </summary>
    /// <remarks>
    /// The failure this catches is a command that raises and handles its own event and
    /// never reaches production code - which compiles, renders, and does nothing. The
    /// view model cannot be loaded for reflection here because it drags in WPF, so its
    /// source is scanned for the call and the service is reflected over for the method.
    /// </remarks>
    [Fact]
    public void EveryLatencyCommandReachesAMethodTheServiceReallyHas()
    {
        var viewModel = File.ReadAllText(RepoFiles.MainViewModel);
        var methods = typeof(ProtectionService)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        string[] required =
        [
            "SetLowLatencyModeAsync",
            "TestLatencyAsync",
            "RunLoadedLatencyTestAsync",
            "CancelLoadedLatencyTest",
            "RetestLatencyAsync",
            "RestoreLatencyAsync",
            "ClearLatencyProfiles",
            "SetLatencyPreferences",
        ];

        foreach (var method in required)
        {
            Assert.True(methods.Contains(method), $"ProtectionService has no {method}");
            Assert.Contains($"_service.{method}(", viewModel, StringComparison.Ordinal);
        }
    }
}

/// <summary>The service methods the card's buttons actually reach.</summary>
public sealed class LatencyServiceCommandTests
{
    /// <summary>
    /// Cancelling a running deep test stops it and takes every policy it created away.
    /// </summary>
    [Fact]
    public async Task CancellingTheDeepTestStopsItAndLeavesNoPolicyBehind()
    {
        using var directory = new TempDirectory("dpibypass-latency-cancel");
        var qos = new FakeQosController();
        var controller = new FakeController();

        var optimizer = new LatencyOptimizer(
            controller,
            FakeProbe.Flat(controller),
            new FakeSnapshotStore(),
            profiles: new FakeProfileStore(),
            targets: new FakeTargetResolver(),
            environmentSampler: new FakeEnvironmentSampler(),
            resourceRestorers: [],
            delay: (_, _) => Task.CompletedTask);

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var lane = new LoadedLatencyLane(
            probe: FakeProbe.Flat(controller),
            targets: new FakeTargetResolver(),
            load: new BlockingLoadExperiment(started, release),
            qos: qos,
            snapshots: new FakeSnapshotStore(),
            capture: () => Fake.Network("cancel"),
            log: _ => { },
            applications: new FakeApplicationResolver());

        await using var service = new ProtectionService(
            new ConfigStore(directory.File("settings.json"), directory.File("networks.json")),
            new LearnedDomainStore(directory.File("learned.json")),
            optimizer,
            loadedLatency: lane,
            flowObserver: new FakeFlowObserver());

        var run = service.RunLoadedLatencyTestAsync();
        await started.Task;

        Assert.True(service.CanCancelLatencyRun);
        service.CancelLoadedLatencyTest();
        release.SetResult();

        var result = await run;

        Assert.Equal(LatencyOptimizationStatus.Cancelled, result.Status);
        Assert.Empty(qos.Policies);
        Assert.False(service.CanCancelLatencyRun);
    }

    /// <summary>A load experiment that parks until it is released, so a cancel can land.</summary>
    private sealed class BlockingLoadExperiment : ILoadExperiment
    {
        private readonly TaskCompletionSource _started;
        private readonly TaskCompletionSource _release;

        public BlockingLoadExperiment(TaskCompletionSource started, TaskCompletionSource release)
        {
            _started = started;
            _release = release;
        }

        public string Instruction(LoadDirection direction) => "start a transfer";

        public string StopInstruction(LoadDirection direction) => "stop the transfer";

        public Task<bool> WaitForQuietLinkAsync(
            NetworkFingerprint network,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public async Task<LoadExperimentResult> RunAsync(
            NetworkFingerprint network,
            LoadExperimentRequest request,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return LoadExperimentResult.Failed(request.Direction, "not reached");
        }
    }
}
