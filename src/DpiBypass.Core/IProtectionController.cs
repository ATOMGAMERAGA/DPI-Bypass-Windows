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

namespace DpiBypass.Core;

/// <summary>The fixed desktop command contract. Native mutation stays in the helper.</summary>
public interface IProtectionController : IAsyncDisposable
{
    AppSettings Settings { get; }
    ProtectionState State { get; }
    EngineStatistics? Stats { get; }
    NetworkFingerprint Network { get; }
    IspProfile Isp { get; }
    IspDetection? Detection { get; }
    BypassStrategy Strategy { get; }
    DateTimeOffset? StrategyChosenAt { get; }
    StrategySelection? LastSelection { get; }
    bool IsFilterDown { get; }
    DnsMode ActiveDnsMode { get; }
    string? DnsProviderInUse { get; }
    long DnsQueriesServed { get; }
    long DnsCacheHits { get; }
    LatencyOptimizationResult LatencyResult { get; }
    LatencyStatusView LatencyStatus { get; }
    bool IsLatencyBusy { get; }
    LoadedLaneProgress LatencyStage { get; }
    event Action<LoadedLaneProgress>? LatencyStageChanged;
    bool CanCancelLatencyRun { get; }
    ProbeResult? LastProbe { get; }
    DateTimeOffset? LastProbeAt { get; }
    DateTimeOffset? LastVerifiedAt { get; }
    string? StatusDetail { get; }
    IReadOnlyList<string> LearnedDomains { get; }
    IReadOnlyList<string> ProtectedDomains();
    int ProtectedDomainCount { get; }
    Task<ProbeResult> ProbeAsync(string host, CancellationToken cancellationToken = default);
    HotspotStatus HotspotStatus { get; }
    HotspotDiagnosticResult? LastHotspotDiagnostics { get; }
    string? LastVodafoneFailure { get; }
    bool IsHotspotBusy { get; }
    string? LastHotspotFailure { get; }
    HotspotStatusView HotspotView { get; }
    event Action? Changed;
    event Action<string, string>? HostRewritten;
    event Action<string, int, int>? TuningProgress;
    event Action<string>? DomainLearned;
    Task StartIndependentFeaturesAsync(CancellationToken cancellationToken = default);
    void StartNetworkWatch();
    Task RunStartupHotspotCheckAsync(CancellationToken cancellationToken = default);
    Task<LatencyOptimizationResult> SetLowLatencyModeAsync( bool enabled, CancellationToken cancellationToken = default);
    Task<LatencyOptimizationResult> TestLatencyAsync(CancellationToken cancellationToken = default);
    Task<LatencyOptimizationResult> RunLoadedLatencyTestAsync(CancellationToken cancellationToken = default);
    void CancelLoadedLatencyTest();
    void CancelLatencyRun();
    Task<IReadOnlyList<string>> ListConnectedProcessesAsync(CancellationToken cancellationToken = default);
    string LatencyLoadInstruction(LoadDirection direction);
    Task<IReadOnlyList<string>> ListBulkApplicationsAsync(CancellationToken cancellationToken = default);
    Task<LatencyOptimizationResult> RetestLatencyAsync(CancellationToken cancellationToken = default);
    void SetLatencyPreferences(LatencyPreferences preferences);
    void SetValorantCaptureRegion(ScreenCaptureRegion region);
    Task<LatencyOptimizationResult> RestoreLatencyAsync(CancellationToken cancellationToken = default);
    bool ClearLatencyProfiles();
    Task StartAsync(CancellationToken cancellationToken = default);
    void OnSystemResumed(string reason);
    Task<HotspotDiagnosticResult> RunHotspotDiagnosticsAsync(CancellationToken cancellationToken = default);
    void SetHotspotDiagnostics(bool enabled);
    void EnableVodafoneModeHere();
    void DisableVodafoneMode();
    void SetVodafoneTtl(int ttl);
    void SetVodafoneDropIPv6(bool drop);
    bool RememberCurrentVodafoneNetwork();
    void ForgetVodafoneNetwork(string key);
    HotspotMigrationResult CleanUpLegacyHotspotConfiguration();
    Task<ProbeResult> VerifyAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(string Host, ProbeResult Result)>> VerifyAllAsync(CancellationToken cancellationToken = default);
    Task<TuningResult?> RetuneAsync(bool measureThroughput, CancellationToken cancellationToken = default);
    Task<TuningResult?> RetuneAsync(CancellationToken cancellationToken = default);
    long ThroughputCostBytes { get; }
    void ApplyScope(ProtectionScope scope);
    void ApplyManualStrategy(string? strategyId);
    void ApplyManualIsp(string? ispProfileId);
    void ApplyQuicSetting(bool blockQuic);
    Task<LunarAdBlockState> ApplyLunarAdBlockAsync( bool enabled, CancellationToken cancellationToken = default);
    LunarAdBlockState SyncLunarAdBlock();
    LunarAdBlockState DescribeLunarAdBlock();
    void SaveSettings();
    Task StopAsync(CancellationToken cancellationToken = default);
    string? DnsRestorePending { get; }
    ConfigSaveResult? LastSaveFailure { get; }
    event Action<ConfigSaveResult?>? SaveStatusChanged;
    bool RetrySave();
    DiagnosticSnapshot CaptureDiagnostics();
    bool AddDomain(string domain);
    bool RemoveDomain(string domain);
    bool RestoreDomain(string domain);
    void ApplyDiscoverySetting(bool enabled);
    void ApplyRecheckInterval(int seconds);
    bool CanProtectHost(string host, string processName);
    bool TryAcquireExperiment(string operationId, string expectedBeforeId);
    bool TryWriteExperiment(string operationId, string strategyId);
    bool TryReleaseExperiment(string operationId, string beforeId, string installedId);
    WindowsSocialRuntime SocialExperiments { get; }
}
