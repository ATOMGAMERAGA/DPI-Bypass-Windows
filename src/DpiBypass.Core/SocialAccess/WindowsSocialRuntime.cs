using DpiBypass.Core.Engine;

namespace DpiBypass.Core.SocialAccess;

public sealed class WindowsSocialRuntime : IAsyncDisposable, IContentObservationAdapter
{
    private readonly SocialPreferences _preferences;
    private readonly Func<string, bool> _probeInScope;
    private readonly NetworkProbeAdapter _probe = new();
    public ExperimentCoordinator Coordinator { get; }
    public BrowserObservationBridge Browser { get; }
    public ExperimentHistory History { get; }
    public NetworkPathResult? LastNetworkProbe { get; private set; }
    public WindowsSocialRuntime(IExperimentStrategyControl strategies, SocialPreferences preferences, Func<bool> ready,
        Func<string, bool> targetInScope, Func<string, bool> probeInScope)
    {
        _preferences = preferences; _probeInScope = probeInScope;
        var handlers = new List<IExperimentHandler> { new OfficialWebHandler(() => Coordinator!.Mode == TestMode.AUTO_BETA) };
        handlers.AddRange(StrategyLibrary.All.Where(s => s != StrategyLibrary.Passthrough)
            .OrderBy(s => s.Fake != FakeMode.None).ThenBy(s => s.OutOfBand).ThenBy(s => s.FakeCount).ThenBy(s => s.Id, StringComparer.Ordinal).Select(s =>
            new EngineStrategyHandler(strategies, s, ready, () => preferences.AllowNetworkChanges, targetInScope)));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DPI Bypass", "social-alpha");
        History = new(Path.Combine(directory, "history.json")); History.Load();
        Coordinator = new(handlers, this, new(Path.Combine(directory, "recovery.json")), suppressed: History.SuppressedTargets);
        Coordinator.RestoreCooldown(History.CooldownUntilUtc);
        Coordinator.BaselineProbe = async (target, budget, token) => LastNetworkProbe =
            await _probe.ProbeAsync(target, budget, _probeInScope(target.Url.Host), token).ConfigureAwait(false);
        Coordinator.Changed += () =>
        {
            if (Coordinator.State.Phase == ExperimentPhase.FINISHED || Coordinator.State.PauseReason == "COOLDOWN")
                History.Save(Coordinator.Results, Coordinator.CooldownUntilUtc);
        };
        Browser = new BrowserObservationBridge(() => preferences.AllowMeteredRenderer ? Coordinator.Context : null, () => Coordinator.State);
    }
    public async Task EnableAsync(bool enabled)
    {
        _preferences.Enabled = enabled;
        if (enabled) { await Coordinator.RecoverAsync().ConfigureAwait(false); Browser.Start(); }
        else { await Coordinator.StopAsync().ConfigureAwait(false); await Browser.StopAsync().ConfigureAwait(false); }
    }
    // Network cost and renderer bytes are not precisely available through the packet engine.
    // A separate visible one-session data preference is therefore required before renderer use.
    public bool CanObserve(SocialGoal goal) => _preferences.AllowMeteredRenderer && Browser.CanObserve(goal);
    public async Task<ObservationRecord> ObserveAsync(ExperimentContext context, CancellationToken token)
    {
        LastNetworkProbe = await _probe.ProbeAsync(context.Target, Coordinator.Budget, _probeInScope(context.Target.Url.Host), token).ConfigureAwait(false);
        if (LastNetworkProbe.PauseReason == "COOLDOWN") return ObservationRecord.For(context) with { ReasonCodes = ["RATE_LIMITED"], RetryAfterSeconds = LastNetworkProbe.RetryAfterSeconds };
        if (!CanObserve(context.Goal)) return ObservationRecord.For(context) with { ObserverError = true, ReasonCodes = ["MANUAL_REVIEW_REQUIRED"] };
        if (Coordinator.Budget.PageLoads >= 6) throw new BudgetExceededException();
        Coordinator.Budget.PageLoads++;
        return await Browser.ObserveAsync(context, token).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    { await Coordinator.StopAsync().ConfigureAwait(false); await Browser.StopAsync().ConfigureAwait(false); }
}
