using DpiBypass.Core.Engine;
using DpiBypass.Core.SocialAccess;

namespace DpiBypass.Core;

public sealed partial class ProtectionService
{
    private WindowsSocialRuntime? _socialRuntime;
    private IExperimentStrategyControl? _experimentStrategies;
    public IExperimentStrategyControl ExperimentStrategies => _experimentStrategies ??= new LocalExperimentStrategyControl(_strategies);
    public bool CanProtectHost(string host, string processName) => _matcher.ShouldProtect(host, processName);
    public bool TryAcquireExperiment(string operationId, string expectedBeforeId) => ExperimentStrategies.TryAcquire(operationId, expectedBeforeId);
    public bool TryWriteExperiment(string operationId, string strategyId) => ExperimentStrategies.TryWrite(operationId, strategyId);
    public bool TryReleaseExperiment(string operationId, string beforeId, string installedId) => ExperimentStrategies.TryRelease(operationId, beforeId, installedId);
    private Task? InvalidateSocialSettingsContext()
    {
        var coordinator = _socialRuntime?.Coordinator;
        if (coordinator is null || coordinator.State.Phase is
            ExperimentPhase.IDLE or ExperimentPhase.DISABLED || coordinator.State.Phase == ExperimentPhase.FINISHED && !coordinator.HasResources) return null;
        // StopAsync cancels probes immediately, before its first await. Scope/DNS/QUIC changes
        // then remain user-owned; Alpha rollback only restores its own strategy field.
        return coordinator.StopAsync("CONTEXT_CHANGED");
    }
    private async Task ApplySelectedStrategyAfterSocialAsync(Task stopped, string? requestedId)
    {
        await stopped.ConfigureAwait(false);
        if (_socialRuntime?.Coordinator.HasResources == true || Settings.ManualStrategyId != requestedId) return;
        if (_engine is not null && StrategyLibrary.Find(requestedId) is { } selected)
        {
            _strategies.ApplyImmediate("Alpha sonrasında kullanıcının seçtiği yöntem", selected);
            SetState(State, $"Koruma etkin · {selected.Name}");
        }
    }
    public WindowsSocialRuntime SocialExperiments => _socialRuntime ??= new WindowsSocialRuntime(ExperimentStrategies,
        Settings.SocialAccess, () => _engine is not null,
        host => _matcher.ShouldProtect(host, "chrome.exe"),
        host => _matcher.ShouldProtect(host, "DpiBypass.exe"));
}
