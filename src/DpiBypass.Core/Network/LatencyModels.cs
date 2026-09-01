using System.Net.NetworkInformation;

namespace DpiBypass.Core.Network;

/// <summary>
/// What the feature is doing, which is not the same question as whether it is on.
/// </summary>
/// <remarks>
/// The distinction that matters most here is between <see cref="Disabled"/> and
/// <see cref="NoGain"/>. A user who turned the mode on and got no local win has a mode
/// that is on and watching the network; showing that as "off" would be a lie about
/// their own settings, and would invite them to turn it on again and again.
/// </remarks>
public enum LatencyOptimizationStatus
{
    /// <summary>The user has not turned the mode on.</summary>
    Disabled,

    /// <summary>Working out the adapter, the target and the starting numbers.</summary>
    Measuring,

    /// <summary>Applying and benchmarking candidates.</summary>
    Optimizing,

    /// <summary>At least one change was verified and is in place.</summary>
    Active,

    /// <summary>On, watching, and nothing locally fixable was found.</summary>
    NoGain,

    /// <summary>No supported physical adapter; diagnostics still work.</summary>
    Unsupported,

    Offline,
    Restoring,
    Failed,
    Cancelled,

    /// <summary>A short measurement pass with no setting changes.</summary>
    QuickTesting,

    /// <summary>A controlled experiment with real load on the link.</summary>
    LoadTesting,

    /// <summary>Only idle latency has been measured; the loaded picture is unknown.</summary>
    NeedsDeepTest,

    /// <summary>On, changing nothing, reporting what it measures.</summary>
    MonitoringOnly,

    /// <summary>A QoS policy is in place and verified to reduce loaded latency.</summary>
    TrafficGuardActive,
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

    /// <summary>A policy-based QoS rule this application created and owns.</summary>
    QosPolicy,
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

