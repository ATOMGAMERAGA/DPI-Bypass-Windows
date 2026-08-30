using System.Net.NetworkInformation;
using DpiBypass.Core.Network;

namespace DpiBypass.Tests.Latency;

/// <summary>Builders for the values the latency tests are written in terms of.</summary>
internal static class Fake
{
    /// <summary>
    /// A distinct network per suffix. The gateway is derived from the name because
    /// <see cref="NetworkFingerprint.Key"/> is built from it, and two fixtures that
    /// accidentally shared a key would silently be the same network to the optimizer.
    /// </summary>
    public static NetworkFingerprint Network(string suffix, bool online = true) => new()
    {
        AdapterId = $"adapter-{suffix}",
        AdapterName = $"Intel {suffix}",
        AdapterType = NetworkInterfaceType.Ethernet,
        InterfaceIndex = online ? 10 : 0,
        GatewayAddress = online ? $"192.0.2.{(suffix.Sum(character => character) % 200) + 1}" : null,
    };

    public static AdapterLatencyCapability Capability(NetworkFingerprint network, params string[] powerProperties)
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

    /// <summary>A measurement with a plausible shape; only what a test names is varied.</summary>
    public static LatencyMeasurement Measurement(
        double median,
        double jitter = 3,
        double? p95 = null,
        double? p99 = null,
        double loss = 0,
        string endpoint = "1.1.1.1",
        LatencyLoadState load = LatencyLoadState.Idle,
        double gateway = 1.2,
        int attempts = 24,
        NetworkLoadSample? loadSample = null) => new()
        {
            MeasuredAt = DateTimeOffset.UtcNow,
            RemoteEndpoint = endpoint,
            Protocol = "ICMP",
            RemoteAttempts = attempts,
            RemoteReplies = (int)Math.Round(attempts * (100 - loss) / 100),
            GatewayAttempts = 8,
            GatewayReplies = 8,
            MinimumRttMs = Math.Max(0.1, median - 3),
            MedianRttMs = median,
            P95RttMs = p95 ?? median + 9,
            P99RttMs = p99 ?? (p95 ?? median + 9) + 4,
            JitterMs = jitter,
            PacketLossPercent = loss,
            GatewayMedianRttMs = gateway,
            GatewayP95RttMs = gateway + 0.5,
            Load = loadSample ?? Load(load),
        };

    public static NetworkLoadSample Load(LatencyLoadState state) => state switch
    {
        LatencyLoadState.Unknown => NetworkLoadSample.Unknown,
        LatencyLoadState.Idle => new NetworkLoadSample { State = state, UplinkKbps = 12, DownlinkKbps = 40 },
        LatencyLoadState.UplinkLoaded => new NetworkLoadSample { State = state, UplinkKbps = 9000, DownlinkKbps = 40 },
        LatencyLoadState.DownlinkLoaded => new NetworkLoadSample { State = state, UplinkKbps = 12, DownlinkKbps = 45000 },
        _ => new NetworkLoadSample { State = state, UplinkKbps = 9000, DownlinkKbps = 45000 },
    };

    public static LatencyOptimizationCandidate Candidate(string property = "SelectiveSuspend", bool cpuSensitive = false) => new()
    {
        Kind = LatencySettingKind.PowerManagement,
        PropertyName = property,
        OriginalPowerValue = 2,
        DesiredPowerValue = 1,
        CpuSensitive = cpuSensitive,
        Description = property,
    };

