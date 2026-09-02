using DpiBypass.Core.Config;
using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

public sealed class LatencyModelTests
{
    [Fact]
    public void LowLatencyModeDefaultsToFalse()
        => Assert.False(new AppSettings().LowLatencyMode);

    [Fact]
    public void AnOlderSettingsFileLoadsWithLatencyModeOff()
    {
        using var directory = new TempDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, "{\"StartWithWindows\":false}");

        var settings = new ConfigStore(settingsPath, Path.Combine(directory.Path, "networks.json")).Load();

        Assert.False(settings.LowLatencyMode);
        Assert.False(settings.StartWithWindows);
    }

    [Fact]
    public void VirtualAdaptersAreNeverCandidates()
    {
        var capability = Fake.Capability(Fake.Network("virtual")) with { IsPhysical = false, IsVirtual = true };

        Assert.False(capability.IsEligible);
        Assert.Empty(capability.BuildSafeCandidates());
    }

    [Fact]
    public void InterruptModerationUsesRegistryKeywordNotALocalisedDisplayName()
    {
        var capability = Fake.Capability(Fake.Network("ethernet")) with
        {
            PowerManagement = [],
            AdvancedProperties =
            [
                new AdapterAdvancedPropertyCapability
                {
                    RegistryKeyword = "*InterruptModeration",
                    RegistryValues = ["1"],
                    ValidRegistryValues = ["0", "1"],
                },
            ],
        };

        var candidate = Assert.Single(capability.BuildSafeCandidates());

        Assert.Equal("*InterruptModeration", candidate.PropertyName);
        Assert.Equal(["0"], candidate.DesiredValues);

        // Turning moderation off costs an interrupt per packet, so it has to clear a
        // higher bar than a change that costs nothing.
        Assert.True(candidate.CpuSensitive);
    }

    [Fact]
    public void UnsupportedAndAlreadyDisabledPropertiesAreSkipped()
    {
        var capability = Fake.Capability(Fake.Network("unsupported")) with
        {
            PowerManagement = new Dictionary<string, int>
            {
                [Fake.DefaultKeyword] = 0,
                ["DeviceSleepOnDisconnect"] = 1,
                [Fake.SecondKeyword] = 0,
            },
            AdvancedProperties = [],
        };

        Assert.Empty(capability.BuildSafeCandidates());
    }

    /// <summary>
    /// The fingerprint is what stops a result verified against one driver from being
    /// replayed against another.
    /// </summary>
    [Fact]
    public void TheCapabilityFingerprintTracksTheDriverSurface()
    {
        var capability = Fake.Capability(Fake.Network("finger"));
        var same = Fake.Capability(Fake.Network("finger"));

        Assert.Equal(capability.CapabilityFingerprint, same.CapabilityFingerprint);

        Assert.NotEqual(
            capability.CapabilityFingerprint,
            (capability with { InterfaceDescription = "Intel I225-V (driver 2.1)" }).CapabilityFingerprint);

        Assert.NotEqual(
            capability.CapabilityFingerprint,
            (capability with
            {
                AdvancedProperties =
                [
                    new AdapterAdvancedPropertyCapability
                    {
                        RegistryKeyword = AdapterInterventionCatalog.RscIPv4Keyword,
                        RegistryValues = ["1"],
                        ValidRegistryValues = ["0", "1"],
                    },
                ],
            }).CapabilityFingerprint);

        // A driver update can change what a keyword does without changing which values
        // it accepts, so the version is part of the key a saved result is filed under.
        Assert.NotEqual(
            capability.CapabilityFingerprint,
            (capability with { DriverVersion = "2.1.4.2" }).CapabilityFingerprint);
    }

    [Fact]
    public async Task SnapshotWritesAreAtomicAndRoundTripEveryOriginalValue()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "latency-snapshot.json");
        var store = new LatencySnapshotStore(path);
        var snapshot = Fake.Snapshot("adapter", "*InterruptModeration", LatencySettingKind.AdvancedProperty);

        await store.SaveAsync(snapshot);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.AdapterId, loaded!.AdapterId);
        Assert.Equal(snapshot.NetworkKey, loaded.NetworkKey);
        Assert.Equal(snapshot.State, loaded.State);
        Assert.Equal(snapshot.Settings[0].PropertyName, loaded.Settings[0].PropertyName);
        Assert.Equal(snapshot.Settings[0].OriginalValues, loaded.Settings[0].OriginalValues);
        Assert.False(File.Exists(path + ".tmp"));

        await store.SaveAsync(snapshot with { AdapterName = "renamed" });
        Assert.Equal("renamed", (await store.LoadAsync())!.AdapterName);
        Assert.False(File.Exists(path + ".tmp"));
    }

    /// <summary>A file written by an older build has to be rolled back, never trusted.</summary>
    [Fact]
    public async Task ASnapshotFromAnOlderSchemaCountsAsIncomplete()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "latency-snapshot.json");
        await File.WriteAllTextAsync(path, """
            {
              "AdapterId": "adapter",
              "AdapterName": "adapter",
              "NetworkKey": "network",
              "CreatedAt": "2026-01-01T00:00:00+00:00",
              "SchemaVersion": 1,
              "State": "Committed",
              "Settings": []
            }
            """);

        var loaded = await new LatencySnapshotStore(path).LoadAsync();

        Assert.NotNull(loaded);
        Assert.True(loaded!.IsIncomplete);
    }

    [Theory]
    [InlineData(LatencyTransactionState.SnapshotCreated, true)]
    [InlineData(LatencyTransactionState.CandidateApplied, true)]
    [InlineData(LatencyTransactionState.Verifying, true)]
    [InlineData(LatencyTransactionState.Committed, false)]
    public void OnlyACommittedSnapshotDescribesSettingsSomebodyChose(LatencyTransactionState state, bool incomplete)
        => Assert.Equal(incomplete, Fake.Snapshot("adapter", Fake.DefaultKeyword, state: state).IsIncomplete);
}

