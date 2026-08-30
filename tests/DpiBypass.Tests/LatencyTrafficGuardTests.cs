using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The loaded-latency lane: pacing this machine's own bulk sending, and only keeping the
/// policy that does so if the queueing actually falls.
/// </summary>
public sealed class TrafficGuardTests
{
    [Fact]
    public async Task AMeasuredReductionInQueueingKeepsThePolicy()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 34));

        var outcome = await new TrafficGuard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.Active, outcome.State.Status);
        Assert.Single(qos.Policies);
        Assert.StartsWith(WindowsQosController.PolicyNamePrefix, outcome.State.PolicyName, StringComparison.Ordinal);
        Assert.Equal(116, outcome.State.UploadQueueingBeforeMs);
        Assert.Equal(10, outcome.State.UploadQueueingAfterMs);
        Assert.NotNull(outcome.Resource);
        Assert.Equal(LatencyResourceKind.QosPolicy, outcome.Resource!.Kind);
    }

    /// <summary>
    /// A local rate limit that does not measurably empty the queue is a rate limit and
    /// nothing else, so it comes straight back off.
    /// </summary>
    [Fact]
    public async Task APolicyThatDoesNotReduceQueueingIsRemovedAgain()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 136));

        var outcome = await new TrafficGuard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.RolledBack, outcome.State.Status);
        Assert.Empty(qos.Policies);
        Assert.Null(outcome.Resource);
        Assert.Contains("anlamlı bir azalma değil", outcome.State.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APolicyThatAddsPacketLossIsRemovedHoweverGoodTheQueueingLooks()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 30, loadedLoss: 9));

        var outcome = await new TrafficGuard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.RolledBack, outcome.State.Status);
        Assert.Empty(qos.Policies);
        Assert.Contains("paket kaybı", outcome.State.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A limit that halves the transfer to save a few milliseconds is not a trade the
    /// user asked for, so the throughput floor rejects it.
    /// </summary>
    [Fact]
    public async Task APolicyThatCostsTooMuchThroughputIsRemoved()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140, uplinkKbps: 20_000),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 30, uplinkKbps: 4_000));

        var outcome = await new TrafficGuard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.RolledBack, outcome.State.Status);
        Assert.Contains("fazla düştü", outcome.State.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALinkWithNoQueueingIsLeftAlone()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 28));

        var outcome = await new TrafficGuard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.NoQueueing, outcome.State.Status);
        Assert.Empty(qos.Policies);
        Assert.Equal(0, load.Calls - 1);
    }

    /// <summary>
    /// Domain-managed machines commonly ship QoS through Group Policy. Adding a policy
    /// beside one is not something this can reason about safely.
    /// </summary>
    [Fact]
    public async Task AnExistingRateLimitingPolicyMakesTheGuardStandDownAndReportIt()
    {
        var qos = new FakeQosController
        {
            ForeignPolicies = ["Corp-Backup-Throttle"],
            CompetingPolicies = ["Corp-Backup-Throttle"],
        };

        var outcome = await new TrafficGuard(qos, new FakeLoadExperiment()).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.ConflictSkipped, outcome.State.Status);
        Assert.Empty(qos.Policies);
        Assert.Empty(qos.Removed);
        Assert.Contains("Corp-Backup-Throttle", outcome.State.Conflicts);
    }

    [Fact]
    public async Task WithoutWindowsQosNothingIsAttempted()
    {
        var qos = new FakeQosController { Available = false };

        var outcome = await new TrafficGuard(qos, new FakeLoadExperiment()).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.Unavailable, outcome.State.Status);
        Assert.Empty(qos.Policies);
    }

    [Fact]
    public async Task AnUnmeasurableLoadedWindowProducesNoVerdictAndNoPolicy()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            LoadExperimentResult.Failed(LoadDirection.Upload, "Beklenen süre içinde yeterli trafik görülmedi."));

        var outcome = await new TrafficGuard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.NotMeasured, outcome.State.Status);
        Assert.Empty(qos.Policies);
    }

    /// <summary>
    /// DSCP asks somebody else's router to do the queueing, and whether it did cannot be
    /// seen from here. Only a measured drop in loaded latency is ever called a gain.
    /// </summary>
    [Fact]
    public async Task MarkingAloneIsNeverCountedAsAGain()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140));

        var outcome = await new TrafficGuard(qos, load).RunAsync(Request());

        Assert.NotEqual(TrafficGuardStatus.Active, outcome.State.Status);
        Assert.Null(outcome.State.ImprovementMs is > 0 ? outcome.State.PolicyName : null);
        Assert.Empty(qos.Policies);
    }

    private static TrafficGuardRequest Request() => new()
    {
        Network = Fake.Network("guard"),
        Endpoint = LatencyEndpoint.Icmp(System.Net.IPAddress.Parse("1.1.1.1"), "test"),
        ProfileId = "profile1",
        BulkApplication = "steam.exe",
    };
}

