using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.Ipc;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

public sealed class ProtectionServiceLatencyRecoveryTests
{
    [Fact]
    public async Task StartupWithLatencyModeOffRestoresACommittedSnapshot()
    {
        using var directory = new TempDirectory();
        var store = new ConfigStore(directory.File("settings.json"), directory.File("networks.json"));
        var controller = new FakeController();
        controller.Live.Add("SelectiveSuspend");
        var snapshots = new FakeSnapshotStore
        {
            Value = Fake.Snapshot("adapter-committed", "SelectiveSuspend"),
        };
        var optimizer = new LatencyOptimizer(
            controller,
            FakeProbe.Flat(controller),
            snapshots,
            profiles: new FakeProfileStore());
        await using var service = new ProtectionService(
            store,
            new LearnedDomainStore(directory.File("learned.json")),
            optimizer);

        await service.StartIndependentFeaturesAsync();

        Assert.Empty(controller.Live);
        Assert.Equal(["SelectiveSuspend"], controller.Restored);
        Assert.Null(snapshots.Value);
    }

    /// <summary>
    /// A run that found nothing locally fixable leaves the mode on and watching. Turning
    /// the switch off behind the user's back would misdescribe their own settings back to
    /// them and invite them to keep switching it on to no effect.
    /// </summary>
    [Fact]
    public async Task ARunThatFoundNothingLeavesTheModeOnAndSaysItIsMonitoring()
    {
        using var harness = new LatencyServiceHarness();

        var result = await harness.Service.SetLowLatencyModeAsync(true);

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.True(harness.Service.Settings.LowLatencyMode);

        var status = harness.Service.LatencyStatus;
        Assert.Equal(LatencyModeState.NoLocalGain, status.State);
        Assert.True(status.ModeEnabled);
        Assert.NotEqual(LatencyModeState.Off, status.State);
    }

    [Fact]
    public async Task TurningTheModeOffRemovesEveryPolicyThisApplicationOwns()
    {
        using var harness = new LatencyServiceHarness();
        await harness.Qos.CreateAsync(new QosPolicyRequest { Name = "DPIBypass.Latency.bulk.x" });

        await harness.Service.SetLowLatencyModeAsync(false);

        Assert.Empty(harness.Qos.Policies);
        Assert.False(harness.Service.Settings.LowLatencyMode);
        Assert.Equal(LatencyModeState.Off, harness.Service.LatencyStatus.State);
    }

    [Fact]
    public async Task RestoringLatencyAlsoRemovesThisApplicationsPolicies()
    {
        using var harness = new LatencyServiceHarness();
        await harness.Qos.CreateAsync(new QosPolicyRequest { Name = "DPIBypass.Latency.bulk.y" });

        await harness.Service.RestoreLatencyAsync();

        Assert.Empty(harness.Qos.Policies);
    }

    /// <summary>
    /// A policy created by a build that died before it could record one has nothing left
    /// pointing at it, so startup with the mode off sweeps the whole namespace.
    /// </summary>
    [Fact]
    public async Task StartupWithTheModeOffSweepsOrphanedPolicies()
    {
        using var harness = new LatencyServiceHarness();
        await harness.Qos.CreateAsync(new QosPolicyRequest { Name = "DPIBypass.Latency.bulk.orphan" });

        await harness.Service.StartIndependentFeaturesAsync();

        Assert.Empty(harness.Qos.Policies);
    }

    [Fact]
    public async Task ChangingTheTargetIsPersistedAndReachesTheOptimizer()
    {
        using var harness = new LatencyServiceHarness();

        harness.Service.SetLatencyPreferences(harness.Service.Settings.Latency with
        {
            TargetKind = LatencyTargetKind.Custom,
            TargetHost = "mc.example.com",
            TargetPort = 25565,
            TargetProtocol = LatencyProtocol.Tcp,
        });

        var reloaded = harness.Store.Load();

        Assert.Equal(LatencyTargetKind.Custom, reloaded.Latency.TargetKind);
        Assert.Equal("mc.example.com", reloaded.Latency.TargetHost);
        Assert.Equal(25565, reloaded.Latency.TargetPort);
        Assert.Contains("25565", harness.Service.LatencyStatus.Target + reloaded.Latency.ToSpec().Describe());
    }

