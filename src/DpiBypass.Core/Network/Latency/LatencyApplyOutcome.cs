namespace DpiBypass.Core.Network;

/// <summary>
/// How far a write to the adapter actually got.
/// </summary>
/// <remarks>
/// <para>
/// The distinction this enum exists for is the one an earlier build did not make: a
/// registry value reading back as written is not the same thing as a driver running with
/// it. Microsoft is explicit about this in the <c>Set-NetAdapterAdvancedProperty</c>
/// reference - "<c>-NoRestart</c>: Indicates that the cmdlet does not restart the network
/// adapter after completing the operation. Many advanced properties require restarting
/// the network adapter before the new settings take effect." A benchmark started against
/// <see cref="RegistryWritten"/> measures the old behaviour and credits it to the new
/// value.
/// </para>
/// <para>
/// Only <see cref="OperationallyVerified"/> and <see cref="AdapterRestarted"/> mean the
/// machine is running with the change; see <see cref="LatencyApplyResult.IsEffective"/>.
/// </para>
/// </remarks>
public enum LatencyApplyState
{
    /// <summary>Nothing was written: refused, unsupported, or not permitted.</summary>
    Refused = 0,

    /// <summary>The value is in the registry. Nothing yet says the driver is using it.</summary>
    RegistryWritten = 1,

    /// <summary>Written, and the adapter has to be restarted before it means anything.</summary>
    RestartRequired = 2,

    /// <summary>The adapter was restarted under our control and came back intact.</summary>
    AdapterRestarted = 3,

    /// <summary>The stack reports the feature itself in the requested state.</summary>
    OperationallyVerified = 4,

    /// <summary>Written, but whether the driver honours it could not be established.</summary>
    NotVerified = 5,

    /// <summary>The adapter did not come back usable after a restart.</summary>
    LinkNotRestored = 6,

    /// <summary>The attempt was undone; the adapter holds its original value again.</summary>
    RolledBack = 7,
}

/// <summary>What the stack says about a feature, as opposed to what the registry says.</summary>
/// <remarks>
/// Sourced from <c>Get-NetAdapterRsc</c>, <c>Get-NetAdapterRss</c> and
/// <c>Get-NetAdapterLso</c>, which report whether the feature is running rather than
/// which keyword is stored. A null means the driver does not answer the question, which
/// is not the same as an answer of "no".
/// </remarks>
public sealed record AdapterOperationalState
{
    public static readonly AdapterOperationalState Empty = new();

    public bool? RscIPv4Operational { get; init; }

    public bool? RscIPv6Operational { get; init; }

    public bool? RssEnabled { get; init; }

    public bool? LsoV2IPv4Enabled { get; init; }

    public bool? LsoV2IPv6Enabled { get; init; }

    /// <summary>Whether the link is up, has an address and can reach its gateway.</summary>
    public bool? LinkUsable { get; init; }

    /// <summary>The keyword's own read-back, kept only so a report can show both.</summary>
    public IReadOnlyList<string> RegistryValues { get; init; } = [];

    /// <summary>
    /// What the stack reports for one keyword, or null when it reports nothing.
    /// </summary>
    /// <remarks>
    /// Only the keywords Windows exposes an operational query for can be answered here.
    /// Interrupt moderation and EEE have no such query, so they are never claimed to be
    /// operationally verified - they go through a restart instead.
    /// </remarks>
    public bool? ForKeyword(string keyword) => keyword switch
    {
        AdapterInterventionCatalog.RscIPv4Keyword => RscIPv4Operational,
        AdapterInterventionCatalog.RscIPv6Keyword => RscIPv6Operational,
        AdapterInterventionCatalog.RssKeyword => RssEnabled,
        AdapterInterventionCatalog.LsoIPv4Keyword => LsoV2IPv4Enabled,
        AdapterInterventionCatalog.LsoIPv6Keyword => LsoV2IPv6Enabled,
        _ => null,
    };

    /// <summary>Whether Windows can answer the operational question for this keyword at all.</summary>
    public static bool HasOperationalQuery(string keyword) => keyword is
        AdapterInterventionCatalog.RscIPv4Keyword
        or AdapterInterventionCatalog.RscIPv6Keyword
        or AdapterInterventionCatalog.RssKeyword
        or AdapterInterventionCatalog.LsoIPv4Keyword
        or AdapterInterventionCatalog.LsoIPv6Keyword;
}