    public static LatencyOptimizationSnapshot Snapshot(
        string adapterId,
        string property,
        LatencySettingKind kind = LatencySettingKind.PowerManagement,
        LatencyTransactionState state = LatencyTransactionState.Committed) => new()
        {
            AdapterId = adapterId,
            AdapterName = adapterId,
            NetworkKey = "network",
            CreatedAt = DateTimeOffset.UtcNow,
            State = state,
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

    public static IReadOnlyList<LatencyPair> Pairs(params (double Baseline, double Candidate)[] medians) =>
    [
        .. medians.Select(pair => new LatencyPair
        {
            Baseline = Measurement(pair.Baseline),
            Candidate = Measurement(pair.Candidate),
        }),
    ];
}

/// <summary>
/// An adapter that remembers what is currently written to it.
/// </summary>
/// <remarks>
/// The live state is the point: a paired A/B test is only meaningful against a probe
/// whose answer depends on what is applied right now, so the two doubles share this set
/// rather than replaying a fixed script of measurements.
/// </remarks>
internal sealed class FakeController : ILatencyAdapterController
{
    private int _concurrentApplies;

    public string[] PowerProperties { get; init; } = ["SelectiveSuspend"];

    /// <summary>Property whose apply throws, simulating a driver error mid-run.</summary>
    public string? ThrowOnApply { get; init; }

    /// <summary>Property the driver silently declines to write.</summary>
    public string? RefuseApply { get; init; }

    public TimeSpan ApplyDelay { get; init; }

    public LatencyRestoreOutcome RestoreOutcome { get; init; } = LatencyRestoreOutcome.Restored;

    /// <summary>Null makes the adapter disappear, as a driver update or unplug would.</summary>
    public Func<NetworkFingerprint, AdapterLatencyCapability?>? Detect { get; init; }

    /// <summary>The values currently written to the adapter.</summary>
    public HashSet<string> Live { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Applied { get; } = [];

    public List<string> Restored { get; } = [];

    public List<string> Events { get; } = [];

    public int MaxConcurrentApplies { get; private set; }

    /// <summary>Called as the write reaches the adapter, for ordering assertions.</summary>
    public Action<string>? OnApply { get; set; }

    public Task<AdapterLatencyCapability?> DetectAsync(
        NetworkFingerprint network,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Detect is null
            ? Fake.Capability(network, PowerProperties)
            : Detect(network));

    public async Task<LatencyApplyResult> ApplyAsync(
        AdapterLatencyCapability adapter,
        LatencyOptimizationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var concurrent = Interlocked.Increment(ref _concurrentApplies);
        MaxConcurrentApplies = Math.Max(MaxConcurrentApplies, concurrent);
        Applied.Add(candidate.PropertyName);
        Events.Add($"{adapter.AdapterId}:{candidate.PropertyName}");
        OnApply?.Invoke(candidate.PropertyName);

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

            if (candidate.PropertyName == RefuseApply)
            {
                return new LatencyApplyResult(false, "sürücü değeri canlı uygulamadı");
            }

            Live.Add(candidate.PropertyName);
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

        if (RestoreOutcome is LatencyRestoreOutcome.Restored or LatencyRestoreOutcome.AlreadyOriginal)
        {
            Live.Remove(setting.PropertyName);
        }

        return Task.FromResult(RestoreOutcome);
    }
}

/// <summary>A probe whose answer is a function of what the controller has applied.</summary>
internal sealed class FakeProbe : ILatencyProbe
{
    private readonly Func<IReadOnlySet<string>, int, LatencyMeasurement> _measure;
    private readonly FakeController _controller;

    public FakeProbe(FakeController controller, Func<IReadOnlySet<string>, int, LatencyMeasurement> measure)
    {
        _controller = controller;
        _measure = measure;
    }

    /// <summary>A link nothing changes: every measurement is the same.</summary>
    public static FakeProbe Flat(FakeController controller, double median = 25)
        => new(controller, (_, _) => Fake.Measurement(median));

    /// <summary>A link where one property really does help, by a repeatable amount.</summary>
    public static FakeProbe Improves(
        FakeController controller,
        string property = "SelectiveSuspend",
        double median = 26,
        double gain = 5)
        => new(controller, (live, _) => live.Contains(property)
            ? Fake.Measurement(median - gain, jitter: 2.2, p95: median - gain + 6)
            : Fake.Measurement(median, jitter: 3.4, p95: median + 9));

    public List<string> Requests { get; } = [];