    /// <summary>Wires one service up with doubles for both latency lanes.</summary>
    private sealed class LatencyServiceHarness : IDisposable
    {
        private readonly TempDirectory _directory = new("dpibypass-latency-service");

        public LatencyServiceHarness()
        {
            Store = new ConfigStore(_directory.File("settings.json"), _directory.File("networks.json"));
            Controller = new FakeController();
            Qos = new FakeQosController();

            var optimizer = new LatencyOptimizer(
                Controller,
                FakeProbe.Flat(Controller),
                new FakeSnapshotStore(),
                profiles: new FakeProfileStore(),
                targets: new FakeTargetResolver(),
                environmentSampler: new FakeEnvironmentSampler(),
                resourceRestorers: [],
                delay: (_, _) => Task.CompletedTask);

            Service = new ProtectionService(
                Store,
                new LearnedDomainStore(_directory.File("learned.json")),
                optimizer,
                loadedLatency: new LoadedLatencyLane(
                    qos: Qos,
                    snapshots: new FakeSnapshotStore(),
                    capture: () => Fake.Network("service"),
                    log: _ => { }));
        }

        public ConfigStore Store { get; }

        public FakeController Controller { get; }

        public FakeQosController Qos { get; }

        public ProtectionService Service { get; }

        public void Dispose()
        {
            Service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _directory.Dispose();
        }
    }
}

/// <summary>
/// The parts of the service that decide what the tuner is asked to do. Everything
/// here runs without a driver or a network, because that is the point: choosing the
/// operator profile is a decision, not an I/O operation.
/// </summary>
public sealed class ProtectionServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly ProtectionService _service;

    public ProtectionServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"dpibypass-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        var store = new ConfigStore(
            Path.Combine(_directory, "settings.json"),
            Path.Combine(_directory, "networks.json"));

        _service = new ProtectionService(store, new LearnedDomainStore(Path.Combine(_directory, "learned.json")));
    }

    [Fact]
    public void ForcingAnOperatorIsReflectedStraightAway()
    {
        _service.ApplyManualIsp(IspCatalog.TurkTelekomHome.Id);

        Assert.Equal(IspCatalog.TurkTelekomHome.Id, _service.Isp.Id);
        Assert.Equal(IspCatalog.TurkTelekomHome.Id, _service.Settings.ManualIspProfileId);
        Assert.NotNull(_service.Detection);
        Assert.False(_service.Detection!.WasAutomatic);
    }

    /// <summary>
    /// Going back to "Otomatik algıla" has to drop the forced answer. Keeping it meant
    /// the status line went on naming the operator the user had just deselected, and -
    /// worse - the next sweep was still ordered by that operator's strategy list.
    /// </summary>
    [Fact]
    public void ChoosingAutomaticDetectionDropsThePreviouslyForcedOperator()
    {
        _service.ApplyManualIsp(IspCatalog.VodafoneMobile.Id);
        Assert.Equal(IspCatalog.VodafoneMobile.Id, _service.Isp.Id);

        _service.ApplyManualIsp(null);

        Assert.Null(_service.Settings.ManualIspProfileId);
        Assert.Null(_service.Detection);
        Assert.Equal(IspCatalog.Unknown.Id, _service.Isp.Id);
    }

    [Fact]
    public void TheOperatorChoiceIsPersisted()
    {
        _service.ApplyManualIsp(IspCatalog.Superonline.Id);

        var reloaded = new ConfigStore(
            Path.Combine(_directory, "settings.json"),
            Path.Combine(_directory, "networks.json")).Load();

        Assert.Equal(IspCatalog.Superonline.Id, reloaded.ManualIspProfileId);
    }

    /// <summary>
    /// The neutral profile still has to be able to drive a sweep: it is what the tuner
    /// is handed between deselecting an operator and detection finishing.
    /// </summary>
    [Fact]
    public void TheUnknownProfileStillOffersEveryStrategy()
    {
        Assert.NotEmpty(IspCatalog.Unknown.PreferredStrategies);
        Assert.DoesNotContain("passthrough", IspCatalog.Unknown.PreferredStrategies);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup only.
        }
    }
}

