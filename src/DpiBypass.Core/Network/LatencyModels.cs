using System.Net.NetworkInformation;

namespace DpiBypass.Core.Network;

public enum LatencyOptimizationStatus
{
    Disabled,
    Measuring,
    Optimizing,
    Active,
    NoGain,
    Unsupported,
    Offline,
    Restoring,
    Failed,
    Cancelled,
}

/// <summary>
/// How far a run got through changing persistent NIC values, written to disk before
/// each step so an interrupted run can be recognised on the next launch.
/// </summary>
/// <remarks>
/// The state is recorded ahead of the action it describes, never after. A crash between
/// writing <see cref="CandidateApplied"/> and the driver accepting the value leaves a
/// snapshot claiming a change that never happened, and putting that "back" is a write of
/// the value the adapter already has. The opposite ordering would lose a real change.
/// </remarks>
public enum LatencyTransactionState
{
    /// <summary>Originals captured; nothing has been written to the adapter.</summary>
    SnapshotCreated = 0,

    /// <summary>A value is being written, or has been.</summary>
    CandidateApplied = 1,

    /// <summary>Written and now being benchmarked; the outcome is not decided.</summary>
    Verifying = 2,

    /// <summary>Every change in the snapshot was measured, verified and kept.</summary>
    Committed = 3,
}

/// <summary>Which part of the path the measurements say the delay is in.</summary>
public enum LatencyBottleneck
{
    Unknown = 0,

    /// <summary>The first hop is slow, which is the part NIC settings can move.</summary>
    LocalLink = 1,

    /// <summary>Delay that appears only while this machine's own traffic is running.</summary>
    LocalQueueing = 2,

    /// <summary>The access link to the operator: the usual home bufferbloat.</summary>
    AccessLink = 3,

    /// <summary>Distance and routing beyond the operator. Nothing local changes it.</summary>
    WanRoute = 4,
}

public enum LatencySettingKind
{
    PowerManagement,
    AdvancedProperty,
}

public enum LatencyRestoreOutcome
{
    Restored,
    AlreadyOriginal,
    MissingProperty,
    MissingAdapter,
    Failed,
}

/// <summary>A statistically useful latency sample; every number comes from real I/O.</summary>
public sealed record LatencyMeasurement
{
    public required DateTimeOffset MeasuredAt { get; init; }

    public required string RemoteEndpoint { get; init; }

    public required string Protocol { get; init; }

    public int RemoteAttempts { get; init; }

    public int RemoteReplies { get; init; }

    public int GatewayAttempts { get; init; }

    public int GatewayReplies { get; init; }

    public double MinimumRttMs { get; init; }

    public double MedianRttMs { get; init; }

    public double P95RttMs { get; init; }

    /// <summary>
    /// The tail. A game stutters on the worst one percent of packets, not on the median,
    /// so a change that trades a slightly better median for a much worse p99 is a
    /// regression however good the average looks.
    /// </summary>
    public double P99RttMs { get; init; }

    /// <summary>Mean absolute difference between consecutive probes (delay variation).</summary>
    public double JitterMs { get; init; }

    public double PacketLossPercent { get; init; }

    public double? GatewayMedianRttMs { get; init; }

    public double? GatewayP95RttMs { get; init; }

    /// <summary>What the link was carrying while this was measured.</summary>
    public NetworkLoadSample Load { get; init; } = NetworkLoadSample.Unknown;

    public bool HasRemoteConnectivity => RemoteReplies > 0;

    public bool HasAnyConnectivity => HasRemoteConnectivity || GatewayReplies > 0;

    /// <summary>How much one lost probe moves <see cref="PacketLossPercent"/>.</summary>
    public double LossQuantumPercent => LatencyStatistics.OneProbeWorth(RemoteAttempts);

    internal static LatencyMeasurement Create(
        string endpoint,
        string protocol,
        IReadOnlyList<double> remoteSamples,
        int remoteAttempts,
        IReadOnlyList<double> gatewaySamples,
        int gatewayAttempts,
        NetworkLoadSample? load = null,
        DateTimeOffset? measuredAt = null)
    {
        // Sorted for the order statistics; the delay variation needs the samples in the
        // order they arrived, so it is computed from the original list.
        var ordered = remoteSamples.Order().ToArray();
        var orderedGateway = gatewaySamples.Order().ToArray();

        return new LatencyMeasurement
        {
            MeasuredAt = measuredAt ?? DateTimeOffset.UtcNow,
            RemoteEndpoint = endpoint,
            Protocol = protocol,
            RemoteAttempts = remoteAttempts,
            RemoteReplies = ordered.Length,
            GatewayAttempts = gatewayAttempts,
            GatewayReplies = orderedGateway.Length,
            MinimumRttMs = ordered.Length == 0 ? 0 : ordered[0],
            MedianRttMs = LatencyStatistics.PercentileOfSorted(ordered, 0.50),
            P95RttMs = LatencyStatistics.PercentileOfSorted(ordered, 0.95),
            P99RttMs = LatencyStatistics.PercentileOfSorted(ordered, 0.99),
            JitterMs = LatencyStatistics.DelayVariation(remoteSamples),
            PacketLossPercent = LatencyStatistics.PacketLossPercent(remoteAttempts, ordered.Length),
            GatewayMedianRttMs = orderedGateway.Length == 0
                ? null
                : LatencyStatistics.PercentileOfSorted(orderedGateway, 0.50),
            GatewayP95RttMs = orderedGateway.Length == 0
                ? null
                : LatencyStatistics.PercentileOfSorted(orderedGateway, 0.95),
            Load = load ?? NetworkLoadSample.Unknown,
        };
    }
}