    /// <summary>The sample size each measurement was asked for, so growth can be asserted.</summary>
    public List<int> ProbeCounts { get; } = [];

    public int Measurements { get; private set; }

    public LatencyConnectivity Connectivity { get; init; } = new(true, true);

    /// <summary>Set to make connectivity fail only once a property is applied.</summary>
    public string? BreaksConnectivity { get; init; }

    public Task<LatencyMeasurement> MeasureAsync(
        NetworkFingerprint network,
        LatencyProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RemoteEndpoint ?? "(survey)");
        ProbeCounts.Add(request.ProbeCount);

        var measurement = _measure(_controller.Live, Measurements);
        Measurements++;
        return Task.FromResult(measurement);
    }

    public Task<LatencyConnectivity> CheckConnectivityAsync(
        NetworkFingerprint network,
        string remoteEndpoint,
        CancellationToken cancellationToken = default)
    {
        if (BreaksConnectivity is not null && _controller.Live.Contains(BreaksConnectivity))
        {
            return Task.FromResult(new LatencyConnectivity(false, false));
        }

        return Task.FromResult(Connectivity);
    }
}

internal sealed class FakeSnapshotStore : ILatencySnapshotStore
{
    public LatencyOptimizationSnapshot? Value { get; set; }

    public List<LatencyTransactionState> States { get; } = [];

    /// <summary>Called on every write, for ordering assertions.</summary>
    public Action<LatencyOptimizationSnapshot>? OnSave { get; set; }

    public Task<LatencyOptimizationSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Clone(Value));

    public Task SaveAsync(LatencyOptimizationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Value = Clone(snapshot);
        States.Add(snapshot.State);
        OnSave?.Invoke(snapshot);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Value = null;
        return Task.CompletedTask;
    }

    private static LatencyOptimizationSnapshot? Clone(LatencyOptimizationSnapshot? snapshot) => snapshot is null
        ? null
        : snapshot with
        {
            Settings = [.. snapshot.Settings.Select(setting => setting with { OriginalValues = [.. setting.OriginalValues] })],
        };
}

/// <summary>Resolves every target to one fixed reference address.</summary>
/// <remarks>
/// Target resolution touches DNS and the live socket tables, neither of which belongs in
/// a unit test. Everything downstream only needs the endpoint to be the same one on both
/// halves of every pair, which is exactly what a fixed answer guarantees.
/// </remarks>
internal sealed class FakeTargetResolver : ILatencyTargetResolver
{
    public LatencyEndpoint Endpoint { get; init; } = LatencyEndpoint.Icmp(
        System.Net.IPAddress.Parse("1.1.1.1"),
        "test hedefi");

    public string? Failure { get; init; }

    public Task<LatencyTargetResolution> ResolveAsync(
        LatencyTargetSpec spec,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Failure is null
            ? new LatencyTargetResolution { Endpoints = [Endpoint] }
            : LatencyTargetResolution.Failed(Failure));
}

/// <summary>Reports a machine whose conditions never change.</summary>
internal sealed class FakeEnvironmentSampler : ILatencyEnvironmentSampler
{
    private readonly Queue<LatencyEnvironment>? _script;

    public FakeEnvironmentSampler(params LatencyEnvironment[] script)
        => _script = script.Length == 0 ? null : new Queue<LatencyEnvironment>(script);

    public LatencyEnvironment Steady { get; init; } = new()
    {
        CpuBusyPercent = 12,
        Power = PowerSource.Mains,
        InterfaceIndex = 10,
        RouteHash = "route",
    };

    public int Samples { get; private set; }

    public LatencyEnvironment Sample(NetworkFingerprint network)
    {
        Samples++;

        if (_script is { Count: > 0 })
        {
            return _script.Count == 1 ? _script.Peek() : _script.Dequeue();
        }

        return Steady;
    }
}

internal sealed class FakeProfileStore : ILatencyProfileStore
{
    public List<LatencyProfile> Profiles { get; } = [];

