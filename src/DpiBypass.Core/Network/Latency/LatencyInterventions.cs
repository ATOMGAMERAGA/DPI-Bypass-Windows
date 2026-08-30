namespace DpiBypass.Core.Network;

/// <summary>Which transports a change can possibly affect.</summary>
[Flags]
public enum LatencyTrafficScope
{
    None = 0,
    Tcp = 1,
    Udp = 2,
    Icmp = 4,
    All = Tcp | Udp | Icmp,
}

public enum InterventionRisk
{
    /// <summary>Reversible in place, cannot drop the link, no restart.</summary>
    Low = 0,

    /// <summary>Reversible, but may briefly interrupt traffic or need a settling period.</summary>
    Moderate = 1,

    /// <summary>Can disconnect the adapter or need a restart to take effect.</summary>
    High = 2,
}

[Flags]
public enum InterventionCost
{
    None = 0,

    /// <summary>Keeping it costs measurable CPU, so it must win by more.</summary>
    Cpu = 1,

    /// <summary>Keeping it costs battery, which the user is told about.</summary>
    Power = 2,
}

public enum InterventionSupportState
{
    /// <summary>The driver offers it and it is not already where we would put it.</summary>
    Supported = 0,

    /// <summary>The driver does not expose this keyword at all.</summary>
    NotOffered = 1,

    /// <summary>Already at the value we would set, so there is nothing to measure.</summary>
    AlreadyAtTarget = 2,

    /// <summary>Offered, but not relevant to this target, link or power state.</summary>
    NotApplicable = 3,

    /// <summary>Offered, but the value we need is not in the driver's valid list.</summary>
    ValueUnsupported = 4,
}

public sealed record InterventionSupport(InterventionSupportState State, string? Reason = null)
{
    public bool CanTest => State == InterventionSupportState.Supported;

    public static readonly InterventionSupport Supported = new(InterventionSupportState.Supported);
}

/// <summary>
/// Everything about one reversible change that is true before it is ever applied.
/// </summary>
/// <remarks>
/// This is deliberately data rather than behaviour. The scheduler needs to know what a
/// change can possibly affect, what keeping it costs and how long the driver needs to
/// settle afterwards, and it needs to know all of that before deciding whether to spend
/// two minutes measuring it.
/// </remarks>
public sealed record InterventionDescriptor
{
    /// <summary>Stable identifier; goes into snapshots, profiles and the JSON status.</summary>
    public required string Id { get; init; }

    /// <summary>Short user-facing name.</summary>
    public required string Title { get; init; }

    /// <summary>One line on what the setting actually does, in the vendor's own terms.</summary>
    public required string Mechanism { get; init; }

    /// <summary>The transports this could move. A TCP-only change is not tested for UDP.</summary>
    public LatencyTrafficScope Scope { get; init; } = LatencyTrafficScope.All;

    public InterventionRisk Risk { get; init; } = InterventionRisk.Low;

    public InterventionCost Cost { get; init; } = InterventionCost.None;

    /// <summary>Whether applying it can briefly take the link down.</summary>
    public bool CanInterruptLink { get; init; }

    /// <summary>
    /// Whether the driver may only honour the change after a miniport restart.
    /// </summary>
    /// <remarks>
    /// Such a change is never applied silently: the value is written with no restart,
    /// read straight back, and abandoned if the driver did not take it. Restarting the
    /// adapter behind a user's back would drop every connection they have open.
    /// </remarks>
    public bool MayNeedRestart { get; init; }

