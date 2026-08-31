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
    /// <summary>
    /// The search applies more than one cap and keeps the one that measured best, and the
    /// choice is then confirmed in a round of its own.
    /// </summary>
    [Fact]
    public async Task TheCapIsChosenByMeasurementAndConfirmedSeparately()
    {
        var qos = new FakeQosController();

        // Baseline, two search trials, then the confirmation round for the winner.
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140, loadedP95: 190),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 60, loadedP95: 88, uplinkKbps: 18_400),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 34, loadedP95: 42, uplinkKbps: 16_000),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 35, loadedP95: 44, uplinkKbps: 16_000));

        var outcome = await Guard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.Active, outcome.State.Status);
        Assert.Single(qos.Policies);
        Assert.StartsWith(WindowsQosController.PolicyNamePrefix, outcome.State.PolicyName, StringComparison.Ordinal);
        Assert.Equal(116, outcome.State.UploadQueueingBeforeMs);
        Assert.Equal(11, outcome.State.UploadQueueingAfterMs);

        // Two caps were measured, and a fourth round ran that the search never saw.
        Assert.Equal(2, outcome.State.Trials.Count);
        Assert.Equal(4, load.Calls);

        // The cap that was kept is not the fixed 85 percent an earlier build assumed.
        Assert.NotEqual(
            (ulong)(20_000 * 0.85 * 1000),
            outcome.State.ThrottleBitsPerSecond);

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

        var outcome = await Guard(qos, load).RunAsync(Request());

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

        var outcome = await Guard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.RolledBack, outcome.State.Status);
        Assert.Empty(qos.Policies);
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

        var outcome = await Guard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.RolledBack, outcome.State.Status);
        Assert.Contains("fazla düştü", outcome.State.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows attaches a QoS policy as a transport endpoint is created, so a transfer
    /// that was already running when the policy appeared is not governed by it.
    /// </summary>
    /// <remarks>
    /// This is the regression the whole ordering exists for: an earlier build created the
    /// policy under a running upload and then measured that same upload, which measures
    /// the unthrottled flow and credits the difference to the policy.
    /// </remarks>
    [Fact]
    public async Task WithoutANewFlowAfterThePolicyNoResultIsProduced()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 30));

        // The observer only ever reports a flow created before the policy existed.
        var observer = new FakeFlowObserver();
        observer.Observed.Add(Fake.Flow(at: DateTimeOffset.UtcNow.AddMinutes(-5)));

        var outcome = await Guard(qos, load, observer).RunAsync(
            Request() with { NewFlowTimeout = TimeSpan.FromMilliseconds(30) });

        Assert.Equal(TrafficGuardStatus.NeedsNewConnection, outcome.State.Status);
        Assert.Contains("yeni bağlantı", outcome.State.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(qos.Policies);

        // Only the baseline was measured: nothing after the policy was treated as data.
        Assert.Equal(1, load.Calls);
    }

    /// <summary>Without an observer the requirement cannot be proved, so nothing is claimed.</summary>
    [Fact]
    public async Task WithoutAFlowObserverTheGuardRefusesToProduceAVerdict()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 30));

        var outcome = await new TrafficGuard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.NeedsNewConnection, outcome.State.Status);
        Assert.Empty(qos.Policies);
    }

    /// <summary>
    /// A policy that exists but does not actually pace the traffic is not evidence about
    /// the cap it names, so the trial is discarded rather than counted.
    /// </summary>
    [Fact]
    public async Task ACapTheTrafficIgnoredIsNotTreatedAsAMeasurementOfThatCap()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140, uplinkKbps: 20_000),
            // Queueing fell, but the transfer ran at the unthrottled rate: the policy is
            // not what changed anything here.
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 30, uplinkKbps: 20_000));

        var outcome = await Guard(qos, load).RunAsync(Request() with
        {
            Mode = TrafficGuardMode.LowestLatency,
            MaximumTrials = 1,
        });

        Assert.Equal(TrafficGuardStatus.RolledBack, outcome.State.Status);
        Assert.Contains("sınırlamadı", outcome.State.Summary, StringComparison.Ordinal);
        Assert.Empty(qos.Policies);
    }

    [Fact]
    public async Task ALinkWithNoQueueingIsLeftAlone()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 28));

        var outcome = await Guard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.NoQueueing, outcome.State.Status);
        Assert.Empty(qos.Policies);
        Assert.Equal(1, load.Calls);
    }

    /// <summary>
    /// A link that never reached its ceiling has not been shown to have a queue, and the
    /// summary says that rather than reporting no queueing.
    /// </summary>
    [Fact]
    public async Task AnUnsaturatedBaselineProducesNotMeasuredRatherThanNoQueueing()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140) with
            {
                Classification = LinkLoadClassification.HighUtilisation,
            });

        var outcome = await Guard(qos, load).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.NotMeasured, outcome.State.Status);
        Assert.Contains("doygunluğa ulaşmadı", outcome.State.Summary, StringComparison.Ordinal);
        Assert.Contains("kuyruklanma yok demek değildir", outcome.State.Summary, StringComparison.Ordinal);
        Assert.Empty(qos.Policies);
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

        var outcome = await Guard(qos, new FakeLoadExperiment()).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.ConflictSkipped, outcome.State.Status);
        Assert.Empty(qos.Policies);
        Assert.Empty(qos.Removed);
        Assert.Contains("Corp-Backup-Throttle", outcome.State.Conflicts);
    }

    [Fact]
    public async Task WithoutWindowsQosNothingIsAttempted()
    {
        var qos = new FakeQosController { Available = false };

        var outcome = await Guard(qos, new FakeLoadExperiment()).RunAsync(Request());

        Assert.Equal(TrafficGuardStatus.Unavailable, outcome.State.Status);
        Assert.Empty(qos.Policies);
    }

    [Fact]
    public async Task AnUnmeasurableLoadedWindowProducesNoVerdictAndNoPolicy()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            LoadExperimentResult.Failed(LoadDirection.Upload, "Beklenen süre içinde yeterli trafik görülmedi."));

        var outcome = await Guard(qos, load).RunAsync(Request());

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

        var outcome = await Guard(qos, load).RunAsync(Request());

        Assert.NotEqual(TrafficGuardStatus.Active, outcome.State.Status);
        Assert.Empty(qos.Policies);
    }

    /// <summary>Cancellation leaves no policy behind, whatever stage it arrived in.</summary>
    [Fact]
    public async Task CancellingMidRunRemovesEveryPolicyTheRunCreated()
    {
        var qos = new FakeQosController();
        using var cancellation = new CancellationTokenSource();

        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140))
        {
            OnRun = _ => cancellation.Cancel(),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Guard(qos, load).RunAsync(Request(), cancellation.Token));

        Assert.Empty(qos.Policies);
    }

    /// <summary>The application being paced is named, and named as the bulk one.</summary>
    [Fact]
    public async Task ThePacedApplicationIsTheBulkOneAndIsReportedAsSuch()
    {
        var qos = new FakeQosController();
        var load = new FakeLoadExperiment(
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140, loadedP95: 190),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 34, loadedP95: 42, uplinkKbps: 16_000),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 34, loadedP95: 42, uplinkKbps: 16_000),
            FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 34, loadedP95: 42, uplinkKbps: 16_000));

        var outcome = await Guard(qos, load).RunAsync(Request() with { MaximumTrials = 1 });

        Assert.Equal(TrafficGuardStatus.Active, outcome.State.Status);
        Assert.Contains("steam.exe", outcome.State.ThrottledApplication, StringComparison.Ordinal);
        Assert.Contains("oyun değil toplu aktarım", outcome.State.Summary, StringComparison.Ordinal);
        Assert.Equal(@"C:\Program Files\Steam\steam.exe", qos.Policies.Values.Single().AppPathName);
    }

    /// <summary>A process the user named that is not running never produces a policy.</summary>
    [Fact]
    public async Task AnApplicationThatIsNotRunningIsRefusedRatherThanGuessedAt()
    {
        var qos = new FakeQosController();

        var outcome = await Guard(qos, new FakeLoadExperiment()).RunAsync(Request() with
        {
            BulkApplication = Fake.BulkApplication() with { ProcessIds = [] },
        });

        Assert.Equal(TrafficGuardStatus.ApplicationNotRunning, outcome.State.Status);
        Assert.Empty(qos.Policies);
    }

    private static TrafficGuard Guard(
        FakeQosController qos,
        FakeLoadExperiment load,
        FakeFlowObserver? observer = null)
    {
        // The default observer reports a fresh flow the moment the guard first asks,
        // which is what a user restarting their transfer looks like.
        observer ??= new FakeFlowObserver { OnQuery = _ => Fake.Flow(at: DateTimeOffset.UtcNow.AddSeconds(1)) };

        return new TrafficGuard(qos, load, flows: observer, delay: (_, _) => Task.CompletedTask);
    }

    private static TrafficGuardRequest Request() => new()
    {
        Network = Fake.Network("guard"),
        Endpoint = LatencyEndpoint.Icmp(System.Net.IPAddress.Parse("1.1.1.1"), "test"),
        ProfileId = "profile1",
        BulkApplication = Fake.BulkApplication(),
        NewFlowTimeout = TimeSpan.FromMilliseconds(200),
    };
}