    public Task<LatencyProfile?> FindAsync(string networkKey, string adapterId, CancellationToken cancellationToken = default)
        => Task.FromResult(Profiles.FirstOrDefault(profile =>
            profile.NetworkKey == networkKey
            && string.Equals(profile.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase)));

    public Task SaveAsync(LatencyProfile profile, CancellationToken cancellationToken = default)
    {
        Profiles.RemoveAll(entry => entry.NetworkKey == profile.NetworkKey
            && string.Equals(entry.AdapterId, profile.AdapterId, StringComparison.OrdinalIgnoreCase));
        Profiles.Add(profile);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string networkKey, string adapterId, CancellationToken cancellationToken = default)
    {
        Profiles.RemoveAll(entry => entry.NetworkKey == networkKey
            && string.Equals(entry.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }
}

/// <summary>Wires one optimizer up with doubles that share the adapter's live state.</summary>
internal sealed class LatencyScenario
{
    public LatencyScenario(
        FakeController? controller = null,
        FakeProbe? probe = null,
        LatencyOptimizerOptions? options = null,
        FakeProfileStore? profiles = null,
        Func<DateTimeOffset>? now = null,
        ILatencyTargetResolver? targets = null,
        ILatencyEnvironmentSampler? environment = null)
    {
        Controller = controller ?? new FakeController();
        Probe = probe ?? FakeProbe.Flat(Controller);
        Profiles = profiles ?? new FakeProfileStore();

        Optimizer = new LatencyOptimizer(
            Controller,
            Probe,
            Snapshots,
            log: line => Logs.Add(line),
            profiles: Profiles,
            options: options ?? new LatencyOptimizerOptions { MinimumCycles = 2, MaximumCycles = 3 },
            now: now,
            targets: targets ?? new FakeTargetResolver(),
            environmentSampler: environment ?? new FakeEnvironmentSampler(),
            resourceRestorers: [],

            // Settling pauses are real seconds on a real driver and nothing at all in a
            // test double, so the wait is injected rather than slept through.
            delay: (_, _) => Task.CompletedTask);
    }

    public FakeController Controller { get; }

    public FakeProbe Probe { get; }

    public FakeSnapshotStore Snapshots { get; } = new();

    public FakeProfileStore Profiles { get; }

    /// <summary>Everything the run logged, so the operational events can be asserted.</summary>
    public List<string> Logs { get; } = [];

    public LatencyOptimizer Optimizer { get; }

    public static LatencyScenario WithImprovement(double gain = 5)
    {
        var controller = new FakeController();
        return new LatencyScenario(controller, FakeProbe.Improves(controller, gain: gain));
    }
}

/// <summary>An in-memory QoS store that behaves like the Windows one, minus Windows.</summary>
internal sealed class FakeQosController : IQosController
{
    public Dictionary<string, QosPolicyRequest> Policies { get; } = new(StringComparer.Ordinal);

    /// <summary>Policies somebody else owns. Must never be touched.</summary>
    public List<string> ForeignPolicies { get; init; } = [];

    public List<string> CompetingPolicies { get; init; } = [];

    public bool Available { get; init; } = true;

    public string? RefuseCreateReason { get; init; }

    public List<string> Removed { get; } = [];

    public Task<QosCapability> DetectAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Available
            ? new QosCapability
            {
                Available = true,
                OwnedPolicies = [.. Policies.Keys],
                ForeignPolicies = ForeignPolicies,
                CompetingPolicies = CompetingPolicies,
            }
            : QosCapability.Unavailable);

    public Task<QosApplyResult> CreateAsync(QosPolicyRequest request, CancellationToken cancellationToken = default)
    {
        if (!WindowsQosController.IsOwnedName(request.Name))
        {
            throw new InvalidOperationException("foreign policy name");
        }

        if (RefuseCreateReason is not null)
        {
            return Task.FromResult(new QosApplyResult(false, RefuseCreateReason));
        }

        Policies[request.Name] = request;
        return Task.FromResult(new QosApplyResult(true));
    }

    public Task<LatencyRestoreOutcome> RemoveAsync(
        string name,
        string policyStore,
        CancellationToken cancellationToken = default)
    {
        if (!WindowsQosController.IsOwnedName(name))
        {
            throw new InvalidOperationException($"'{name}' is not ours");
        }

        Removed.Add(name);
        return Task.FromResult(Policies.Remove(name)
            ? LatencyRestoreOutcome.Restored
            : LatencyRestoreOutcome.AlreadyOriginal);
    }

    public async Task<int> RemoveAllOwnedAsync(CancellationToken cancellationToken = default)
    {
        var owned = Policies.Keys.ToArray();
        foreach (var name in owned)
        {
            await RemoveAsync(name, QosPolicyStores.Active, cancellationToken);
        }

        return owned.Length;
    }
}

/// <summary>A load experiment that replays a script of results, in call order.</summary>
internal sealed class FakeLoadExperiment : ILoadExperiment
{
    private readonly Queue<LoadExperimentResult> _results;