/// <summary>
/// What the measurements say about where the delay lives, and therefore about what a
/// NIC setting could possibly move.
/// </summary>
/// <remarks>
/// Splitting the path matters because most of what users call ping is not local. If the
/// first hop answers in 1 ms and the far end in 70 ms, no adapter property is going to
/// help, and saying so is more useful than testing eight settings and finding nothing.
/// </remarks>
public sealed record LatencyPathAnalysis
{
    public required LatencyBottleneck Bottleneck { get; init; }

    /// <summary>Median RTT to the default gateway, when it answers.</summary>
    public double? LocalLinkMs { get; init; }

    /// <summary>Median RTT beyond the gateway: the operator and the internet path.</summary>
    public double? RemotePathMs { get; init; }

    /// <summary>Extra median delay seen while the link carried this machine's own traffic.</summary>
    public double? QueueingMs { get; init; }

    /// <summary>Whether a NIC change has any realistic chance of moving this number.</summary>
    public required bool LocallyImprovable { get; init; }

    public required string Summary { get; init; }

    /// <summary>
    /// The gateway has to be answering, and answering with something worth attributing,
    /// before any split is claimed.
    /// </summary>
    public static LatencyPathAnalysis Describe(LatencyMeasurement measurement, LatencyMeasurement? loaded = null)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        var gateway = measurement.GatewayMedianRttMs;
        double? queueing = null;

        // Only a like-for-like pair says anything: an idle window against a busy one on
        // the same path. Anything else is two different networks being subtracted.
        if (loaded is not null
            && loaded.HasRemoteConnectivity
            && measurement.HasRemoteConnectivity
            && measurement.Load.State == LatencyLoadState.Idle
            && loaded.Load.IsLoaded)
        {
            queueing = Math.Max(0, loaded.MedianRttMs - measurement.MedianRttMs);
        }

        if (!measurement.HasRemoteConnectivity)
        {
            return new LatencyPathAnalysis
            {
                Bottleneck = LatencyBottleneck.Unknown,
                LocalLinkMs = gateway,
                RemotePathMs = null,
                QueueingMs = queueing,
                LocallyImprovable = false,
                Summary = "Uzak uç ölçülemedi; gecikmenin nerede olduğu belirlenemiyor.",
            };
        }

        var remotePath = gateway is { } local ? Math.Max(0, measurement.MedianRttMs - local) : (double?)null;

        // Queueing that only appears under load is the one local problem a user can
        // actually act on, so it outranks the static split.
        if (queueing is { } queue && queue >= 15)
        {
            return new LatencyPathAnalysis
            {
                Bottleneck = LatencyBottleneck.LocalQueueing,
                LocalLinkMs = gateway,
                RemotePathMs = remotePath,
                QueueingMs = queue,
                LocallyImprovable = false,
                Summary = $"Kendi trafiğiniz aktifken gecikme {queue:F0} ms artıyor (kuyruklanma). "
                    + "Bu, yükleme/gönderim hızını sınırlamakla düzelir; NIC ayarıyla düzelmez.",
            };
        }

