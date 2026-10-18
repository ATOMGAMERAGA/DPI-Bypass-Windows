using DpiBypass.Core.Apps;
using DpiBypass.Core.Config;
using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Logging;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;
using DpiBypass.Core.Network.Latency;
using DpiBypass.Core.SocialAccess;
using DpiBypass.Core.Vodafone;

namespace DpiBypass.Core.Ipc;

public sealed record ProtectionSnapshot
{
    public long Sequence { get; init; }
    public IReadOnlyList<string> ProtectedHosts { get; init; } = [];
    public LunarAdBlockState LunarAdBlock { get; init; } = new(false, false, false, 0, "Ölçülmedi");
    public AppSettings Settings { get; init; } = default!;
    public ProtectionState State { get; init; } = default!;
    public EngineStatistics? Stats { get; init; } = default!;
    public NetworkFingerprint Network { get; init; } = default!;
    public IspProfile Isp { get; init; } = default!;
    public IspDetection? Detection { get; init; } = default!;
    public BypassStrategy Strategy { get; init; } = default!;
    public DateTimeOffset? StrategyChosenAt { get; init; } = default!;
    public StrategySelection? LastSelection { get; init; } = default!;
    public bool IsFilterDown { get; init; } = default!;
    public DnsMode ActiveDnsMode { get; init; } = default!;
    public string? DnsProviderInUse { get; init; } = default!;
    public long DnsQueriesServed { get; init; } = default!;
    public long DnsCacheHits { get; init; } = default!;
    public LatencyOptimizationResult LatencyResult { get; init; } = default!;
    public LatencyStatusView LatencyStatus { get; init; } = default!;
    public bool IsLatencyBusy { get; init; } = default!;
    public LoadedLaneProgress LatencyStage { get; init; } = default!;
    public bool CanCancelLatencyRun { get; init; } = default!;
    public ProbeResult? LastProbe { get; init; } = default!;
    public DateTimeOffset? LastProbeAt { get; init; } = default!;
    public DateTimeOffset? LastVerifiedAt { get; init; } = default!;
    public string? StatusDetail { get; init; } = default!;
    public IReadOnlyList<string> LearnedDomains { get; init; } = default!;
    public int ProtectedDomainCount { get; init; } = default!;
    public HotspotStatus HotspotStatus { get; init; } = default!;
    public HotspotDiagnosticResult? LastHotspotDiagnostics { get; init; } = default!;
    public string? LastVodafoneFailure { get; init; } = default!;
    public bool IsHotspotBusy { get; init; } = default!;
    public string? LastHotspotFailure { get; init; } = default!;
    public HotspotStatusView HotspotView { get; init; } = default!;
    public long ThroughputCostBytes { get; init; } = default!;
    public string? DnsRestorePending { get; init; } = default!;
    public ConfigSaveResult? LastSaveFailure { get; init; } = default!;
    public static ProtectionSnapshot Capture(IProtectionController service, long sequence) => new()
    {
        Sequence = sequence,
        ProtectedHosts = service.ProtectedDomains(),
        LunarAdBlock = service.DescribeLunarAdBlock(),
        Settings = service.Settings,
        State = service.State,
        Stats = service.Stats,
        Network = service.Network,
        Isp = service.Isp,
        Detection = service.Detection,
        Strategy = service.Strategy,
        StrategyChosenAt = service.StrategyChosenAt,
        LastSelection = service.LastSelection,
        IsFilterDown = service.IsFilterDown,
        ActiveDnsMode = service.ActiveDnsMode,
        DnsProviderInUse = service.DnsProviderInUse,
        DnsQueriesServed = service.DnsQueriesServed,
        DnsCacheHits = service.DnsCacheHits,
        LatencyResult = service.LatencyResult,
        LatencyStatus = service.LatencyStatus,
        IsLatencyBusy = service.IsLatencyBusy,
        LatencyStage = service.LatencyStage,
        CanCancelLatencyRun = service.CanCancelLatencyRun,
        LastProbe = service.LastProbe,
        LastProbeAt = service.LastProbeAt,
        LastVerifiedAt = service.LastVerifiedAt,
        StatusDetail = service.StatusDetail,
        LearnedDomains = service.LearnedDomains,
        ProtectedDomainCount = service.ProtectedDomainCount,
        HotspotStatus = service.HotspotStatus,
        LastHotspotDiagnostics = service.LastHotspotDiagnostics,
        LastVodafoneFailure = service.LastVodafoneFailure,
        IsHotspotBusy = service.IsHotspotBusy,
        LastHotspotFailure = service.LastHotspotFailure,
        HotspotView = service.HotspotView,
        ThroughputCostBytes = service.ThroughputCostBytes,
        DnsRestorePending = service.DnsRestorePending,
        LastSaveFailure = service.LastSaveFailure,
    };
}