    /// <summary>
    /// The smallest difference this measurement could possibly have resolved, in ms.
    /// </summary>
    /// <remarks>
    /// ICMP replies come back from <c>Ping</c> as whole milliseconds, so a "0.4 ms
    /// improvement" measured over ICMP is an artefact of rounding, not a result. Carrying
    /// the resolution with the numbers is what lets the evaluator refuse to accept a gain
    /// smaller than the instrument can see - and lets the report say what the instrument
    /// was.
    /// </remarks>
    public double ClockResolutionMs { get; init; } = 1.0;

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
        DateTimeOffset? measuredAt = null,
        double clockResolutionMs = 1.0)
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
            ClockResolutionMs = clockResolutionMs,
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

    /// <summary>Extra median delay measured specifically while sending.</summary>
    public double? UploadQueueingMs { get; init; }

    /// <summary>Extra median delay measured specifically while receiving.</summary>
    public double? DownloadQueueingMs { get; init; }

    /// <summary>Whether a NIC change has any realistic chance of moving this number.</summary>
    public required bool LocallyImprovable { get; init; }

    /// <summary>
    /// Whether pacing this machine's own outbound bulk traffic could move it.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="LocallyImprovable"/>, which is about adapter
    /// properties. Queueing on the access link is not something a NIC keyword touches,
    /// but it is something a send-rate limit can, and conflating the two is how a user
    /// ends up being told nothing can be done when something can.
    /// </remarks>
    public bool TrafficGuardApplicable { get; init; }

    public required string Summary { get; init; }

    /// <summary>Whether an idle-versus-loaded comparison was actually available.</summary>
    public bool HasLoadedEvidence => QueueingMs is not null;

    /// <summary>
    /// The gateway has to be answering, and answering with something worth attributing,
    /// before any split is claimed.
    /// </summary>
    public static LatencyPathAnalysis Describe(LatencyMeasurement measurement, LatencyMeasurement? loaded = null)
        => Describe(
            measurement,
            loaded?.Load.State is LatencyLoadState.UplinkLoaded or LatencyLoadState.BidirectionalLoaded ? loaded : null,
            loaded?.Load.State is LatencyLoadState.DownlinkLoaded or LatencyLoadState.BidirectionalLoaded ? loaded : null);

    /// <summary>
    /// The full picture: idle against a measured upload window and a measured download one.
    /// </summary>
    /// <remarks>
    /// Reporting the two directions separately matters because they have different
    /// fixes. Upload queueing is the one this machine can do something about, by pacing
    /// what it sends; download queueing lives in the operator's equipment, where a rate
    /// limit set here arrives far too late to help.
    /// </remarks>
    public static LatencyPathAnalysis Describe(
        LatencyMeasurement measurement,
        LatencyMeasurement? uploadLoaded,
        LatencyMeasurement? downloadLoaded)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        var gateway = measurement.GatewayMedianRttMs;
        var uploadQueueing = QueueingAgainst(measurement, uploadLoaded);
        var downloadQueueing = QueueingAgainst(measurement, downloadLoaded);

        double? queueing = (uploadQueueing, downloadQueueing) switch
        {
            (null, null) => null,
            ({ } up, null) => up,
            (null, { } down) => down,
            ({ } up, { } down) => Math.Max(up, down),
        };

        if (!measurement.HasRemoteConnectivity)
        {
            return new LatencyPathAnalysis
            {
                Bottleneck = LatencyBottleneck.Unknown,
                LocalLinkMs = gateway,
                RemotePathMs = null,
                QueueingMs = queueing,
                UploadQueueingMs = uploadQueueing,
                DownloadQueueingMs = downloadQueueing,
                LocallyImprovable = false,
                Summary = "Uzak uç ölçülemedi; gecikmenin nerede olduğu belirlenemiyor.",
            };
        }

        var remotePath = gateway is { } local ? Math.Max(0, measurement.MedianRttMs - local) : (double?)null;

        // Queueing that only appears under load is the one local problem a user can
        // actually act on, so it outranks the static split.
        if (queueing is { } queue && queue >= QueueingThresholdMs)
        {
            return new LatencyPathAnalysis
            {
                Bottleneck = LatencyBottleneck.LocalQueueing,
                LocalLinkMs = gateway,
                RemotePathMs = remotePath,
                QueueingMs = queue,
                UploadQueueingMs = uploadQueueing,
                DownloadQueueingMs = downloadQueueing,
                LocallyImprovable = false,
                TrafficGuardApplicable = uploadQueueing >= QueueingThresholdMs,
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
                    UploadQueueingMs = uploadQueueing,
                    DownloadQueueingMs = downloadQueueing,
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
                    UploadQueueingMs = uploadQueueing,
                    DownloadQueueingMs = downloadQueueing,
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
                UploadQueueingMs = uploadQueueing,
                DownloadQueueingMs = downloadQueueing,
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
            UploadQueueingMs = uploadQueueing,
            DownloadQueueingMs = downloadQueueing,
            LocallyImprovable = false,
            TrafficGuardApplicable = uploadQueueing >= QueueingThresholdMs,
            Summary = "Ağ geçidi ICMP yanıtlamıyor; yerel ve uzak gecikme ayrıştırılamadı. "
                + "Bu bilinmeyen bir kaynak sınıflandırmasıdır; yerel NIC sorunu olduğu varsayılmaz.",
        };
    }

    /// <summary>Added delay under load must clear this before it is called queueing.</summary>
    public const double QueueingThresholdMs = 15;

    /// <summary>
    /// The added median delay of a loaded window against an idle one, when the pair is
    /// worth subtracting at all.
    /// </summary>
    /// <remarks>
    /// Both halves have to have reached the same endpoint over the same transport, one
    /// has to have been idle and the other loaded. Anything else is two measurements of
    /// two different situations, and their difference is not a queue.
    /// </remarks>
    private static double? QueueingAgainst(LatencyMeasurement idle, LatencyMeasurement? loaded)
    {
        if (loaded is null
            || !loaded.HasRemoteConnectivity
            || !idle.HasRemoteConnectivity
            || idle.Load.State != LatencyLoadState.Idle
            || !loaded.Load.IsLoaded
            || !string.Equals(idle.RemoteEndpoint, loaded.RemoteEndpoint, StringComparison.Ordinal)
            || !string.Equals(idle.Protocol, loaded.Protocol, StringComparison.Ordinal))
        {
            return null;
        }

        return Math.Max(0, loaded.MedianRttMs - idle.MedianRttMs);
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

    /// <summary>
    /// The miniport driver version, as Windows reports it.
    /// </summary>
    /// <remarks>
    /// Part of <see cref="CapabilityFingerprint"/>: a driver update can change what a
    /// keyword does as well as which values it accepts, so a result verified against the
    /// old driver is not a result about the new one.
    /// </remarks>
    public string DriverVersion { get; init; } = string.Empty;

    public NetworkInterfaceType AdapterType { get; init; }

    public bool IsPhysical { get; init; }

    public bool IsVirtual { get; init; }

    public bool IsUp { get; init; }

    /// <summary>Raw NetAdapter power values: 0 unsupported, 1 disabled, 2 enabled.</summary>
    public Dictionary<string, int> PowerManagement { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<AdapterAdvancedPropertyCapability> AdvancedProperties { get; init; } = [];

    /// <summary>
    /// Whether receive segment coalescing is actually in effect, per address family.
    /// </summary>
    /// <remarks>
    /// Separate from the keyword because the two can disagree: a keyword can say enabled
    /// while the stack reports the feature as non-operational, and turning off something
    /// that is not running would be measured as no change - correctly, but at the cost of
    /// several minutes.
    /// </remarks>
    public bool? RscIPv4Operational { get; init; }

    public bool? RscIPv6Operational { get; init; }

    public bool? RssEnabled { get; init; }

    public int? RssMaxProcessors { get; init; }

    /// <summary>Whether large send offload v2 is actually running, per address family.</summary>
    public bool? LsoV2IPv4Enabled { get; init; }

    public bool? LsoV2IPv6Enabled { get; init; }

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
                DriverVersion,
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

    /// <summary>Everything this driver could be asked to change, for any target.</summary>
    public IReadOnlyList<LatencyOptimizationCandidate> BuildSafeCandidates()
        => AdapterInterventionCatalog.Build(this, LatencyCandidateContext.Unrestricted);

    /// <summary>The candidates worth measuring for one particular target and machine state.</summary>
    public IReadOnlyList<LatencyOptimizationCandidate> BuildSafeCandidates(LatencyCandidateContext context)
        => AdapterInterventionCatalog.Build(this, context);
}

public sealed record LatencyOptimizationCandidate
{
    private readonly InterventionDescriptor? _descriptor;
    private readonly bool? _cpuSensitive;

    public required LatencySettingKind Kind { get; init; }

    public required string PropertyName { get; init; }

    public int? OriginalPowerValue { get; init; }

    public int? DesiredPowerValue { get; init; }

    public List<string> OriginalValues { get; init; } = [];

    public List<string> DesiredValues { get; init; } = [];

    public required string Description { get; init; }

    /// <summary>
    /// What this change is, what it can affect and what keeping it costs.
    /// </summary>
    /// <remarks>
    /// Falls back to the catalogue entry for the property name, so a candidate built by
    /// hand still carries the right risk and scope rather than a blank one.
    /// </remarks>
    public InterventionDescriptor Descriptor
    {
        get => _descriptor ?? AdapterInterventionCatalog.DescriptorFor(PropertyName);
        init => _descriptor = value;
    }

    /// <summary>
    /// Whether keeping this value costs measurable CPU, so it needs a clearly bigger
    /// win than a free change before it is worth keeping.
    /// </summary>
    public bool CpuSensitive
    {
        get => _cpuSensitive ?? Descriptor.Cost.HasFlag(InterventionCost.Cpu);
        init => _cpuSensitive = value;
    }

    /// <summary>Whether the user pays for this in battery life rather than in CPU.</summary>
    public bool PowerSensitive => Descriptor.Cost.HasFlag(InterventionCost.Power);

    public LatencySettingSnapshot ToSnapshot(AdapterLatencyCapability adapter) => new()
    {
        AdapterId = adapter.AdapterId,
        AdapterName = adapter.AdapterName,
        Kind = Kind,
        PropertyName = PropertyName,
        OriginalPowerValue = OriginalPowerValue,
        OriginalValues = [.. OriginalValues],
        AppliedDescription = Description,
        InterventionId = Descriptor.Id,
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

    /// <summary>The catalogue entry this came from, for the recovery log and reports.</summary>
    public string InterventionId { get; init; } = string.Empty;

    public required DateTimeOffset CapturedAt { get; init; }
}

public sealed record LatencyOptimizationSnapshot
{
    /// <summary>Bumped when the shape changes, so an older file is rolled back rather than misread.</summary>
    /// <remarks>
    /// 3 added <see cref="Resources"/>, which carries everything that is not an adapter
    /// property - today that means QoS policies. A version-2 file describes only adapter
    /// settings, and is treated as an interrupted run and rolled back rather than being
    /// half-understood by this build.
    /// </remarks>
    public const int CurrentSchemaVersion = 3;

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

    /// <summary>
    /// Everything changed outside the adapter itself, in apply order.
    /// </summary>
    /// <remarks>
    /// A QoS policy created by this app lives here rather than in
    /// <see cref="Settings"/> because it is not a property of any adapter and is undone
    /// by deleting it, not by writing a value back. Recovery walks both lists, and a
    /// resource that cannot be undone never blocks one that can.
    /// </remarks>
    public List<LatencyResourceSnapshot> Resources { get; init; } = [];

    /// <summary>True when the values on the adapter were never proved to be wanted.</summary>
    public bool IsIncomplete => State != LatencyTransactionState.Committed
        || SchemaVersion != CurrentSchemaVersion;

    /// <summary>Whether anything at all is recorded as changed.</summary>
    public bool IsEmpty => Settings.Count == 0 && Resources.Count == 0;
}

public sealed record LatencyOptimizationResult
{
    public required LatencyOptimizationStatus Status { get; init; }

    public required string StatusLine { get; init; }

    public string AdapterName { get; init; } = string.Empty;

    public string NetworkKey { get; init; } = string.Empty;

    public LatencyMeasurement? Before { get; init; }

    public LatencyMeasurement? After { get; init; }

    /// <summary>What was measured, exactly, so "improvement" can never be ambiguous.</summary>
    public string TargetLabel { get; init; } = string.Empty;

    /// <summary>ICMP, TCP/25565 and so on: the Type-P the numbers belong to.</summary>
    public string TargetProtocol { get; init; } = string.Empty;

    /// <summary>Set when the number measures the route rather than the application's own RTT.</summary>
    public bool RouteReferenceOnly { get; init; }

    /// <summary>The loaded windows, when a controlled load experiment produced them.</summary>
    public LatencyMeasurement? UploadLoaded { get; init; }

    public LatencyMeasurement? DownloadLoaded { get; init; }

    /// <summary>What the Traffic Guard is doing, when it has run.</summary>
    public TrafficGuardState? TrafficGuard { get; init; }

    /// <summary>Anything the user should know that is not a number.</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    public IReadOnlyList<string> AppliedChanges { get; init; } = [];

    /// <summary>Where the measurements say the delay is, and whether it is ours to move.</summary>
    public LatencyPathAnalysis? Path { get; init; }

    /// <summary>What the paired benchmark concluded about each candidate that was tried.</summary>
    public IReadOnlyList<LatencyVerdict> Verdicts { get; init; } = [];

    /// <summary>What the run established about the link's own ceiling, per direction.</summary>
    public LinkCapacityEstimate Capacity { get; init; } = LinkCapacityEstimate.Unknown;

    /// <summary>Bytes the user's own transfers moved while the run was measuring them.</summary>
    public long DataUsedBytes { get; init; }

    /// <summary>Other endpoints discovery found, so the user can measure a different one.</summary>
    public IReadOnlyList<GameEndpointCandidate> Candidates { get; init; } = [];

    /// <summary>The observed original-to-final improvement, present only when verified.</summary>
    public LatencyDelta? VerifiedImprovement { get; init; }

    public bool HasVerifiedGain => Status == LatencyOptimizationStatus.Active
        && Before is not null
        && After is not null
        && AppliedChanges.Count > 0;
}
