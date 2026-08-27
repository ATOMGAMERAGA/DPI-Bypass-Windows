using System.Net.NetworkInformation;
using System.Text.Json;
using DpiBypass.Core.Config;
using DpiBypass.Core.Network;
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
        var directory = NewDirectory();
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            File.WriteAllText(settingsPath, "{\"StartWithWindows\":false}");

            var settings = new ConfigStore(settingsPath, Path.Combine(directory, "networks.json")).Load();

            Assert.False(settings.LowLatencyMode);
            Assert.False(settings.StartWithWindows);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VirtualAdaptersAreNeverCandidates()
    {
        var capability = Capability(Network("virtual")) with { IsPhysical = false, IsVirtual = true };
        Assert.False(capability.IsEligible);
        Assert.Empty(capability.BuildSafeCandidates());
    }

    [Fact]
    public void InterruptModerationUsesRegistryKeywordNotALocalisedDisplayName()
    {
        var capability = Capability(Network("ethernet")) with
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
    }

    [Fact]
    public void UnsupportedAndAlreadyDisabledPropertiesAreSkipped()
    {
        var capability = Capability(Network("unsupported")) with
        {
            PowerManagement = new Dictionary<string, int>
            {
                ["SelectiveSuspend"] = 0,
                ["DeviceSleepOnDisconnect"] = 1,
                ["D0PacketCoalescing"] = 0,
            },
            AdvancedProperties = [],
        };

        Assert.Empty(capability.BuildSafeCandidates());
    }

    [Fact]
    public void PacketLossIncreaseCanNeverBeCalledAnImprovement()
        => Assert.False(LatencyOptimizer.HasVerifiedImprovement(
            Measurement(25, 4, 34, loss: 0),
            Measurement(19, 2, 26, loss: 8)));

    [Fact]
    public void AClearlyWorseMedianCanNeverBeCalledAnImprovement()
        => Assert.False(LatencyOptimizer.HasVerifiedImprovement(
            Measurement(20, 3, 28),
            Measurement(25, 2, 30)));

    [Fact]
    public void AOneMillisecondNoiseSampleIsNotPresentedAsAGain()
        => Assert.False(LatencyOptimizer.HasVerifiedImprovement(
            Measurement(21, 3, 29),
            Measurement(20, 3, 29)));

    [Fact]
    public void MeasurementCalculatesMedianP95JitterLossAndGatewaySeparately()
    {
        var measurement = LatencyMeasurement.Create(
            "1.1.1.1",
            "ICMP",
            [10, 12, 14, 16],
            remoteAttempts: 5,
            gatewaySamples: [1, 2, 3],
            gatewayAttempts: 3);

        Assert.Equal(10, measurement.MinimumRttMs);
        Assert.Equal(13, measurement.MedianRttMs);
        Assert.Equal(15.7, measurement.P95RttMs, precision: 1);
        Assert.Equal(2, measurement.JitterMs);
        Assert.Equal(20, measurement.PacketLossPercent);
        Assert.Equal(2, measurement.GatewayMedianRttMs);
    }

    [Fact]
    public async Task SnapshotWritesAreAtomicAndRoundTripEveryOriginalValue()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "latency-snapshot.json");
        var store = new LatencySnapshotStore(path);
        var snapshot = Snapshot("adapter", "*InterruptModeration", LatencySettingKind.AdvancedProperty);

        try
        {
            await store.SaveAsync(snapshot);
            var loaded = await store.LoadAsync();

            Assert.NotNull(loaded);
            Assert.Equal(snapshot.AdapterId, loaded!.AdapterId);
            Assert.Equal(snapshot.NetworkKey, loaded.NetworkKey);
            Assert.Equal(snapshot.Settings[0].PropertyName, loaded.Settings[0].PropertyName);
            Assert.Equal(snapshot.Settings[0].OriginalValues, loaded.Settings[0].OriginalValues);
            Assert.False(File.Exists(path + ".tmp"));

            await store.SaveAsync(snapshot with { AdapterName = "renamed" });
            Assert.Equal("renamed", (await store.LoadAsync())!.AdapterName);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dpibypass-latency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    internal static NetworkFingerprint Network(string suffix, bool online = true) => new()
    {
        AdapterId = $"adapter-{suffix}",
        AdapterName = $"Intel {suffix}",
        AdapterType = NetworkInterfaceType.Ethernet,
        InterfaceIndex = online ? 10 : 0,
        GatewayAddress = online ? $"192.0.2.{Math.Abs((long)suffix.GetHashCode()) % 200 + 1}" : null,
    };

    internal static AdapterLatencyCapability Capability(NetworkFingerprint network, params string[] powerProperties)
    {
        powerProperties = powerProperties.Length == 0 ? ["SelectiveSuspend"] : powerProperties;
        return new AdapterLatencyCapability
        {
            AdapterId = network.AdapterId!,
            AdapterName = network.AdapterName!,
            AdapterType = network.AdapterType,
            IsPhysical = true,
            IsVirtual = false,
            IsUp = true,
            PowerManagement = powerProperties.ToDictionary(property => property, _ => 2, StringComparer.OrdinalIgnoreCase),
        };
    }

    internal static LatencyMeasurement Measurement(double median, double jitter, double p95, double loss = 0) => new()
    {
        MeasuredAt = DateTimeOffset.UtcNow,
        RemoteEndpoint = "1.1.1.1",
        Protocol = "ICMP",
        RemoteAttempts = 12,
        RemoteReplies = loss == 0 ? 12 : 11,
        GatewayAttempts = 6,
        GatewayReplies = 6,
        MinimumRttMs = Math.Max(0.1, median - 3),
        MedianRttMs = median,
        P95RttMs = p95,
        JitterMs = jitter,
        PacketLossPercent = loss,
        GatewayMedianRttMs = 1.2,
    };

    internal static LatencyOptimizationSnapshot Snapshot(
        string adapterId,
        string property,
        LatencySettingKind kind = LatencySettingKind.PowerManagement) => new()
    {
        AdapterId = adapterId,
        AdapterName = adapterId,
        NetworkKey = "network",
        CreatedAt = DateTimeOffset.UtcNow,
        Settings =
        [
            new LatencySettingSnapshot
            {
                AdapterId = adapterId,
                AdapterName = adapterId,
                Kind = kind,
                PropertyName = property,
                OriginalPowerValue = kind == LatencySettingKind.PowerManagement ? 2 : null,
                OriginalValues = kind == LatencySettingKind.AdvancedProperty ? ["1"] : [],
                AppliedDescription = property,
                CapturedAt = DateTimeOffset.UtcNow,
            },
        ],
    };
}

