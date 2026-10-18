using System.Diagnostics;
using DpiBypass.Core.Engine;

namespace DpiBypass.Core.SocialAccess;

public sealed class OfficialWebHandler(Func<bool> automatic) : IExperimentHandler
{
    public CandidateDefinition Definition { get; } = new() { Id = "official-web", HandlerId = "official-web", Family = "A1",
        DisplayName = "Resmî web yolunda aynı içeriği aç", EffectKey = "official-web-selected-content", RollbackKind = "NO_SYSTEM_MUTATION",
        ScopeNote = "Açılan tarayıcı sekmesi kullanıcıya aittir; kapanmaya zorlanmaz. Native uygulama onarımı değildir.",
        Source = "https://www.youtube.com/ ; https://www.instagram.com/" };
    public Eligibility Inspect(ExperimentContext c) => !automatic() && !DesktopUrlLauncher.Available()
        ? new("MISSING_PERMISSION", "UNELEVATED_USER_SHELL_UNAVAILABLE") : !c.Goal.AllowedSurfaces.Contains("OFFICIAL_WEB")
        ? new("UNSUPPORTED_CLIENT", "WEB_SURFACE_NOT_ALLOWED") : c.Goal.OriginalSurface == "OFFICIAL_WEB"
            ? new("UNSUPPORTED_CLIENT", "NO_OP_ORIGINAL_SURFACE") : Eligibility.Eligible;
    public Task<UndoRecord> PrepareAsync(ExperimentContext c, CancellationToken token)
        => Task.FromResult(new UndoRecord { HandlerId = Definition.Id, OperationId = c.OperationId });
    public Task ApplyAsync(ExperimentContext c, UndoRecord undo, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // AUTO_BETA uses the explicitly bound companion tab. It must not spawn a tab per strategy.
        if (!automatic()) DesktopUrlLauncher.Open(c.Target);
        return Task.CompletedTask;
    }
    public Task<bool> RollbackAsync(UndoRecord undo, CancellationToken token) => Task.FromResult(true);
}

public sealed class EngineStrategyHandler(IExperimentStrategyControl strategies, BypassStrategy candidate,
    Func<bool> ready, Func<bool> consent, Func<string, bool> targetInScope) : IExperimentHandler
{
    private string? _operation;
    public CandidateDefinition Definition { get; } = new() { Id = "network-" + candidate.Id, HandlerId = "network-" + candidate.Id, Family = "N4",
        DisplayName = candidate.Name, EffectKey = "existing-engine:" + candidate.Id, NetworkMutation = true,
        ScopeNote = "Geçerli koruma kapsamındaki yeni bağlantıları etkiler; tek sekme izolasyonu yoktur. Mevcut DNS/QUIC ayarı korunur.",
        Source = "https://github.com/ATOMGAMERAGA/DPI-Bypass-Windows/blob/main/src/DpiBypass.Core/Engine/StrategyLibrary.cs" };
    public Eligibility Inspect(ExperimentContext c)
    {
        if (!consent()) return new("EXCLUDED_BY_PREFERENCE", "EXISTING_ENGINE_SCOPE_NOT_ACCEPTED");
        if (!ready()) return new("MISSING_DEPENDENCY", "ENGINE_NOT_RUNNING");
        if (!targetInScope(c.Target.Url.Host)) return new("UNSUPPORTED_OS", "TARGET_OUTSIDE_CURRENT_ENGINE_SCOPE");
        if (c.Goal.ProblemClass is ProblemClass.AGE_OR_ID_VERIFICATION_REQUIRED or ProblemClass.LOGIN_REQUIRED
            or ProblemClass.BOT_CHALLENGE or ProblemClass.ACCOUNT_RESTRICTION)
            return new("UNSUPPORTED_CLIENT", "SERVER_REQUIREMENT_NOT_A_NETWORK_EXPERIMENT");
        if (strategies.CurrentStrategy == candidate) return new("UNSUPPORTED_CLIENT", "NO_OP_CURRENT_STRATEGY");
        return Eligibility.Eligible;
    }
    public Task<UndoRecord> PrepareAsync(ExperimentContext c, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new UndoRecord { HandlerId = Definition.Id, OperationId = c.OperationId,
            Before = strategies.CurrentStrategy.Id, Installed = candidate.Id });
    }
    public Task ApplyAsync(ExperimentContext c, UndoRecord undo, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_operation == c.OperationId) return Task.CompletedTask;
        if (undo.Before is null || undo.Before != strategies.CurrentStrategy.Id) throw new InvalidOperationException("CONCURRENT_STRATEGY_CHANGE");
        if (!strategies.TryAcquire(c.OperationId, undo.Before)) throw new InvalidOperationException("ENGINE_OWNED_ELSEWHERE_OR_CHANGED");
        _operation = c.OperationId;
        if (!strategies.TryWrite(c.OperationId, candidate.Id)) throw new InvalidOperationException("STALE_OPERATION");
        return Task.CompletedTask;
    }
    public Task<bool> RollbackAsync(UndoRecord undo, CancellationToken token)
    {
        // Always ask the engine owner: the GUI can restart while its helper still
        // holds the lease recorded in the recovery journal.
        if (undo.Before is null || undo.Installed != candidate.Id) return Task.FromResult(false);
        var restored = strategies.TryRelease(undo.OperationId, undo.Before, undo.Installed);
        if (restored) _operation = null;
        return Task.FromResult(restored);
    }
}