/// <summary>What the machine and the user permit a run to do to a live adapter.</summary>
/// <remarks>
/// Restarting a miniport drops every connection on it for a few seconds. That is a thing
/// a user may reasonably agree to in order to measure a setting, and a thing they must
/// never have happen without agreeing - least of all over a remote session, where the
/// restart would take away the session asking for it.
/// </remarks>
public sealed record AdapterRestartPolicy
{
    /// <summary>The default: write nothing that needs a restart to take effect.</summary>
    public static readonly AdapterRestartPolicy Never = new();

    /// <summary>Whether the user explicitly agreed to controlled adapter restarts.</summary>
    public bool UserConsented { get; init; }

    /// <summary>True when this is a remote session, where a restart is never automatic.</summary>
    public bool RemoteSession { get; init; }

    /// <summary>How long the adapter is given to come back before the change is undone.</summary>
    public TimeSpan LinkRecoveryTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public bool Allowed => UserConsented && !RemoteSession;

    public string? RefusalReason => (UserConsented, RemoteSession) switch
    {
        (false, _) => "Bu ayar etkinleşmek için bağdaştırıcının yeniden başlatılmasını gerektiriyor; "
            + "kullanıcı onayı verilmediği için ölçülmedi.",
        (true, true) => "Uzak oturumda bağdaştırıcı yeniden başlatılmaz; bu aday ölçülmedi.",
        _ => null,
    };
}

/// <summary>The result of asking Windows to put one value on an adapter.</summary>
public sealed record LatencyApplyResult
{
    public required LatencyApplyState State { get; init; }

    public string? Reason { get; init; }

    /// <summary>Whether this call restarted the adapter itself.</summary>
    public bool RestartPerformed { get; init; }

    /// <summary>What the stack reported afterwards, when it was asked.</summary>
    public AdapterOperationalState Operational { get; init; } = AdapterOperationalState.Empty;

    /// <summary>
    /// Whether the machine is now genuinely running with the change.
    /// </summary>
    /// <remarks>
    /// This, and only this, is what lets a measurement start. A registry write that the
    /// driver has not picked up yet is not an experiment arm, it is a pending one.
    /// </remarks>
    public bool IsEffective => State is LatencyApplyState.OperationallyVerified
        or LatencyApplyState.AdapterRestarted;

    /// <summary>Whether the value is sitting in the registry unused and must be undone.</summary>
    public bool NeedsRollback => State is LatencyApplyState.RegistryWritten
        or LatencyApplyState.RestartRequired
        or LatencyApplyState.NotVerified
        or LatencyApplyState.LinkNotRestored;

    public static LatencyApplyResult Refused(string reason)
        => new() { State = LatencyApplyState.Refused, Reason = reason };

    public static LatencyApplyResult Verified(AdapterOperationalState operational, bool restarted = false)
        => new()
        {
            State = LatencyApplyState.OperationallyVerified,
            Operational = operational,
            RestartPerformed = restarted,
        };

    /// <summary>A short phrase for the log and the rejection reason shown to the user.</summary>
    public string Describe() => State switch
    {
        LatencyApplyState.Refused => Reason ?? "sürücü değeri kabul etmedi",
        LatencyApplyState.RegistryWritten => "değer kayıt defterine yazıldı ancak sürücü henüz kullanmıyor",
        LatencyApplyState.RestartRequired => Reason ?? "etkinleşmesi için bağdaştırıcı yeniden başlatılmalı",
        LatencyApplyState.AdapterRestarted => "bağdaştırıcı yeniden başlatıldı ve bağlantı geri geldi",
        LatencyApplyState.OperationallyVerified => "ayar işletim sistemi tarafından etkin bildirildi",
        LatencyApplyState.NotVerified => Reason ?? "ayarın etkin olup olmadığı doğrulanamadı",
        LatencyApplyState.LinkNotRestored => Reason ?? "yeniden başlatmadan sonra bağlantı geri gelmedi",
        LatencyApplyState.RolledBack => Reason ?? "değişiklik geri alındı",
        _ => "bilinmeyen sonuç",
    };
}