public sealed class LatencyOptimizerTests
{
    [Fact]
    public async Task OfflineStateFailsGracefullyWithoutTouchingTheAdapter()
    {
        var controller = new FakeController();
        var optimizer = CreateOptimizer(controller, new FakeProbe(), new FakeSnapshotStore());

        var result = await optimizer.OptimizeAsync(LatencyModelTests.Network("offline", online: false));

        Assert.Equal(LatencyOptimizationStatus.Offline, result.Status);
        Assert.Empty(controller.Applied);
    }

    [Fact]
    public async Task ConnectivityFailureImmediatelyRollsBack()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(LatencyModelTests.Measurement(25, 5, 36))
        {
            Connectivity = new LatencyConnectivity(false, false),
        };
        var snapshots = new FakeSnapshotStore();
        var optimizer = CreateOptimizer(controller, probe, snapshots);

        var result = await optimizer.OptimizeAsync(LatencyModelTests.Network("connectivity"));

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Equal(["SelectiveSuspend"], controller.Restored);
        Assert.Null(snapshots.Value);
    }

    [Fact]
    public async Task NoMeasuredGainRestoresTheCandidate()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(
            LatencyModelTests.Measurement(24, 3, 31),
            LatencyModelTests.Measurement(23.8, 3.1, 31));
        var snapshots = new FakeSnapshotStore();
        var optimizer = CreateOptimizer(controller, probe, snapshots);

        var result = await optimizer.OptimizeAsync(LatencyModelTests.Network("noise"));

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Contains("geri alındı", result.StatusLine, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["SelectiveSuspend"], controller.Restored);
        Assert.Null(snapshots.Value);
    }

    [Fact]
    public async Task RealRepeatableGainKeepsTheExactSnapshot()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(
            LatencyModelTests.Measurement(26, 5, 38),
            LatencyModelTests.Measurement(21, 2.5, 29),
            LatencyModelTests.Measurement(21.2, 2.6, 29.5));
        var snapshots = new FakeSnapshotStore();
        var optimizer = CreateOptimizer(controller, probe, snapshots);

        var result = await optimizer.OptimizeAsync(LatencyModelTests.Network("gain"));

        Assert.Equal(LatencyOptimizationStatus.Active, result.Status);
        Assert.True(result.HasVerifiedGain);
        Assert.NotNull(snapshots.Value);
        Assert.Single(snapshots.Value!.Settings);
        Assert.Empty(controller.Restored);
        Assert.Contains("26.0 → 21.2", result.StatusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PacketLossIncreaseRollsBackEvenWhenMedianLooksBetter()
    {
        var controller = new FakeController();
        var probe = new FakeProbe(
            LatencyModelTests.Measurement(26, 5, 38),
            LatencyModelTests.Measurement(20, 2, 28, loss: 8));
        var optimizer = CreateOptimizer(controller, probe, new FakeSnapshotStore());

        var result = await optimizer.OptimizeAsync(LatencyModelTests.Network("loss"));

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Equal(["SelectiveSuspend"], controller.Restored);
    }

    [Fact]
    public async Task ApplyExceptionRollsBackEverythingInReverseOrder()
    {
        var controller = new FakeController
        {
            PowerProperties = ["SelectiveSuspend", "DeviceSleepOnDisconnect"],
            ThrowOnApply = "DeviceSleepOnDisconnect",
        };
        var probe = new FakeProbe(
            LatencyModelTests.Measurement(28, 6, 40),
            LatencyModelTests.Measurement(22, 3, 31));
        var snapshots = new FakeSnapshotStore();
        var optimizer = CreateOptimizer(controller, probe, snapshots);

        var result = await optimizer.OptimizeAsync(LatencyModelTests.Network("throw"));

        Assert.Equal(LatencyOptimizationStatus.Failed, result.Status);
        Assert.Equal(["DeviceSleepOnDisconnect", "SelectiveSuspend"], controller.Restored);
        Assert.Null(snapshots.Value);
    }

    [Fact]
    public async Task MissingAdapterKeepsSnapshotForLaterRecovery()
    {
        var snapshots = new FakeSnapshotStore
        {
            Value = LatencyModelTests.Snapshot("missing", "SelectiveSuspend"),
        };
        var controller = new FakeController { RestoreOutcome = LatencyRestoreOutcome.MissingAdapter };
        var optimizer = CreateOptimizer(controller, new FakeProbe(), snapshots);

        var result = await optimizer.RestoreAsync();

        Assert.Equal(LatencyOptimizationStatus.Failed, result.Status);
        Assert.NotNull(snapshots.Value);
    }

    [Fact]
    public async Task MissingPropertyDoesNotPreventTheRemainingSnapshotFromClearing()
    {
        var snapshots = new FakeSnapshotStore
        {
            Value = LatencyModelTests.Snapshot("updated-driver", "SelectiveSuspend"),
        };
        var controller = new FakeController { RestoreOutcome = LatencyRestoreOutcome.MissingProperty };
        var optimizer = CreateOptimizer(controller, new FakeProbe(), snapshots);

        var result = await optimizer.RestoreAsync();

        Assert.Equal(LatencyOptimizationStatus.Disabled, result.Status);
        Assert.Null(snapshots.Value);
    }

    [Fact]
    public async Task ModeOffPerformsAFullRestore()
    {
        var controller = new FakeController();
        var snapshots = new FakeSnapshotStore();
        var optimizer = CreateSuccessfulOptimizer(controller, snapshots, "mode-off");

        await optimizer.OptimizeAsync(LatencyModelTests.Network("mode-off"));
        var result = await optimizer.StopAndRestoreAsync();

        Assert.Equal(LatencyOptimizationStatus.Disabled, result.Status);
        Assert.Equal(["SelectiveSuspend"], controller.Restored);
        Assert.Null(snapshots.Value);
    }

    [Fact]
    public async Task AppShutdownRestoresPersistentNicSettings()
    {
        var controller = new FakeController();
        var snapshots = new FakeSnapshotStore();
        var optimizer = CreateSuccessfulOptimizer(controller, snapshots, "shutdown");

        await optimizer.OptimizeAsync(LatencyModelTests.Network("shutdown"));
        await optimizer.DisposeAsync();

        Assert.Equal(["SelectiveSuspend"], controller.Restored);
        Assert.Null(snapshots.Value);
    }

    [Fact]
    public async Task NetworkChangeRestoresTheOldAdapterBeforeApplyingTheNewOne()
    {
        var controller = new FakeController();
        var snapshots = new FakeSnapshotStore();
        var probe = new FakeProbe(
            LatencyModelTests.Measurement(28, 6, 40),
            LatencyModelTests.Measurement(21, 2, 29),
            LatencyModelTests.Measurement(21.2, 2.1, 30),
            LatencyModelTests.Measurement(31, 6, 44),
            LatencyModelTests.Measurement(24, 3, 34),
            LatencyModelTests.Measurement(24.2, 3, 34.5));
        var optimizer = CreateOptimizer(controller, probe, snapshots);

        await optimizer.OptimizeAsync(LatencyModelTests.Network("old"));
        await optimizer.OptimizeNetworkChangeAsync(LatencyModelTests.Network("new"));

        Assert.Equal("adapter-old:SelectiveSuspend", controller.Events[0]);
        Assert.Contains("restore:adapter-old:SelectiveSuspend", controller.Events);
        Assert.Equal("adapter-new:SelectiveSuspend", controller.Events[^1]);
    }

    [Fact]
    public async Task DuplicateNetworkNotificationDoesNotApplyTwice()
    {
        var controller = new FakeController();
        var snapshots = new FakeSnapshotStore();
        var network = LatencyModelTests.Network("same");
        var optimizer = CreateSuccessfulOptimizer(controller, snapshots, "same");

        await optimizer.OptimizeAsync(network);
        await optimizer.OptimizeNetworkChangeAsync(network);

        Assert.Single(controller.Applied);
    }

    [Fact]
    public async Task ConcurrentOperationsNeverApplyAtTheSameTime()
    {
        var controller = new FakeController { ApplyDelay = TimeSpan.FromMilliseconds(150) };
        var probe = new FakeProbe(
            LatencyModelTests.Measurement(30, 6, 43),
            LatencyModelTests.Measurement(31, 6, 44),
            LatencyModelTests.Measurement(24, 3, 34),
            LatencyModelTests.Measurement(24.2, 3, 34.5));
        var optimizer = CreateOptimizer(controller, probe, new FakeSnapshotStore());

        var first = optimizer.OptimizeAsync(LatencyModelTests.Network("concurrent-a"));
        await Task.Delay(30);
        var second = optimizer.OptimizeAsync(LatencyModelTests.Network("concurrent-b"));
        await Task.WhenAll(first, second);

        Assert.Equal(1, controller.MaxConcurrentApplies);
    }

    [Fact]
    public async Task CancellationAfterSnapshotCaptureRestoresTheCandidate()
    {
        var controller = new FakeController { ApplyDelay = TimeSpan.FromSeconds(5) };
        var snapshots = new FakeSnapshotStore();
        var optimizer = CreateOptimizer(
            controller,
            new FakeProbe(LatencyModelTests.Measurement(30, 6, 43)),
            snapshots);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await optimizer.OptimizeAsync(LatencyModelTests.Network("cancel"), cancellation.Token);

        Assert.Equal(LatencyOptimizationStatus.Cancelled, result.Status);
        Assert.Equal(["SelectiveSuspend"], controller.Restored);
        Assert.Null(snapshots.Value);
    }

    private static LatencyOptimizer CreateSuccessfulOptimizer(
        FakeController controller,
        FakeSnapshotStore snapshots,
        string suffix) => CreateOptimizer(
        controller,
        new FakeProbe(
            LatencyModelTests.Measurement(28, 6, 40),
            LatencyModelTests.Measurement(21, 2, 29),
            LatencyModelTests.Measurement(21.2, 2.1, 30)),
        snapshots);

    private static LatencyOptimizer CreateOptimizer(
        FakeController controller,
        FakeProbe probe,
        FakeSnapshotStore snapshots)
        => new(controller, probe, snapshots, log: _ => { });

    private sealed class FakeController : ILatencyAdapterController
    {
        private int _concurrentApplies;

        public string[] PowerProperties { get; init; } = ["SelectiveSuspend"];
        public string? ThrowOnApply { get; init; }
        public TimeSpan ApplyDelay { get; init; }
        public LatencyRestoreOutcome RestoreOutcome { get; init; } = LatencyRestoreOutcome.Restored;
        public List<string> Applied { get; } = [];
        public List<string> Restored { get; } = [];
        public List<string> Events { get; } = [];
        public int MaxConcurrentApplies { get; private set; }

        public Task<AdapterLatencyCapability?> DetectAsync(
            NetworkFingerprint network,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AdapterLatencyCapability?>(LatencyModelTests.Capability(network, PowerProperties));

        public async Task<LatencyApplyResult> ApplyAsync(
            AdapterLatencyCapability adapter,
            LatencyOptimizationCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref _concurrentApplies);
            MaxConcurrentApplies = Math.Max(MaxConcurrentApplies, concurrent);
            Applied.Add(candidate.PropertyName);
            Events.Add($"{adapter.AdapterId}:{candidate.PropertyName}");

            try
            {
                if (ApplyDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ApplyDelay, cancellationToken);
                }

                if (candidate.PropertyName == ThrowOnApply)
                {
                    throw new InvalidOperationException("driver apply failed");
                }

                return new LatencyApplyResult(true);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentApplies);
            }
        }

        public Task<LatencyRestoreOutcome> RestoreAsync(
            LatencySettingSnapshot setting,
            CancellationToken cancellationToken = default)
        {
            Restored.Add(setting.PropertyName);
            Events.Add($"restore:{setting.AdapterId}:{setting.PropertyName}");
            return Task.FromResult(RestoreOutcome);
        }
    }

    private sealed class FakeProbe(params LatencyMeasurement[] measurements) : ILatencyProbe
    {
        private readonly Queue<LatencyMeasurement> _measurements = new(measurements);

        public LatencyConnectivity Connectivity { get; init; } = new(true, true);

        public Task<LatencyMeasurement> MeasureAsync(
            NetworkFingerprint network,
            string? remoteEndpoint = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_measurements.Count == 0)
            {
                throw new InvalidOperationException("Fake measurement queue is empty.");
            }

            return Task.FromResult(_measurements.Dequeue());
        }

        public Task<LatencyConnectivity> CheckConnectivityAsync(
            NetworkFingerprint network,
            string remoteEndpoint,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Connectivity);
    }

    private sealed class FakeSnapshotStore : ILatencySnapshotStore
    {
        public LatencyOptimizationSnapshot? Value { get; set; }

        public Task<LatencyOptimizationSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Clone(Value));

        public Task SaveAsync(LatencyOptimizationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Value = Clone(snapshot);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Value = null;
            return Task.CompletedTask;
        }

        private static LatencyOptimizationSnapshot? Clone(LatencyOptimizationSnapshot? snapshot)
            => snapshot is null
                ? null
                : snapshot with { Settings = snapshot.Settings.Select(setting => setting with { OriginalValues = [.. setting.OriginalValues] }).ToList() };
    }
}