/// <summary>The cap search itself, over measurements a test writes by hand.</summary>
public sealed class TrafficGuardCapPlannerTests
{
    /// <summary>
    /// Balanced mode does not simply take the lowest tail: past the point where the
    /// difference is a few milliseconds, the cap that keeps more of the transfer wins.
    /// </summary>
    [Fact]
    public void BalancedModeTradesTheLastMillisecondForThroughput()
    {
        var baseline = FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140, loadedP95: 190);
        var gentle = Trial(0.92, 18_400, 40, 46);
        var harsh = Trial(0.68, 13_600, 34, 44);

        var choice = TrafficGuardCapPlanner.Choose([gentle, harsh], baseline, TrafficGuardMode.Balanced);

        Assert.NotNull(choice);
        Assert.Equal(gentle.BitsPerSecond, choice!.BitsPerSecond);
        Assert.Contains("throughput", choice.Why, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lowest-latency mode takes the tail and shows what it cost.</summary>
    [Fact]
    public void LowestLatencyModeTakesTheBestTail()
    {
        var baseline = FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140, loadedP95: 190);
        var gentle = Trial(0.80, 16_000, 40, 46);
        var harsh = Trial(0.50, 10_000, 30, 34);

        var choice = TrafficGuardCapPlanner.Choose([gentle, harsh], baseline, TrafficGuardMode.LowestLatency);

        Assert.NotNull(choice);
        Assert.Equal(harsh.BitsPerSecond, choice!.BitsPerSecond);
        Assert.InRange(choice.RetainedThroughputShare, 0.49, 0.51);
    }