    /// <summary>How long the driver and link need after a write before measuring again.</summary>
    public TimeSpan SettlingTime { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>The official documentation this candidate is derived from.</summary>
    public string Reference { get; init; } = string.Empty;

    public bool IsRelevantTo(LatencyTrafficScope scope) => (Scope & scope) != LatencyTrafficScope.None;
}

/// <summary>Which subsystem a snapshot entry belongs to, so recovery knows who owns it.</summary>
public enum LatencyResourceKind
{
    AdapterPowerManagement = 0,
    AdapterAdvancedProperty = 1,
    QosPolicy = 2,
}

/// <summary>
/// One captured original value, in a form that survives a crash without the object
/// that produced it.
/// </summary>
/// <remarks>
/// Recovery runs on the next launch, from JSON alone. Nothing here may depend on a live
/// intervention instance, an adapter handle or a resolved target: the file has to be
/// enough to put the machine back exactly as it was found.
/// </remarks>
public sealed record LatencyResourceSnapshot
{
    public required LatencyResourceKind Kind { get; init; }

    /// <summary>The intervention that created this entry.</summary>
    public required string InterventionId { get; init; }

    /// <summary>Adapter interface GUID, QoS policy name - whatever identifies the target.</summary>
    public required string TargetId { get; init; }

    public string TargetName { get; init; } = string.Empty;

    public required string Description { get; init; }

    /// <summary>The original state, as flat strings so the schema never has to guess.</summary>
    public Dictionary<string, string> OriginalState { get; init; } = new(StringComparer.Ordinal);

    public required DateTimeOffset CapturedAt { get; init; }
}

/// <summary>Puts one kind of resource back, from the snapshot alone.</summary>
public interface ILatencyResourceRestorer
{
    bool CanRestore(LatencyResourceKind kind);

    Task<LatencyRestoreOutcome> RestoreAsync(
        LatencyResourceSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One thing the optimizer can change, measure and put back.
/// </summary>
/// <remarks>
/// The experiment runner only ever sees this. Whether the change behind it is an NDIS
/// keyword, a power-management flag or a QoS policy is the implementation's business,
/// which is what lets a new kind of change be added without touching the measurement,
/// the statistics or the rollback.
/// </remarks>
public interface ILatencyIntervention
{
    InterventionDescriptor Descriptor { get; }

    /// <summary>Whether this is worth testing for this target, link and power state.</summary>
    InterventionSupport Applicability(LatencyCandidateContext context);

    /// <summary>Asks the live system whether the change is available and not already made.</summary>
    Task<InterventionSupport> DetectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads and returns the original value. Never writes.</summary>
    Task<LatencyResourceSnapshot?> CaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the change and reads it back; false when the driver did not take it.</summary>
    Task<LatencyApplyResult> ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>Puts the captured original back.</summary>
    Task<LatencyRestoreOutcome> RestoreAsync(
        LatencyResourceSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>What the machine and the target look like when candidates are chosen.</summary>
public sealed record LatencyCandidateContext
{
    public static readonly LatencyCandidateContext Unrestricted = new();

    /// <summary>The transport the measurement will actually use.</summary>
    public LatencyTrafficScope Scope { get; init; } = LatencyTrafficScope.All;

    /// <summary>
    /// The transport the user's application uses, when it is not the one being probed.
    /// </summary>
    /// <remarks>
    /// A UDP game measured over ICMP still wants UDP-relevant settings tested and
    /// TCP-only ones left alone, so the application's own transport decides relevance
    /// even when it is not what the probe speaks.
    /// </remarks>
    public LatencyTrafficScope ApplicationScope { get; init; } = LatencyTrafficScope.All;

    public PowerSource Power { get; init; } = PowerSource.Unknown;

    public int ProcessorCount { get; init; } = Environment.ProcessorCount;

    public bool IsWireless { get; init; }

    /// <summary>Set when the user accepted the battery cost of power-relevant changes.</summary>
    public bool AllowPowerCost { get; init; } = true;

    /// <summary>
    /// Whether settings that only act on bulk transfers belong in this run.
    /// </summary>
    /// <remarks>
    /// Large send offload segments blocks bigger than the MTU. An idle-latency run never
    /// produces one, so offering it there would spend minutes measuring a change that
    /// cannot possibly show up. The loaded-latency lane, which does move bulk data,
    /// turns it on.
    /// </remarks>
    public bool IncludeThroughputSensitive { get; init; } = true;

    public LatencyTrafficScope EffectiveScope => Scope | ApplicationScope;
}