        if (gateway is { } linkMs)
        {
            // A first hop that is slow in its own right, or that accounts for most of the
            // total, is the part sitting on this side of the operator.
            if (linkMs >= 8 || (measurement.MedianRttMs > 0 && linkMs >= measurement.MedianRttMs * 0.5))
            {
                return new LatencyPathAnalysis
                {
                    Bottleneck = LatencyBottleneck.LocalLink,
                    LocalLinkMs = linkMs,
                    RemotePathMs = remotePath,
                    QueueingMs = queueing,
                    LocallyImprovable = true,
                    Summary = $"Gecikmenin büyük kısmı ilk atlamada ({linkMs:F1} ms). "
                        + "Bağdaştırıcı ayarları burada işe yarayabilir.",
                };
            }

            if (linkMs < 3 && measurement.MedianRttMs >= 25)
            {
                return new LatencyPathAnalysis
                {
                    Bottleneck = LatencyBottleneck.WanRoute,
                    LocalLinkMs = linkMs,
                    RemotePathMs = remotePath,
                    QueueingMs = queueing,
                    LocallyImprovable = false,
                    Summary = $"İlk atlama {linkMs:F1} ms; gecikmenin {remotePath:F0} ms'i operatör ve "
                        + "internet yolunda. Bunu bilgisayardaki bir ayar değiştiremez.",
                };
            }

            return new LatencyPathAnalysis
            {
                Bottleneck = LatencyBottleneck.AccessLink,
                LocalLinkMs = linkMs,
                RemotePathMs = remotePath,
                QueueingMs = queueing,
                LocallyImprovable = true,
                Summary = $"İlk atlama {linkMs:F1} ms, kalan {remotePath:F0} ms erişim hattında ve ötesinde.",
            };
        }

        return new LatencyPathAnalysis
        {
            Bottleneck = LatencyBottleneck.Unknown,
            LocalLinkMs = null,
            RemotePathMs = null,
            QueueingMs = queueing,
            LocallyImprovable = true,
            Summary = "Ağ geçidi ICMP yanıtlamıyor; yerel ve uzak gecikme ayrıştırılamadı.",
        };
    }
}

public sealed record AdapterAdvancedPropertyCapability
{
    public required string RegistryKeyword { get; init; }

    public List<string> RegistryValues { get; init; } = [];

    public List<string> ValidRegistryValues { get; init; } = [];
}

/// <summary>Only capability data returned by Windows; no display-language parsing.</summary>
public sealed record AdapterLatencyCapability
{
    public required string AdapterId { get; init; }

    public required string AdapterName { get; init; }

    public string InterfaceDescription { get; init; } = string.Empty;

    public NetworkInterfaceType AdapterType { get; init; }

    public bool IsPhysical { get; init; }

    public bool IsVirtual { get; init; }

    public bool IsUp { get; init; }

    /// <summary>Raw NetAdapter power values: 0 unsupported, 1 disabled, 2 enabled.</summary>
    public Dictionary<string, int> PowerManagement { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<AdapterAdvancedPropertyCapability> AdvancedProperties { get; init; } = [];

    public bool IsEligible => IsPhysical && !IsVirtual && IsUp
        && AdapterType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;

    /// <summary>
    /// A short hash of everything about this adapter that a saved result depends on.
    /// </summary>
    /// <remarks>
    /// A driver update can add, remove or renumber the values a keyword accepts, and a
    /// result verified against the old driver says nothing about the new one. Anything
    /// that would change which candidates exist goes into this, so a stale profile is
    /// discarded rather than replayed. It is a local cache key and never leaves the
    /// machine; only the adapter's own capability surface is hashed, no addresses and
    /// no network names.
    /// </remarks>
    public string CapabilityFingerprint
    {
        get
        {
            var parts = new List<string>
            {
                AdapterId,
                InterfaceDescription,
                AdapterType.ToString(),
            };

            parts.AddRange(PowerManagement
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}={entry.Value}"));

            parts.AddRange(AdvancedProperties
                .OrderBy(property => property.RegistryKeyword, StringComparer.Ordinal)
                .Select(property => $"{property.RegistryKeyword}=[{string.Join('/', property.ValidRegistryValues)}]"));

            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join('|', parts)));

            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
    }