    /// <summary>A trial the traffic ignored is not evidence about that cap.</summary>
    [Fact]
    public void ACapTheTrafficDidNotObeyIsExcludedFromTheSearch()
    {
        var baseline = FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 140, loadedP95: 190);
        var ignored = Trial(0.80, 16_000, 30, 34) with { RateHonoured = false };

        Assert.Null(TrafficGuardCapPlanner.Choose([ignored], baseline, TrafficGuardMode.Balanced));
        Assert.Contains(
            "sınırlamadı",
            TrafficGuardCapPlanner.ExplainRejection([ignored], baseline, TrafficGuardMode.Balanced),
            StringComparison.Ordinal);
    }

    /// <summary>Shares descend, so the least disruptive cap is measured first.</summary>
    [Fact]
    public void TheSearchStartsFromTheLeastDisruptiveCap()
    {
        foreach (var mode in new[] { TrafficGuardMode.Balanced, TrafficGuardMode.LowestLatency })
        {
            var shares = TrafficGuardCapPlanner.SharesFor(mode);

            Assert.NotEmpty(shares);
            Assert.Equal(shares.OrderByDescending(share => share), shares);
            Assert.All(shares, share => Assert.InRange(share, 0.1, 1.0));
        }

        // Lowest-latency mode is allowed to give up more throughput, and says so.
        Assert.True(
            TrafficGuardCapPlanner.ThroughputFloor(TrafficGuardMode.LowestLatency)
            < TrafficGuardCapPlanner.ThroughputFloor(TrafficGuardMode.Balanced));
    }

    private static TrafficGuardCapTrial Trial(double share, double uplinkKbps, double loadedMedian, double loadedP95)
        => new()
        {
            BitsPerSecond = TrafficGuardCapPlanner.CapFor(20_000, share),
            Share = share,
            Result = FakeLoadExperiment.Upload(
                idleMedian: 24,
                loadedMedian: loadedMedian,
                uplinkKbps: uplinkKbps,
                loadedP95: loadedP95),
            RateHonoured = true,
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
            Value = Fake.Snapshot("adapter", Fake.DefaultKeyword) with
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
        Assert.Contains(Fake.DefaultKeyword, controller.Restored);
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
        Assert.Contains("hattı doldurmadı", result.Failure, StringComparison.Ordinal);
        Assert.Contains("kuyruklanma bu veriden çıkarılamaz", result.Failure, StringComparison.Ordinal);
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
    /// Traffic, high utilisation and saturation are three different things, and only the
    /// third can produce the standing queue the Traffic Guard exists to remove.
    /// </summary>
    [Fact]
    public void AQuarterOfTheLinkIsNotSaturation()
    {
        var link = Measured(40_000);

        Assert.Equal(LinkLoadClassification.Traffic, link.Classify(Uplink(10_000), LoadDirection.Upload));
        Assert.Equal(LinkLoadClassification.HighUtilisation, link.Classify(Uplink(26_000), LoadDirection.Upload));
        Assert.Equal(LinkLoadClassification.Saturated, link.Classify(Uplink(35_000), LoadDirection.Upload));
        Assert.Equal(LinkLoadClassification.Quiet, link.Classify(Uplink(40), LoadDirection.Upload));
    }

    /// <summary>
    /// A capacity nobody measured cannot be reached, so it never produces a verdict.
    /// </summary>
    [Fact]
    public void SaturationIsNeverClaimedWithoutAConfidentCapacity()
    {
        Assert.Equal(
            LinkLoadClassification.Traffic,
            LinkCapacityEstimate.Unknown.Classify(Uplink(50_000), LoadDirection.Upload));

        // A single busy window is a lower bound on the line, not its ceiling.
        var weak = LinkCapacityEstimate.Unknown.With(
            LoadDirection.Upload,
            new LinkCapacityRamp.Result(9_000, LinkCapacityConfidence.Weak, 4),
            DateTimeOffset.UtcNow);

        Assert.False(weak.IsConfident(LoadDirection.Upload));
        Assert.Equal(LinkLoadClassification.Traffic, weak.Classify(Uplink(9_000), LoadDirection.Upload));
        Assert.Null(weak.ShareOfCapacity(Uplink(9_000), LoadDirection.Upload));

        Assert.Equal(
            LinkLoadClassification.Unknown,
            Measured(40_000).Classify(NetworkLoadSample.Unknown, LoadDirection.Upload));
    }

    /// <summary>The ramp reports a ceiling only once the rate has stopped climbing.</summary>
    [Fact]
    public void CapacityIsLearnedFromAPlateauNotFromOneWindow()
    {
        var climbing = new LinkCapacityRamp();
        foreach (var kbps in new double[] { 1_000, 4_000, 9_000, 18_000, 34_000 })
        {
            climbing.Add(kbps);
        }

        var stillRising = climbing.Evaluate();
        Assert.Equal(LinkCapacityConfidence.Weak, stillRising.Confidence);
        Assert.Equal(34_000, stillRising.Kbps);

        // Three windows that sit together near the peak: that is the line rate.
        climbing.Add(35_500);
        climbing.Add(36_000);
        climbing.Add(35_800);

        var flattened = climbing.Evaluate();
        Assert.Equal(LinkCapacityConfidence.Measured, flattened.Confidence);
        Assert.Equal(35_800, flattened.Kbps);
    }

    /// <summary>A pause in the transfer ends the ramp rather than shortening it.</summary>
    [Fact]
    public void AGapInTheTransferRestartsTheRamp()
    {
        var ramp = new LinkCapacityRamp();
        foreach (var kbps in new double[] { 30_000, 31_000, 30_500, 30_800 })
        {
            ramp.Add(kbps);
        }

        Assert.Equal(LinkCapacityConfidence.Measured, ramp.Evaluate().Confidence);

        ramp.Add(10);
        Assert.Equal(0, ramp.Count);
        Assert.Equal(LinkCapacityConfidence.None, ramp.Evaluate().Confidence);
    }

    /// <summary>Each direction carries its own figure, confidence and timestamp.</summary>
    [Fact]
    public void UploadAndDownloadCapacitiesAreKeptApart()
    {
        var at = DateTimeOffset.UtcNow;
        var link = LinkCapacityEstimate.Unknown
            .With(LoadDirection.Upload, new LinkCapacityRamp.Result(9_000, LinkCapacityConfidence.Measured, 6), at)
            .With(LoadDirection.Download, new LinkCapacityRamp.Result(90_000, LinkCapacityConfidence.Weak, 4), at);

        Assert.Equal(9_000, link.CapacityFor(LoadDirection.Upload));
        Assert.Equal(90_000, link.CapacityFor(LoadDirection.Download));
        Assert.True(link.IsConfident(LoadDirection.Upload));
        Assert.False(link.IsConfident(LoadDirection.Download));
        Assert.Equal(at, link.ObservedAt(LoadDirection.Upload));
        Assert.Equal(6, link.UplinkWindows);
    }

    /// <summary>A figure the user typed is never overwritten by an observation.</summary>
    [Fact]
    public void AUserSuppliedCapacityOutranksWhatWeHappenedToSee()
    {
        var manual = LinkCapacityEstimate.FromUser(5_000, null);

        Assert.Equal(LinkCapacityConfidence.UserSupplied, manual.UplinkConfidence);
        Assert.True(manual.IsConfident(LoadDirection.Upload));

        var observed = manual.With(
            LoadDirection.Upload,
            new LinkCapacityRamp.Result(9_000, LinkCapacityConfidence.Measured, 8),
            DateTimeOffset.UtcNow);

        Assert.Equal(5_000, observed.CapacityFor(LoadDirection.Upload));
    }

    private static LinkCapacityEstimate Measured(double uplinkKbps) => LinkCapacityEstimate.Unknown.With(
        LoadDirection.Upload,
        new LinkCapacityRamp.Result(uplinkKbps, LinkCapacityConfidence.Measured, 6),
        DateTimeOffset.UtcNow);

    private static NetworkLoadSample Uplink(double kbps) => new()
    {
        State = kbps >= NetworkLoadSample.LoadedKbps ? LatencyLoadState.UplinkLoaded : LatencyLoadState.Idle,
        UplinkKbps = kbps,
        DownlinkKbps = 20,
    };

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

        // 1,125,000 bytes across a one-second window is 9,000 kbit/s, which is what
        // Fake.Load(UplinkLoaded) reports for the measurement itself. The two have to
        // agree or the ramp learns a ceiling the measured window never reaches.
        public NetworkCounters? Read(NetworkFingerprint network)
        {
            var busy = _reads > _idleWindows;
            _sent += busy ? 1_125_000 : 1_000;
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

        // The lane's own upload window, then the guard's unthrottled reference, then one
        // capped trial and the independent confirmation round. The download half is
        // skipped so the script maps one-to-one onto calls.
        var lane = Lane(
            out var qos,
            new FakeLoadExperiment(
                FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 150),
                FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 150, loadedP95: 200),
                FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 34, loadedP95: 42, uplinkKbps: 16_000),
                FakeLoadExperiment.Upload(idleMedian: 24, loadedMedian: 34, loadedP95: 42, uplinkKbps: 16_000)),
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
        Classification = LinkLoadClassification.Saturated,
        Capacity = Fake.Capacity(9_000, downlinkKbps: 45_000),
    };

    private static LoadedLatencyLane Lane(
        out FakeQosController qos,
        FakeLoadExperiment load,
        FakeSnapshotStore? snapshots = null,
        FakeFlowObserver? flows = null,
        RecordingStages? stages = null)
    {
        qos = new FakeQosController();

        return new LoadedLatencyLane(
            probe: new StubProbe(),
            targets: new FakeTargetResolver(),
            load: load,
            qos: qos,
            snapshots: snapshots ?? new FakeSnapshotStore(),
            capture: () => Fake.Network("loaded-lane"),
            log: _ => { },
            flows: flows ?? new FakeFlowObserver
            {
                OnQuery = _ => Fake.Flow(at: DateTimeOffset.UtcNow.AddSeconds(1)),
            },
            applications: new FakeApplicationResolver(),
            stages: stages ?? new RecordingStages());
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