/// <summary>The rule that this application only ever touches its own QoS policies.</summary>
public sealed class QosOwnershipTests
{
    [Fact]
    public void OnlyNamesInThisApplicationsNamespaceAreEverConsideredOurs()
    {
        Assert.True(WindowsQosController.IsOwnedName("DPIBypass.Latency.bulk.abc"));
        Assert.False(WindowsQosController.IsOwnedName("Corp-Backup-Throttle"));
        Assert.False(WindowsQosController.IsOwnedName("dpibypass.latency.bulk"));
        Assert.False(WindowsQosController.IsOwnedName(null));
    }

    [Fact]
    public void PolicyNamesAreBuiltFromSafeCharactersOnly()
    {
        var name = WindowsQosController.NameFor("net key; rm -rf /", "bulk*");

        Assert.StartsWith("DPIBypass.Latency.", name, StringComparison.Ordinal);
        Assert.DoesNotContain(';', name);
        Assert.DoesNotContain(' ', name);
        Assert.DoesNotContain('*', name);
    }

    [Fact]
    public async Task CreatingAPolicyOutsideTheNamespaceIsRefused()
    {
        var controller = new WindowsQosController();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.CreateAsync(new QosPolicyRequest { Name = "Corp-Backup-Throttle" }));
    }

    [Fact]
    public async Task RemovingAPolicyOutsideTheNamespaceIsRefused()
    {
        var controller = new WindowsQosController();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.RemoveAsync("Corp-Backup-Throttle", QosPolicyStores.Active));
    }

    /// <summary>
    /// Recovery reads a snapshot written by a build that may have crashed. A snapshot
    /// naming somebody else's policy is corrupt, not an instruction.
    /// </summary>
    [Fact]
    public async Task ASnapshotNamingAForeignPolicyIsIgnoredRatherThanObeyed()
    {
        var qos = new FakeQosController();
        var restorer = new QosResourceRestorer(qos);

        var outcome = await restorer.RestoreAsync(new LatencyResourceSnapshot
        {
            Kind = LatencyResourceKind.QosPolicy,
            InterventionId = "qos.bulk-upload-throttle",
            TargetId = "Corp-Backup-Throttle",
            Description = "not ours",
            CapturedAt = DateTimeOffset.UtcNow,
            OriginalState = { ["policyName"] = "Corp-Backup-Throttle" },
        });

        Assert.Equal(LatencyRestoreOutcome.MissingProperty, outcome);
        Assert.Empty(qos.Removed);
    }

    [Fact]
    public async Task APolicyThatExistedBeforeUsIsLeftWhereItWas()
    {
        var qos = new FakeQosController();
        var restorer = new QosResourceRestorer(qos);

        var outcome = await restorer.RestoreAsync(Resource("DPIBypass.Latency.bulk.x", existedBefore: true));

        Assert.Equal(LatencyRestoreOutcome.AlreadyOriginal, outcome);
        Assert.Empty(qos.Removed);
    }

    [Fact]
    public async Task APolicyWeCreatedIsDeleted()
    {
        var qos = new FakeQosController();
        await qos.CreateAsync(new QosPolicyRequest { Name = "DPIBypass.Latency.bulk.x" });

        var outcome = await new QosResourceRestorer(qos).RestoreAsync(Resource("DPIBypass.Latency.bulk.x"));

        Assert.Equal(LatencyRestoreOutcome.Restored, outcome);
        Assert.Empty(qos.Policies);
    }

    /// <summary>
    /// A machine that died mid-run comes back with a snapshot and no live objects. The
    /// file has to be enough to remove everything it describes.
    /// </summary>
    [Fact]
    public async Task CrashRecoveryRemovesThePolicyFromTheSnapshotAlone()
    {
        var qos = new FakeQosController();
        await qos.CreateAsync(new QosPolicyRequest { Name = "DPIBypass.Latency.bulk.crash" });

        var snapshots = new FakeSnapshotStore
        {
            Value = new LatencyOptimizationSnapshot
            {
                AdapterId = "adapter",
                AdapterName = "adapter",
                NetworkKey = "net",
                CreatedAt = DateTimeOffset.UtcNow,
                State = LatencyTransactionState.Verifying,
                Resources = [Resource("DPIBypass.Latency.bulk.crash")],
            },
        };

        var restorer = new LatencySnapshotRestorer(
            snapshots,
            new FakeController(),
            [new QosResourceRestorer(qos)]);

        Assert.True(await restorer.RestoreAllAsync());
        Assert.Empty(qos.Policies);
        Assert.Null(snapshots.Value);
    }

    /// <summary>
    /// One resource that cannot be undone must not strand the ones that already were:
    /// the file is rewritten with only what is still outstanding.
    /// </summary>
    [Fact]
    public async Task AResourceThatCannotBeUndoneDoesNotStrandTheAdapterSettings()
    {
        var snapshots = new FakeSnapshotStore
        {
            Value = Fake.Snapshot("adapter", "SelectiveSuspend") with
            {
                Resources =
                [
                    new LatencyResourceSnapshot
                    {
                        Kind = LatencyResourceKind.QosPolicy,
                        InterventionId = "qos.bulk-upload-throttle",
                        TargetId = "DPIBypass.Latency.bulk.stuck",
                        Description = "stuck",
                        CapturedAt = DateTimeOffset.UtcNow,
                        OriginalState = { ["policyName"] = "DPIBypass.Latency.bulk.stuck" },
                    },
                ],
            },
        };

        var controller = new FakeController();
        var restorer = new LatencySnapshotRestorer(snapshots, controller, [new ThrowingRestorer()]);

        Assert.False(await restorer.RestoreAllAsync());

        // The adapter came back, and only the resource is still recorded as outstanding.
        Assert.Contains("SelectiveSuspend", controller.Restored);
        Assert.NotNull(snapshots.Value);
        Assert.Empty(snapshots.Value!.Settings);
        Assert.Single(snapshots.Value.Resources);
    }

    [Fact]
    public async Task AResourceWithNoRestorerIsKeptRatherThanQuietlyForgotten()
    {
        var snapshots = new FakeSnapshotStore
        {
            Value = new LatencyOptimizationSnapshot
            {
                AdapterId = "adapter",
                AdapterName = "adapter",
                NetworkKey = "net",
                CreatedAt = DateTimeOffset.UtcNow,
                State = LatencyTransactionState.Verifying,
                Resources = [Resource("DPIBypass.Latency.bulk.orphan")],
            },
        };

        var restorer = new LatencySnapshotRestorer(snapshots, new FakeController(), []);

        Assert.False(await restorer.RestoreAllAsync());
        Assert.Single(snapshots.Value!.Resources);
    }

    private static LatencyResourceSnapshot Resource(string name, bool existedBefore = false) => new()
    {
        Kind = LatencyResourceKind.QosPolicy,
        InterventionId = "qos.bulk-upload-throttle",
        TargetId = name,
        TargetName = name,
        Description = "test policy",
        CapturedAt = DateTimeOffset.UtcNow,
        OriginalState =
        {
            ["policyName"] = name,
            ["policyStore"] = QosPolicyStores.Active,
            ["existedBefore"] = existedBefore ? "true" : "false",
        },
    };

    private sealed class ThrowingRestorer : ILatencyResourceRestorer
    {
        public bool CanRestore(LatencyResourceKind kind) => kind == LatencyResourceKind.QosPolicy;

        public Task<LatencyRestoreOutcome> RestoreAsync(
            LatencyResourceSnapshot snapshot,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("policy service unavailable");
    }
}