    public IReadOnlyList<LatencyOptimizationCandidate> BuildSafeCandidates()
    {
        if (!IsEligible)
        {
            return [];
        }

        var candidates = new List<LatencyOptimizationCandidate>();
        AddPowerCandidate(candidates, "SelectiveSuspend", "Seçmeli askıya alma kapalı");
        AddPowerCandidate(candidates, "DeviceSleepOnDisconnect", "Bağlantı kesilince aygıt uyutma kapalı");
        AddPowerCandidate(candidates, "D0PacketCoalescing", "D0 paket birleştirme kapalı");

        // *InterruptModeration is an NDIS registry keyword. DisplayName and
        // DisplayValue are deliberately ignored because Windows localises them.
        if (AdapterType == NetworkInterfaceType.Ethernet)
        {
            var moderation = AdvancedProperties.FirstOrDefault(property =>
                string.Equals(property.RegistryKeyword, "*InterruptModeration", StringComparison.OrdinalIgnoreCase));

            if (moderation is not null
                && moderation.ValidRegistryValues.Contains("0", StringComparer.OrdinalIgnoreCase)
                && !moderation.RegistryValues.SequenceEqual(["0"], StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(new LatencyOptimizationCandidate
                {
                    Kind = LatencySettingKind.AdvancedProperty,
                    PropertyName = moderation.RegistryKeyword,
                    OriginalValues = [.. moderation.RegistryValues],
                    DesiredValues = ["0"],
                    // Not "lower latency": turning moderation off means an interrupt per
                    // packet, and on a busy or slow machine the extra interrupt handling
                    // costs more than the coalescing delay it removes. Which way it goes
                    // on this adapter is what the paired benchmark is for.
                    CpuSensitive = true,
                    Description = "Interrupt Moderation kapalı (ölçümle sınanır · CPU maliyeti olabilir)",
                });
            }
        }

        return candidates;
    }

    private void AddPowerCandidate(List<LatencyOptimizationCandidate> candidates, string propertyName, string description)
    {
        // Disabled and unsupported properties are already safe/no-op. Only an
        // explicitly enabled setting is ever considered.
        if (PowerManagement.GetValueOrDefault(propertyName) != 2)
        {
            return;
        }

        candidates.Add(new LatencyOptimizationCandidate
        {
            Kind = LatencySettingKind.PowerManagement,
            PropertyName = propertyName,
            OriginalPowerValue = 2,
            DesiredPowerValue = 1,
            Description = description,
        });
    }
}

public sealed record LatencyOptimizationCandidate
{
    public required LatencySettingKind Kind { get; init; }

    public required string PropertyName { get; init; }

    public int? OriginalPowerValue { get; init; }

    public int? DesiredPowerValue { get; init; }

    public List<string> OriginalValues { get; init; } = [];

    public List<string> DesiredValues { get; init; } = [];

    public required string Description { get; init; }

    /// <summary>
    /// Whether keeping this value costs measurable CPU, so it needs a clearly bigger
    /// win than a free change before it is worth keeping.
    /// </summary>
    public bool CpuSensitive { get; init; }

    public LatencySettingSnapshot ToSnapshot(AdapterLatencyCapability adapter) => new()
    {
        AdapterId = adapter.AdapterId,
        AdapterName = adapter.AdapterName,
        Kind = Kind,
        PropertyName = PropertyName,
        OriginalPowerValue = OriginalPowerValue,
        OriginalValues = [.. OriginalValues],
        AppliedDescription = Description,
        CapturedAt = DateTimeOffset.UtcNow,
    };
}

public sealed record LatencySettingSnapshot
{
    public required string AdapterId { get; init; }

    public required string AdapterName { get; init; }

    public required LatencySettingKind Kind { get; init; }

    public required string PropertyName { get; init; }

    public int? OriginalPowerValue { get; init; }

    public List<string> OriginalValues { get; init; } = [];

    public required string AppliedDescription { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}

public sealed record LatencyOptimizationSnapshot
{
    /// <summary>Bumped when the shape changes, so an older file is rolled back rather than misread.</summary>
    public const int CurrentSchemaVersion = 2;

    public required string AdapterId { get; init; }

    public required string AdapterName { get; init; }

    public required string NetworkKey { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// How far the run that wrote this had got. Anything other than
    /// <see cref="LatencyTransactionState.Committed"/> is an interrupted run, and the
    /// next launch puts it back before it does anything else.
    /// </summary>
    public LatencyTransactionState State { get; init; } = LatencyTransactionState.SnapshotCreated;

    /// <summary>The property being written when this was saved, for the recovery log.</summary>
    public string? PendingProperty { get; init; }

    public List<LatencySettingSnapshot> Settings { get; init; } = [];

    /// <summary>True when the values on the adapter were never proved to be wanted.</summary>
    public bool IsIncomplete => State != LatencyTransactionState.Committed
        || SchemaVersion != CurrentSchemaVersion;
}

public sealed record LatencyOptimizationResult
{
    public required LatencyOptimizationStatus Status { get; init; }

    public required string StatusLine { get; init; }

    public string AdapterName { get; init; } = string.Empty;

    public string NetworkKey { get; init; } = string.Empty;

    public LatencyMeasurement? Before { get; init; }

    public LatencyMeasurement? After { get; init; }

    public IReadOnlyList<string> AppliedChanges { get; init; } = [];

    /// <summary>Where the measurements say the delay is, and whether it is ours to move.</summary>
    public LatencyPathAnalysis? Path { get; init; }

    /// <summary>What the paired benchmark concluded about each candidate that was tried.</summary>
    public IReadOnlyList<LatencyVerdict> Verdicts { get; init; } = [];

    /// <summary>The aggregate improvement, present only when one was actually verified.</summary>
    public LatencyDelta? VerifiedImprovement { get; init; }

    public bool HasVerifiedGain => Status == LatencyOptimizationStatus.Active
        && Before is not null
        && After is not null
        && AppliedChanges.Count > 0;
}