/// <summary>
/// The run itself: what gets applied, what gets put back, and what the user is told.
/// </summary>
public sealed class LatencyOptimizerTests
{
    [Fact]
    public async Task OfflineStateFailsGracefullyWithoutTouchingTheAdapter()
    {
        var scenario = new LatencyScenario();
        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("offline", online: false));

        Assert.Equal(LatencyOptimizationStatus.Offline, result.Status);
        Assert.Empty(scenario.Controller.Applied);
    }

    [Fact]
    public async Task AnUnsupportedAdapterIsReportedWithoutMeasuring()
    {
        var scenario = new LatencyScenario(controller: new FakeController { Detect = _ => null });
        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("unsupported"));

        Assert.Equal(LatencyOptimizationStatus.Unsupported, result.Status);
        Assert.Empty(scenario.Controller.Applied);
    }

    [Fact]
    public async Task ARepeatableGainIsAcceptedKeptAndReportedWithRealNumbers()
    {
        var scenario = LatencyScenario.WithImprovement(gain: 5);
        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("gain"));

        Assert.Equal(LatencyOptimizationStatus.Active, result.Status);
        Assert.True(result.HasVerifiedGain);
        Assert.Equal(["Interrupt Moderation kapalı"], result.AppliedChanges);

        // The number the user is shown is the observed original-to-final difference.
        Assert.NotNull(result.VerifiedImprovement);
        Assert.Equal(5, result.VerifiedImprovement!.MedianMs, precision: 3);
        Assert.Contains("Doğrulanmış iyileşme", result.StatusLine, StringComparison.Ordinal);
        Assert.Contains("-5.0 ms", result.StatusLine, StringComparison.Ordinal);

        // The setting is left on the adapter, and the snapshot says so.
        Assert.Contains(Fake.DefaultKeyword, scenario.Controller.Live);
        Assert.NotNull(scenario.Snapshots.Value);
        Assert.Equal(LatencyTransactionState.Committed, scenario.Snapshots.Value!.State);
    }

    [Fact]
    public async Task TheHeadlineGainComesFromBaselineToFinalRatherThanSummedCandidateDeltas()
    {
        // Two power properties that are both real candidates. DeviceSleepOnDisconnect
        // deliberately is not one: the keyword governs what the adapter does when the
        // media is disconnected, which is not a state a running game is ever in.
        var controller = new FakeController
        {
            Properties = [Fake.DefaultKeyword, Fake.SecondKeyword],
        };
        var probe = new FakeProbe(controller, (live, call) =>
        {
            if (live.Contains(Fake.DefaultKeyword) && live.Contains(Fake.SecondKeyword))
            {
                // Paired B cycles see 32 ms (a 3 ms incremental gain), while the
                // independent final state measures 34 ms.
                return Fake.Measurement(call >= 10 ? 34 : 32);
            }

            return live.Contains(Fake.DefaultKeyword) ? Fake.Measurement(35) : Fake.Measurement(40);
        });
        var scenario = new LatencyScenario(controller, probe);

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("end-to-end"));

        Assert.Equal(LatencyOptimizationStatus.Active, result.Status);
        Assert.Equal(6, result.VerifiedImprovement!.MedianMs);
        Assert.Equal(8, result.Verdicts.Where(verdict => verdict.Accepted).Sum(verdict => verdict.Delta.MedianMs));
        Assert.Equal(40, result.Before!.MedianRttMs);
        Assert.Equal(34, result.After!.MedianRttMs);
    }

    [Fact]
    public async Task ALinkNoSettingChangesIsLeftExactlyAsItWasFound()
    {
        var scenario = new LatencyScenario();
        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("flat"));

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Contains("doğrulanmış bir gecikme iyileşmesi bulunamadı", result.StatusLine, StringComparison.Ordinal);
        Assert.Contains("Özgün ayarlar geri yüklendi", result.StatusLine, StringComparison.Ordinal);
        Assert.Empty(scenario.Controller.Live);
        Assert.Null(scenario.Snapshots.Value);
        Assert.Null(result.VerifiedImprovement);
    }

    /// <summary>
    /// The heart of the paired design: a network that simply gets quieter halfway through
    /// the run must not be reported as a setting that worked.
    /// </summary>
    [Fact]
    public async Task ANetworkThatQuietensDownOnItsOwnIsNotCreditedToTheCandidate()
    {
        var controller = new FakeController();

        // Latency falls steadily with every measurement regardless of what is applied.
        var probe = new FakeProbe(controller, (_, call) => Fake.Measurement(40 - call));
        var scenario = new LatencyScenario(controller, probe);

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("drifting"));

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Empty(controller.Live);
    }

    [Fact]
    public async Task EveryCandidateGetsAtLeastTwoPairedCycles()
    {
        var scenario = LatencyScenario.WithImprovement(gain: 6);
        await scenario.Optimizer.OptimizeAsync(Fake.Network("cycles"));

        var verdict = Assert.Single(scenario.Optimizer.Current.Verdicts);

        Assert.True(verdict.Accepted);
        Assert.True(verdict.Cycles >= 2, $"only {verdict.Cycles} cycle(s) were run");

        // Applied and restored once per cycle, plus the apply that keeps it.
        Assert.True(scenario.Controller.Applied.Count >= 3);
        Assert.True(scenario.Controller.Restored.Count >= 2);
    }

    [Fact]
    public async Task ANoisyLinkGetsExtraCyclesAndThenAClearNo()
    {
        var controller = new FakeController();

        // The first cycle looks like a large win and every later one like a small loss:
        // the mean stays above the gain threshold while the cycles never agree.
        var probe = new FakeProbe(controller, (live, call) => live.Contains(Fake.DefaultKeyword)
            ? Fake.Measurement(call == 3 ? 18 : 32)
            : Fake.Measurement(30));
        var scenario = new LatencyScenario(controller, probe, new LatencyOptimizerOptions
        {
            MinimumCycles = 2,
            MaximumCycles = 4,
        });

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("noisy"));
        var verdict = Assert.Single(result.Verdicts);

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.False(verdict.Accepted);
        Assert.True(verdict.Cycles > 2, "a noisy link should have been given more than the minimum");
        Assert.Empty(controller.Live);
    }

    /// <summary>
    /// A candidate measured while a download runs, against a baseline measured on an idle
    /// link, is a measurement of the download. The cycle is thrown away and re-run.
    /// </summary>
    [Fact]
    public async Task ACycleWhereOnlyOneHalfRanOnABusyLinkIsDiscardedAndRepeated()
    {
        var controller = new FakeController();

        // Every "with the setting" window happens to be busy, and looks enormously
        // better for it. None of those pairs may count.
        var probe = new FakeProbe(controller, (live, _) => live.Contains(Fake.DefaultKeyword)
            ? Fake.Measurement(12, load: LatencyLoadState.DownlinkLoaded)
            : Fake.Measurement(40, load: LatencyLoadState.Idle));
        var scenario = new LatencyScenario(controller, probe, new LatencyOptimizerOptions
        {
            MinimumCycles = 2,
            MaximumCycles = 3,
            MaximumLoadRetries = 2,
        });

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("busy"));

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Empty(controller.Live);
        Assert.Contains(scenario.Logs, line => line.Contains("latency.cycle.discarded", StringComparison.Ordinal));
    }

    /// <summary>
    /// The operational log is what an incident is reconstructed from, so the event names
    /// are part of the contract rather than decoration.
    /// </summary>
    [Fact]
    public async Task EveryStageOfARunIsLoggedUnderAStableEventName()
    {
        var scenario = LatencyScenario.WithImprovement(gain: 6);
        await scenario.Optimizer.OptimizeAsync(Fake.Network("logged"));

        foreach (var expected in new[]
        {
            "latency.baseline.started",
            "latency.baseline.completed",
            "latency.candidate.applied",
            "latency.cycle.completed",
            "latency.candidate.accepted",
            "latency.verification.completed",
            "latency.committed",
        })
        {
            Assert.Contains(scenario.Logs, line => line.Contains(expected, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ARollbackIsLoggedFromStartToFinish()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, (_, _) => Fake.Measurement(30))
        {
            BreaksConnectivity = Fake.DefaultKeyword,
        };
        var scenario = new LatencyScenario(controller, probe);

        await scenario.Optimizer.OptimizeAsync(Fake.Network("logged-rollback"));

        Assert.Contains(scenario.Logs, line => line.Contains("latency.rollback.started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACandidateThatAddsPacketLossIsRolledBack()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, (live, _) => live.Contains(Fake.DefaultKeyword)
            ? Fake.Measurement(20, loss: 12)
            : Fake.Measurement(30));
        var scenario = new LatencyScenario(controller, probe);

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("loss"));

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Empty(controller.Live);
        Assert.Contains("paket kaybı", result.StatusLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LosingConnectivityRollsEverythingBackAtOnce()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(controller, (_, _) => Fake.Measurement(30))
        {
            BreaksConnectivity = Fake.DefaultKeyword,
        };
        var scenario = new LatencyScenario(controller, probe);

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("dead"));

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Contains("Bağlantı denetimi", result.StatusLine, StringComparison.Ordinal);
        Assert.Equal([Fake.DefaultKeyword], controller.Restored);
        Assert.Empty(controller.Live);
        Assert.Null(scenario.Snapshots.Value);
    }

    [Fact]
    public async Task ConnectivityRollbackFailureIsReportedAndPreservesTheSnapshot()
    {
        var controller = new FakeController { RestoreOutcome = LatencyRestoreOutcome.Failed };
        var probe = new FakeProbe(controller, (_, _) => Fake.Measurement(30))
        {
            BreaksConnectivity = Fake.DefaultKeyword,
        };
        var scenario = new LatencyScenario(controller, probe);

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("dead-rollback-failure"));

        Assert.Equal(LatencyOptimizationStatus.Failed, result.Status);
        Assert.Contains("snapshot", result.StatusLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Fake.DefaultKeyword, controller.Live);
        Assert.NotNull(scenario.Snapshots.Value);
    }

    [Fact]
    public async Task ADriverThatDeclinesTheWriteEndsThatCandidateWithoutAVerdict()
    {
        var controller = new FakeController { RefuseApply = Fake.DefaultKeyword };
        var scenario = new LatencyScenario(controller, FakeProbe.Flat(controller));

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("refused"));
        var verdict = Assert.Single(result.Verdicts);

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Contains("uygulamadı", verdict.Reason, StringComparison.Ordinal);
        Assert.Empty(controller.Live);
        Assert.Null(scenario.Snapshots.Value);
    }

    [Fact]
    public async Task ApplyExceptionRollsBackEverythingInReverseOrder()
    {
        var controller = new FakeController
        {
            Properties = [Fake.DefaultKeyword, Fake.SecondKeyword],
            ThrowOnApply = Fake.SecondKeyword,
        };
        var probe = FakeProbe.Improves(controller, gain: 6);
        var scenario = new LatencyScenario(controller, probe);

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("throw"));

        Assert.Equal(LatencyOptimizationStatus.Failed, result.Status);
        Assert.Contains(Fake.SecondKeyword, controller.Restored);
        Assert.Contains(Fake.DefaultKeyword, controller.Restored);
        Assert.Empty(controller.Live);
        Assert.Null(scenario.Snapshots.Value);
    }

    [Fact]
    public async Task AnAdapterThatDisappearsMidRunKeepsTheSnapshotForLater()
    {
        var scenario = new LatencyScenario(new FakeController { RestoreOutcome = LatencyRestoreOutcome.MissingAdapter })
        {
            Snapshots = { Value = Fake.Snapshot("missing", Fake.DefaultKeyword) },
        };

        var result = await scenario.Optimizer.RestoreAsync();

        Assert.Equal(LatencyOptimizationStatus.Failed, result.Status);
        Assert.NotNull(scenario.Snapshots.Value);
    }

    [Fact]
    public async Task APropertyThatNoLongerExistsDoesNotBlockTheSnapshotFromClearing()
    {
        var scenario = new LatencyScenario(new FakeController { RestoreOutcome = LatencyRestoreOutcome.MissingProperty })
        {
            Snapshots = { Value = Fake.Snapshot("updated-driver", Fake.DefaultKeyword) },
        };

        var result = await scenario.Optimizer.RestoreAsync();

        Assert.Equal(LatencyOptimizationStatus.Disabled, result.Status);
        Assert.Null(scenario.Snapshots.Value);
    }

    [Fact]
    public async Task ModeOffPerformsAFullRestore()
    {
        var scenario = LatencyScenario.WithImprovement();

        await scenario.Optimizer.OptimizeAsync(Fake.Network("mode-off"));
        var result = await scenario.Optimizer.StopAndRestoreAsync();

        Assert.Equal(LatencyOptimizationStatus.Disabled, result.Status);
        Assert.Empty(scenario.Controller.Live);
        Assert.Null(scenario.Snapshots.Value);
    }

    [Fact]
    public async Task AppShutdownRestoresPersistentNicSettings()
    {
        var scenario = LatencyScenario.WithImprovement();

        await scenario.Optimizer.OptimizeAsync(Fake.Network("shutdown"));
        await scenario.Optimizer.DisposeAsync();

        Assert.Empty(scenario.Controller.Live);
        Assert.Null(scenario.Snapshots.Value);
    }

    // --- crash recovery ---------------------------------------------------------------

    [Fact]
    public async Task AnInterruptedRunIsRolledBackOnTheNextLaunch()
    {
        var scenario = new LatencyScenario
        {
            Snapshots =
            {
                Value = Fake.Snapshot("adapter-crash", Fake.DefaultKeyword, state: LatencyTransactionState.CandidateApplied),
            },
        };

        Assert.True(await scenario.Optimizer.RecoverAsync());

        Assert.Equal([Fake.DefaultKeyword], scenario.Controller.Restored);
        Assert.Null(scenario.Snapshots.Value);
    }

    /// <summary>
    /// Recovery is not gated on the mode being on. A machine that crashed mid-run has
    /// values on it nobody verified, whatever the settings file says now.
    /// </summary>
    [Fact]
    public async Task RecoveryLeavesACommittedSnapshotAloneForTheModeToOwn()
    {
        var scenario = new LatencyScenario
        {
            Snapshots = { Value = Fake.Snapshot("adapter-live", Fake.DefaultKeyword) },
        };

        Assert.True(await scenario.Optimizer.RecoverAsync());

        Assert.Empty(scenario.Controller.Restored);
        Assert.NotNull(scenario.Snapshots.Value);
    }

    [Fact]
    public async Task RecoveryWithNothingToDoIsAQuietSuccess()
    {
        var scenario = new LatencyScenario();

        Assert.True(await scenario.Optimizer.RecoverAsync());
        Assert.Empty(scenario.Controller.Restored);
    }

    [Fact]
    public async Task ARecoveryThatCannotFinishIsReportedAndTheSnapshotIsKept()
    {
        var scenario = new LatencyScenario(new FakeController { RestoreOutcome = LatencyRestoreOutcome.Failed })
        {
            Snapshots =
            {
                Value = Fake.Snapshot("adapter-stuck", Fake.DefaultKeyword, state: LatencyTransactionState.Verifying),
            },
        };

        Assert.False(await scenario.Optimizer.RecoverAsync());

        Assert.NotNull(scenario.Snapshots.Value);
        Assert.Equal(LatencyOptimizationStatus.Failed, scenario.Optimizer.Current.Status);
    }

    [Fact]
    public async Task AnUnrestorableSnapshotStopsANewRunFromStarting()
    {
        var scenario = new LatencyScenario(new FakeController { RestoreOutcome = LatencyRestoreOutcome.Failed })
        {
            Snapshots = { Value = Fake.Snapshot("stuck", Fake.DefaultKeyword) },
        };

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("blocked"));

        Assert.Equal(LatencyOptimizationStatus.Failed, result.Status);
        Assert.Empty(scenario.Controller.Applied);
    }

    /// <summary>The original value has to be on disk before it is overwritten, not after.</summary>
    [Fact]
    public async Task TheOriginalValueIsRecordedBeforeItIsOverwritten()
    {
        var controller = new FakeController();
        var scenario = new LatencyScenario(controller, FakeProbe.Improves(controller));
        var snapshotWrites = new List<string?>();
        var applies = new List<string>();

        scenario.Snapshots.OnSave = snapshot => snapshotWrites.Add(snapshot.PendingProperty);
        controller.OnApply = property => applies.Add(property);

        await scenario.Optimizer.OptimizeAsync(Fake.Network("ordering"));

        Assert.Contains(Fake.DefaultKeyword, snapshotWrites);
        Assert.NotEmpty(applies);
        Assert.Equal(LatencyTransactionState.Committed, scenario.Snapshots.Value!.State);
    }

    // --- network identity ---------------------------------------------------------------

    [Fact]
    public async Task NetworkChangeRestoresTheOldAdapterBeforeApplyingTheNewOne()
    {
        var controller = new FakeController();
        var scenario = new LatencyScenario(controller, FakeProbe.Improves(controller, gain: 6));

        await scenario.Optimizer.OptimizeAsync(Fake.Network("old"));
        await scenario.Optimizer.OptimizeNetworkChangeAsync(Fake.Network("new"));

        Assert.Equal($"adapter-old:{Fake.DefaultKeyword}", controller.Events[0]);
        Assert.Contains($"restore:adapter-old:{Fake.DefaultKeyword}", controller.Events);
        Assert.Equal($"adapter-new:{Fake.DefaultKeyword}", controller.Events[^1]);
    }

    [Fact]
    public async Task DuplicateNetworkNotificationDoesNotRunTheBenchmarkTwice()
    {
        var scenario = LatencyScenario.WithImprovement();
        var network = Fake.Network("same");

        await scenario.Optimizer.OptimizeAsync(network);
        var applies = scenario.Controller.Applied.Count;

        await scenario.Optimizer.OptimizeNetworkChangeAsync(network);

        Assert.Equal(applies, scenario.Controller.Applied.Count);
    }

    [Fact]
    public async Task ConcurrentOperationsNeverApplyAtTheSameTime()
    {
        var controller = new FakeController { ApplyDelay = TimeSpan.FromMilliseconds(60) };
        var scenario = new LatencyScenario(controller, FakeProbe.Flat(controller));

        var first = scenario.Optimizer.OptimizeAsync(Fake.Network("concurrent-a"));
        await Task.Delay(30);
        var second = scenario.Optimizer.OptimizeAsync(Fake.Network("concurrent-b"));
        await Task.WhenAll(first, second);

        Assert.Equal(1, controller.MaxConcurrentApplies);
    }

    [Fact]
    public async Task CancellationAfterSnapshotCaptureRestoresTheCandidate()
    {
        var controller = new FakeController { ApplyDelay = TimeSpan.FromSeconds(5) };
        var scenario = new LatencyScenario(controller, FakeProbe.Flat(controller));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("cancel"), cancellation.Token);

        Assert.Equal(LatencyOptimizationStatus.Cancelled, result.Status);
        Assert.Equal([Fake.DefaultKeyword], controller.Restored);
        Assert.Empty(controller.Live);
        Assert.Null(scenario.Snapshots.Value);
    }

    // --- profiles -------------------------------------------------------------------------

    [Fact]
    public async Task AVerifiedResultIsRememberedAgainstTheAdapterAndNetwork()
    {
        var scenario = LatencyScenario.WithImprovement();
        var network = Fake.Network("profiled");

        await scenario.Optimizer.OptimizeAsync(network);
        var profile = Assert.Single(scenario.Profiles.Profiles);

        Assert.Equal(network.Key, profile.NetworkKey);
        Assert.Equal(network.AdapterId, profile.AdapterId);
        Assert.Equal([Fake.DefaultKeyword], profile.AcceptedProperties);
        Assert.NotNull(profile.Baseline);
        Assert.NotNull(profile.Optimized);
    }

    [Fact]
    public async Task ACachedProfileIsKeptOnlyWhenFreshMeasurementsStillShowBenefit()
    {
        var network = Fake.Network("profile-beneficial");
        var first = LatencyScenario.WithImprovement(gain: 6);
        await first.Optimizer.OptimizeAsync(network);

        var controller = new FakeController();
        var replay = new LatencyScenario(controller, FakeProbe.Improves(controller, gain: 6), profiles: first.Profiles);
        var result = await replay.Optimizer.OptimizeAsync(network);

        Assert.Equal(LatencyOptimizationStatus.Active, result.Status);

        // A replay re-applies a profile an earlier run verified and checks it against a
        // fresh baseline. That single before/after reading is a baseline comparison, not
        // a paired experiment, so it is not reported as a verified causal gain.
        Assert.Null(result.VerifiedImprovement);
        Assert.Equal(6, result.BaselineComparison!.MedianMs);
        Assert.Contains(Fake.DefaultKeyword, controller.Live);
        Assert.Single(controller.Applied);
    }

    [Fact]
    public async Task ACachedProfileWithZeroCurrentBenefitIsRestoredAndDowngraded()
    {
        var network = Fake.Network("profile-flat");
        var first = LatencyScenario.WithImprovement(gain: 6);
        await first.Optimizer.OptimizeAsync(network);

        var controller = new FakeController();
        var replay = new LatencyScenario(controller, FakeProbe.Flat(controller), profiles: first.Profiles);
        var result = await replay.Optimizer.OptimizeAsync(network);

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Empty(controller.Live);
        Assert.Contains(Fake.DefaultKeyword, controller.Restored);
        var refreshed = Assert.Single(first.Profiles.Profiles);
        Assert.Empty(refreshed.AcceptedProperties);
        Assert.Equal([Fake.DefaultKeyword], refreshed.RejectedProperties);
    }

    [Fact]
    public async Task ACachedProfileThatBecameWorseIsRestored()
    {
        var network = Fake.Network("profile-worse");
        var first = LatencyScenario.WithImprovement(gain: 6);
        await first.Optimizer.OptimizeAsync(network);

        var controller = new FakeController();
        var probe = new FakeProbe(controller, (live, _) =>
            live.Contains(Fake.DefaultKeyword) ? Fake.Measurement(35) : Fake.Measurement(25));
        var replay = new LatencyScenario(controller, probe, profiles: first.Profiles);

        var result = await replay.Optimizer.OptimizeAsync(network);

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Empty(controller.Live);
        Assert.Contains(Fake.DefaultKeyword, controller.Restored);
    }

    [Fact]
    public async Task ACachedProfileThatBreaksConnectivityIsRestoredAndInvalidated()
    {
        var network = Fake.Network("profile-connectivity");
        var first = LatencyScenario.WithImprovement(gain: 6);
        await first.Optimizer.OptimizeAsync(network);

        var controller = new FakeController();
        var probe = new FakeProbe(controller, (live, _) => live.Contains(Fake.DefaultKeyword)
            ? Fake.Measurement(20)
            : Fake.Measurement(26))
        {
            BreaksConnectivity = Fake.DefaultKeyword,
        };
        var replay = new LatencyScenario(controller, probe, profiles: first.Profiles);

        var result = await replay.Optimizer.OptimizeAsync(network);

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Empty(controller.Live);
        Assert.Empty(first.Profiles.Profiles);
    }

    [Fact]
    public async Task AStaleCpuSensitiveCachedSettingIsRestoredWhenItsBenefitDisappears()
    {
        var network = Fake.Network("profile-cpu");
        AdapterLatencyCapability Capability(NetworkFingerprint fingerprint) => Fake.Capability(fingerprint) with
        {
            PowerManagement = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            AdvancedProperties =
            [
                new AdapterAdvancedPropertyCapability
                {
                    RegistryKeyword = "*InterruptModeration",
                    RegistryValues = ["1"],
                    ValidRegistryValues = ["0", "1"],
                },
            ],
        };

        var firstController = new FakeController { Detect = Capability };
        var first = new LatencyScenario(
            firstController,
            FakeProbe.Improves(firstController, "*InterruptModeration", median: 40, gain: 10));
        await first.Optimizer.OptimizeAsync(network);

        var replayController = new FakeController { Detect = Capability };
        var replay = new LatencyScenario(
            replayController,
            FakeProbe.Flat(replayController, median: 40),
            profiles: first.Profiles);

        var result = await replay.Optimizer.OptimizeAsync(network);

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Empty(replayController.Live);
        Assert.Contains("*InterruptModeration", replayController.Restored);
    }

    [Fact]
    public async Task SuccessfulReplayRefreshesTheProfilesCurrentMeasurementsAndAge()
    {
        var network = Fake.Network("profile-refresh");
        var firstAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var refreshedAt = firstAt + TimeSpan.FromDays(10);
        var firstController = new FakeController();
        var first = new LatencyScenario(
            firstController,
            FakeProbe.Improves(firstController, gain: 5),
            now: () => firstAt);
        await first.Optimizer.OptimizeAsync(network);

        var replayController = new FakeController();
        var replay = new LatencyScenario(
            replayController,
            FakeProbe.Improves(replayController, median: 32, gain: 7),
            profiles: first.Profiles,
            now: () => refreshedAt);
        await replay.Optimizer.OptimizeAsync(network);

        var profile = Assert.Single(first.Profiles.Profiles);
        Assert.Equal(refreshedAt, profile.VerifiedAt);
        Assert.Equal(32, profile.Baseline!.MedianRttMs);
        Assert.Equal(25, profile.Optimized!.MedianRttMs);
    }

    [Fact]
    public async Task ReplayRollbackFailureStopsFurtherTuningAndPreservesRecoveryData()
    {
        var network = Fake.Network("profile-rollback-failure");
        var first = LatencyScenario.WithImprovement(gain: 6);
        await first.Optimizer.OptimizeAsync(network);

        var controller = new FakeController { RestoreOutcome = LatencyRestoreOutcome.Failed };
        var replay = new LatencyScenario(controller, FakeProbe.Flat(controller), profiles: first.Profiles);

        var result = await replay.Optimizer.OptimizeAsync(network);

        Assert.Equal(LatencyOptimizationStatus.Failed, result.Status);
        Assert.Contains("snapshot", result.StatusLine, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(replay.Snapshots.Value);
        Assert.Contains(Fake.DefaultKeyword, controller.Live);
        Assert.Empty(first.Profiles.Profiles);
    }

    [Fact]
    public async Task ACandidateAlreadyRejectedOnThisExactNetworkIsNotRetested()
    {
        var scenario = new LatencyScenario();
        var network = Fake.Network("cached");

        await scenario.Optimizer.OptimizeAsync(network);
        Assert.NotEmpty(scenario.Controller.Applied);

        var second = new LatencyScenario(new FakeController(), profiles: scenario.Profiles);
        var result = await second.Optimizer.OptimizeAsync(network);

        Assert.Empty(second.Controller.Applied);
        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Contains("denenecek güvenli bir ayar kalmadı", result.StatusLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// A profile proved on one adapter must never suppress testing on another. Same
    /// network, different card.
    /// </summary>
    [Fact]
    public async Task AProfileFromAnotherAdapterIsNotReused()
    {
        var scenario = new LatencyScenario();
        await scenario.Optimizer.OptimizeAsync(Fake.Network("shared-net"));

        var other = new LatencyScenario(new FakeController(), profiles: scenario.Profiles);
        await other.Optimizer.OptimizeAsync(Fake.Network("shared-net") with
        {
            AdapterId = "adapter-other",
            AdapterName = "Realtek Wi-Fi",
        });

        Assert.NotEmpty(other.Controller.Applied);
    }

    [Fact]
    public async Task ADriverUpdateInvalidatesTheStoredResult()
    {
        var scenario = new LatencyScenario();
        var network = Fake.Network("driver");
        await scenario.Optimizer.OptimizeAsync(network);

        // Same adapter and network, different capability surface.
        var updated = new FakeController
        {
            Detect = fingerprint => Fake.Capability(fingerprint) with { InterfaceDescription = "new driver 2.0" },
        };
        var second = new LatencyScenario(updated, profiles: scenario.Profiles);
        await second.Optimizer.OptimizeAsync(network);

        Assert.NotEmpty(second.Controller.Applied);
    }

    /// <summary>
    /// A driver offering many candidates on a link that never settles must not be able to
    /// hold the user for half an hour. What was verified is still committed.
    /// </summary>
    [Fact]
    public async Task ARunStopsMeasuringOnceItHasSpentItsWholeBudget()
    {
        var controller = new FakeController
        {
            Properties = [Fake.DefaultKeyword, Fake.SecondKeyword],
        };
        var scenario = new LatencyScenario(
            controller,
            FakeProbe.Flat(controller),
            new LatencyOptimizerOptions { MinimumCycles = 2, MaximumCycles = 3, TotalBudget = TimeSpan.Zero });

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("budget"));

        Assert.Empty(controller.Applied);
        Assert.Empty(result.Verdicts);
        Assert.Contains(scenario.Logs, line => line.Contains("latency.run.budget", StringComparison.Ordinal));
        Assert.Contains(result.Notices, notice => notice.Contains("Süre sınırı", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProfileOlderThanAMonthIsMeasuredAgain()
    {
        var scenario = new LatencyScenario();
        var network = Fake.Network("stale");
        await scenario.Optimizer.OptimizeAsync(network);

        var later = DateTimeOffset.UtcNow + LatencyProfile.MaximumAge + TimeSpan.FromDays(1);
        var second = new LatencyScenario(new FakeController(), profiles: scenario.Profiles, now: () => later);
        await second.Optimizer.OptimizeAsync(network);

        Assert.NotEmpty(second.Controller.Applied);
    }
}