    public FakeLoadExperiment(params LoadExperimentResult[] results)
        => _results = new Queue<LoadExperimentResult>(results);

    public int Calls { get; private set; }

    public string Instruction(LoadDirection direction) => "start a transfer";

    public Task<LoadExperimentResult> RunAsync(
        NetworkFingerprint network,
        LoadExperimentRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(_results.Count switch
        {
            0 => LoadExperimentResult.Failed(request.Direction, "no scripted result"),
            1 => _results.Peek(),
            _ => _results.Dequeue(),
        });
    }

    /// <summary>A successful upload experiment with the given idle and loaded medians.</summary>
    public static LoadExperimentResult Upload(
        double idleMedian,
        double loadedMedian,
        double uplinkKbps = 20_000,
        double loadedLoss = 0) => new()
        {
            Direction = LoadDirection.Upload,
            Idle = Fake.Measurement(idleMedian, load: LatencyLoadState.Idle),
            Loaded = Fake.Measurement(loadedMedian, load: LatencyLoadState.UplinkLoaded, loss: loadedLoss),
            ObservedLoad = new NetworkLoadSample
            {
                State = LatencyLoadState.UplinkLoaded,
                UplinkKbps = uplinkKbps,
                DownlinkKbps = 40,
            },
            Capacity = new LinkCapacityEstimate { UplinkKbps = uplinkKbps },
        };
}

/// <summary>Counts the settling pauses an experiment asked for.</summary>
internal sealed class RecordingDelay
{
    public List<TimeSpan> Waits { get; } = [];

    public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        Waits.Add(duration);
        return Task.CompletedTask;
    }
}

/// <summary>
/// An experiment arm that records what happened to it.
/// </summary>
/// <remarks>
/// When given a controller it also drives that controller's live set, so a probe whose
/// answer depends on what is applied sees the arm's effect - which is the only way an
/// experiment over test doubles measures anything at all.
/// </remarks>
internal sealed class RecordingArm : ILatencyExperimentArm
{
    private readonly FakeController? _controller;
    private readonly string _property;

    public RecordingArm(FakeController? controller = null, string property = "SelectiveSuspend")
    {
        _controller = controller;
        _property = property;
    }

    public List<string> Events { get; } = [];

    public bool Applied { get; private set; }

    public bool RefuseApply { get; init; }

    public bool BreakLink { get; init; }

    public Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        Events.Add("apply");

        if (RefuseApply)
        {
            return Task.FromResult(false);
        }

        Applied = true;
        _controller?.Live.Add(_property);
        return Task.FromResult(true);
    }

    public Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        Events.Add("restore");
        Applied = false;
        _controller?.Live.Remove(_property);
        return Task.CompletedTask;
    }

    public Task<bool> IsUsableAsync(CancellationToken cancellationToken = default)
    {
        Events.Add("check");
        return Task.FromResult(!BreakLink);
    }
}