/// <summary>
/// The service side of the mobile hotspot feature: cleanup that can be called at any
/// time, from anywhere, twice.
/// </summary>
public sealed class ProtectionServiceHotspotTests : IDisposable
{
    private readonly TempDirectory _directory = new("dpibypass-hotspot");

    [Fact]
    public async Task ALegacyConfigIsSafelyMigratedAndPersistedWhenTheServiceIsBuilt()
    {
        File.WriteAllText(SettingsPath, """
            {
              "HotspotTtlFix": true,
              "HotspotTtlNetworks": [ { "Key": "abc", "DisplayName": "phone", "AdapterName": "Wi-Fi" } ]
            }
            """);

        await using var service = Build();

        Assert.False(service.Settings.HotspotTtlFix);
        Assert.True(service.Settings.VodafoneModeEnabled);
        Assert.Single(service.Settings.VodafoneModeNetworks);
        Assert.True(service.Settings.HotspotDiagnostics);

        // On disk, not only in memory: the next process to read this file, including the
        // uninstaller, must see the cleaned version.
        var onDisk = File.ReadAllText(SettingsPath);
        Assert.Contains("\"HotspotTtlFix\": false", onDisk, StringComparison.Ordinal);
        Assert.Contains("\"VodafoneModeEnabled\": true", onDisk, StringComparison.Ordinal);
        Assert.Contains("\"VodafoneModeNetworks\"", onDisk, StringComparison.Ordinal);
        Assert.Contains("\"Key\": \"abc\"", onDisk, StringComparison.Ordinal);
        Assert.Contains("\"HotspotDiagnostics\": true", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupCanBeCalledTwiceAndOnAMachineThatNeverHadTheOldMode()
    {
        await using var service = Build();

        var first = service.CleanUpLegacyHotspotConfiguration();
        var second = service.CleanUpLegacyHotspotConfiguration();

        Assert.False(first.Changed);
        Assert.False(second.Changed);
        Assert.False(service.Settings.HotspotTtlFix);
    }

    [Fact]
    public async Task CleanupReportsWhatItActuallyRemoved()
    {
        File.WriteAllText(SettingsPath, """{"HotspotTtlFix": true}""");
        await using var service = Build();

        // The constructor already migrated, so an explicit cleanup finds nothing left -
        // which is the point: there is no second copy of the state to go stale.
        Assert.False(service.CleanUpLegacyHotspotConfiguration().Changed);
        Assert.False(service.Settings.HotspotTtlFix);
    }

    [Fact]
    public async Task RunningTheDiagnosticsRemembersTheResultForTheUi()
    {
        var diagnostics = new FakeHotspotDiagnostics();
        await using var service = Build(diagnostics);

        Assert.Null(service.HotspotStatus.LastResult);

        var result = await service.RunHotspotDiagnosticsAsync();

        Assert.Same(result, service.HotspotStatus.LastResult);
        Assert.Equal(1, diagnostics.Runs);
    }

    [Fact]
    public async Task ANewDiagnosticsRunCancelsTheObsoleteOneBeforeItCanPublish()
    {
        var diagnostics = new CancellableHotspotDiagnostics();
        await using var service = Build(diagnostics);

        var obsolete = service.RunHotspotDiagnosticsAsync();
        await diagnostics.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var current = service.RunHotspotDiagnosticsAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete);
        var result = await current;

        Assert.Equal(2, diagnostics.Runs);
        Assert.Equal("current", result.NetworkName);
        Assert.Same(result, service.HotspotStatus.LastResult);
    }

    [Fact]
    public async Task TheDiagnosticsSwitchIsPersisted()
    {
        await using var service = Build();

        service.SetHotspotDiagnostics(true);

        Assert.True(new ConfigStore(SettingsPath, ProfilesPath).Load().HotspotDiagnostics);
    }

    [Fact]
    public async Task SettingTheSwitchToWhatItAlreadyIsDoesNotWriteAnything()
    {
        await using var service = Build();
        service.SetHotspotDiagnostics(true);

        var written = File.GetLastWriteTimeUtc(SettingsPath);
        service.SetHotspotDiagnostics(true);

        Assert.Equal(written, File.GetLastWriteTimeUtc(SettingsPath));
    }

    [Fact]
    public async Task NormalStartupDoesNotEraseUnrelatedConfiguration()
    {
        File.WriteAllText(SettingsPath, """
            {
              "HotspotTtlFix": true,
              "HotspotTtlNetworks": [ { "Key": "abc", "DisplayName": "phone", "AdapterName": "Wi-Fi" } ],
              "ManualIspProfileId": "vodafone-mobile",
              "StartEngineOnLaunch": false,
              "MinimiseToTrayOnClose": false,
              "ExtraDomains": ["kept.example"]
            }
            """);

        await using var service = Build();

        Assert.Equal("vodafone-mobile", service.Settings.ManualIspProfileId);
        Assert.False(service.Settings.StartEngineOnLaunch);
        Assert.False(service.Settings.MinimiseToTrayOnClose);
        Assert.Equal(["kept.example"], service.Settings.ExtraDomains);
        Assert.Single(service.Settings.VodafoneModeNetworks);

        var reloaded = new ConfigStore(SettingsPath, ProfilesPath).Load();
        Assert.Equal("vodafone-mobile", reloaded.ManualIspProfileId);
        Assert.False(reloaded.StartEngineOnLaunch);
        Assert.False(reloaded.MinimiseToTrayOnClose);
        Assert.Equal(["kept.example"], reloaded.ExtraDomains);
        Assert.Single(reloaded.VodafoneModeNetworks);
    }

    [Fact]
    public async Task SafeVodafoneModeRegistersTheNetworkWithoutInstallingTtlState()
    {
        await using var service = Build();
        var network = new NetworkFingerprint
        {
            Ssid = "phone",
            AdapterName = "Wi-Fi",
            InterfaceIndex = 7,
        };

        service.EnableVodafoneMode(network);

        Assert.True(service.Settings.VodafoneModeEnabled);
        Assert.True(service.Settings.HotspotDiagnostics);
        Assert.False(service.Settings.HotspotTtlFix);
        Assert.Empty(service.Settings.HotspotTtlNetworks);
        Assert.True(service.Settings.VodafoneNetworkRegistered(network.Key));
        Assert.True(service.HotspotStatus.RegisteredHere);
    }

    [Fact]
    public async Task DisablingAndCleanupPreserveSafeNetworksAndOtherPreferences()
    {
        await using var service = Build();
        var network = new NetworkFingerprint { Ssid = "phone", InterfaceIndex = 7 };
        service.Settings.ManualIspProfileId = "vodafone-mobile";
        service.EnableVodafoneMode(network);

        service.DisableVodafoneMode();
        var first = service.CleanUpLegacyHotspotConfiguration();
        var second = service.CleanUpLegacyHotspotConfiguration();

        Assert.False(service.Settings.VodafoneModeEnabled);
        Assert.False(service.Settings.HotspotDiagnostics);
        Assert.True(service.Settings.VodafoneNetworkRegistered(network.Key));
        Assert.Equal("vodafone-mobile", service.Settings.ManualIspProfileId);
        Assert.False(first.Changed);
        Assert.False(second.Changed);
    }

    [Fact]
    public async Task VodafoneControlCommandsKeepStatusAndOffCompatibility()
    {
        await using var service = Build();
        service.EnableVodafoneMode(new NetworkFingerprint { Ssid = "phone", InterfaceIndex = 7 });
        var commands = new ControlCommands(service);

        var status = await commands.HandleAsync(new ControlRequest
        {
            Command = ControlProtocol.Commands.VodafoneStatus,
        });
        var disabled = await commands.HandleAsync(new ControlRequest
        {
            Command = ControlProtocol.Commands.VodafoneOff,
        });

        Assert.True(status.Ok);
        Assert.Contains("etkin", status.Text, StringComparison.Ordinal);
        Assert.True(disabled.Ok);
        Assert.Contains("kapalı", disabled.Text, StringComparison.Ordinal);
        Assert.Single(service.Settings.VodafoneModeNetworks);
    }

    [Fact]
    public void AutomaticDiagnosticsPreservePr11CompatibilityThenRespectRegisteredNetworks()
    {
        var current = new NetworkFingerprint { Ssid = "current", InterfaceIndex = 7 };
        var other = new NetworkFingerprint { Ssid = "other", InterfaceIndex = 8 };
        var settings = new AppSettings
        {
            VodafoneModeEnabled = true,
            HotspotDiagnostics = true,
        };

        // PR #11 may already have erased the list. Do not silently turn off the
        // automatic diagnostics it enabled for the affected user.
        Assert.True(ProtectionService.ShouldRunHotspotDiagnostics(settings, current));

        settings.RememberVodafoneNetwork(current.Key, current.DisplayName, "Wi-Fi");

        Assert.True(ProtectionService.ShouldRunHotspotDiagnostics(settings, current));
        Assert.False(ProtectionService.ShouldRunHotspotDiagnostics(settings, other));

        settings.HotspotDiagnostics = false;
        Assert.False(ProtectionService.ShouldRunHotspotDiagnostics(settings, current));
    }

    private string SettingsPath => Path.Combine(_directory.Path, "settings.json");

    private string ProfilesPath => Path.Combine(_directory.Path, "networks.json");

    private ProtectionService Build(IMobileHotspotDiagnostics? diagnostics = null) => new(
        new ConfigStore(SettingsPath, ProfilesPath),
        new LearnedDomainStore(Path.Combine(_directory.Path, "learned.json")),
        hotspotDiagnostics: diagnostics ?? new FakeHotspotDiagnostics());

    public void Dispose() => _directory.Dispose();

    private sealed class FakeHotspotDiagnostics : IMobileHotspotDiagnostics
    {
        public int Runs { get; private set; }

        public Task<HotspotDiagnosticResult> RunAsync(
            NetworkFingerprint network,
            CancellationToken cancellationToken = default)
        {
            Runs++;

            return Task.FromResult(new HotspotDiagnosticResult
            {
                NetworkName = network.DisplayName,
                AdapterName = network.AdapterName ?? "-",
                HasIpv4 = true,
                HasIpv6 = false,
                Ipv4Works = true,
                Ipv6Works = false,
                DnsWorks = true,
                AddressKind = HotspotAddressKind.Private,
                VpnAdapterActive = false,
            });
        }
    }

    private sealed class CancellableHotspotDiagnostics : IMobileHotspotDiagnostics
    {
        private int _runs;

        public int Runs => _runs;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HotspotDiagnosticResult> RunAsync(
            NetworkFingerprint network,
            CancellationToken cancellationToken = default)
        {
            var run = Interlocked.Increment(ref _runs);
            if (run == 1)
            {
                FirstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HotspotDiagnosticResult
            {
                NetworkName = "current",
                AdapterName = network.AdapterName ?? "-",
                HasIpv4 = true,
                HasIpv6 = false,
                Ipv4Works = true,
                Ipv6Works = false,
                DnsWorks = true,
                AddressKind = HotspotAddressKind.Private,
                VpnAdapterActive = false,
            };
        }
    }
}