/// <summary>Measuring the loaded window from traffic the user starts, and only that.</summary>
public sealed class ObservedLoadExperimentTests
{
    [Fact]
    public async Task NothingIsSentAndTheLoadedWindowComesFromTheUsersOwnTraffic()
    {
        var controller = new FakeController();
        var probe = new ScriptedLoadProbe(
            Fake.Measurement(24, load: LatencyLoadState.Idle),
            Fake.Measurement(150, load: LatencyLoadState.UplinkLoaded));

        var sampler = new ScriptedLoadSampler(idleWindows: 1);
        var experiment = new ObservedLoadExperiment(probe, sampler, (_, _) => Task.CompletedTask);

        var result = await experiment.RunAsync(Fake.Network("loadtest"), Request());

        Assert.True(result.Succeeded);
        Assert.Equal(126, result.QueueingMs);
        Assert.Equal(0, probe.BytesSent);
    }

    [Fact]
    public async Task NoTrafficWithinTheWindowIsReportedAsNotMeasuredRatherThanEstimated()
    {
        var probe = new ScriptedLoadProbe(Fake.Measurement(24, load: LatencyLoadState.Idle));
        var sampler = new ScriptedLoadSampler(idleWindows: int.MaxValue);
        var experiment = new ObservedLoadExperiment(probe, sampler, (_, _) => Task.CompletedTask);

        var result = await experiment.RunAsync(
            Fake.Network("quiet"),
            Request() with { LoadWaitTimeout = TimeSpan.FromMilliseconds(1) });

        Assert.False(result.Succeeded);
        Assert.Null(result.Loaded);
        Assert.Contains("yeterli trafik görülmedi", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMeasurementWindowThatStoppedBeingBusyIsNotCalledLoaded()
    {
        var probe = new ScriptedLoadProbe(
            Fake.Measurement(24, load: LatencyLoadState.Idle),
            Fake.Measurement(150, load: LatencyLoadState.Idle));

        var sampler = new ScriptedLoadSampler(idleWindows: 1);
        var experiment = new ObservedLoadExperiment(probe, sampler, (_, _) => Task.CompletedTask);

        var result = await experiment.RunAsync(Fake.Network("stopped"), Request());

        Assert.False(result.Succeeded);
        Assert.Contains("sürmedi", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALinkThatIsAlreadyBusyCannotProduceAnIdleBaseline()
    {
        var probe = new ScriptedLoadProbe(Fake.Measurement(80, load: LatencyLoadState.UplinkLoaded));
        var experiment = new ObservedLoadExperiment(probe, new ScriptedLoadSampler(0), (_, _) => Task.CompletedTask);

        var result = await experiment.RunAsync(Fake.Network("busy"), Request());

        Assert.False(result.Succeeded);
        Assert.Contains("zaten meşguldü", result.Failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// 256 kbit/s saturates a half-megabit uplink and is background noise on a gigabit
    /// one, so "loaded" is a share of what this link has been seen to carry.
    /// </summary>
    [Fact]
    public void LoadIsClassifiedAgainstTheMeasuredCapacityNotAFixedNumber()
    {
        var fast = new LinkCapacityEstimate { UplinkKbps = 40_000 };
        var slow = new LinkCapacityEstimate { UplinkKbps = 800 };

        Assert.Equal(10_000, fast.LoadedUplinkThresholdKbps);
        Assert.Equal(NetworkLoadSample.LoadedKbps, slow.LoadedUplinkThresholdKbps);
        Assert.Equal(NetworkLoadSample.LoadedKbps, LinkCapacityEstimate.Unknown.LoadedUplinkThresholdKbps);
    }

    [Fact]
    public void ObservingABusierWindowRaisesTheCapacityEstimate()
    {
        var estimate = LinkCapacityEstimate.Unknown
            .Observing(Fake.Load(LatencyLoadState.UplinkLoaded), DateTimeOffset.UtcNow);

        Assert.Equal(9000, estimate.UplinkKbps);

        // A user-supplied figure is never overwritten by an observation.
        var manual = new LinkCapacityEstimate { UplinkKbps = 5_000, UserSupplied = true };
        Assert.Equal(5_000, manual.Observing(Fake.Load(LatencyLoadState.UplinkLoaded), DateTimeOffset.UtcNow).UplinkKbps);
    }

    private static LoadExperimentRequest Request() => new()
    {
        Endpoint = LatencyEndpoint.Icmp(System.Net.IPAddress.Parse("1.1.1.1"), "test"),
        Direction = LoadDirection.Upload,
        LoadWaitTimeout = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>A probe that replays measurements and counts anything it would send.</summary>
    private sealed class ScriptedLoadProbe : ILatencyProbe
    {
        private readonly Queue<LatencyMeasurement> _script;

        public ScriptedLoadProbe(params LatencyMeasurement[] script)
            => _script = new Queue<LatencyMeasurement>(script);

        public long BytesSent { get; }

        public Task<LatencyMeasurement> MeasureAsync(
            NetworkFingerprint network,
            LatencyProbeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());

        public Task<LatencyConnectivity> CheckConnectivityAsync(
            NetworkFingerprint network,
            string remoteEndpoint,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LatencyConnectivity(true, true));
    }

    /// <summary>Counters that stay quiet for a while and then show a real upload.</summary>
    private sealed class ScriptedLoadSampler : INetworkLoadSampler
    {
        private readonly int _idleWindows;
        private int _reads;
        private long _sent;

        public ScriptedLoadSampler(int idleWindows) => _idleWindows = idleWindows;

        public NetworkCounters? Read(NetworkFingerprint network)
        {
            var busy = _reads > _idleWindows;
            _sent += busy ? 4_000_000 : 1_000;
            _reads++;

            return new NetworkCounters(_sent, 0, DateTimeOffset.UtcNow.AddSeconds(_reads));
        }
    }
}

/// <summary>
/// The lane that actually produces loaded-latency evidence in production.
/// </summary>
/// <remarks>
/// The point of these is that the queueing path is reached by the code the app runs, not
/// only by a unit test calling the analysis directly. An earlier build could describe
/// queueing it had no way of ever measuring.
/// </remarks>
public sealed class LoadedLatencyLaneTests
{
    [Fact]
    public async Task TheLaneFeedsRealLoadedMeasurementsIntoThePathAnalysis()
    {
        var lane = Lane(
            out var qos,
            new FakeLoadExperiment(
                FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 150),
                Download(idleMedian: 24, loadedMedian: 60)));

        var result = await lane.RunAsync(new LoadedLaneRequest());

        Assert.NotNull(result.UploadLoaded);
        Assert.NotNull(result.DownloadLoaded);
        Assert.NotNull(result.Path);
        Assert.True(result.Path!.HasLoadedEvidence);
        Assert.Equal(126, result.Path.UploadQueueingMs);
        Assert.Equal(36, result.Path.DownloadQueueingMs);
        Assert.Equal(LatencyBottleneck.LocalQueueing, result.Path.Bottleneck);
        Assert.True(result.Path.TrafficGuardApplicable);
        Assert.Empty(qos.Policies);
    }

    /// <summary>
    /// Upload and download queueing have different fixes: one can be paced from here, the
    /// other is filled by the far end into the operator's equipment.
    /// </summary>
    [Fact]
    public async Task UploadAndDownloadQueueingAreReportedSeparately()
    {
        var lane = Lane(
            out _,
            new FakeLoadExperiment(
                FakeLoadExperiment.Upload(idleMedian: 20, loadedMedian: 25),
                Download(idleMedian: 20, loadedMedian: 180)));

        var result = await lane.RunAsync(new LoadedLaneRequest());

        Assert.Equal(5, result.Path!.UploadQueueingMs);
        Assert.Equal(160, result.Path.DownloadQueueingMs);

        // Only the upload half is something a send-rate limit could act on.
        Assert.False(result.Path.TrafficGuardApplicable);
    }

    [Fact]
    public async Task TheGuardIsNotRunUntilTheUserAsksForItAndNamesAnApplication()
    {
        var lane = Lane(
            out var qos,
            new FakeLoadExperiment(FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 150)));

        var withoutGuard = await lane.RunAsync(new LoadedLaneRequest());
        Assert.Equal(TrafficGuardStatus.Off, withoutGuard.TrafficGuard!.Status);
        Assert.Empty(qos.Policies);

        var withoutApplication = await lane.RunAsync(new LoadedLaneRequest { RunTrafficGuard = true });
        Assert.Equal(TrafficGuardStatus.NotMeasured, withoutApplication.TrafficGuard!.Status);
        Assert.Empty(qos.Policies);
    }

    /// <summary>
    /// A policy that survives a crash needs something able to remove it, and that
    /// something is the same transaction file the adapter settings use.
    /// </summary>
    [Fact]
    public async Task AKeptPolicyIsRecordedInTheTransactionSnapshot()
    {
        var snapshots = new FakeSnapshotStore();

        // Three windows: the lane's own upload measurement, then the guard's before and
        // after. The download half is skipped so the script maps one-to-one onto calls.
        var lane = Lane(
            out var qos,
            new FakeLoadExperiment(
                FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 150),
                FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 150),
                FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 34)),
            snapshots);

        var result = await lane.RunAsync(new LoadedLaneRequest
        {
            RunTrafficGuard = true,
            BulkApplication = "steam.exe",
            MeasureDownload = false,
        });

        Assert.Equal(TrafficGuardStatus.Active, result.TrafficGuard!.Status);
        Assert.Single(qos.Policies);

        var resource = Assert.Single(snapshots.Value!.Resources);
        Assert.Equal(LatencyResourceKind.QosPolicy, resource.Kind);
        Assert.Equal(result.TrafficGuard.PolicyName, resource.TargetId);
        Assert.Equal("false", resource.OriginalState["existedBefore"]);
        Assert.Equal(LatencyTransactionState.Committed, snapshots.Value.State);
    }

    [Fact]
    public async Task StoppingTheModeRemovesEveryPolicyThisApplicationOwnsAndNothingElse()
    {
        var qos = new FakeQosController { ForeignPolicies = ["Corp-Backup-Throttle"] };
        await qos.CreateAsync(new QosPolicyRequest { Name = "DPIBypass.Latency.bulk.a" });
        await qos.CreateAsync(new QosPolicyRequest { Name = "DPIBypass.Latency.bulk.b" });

        var lane = new LoadedLatencyLane(qos: qos, log: _ => { });

        Assert.Equal(2, await lane.ClearOwnedPoliciesAsync());
        Assert.Empty(qos.Policies);
        Assert.Equal(["DPIBypass.Latency.bulk.a", "DPIBypass.Latency.bulk.b"], qos.Removed.Order());
    }

    [Fact]
    public async Task AnOfflineMachineIsReportedWithoutTouchingAnything()
    {
        var qos = new FakeQosController();
        var lane = new LoadedLatencyLane(
            probe: new StubProbe(),
            targets: new FakeTargetResolver(),
            load: new FakeLoadExperiment(),
            qos: qos,
            snapshots: new FakeSnapshotStore(),
            capture: () => Fake.Network("offline", online: false),
            log: _ => { });

        var result = await lane.RunAsync(new LoadedLaneRequest { RunTrafficGuard = true });

        Assert.Equal(LatencyOptimizationStatus.Offline, result.Status);
        Assert.Empty(qos.Policies);
    }

    private static LoadExperimentResult Download(double idleMedian, double loadedMedian) => new()
    {
        Direction = LoadDirection.Download,
        Idle = Fake.Measurement(idleMedian, load: LatencyLoadState.Idle),
        Loaded = Fake.Measurement(loadedMedian, load: LatencyLoadState.DownlinkLoaded),
        ObservedLoad = Fake.Load(LatencyLoadState.DownlinkLoaded),
    };

    private static LoadedLatencyLane Lane(
        out FakeQosController qos,
        FakeLoadExperiment load,
        FakeSnapshotStore? snapshots = null)
    {
        qos = new FakeQosController();

        return new LoadedLatencyLane(
            probe: new StubProbe(),
            targets: new FakeTargetResolver(),
            load: load,
            qos: qos,
            snapshots: snapshots ?? new FakeSnapshotStore(),
            capture: () => Fake.Network("loaded-lane"),
            log: _ => { });
    }

    private sealed class StubProbe : ILatencyProbe
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
