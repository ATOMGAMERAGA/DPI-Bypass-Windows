using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DpiBypass.Core.SocialAccess;

public sealed class ExperimentCoordinator
{
    private readonly IReadOnlyList<IExperimentHandler> _handlers;
    private readonly IContentObservationAdapter _observer;
    private readonly ExperimentJournal _journal;
    private readonly TimeProvider _clock;
    private readonly ISet<string> _suppressed;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly HashSet<string> _seenEvents = [];
    private readonly HashSet<string> _effects = [];
    private readonly List<IExperimentHandler> _deferred = [];
    private Queue<IExperimentHandler> _remaining = new();
    private CancellationTokenSource? _run;
    private CancellationTokenSource? _observation;
    private ITimer? _inactivity;
    private IExperimentHandler? _active;
    private UndoRecord? _undo;
    private SocialGoal? _goal;
    private SocialTarget? _target;
    private string _sessionId = "";
    private long _contextGeneration;
    private long _modeGeneration;
    private long _documentGeneration;
    private bool _baselineKnown;
    private bool _observerWasAvailable;
    private bool _autoCorrection;
    private FeedbackEvent? _pendingResolution;
    private bool _serverGateObserved;
    private long _cooldownUntil;
    private readonly List<AttemptResult> _results = [];
    public ExperimentCoordinator(IReadOnlyList<IExperimentHandler> handlers, IContentObservationAdapter observer,
        ExperimentJournal journal, TimeProvider? clock = null, ISet<string>? suppressed = null)
    { _handlers = handlers; _observer = observer; _journal = journal; _clock = clock ?? TimeProvider.System; _suppressed = suppressed ?? new HashSet<string>(); }
    public event Action? Changed;
    public ExperimentState State { get; private set; } = new();
    public TestMode Mode { get; private set; }
    public ExperimentBudget Budget { get; private set; } = new();
    public ExperimentContext? Context { get; private set; }
    public Func<SocialTarget, ExperimentBudget, CancellationToken, Task<NetworkPathResult>>? BaselineProbe { get; set; }
    public DateTimeOffset? CooldownUntilUtc { get; private set; }
    public void RestoreCooldown(DateTimeOffset? until)
    {
        if (until is not { } date || date <= _clock.GetUtcNow()) return;
        SetCooldown(Math.Clamp((date - _clock.GetUtcNow()).TotalSeconds, 1, 3600));
    }
    private void SetCooldown(double seconds)
    {
        _cooldownUntil = _clock.GetTimestamp() + (long)(seconds * _clock.TimestampFrequency);
        CooldownUntilUtc = _clock.GetUtcNow().AddSeconds(seconds);
    }
    public IReadOnlyList<AttemptResult> Results => _results.ToArray();
    public IReadOnlyList<(string Id, string Status, string Reason)> EligibilityResults => _eligibility.ToArray();
    private readonly List<(string Id, string Status, string Reason)> _eligibility = [];
    public bool HasResources => _undo is not null || State.Phase == ExperimentPhase.RECOVERY_REQUIRED;

    public async Task RecoverAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = _journal.Read();
            if (entry is null) return;
            Budget.Restore(entry); _undo = entry.Undo;
            _active = _handlers.FirstOrDefault(h => h.Definition.Id == _undo.HandlerId);
            if (_active is null || _undo.SchemaVersion != 2) { Recovery(); return; }
            if (await RollbackAsync().ConfigureAwait(false)) Set(ExperimentPhase.PAUSED, "Önceki deneme geri alındı. Yeniden başlatmak için hedefi seçin.", pause: "CONTEXT_CHANGED");
        }
        catch (Exception) { Recovery(); }
        finally { _serial.Release(); }
    }

    public async Task StartAsync(SocialGoal goal, SocialTarget target, TestMode mode, bool? baselinePresent)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_clock.GetTimestamp() < _cooldownUntil) throw new InvalidOperationException("Hedefin bekleme süresi henüz dolmadı.");
            if (HasResources || _journal.Exists || State.Phase is not (ExperimentPhase.IDLE or ExperimentPhase.DISABLED or ExperimentPhase.FINISHED or ExperimentPhase.PAUSED))
                throw new InvalidOperationException("Önce etkin denemeyi durdurun veya kurtarın.");
            if (!Enum.IsDefined(mode) || goal.PlatformId != target.PlatformId || goal.AllowedSurfaces.Length == 0
                || goal.RequiredCapabilities.Length == 0 || goal.IdentityContext is not ("SAME_ACCOUNT" or "ANONYMOUS"))
                throw new ArgumentException("Geçersiz hedef kapsamı.");
            _goal = goal; _target = target; Mode = mode;
            _sessionId = Guid.NewGuid().ToString("N"); _contextGeneration++; _modeGeneration++;
            _effects.Clear(); _seenEvents.Clear(); _deferred.Clear(); _results.Clear(); _eligibility.Clear();
            _remaining = new(_handlers); Budget = new(); _serverGateObserved = false;
            _autoCorrection = _suppressed.Contains(CorrectionKey(goal, target)); _pendingResolution = null;
            _run?.Dispose(); _run = new(); _baselineKnown = baselinePresent == true;
            _observerWasAvailable = _observer.CanObserve(goal);
            Set(ExperimentPhase.BASELINING, baselinePresent is null ? "Başlangıç sorunu doğrulanamadı; sonuç nedensellik kanıtı olmayacak." : "Başlangıç durumu kaydedildi.");
            if (baselinePresent == false) { Finish(FinishReason.ALREADY_ACCESSIBLE, "Sorun başlangıçta yok; hiçbir yönteme başarı yazılmadı."); return; }
            if (BaselineProbe is not null)
            {
                var network = await BaselineProbe(target, Budget, _run.Token).ConfigureAwait(false);
                if (network.PauseReason == "COOLDOWN")
                {
                    SetCooldown(network.RetryAfterSeconds > 0 ? network.RetryAfterSeconds : 300);
                    Set(ExperimentPhase.PAUSED, "Hedef bekleme süresi bildirdi; başlangıç tanısı durduruldu.", pause: "COOLDOWN"); return;
                }
            }
            if (mode == TestMode.AUTO_BETA && _observerWasAvailable && goal.OriginalSurface == "OFFICIAL_WEB" && !_autoCorrection)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(goal, SocialJson.Options))));
                var baseline = new ExperimentContext(_sessionId, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), _contextGeneration,
                    _modeGeneration, ++_documentGeneration, hash, goal, target, new CandidateDefinition { Id = "baseline", Family = "A0" });
                Context = baseline;
                _observation?.Dispose();
                _observation = CancellationTokenSource.CreateLinkedTokenSource(_run.Token);
                _observation.CancelAfter(TimeSpan.FromSeconds(20));
                Set(ExperimentPhase.PROBING, "Özgün web hedefinin başlangıç durumu otomatik gözleniyor…");
                try
                {
                    var initial = await _observer.ObserveAsync(baseline, _observation.Token).WaitAsync(_observation.Token).ConfigureAwait(false);
                    var initialDecision = initial.Matches(baseline) ? GoalEvaluator.Evaluate(goal, initial) : new(AutoVerdict.INCONCLUSIVE, "STALE_OBSERVATION");
                    if (initialDecision.Verdict == AutoVerdict.PASS) { Finish(FinishReason.ALREADY_ACCESSIBLE, "Özgün hedef başlangıçta açıktı; yönteme başarı yazılmadı."); return; }
                    _baselineKnown = initialDecision.Verdict == AutoVerdict.FAIL;
                }
                catch (Exception) { _baselineKnown = false; }
            }
            await NextAsync().ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    public FeedbackEvent CreateFeedback(UserOutcome outcome)
    {
        var c = Context ?? throw new InvalidOperationException("Etkin deneme yok.");
        return new(c.SessionId, c.AttemptId, c.OperationId, c.ContextGeneration, c.ModeGeneration,
            Guid.NewGuid().ToString("N"), State.Version, outcome);
    }
    public async Task FeedbackAsync(FeedbackEvent e)
    {
        // Human resolution cancels asynchronous observations immediately, before the serial queue.
        if (e.Outcome == UserOutcome.RESOLVED && MatchesFeedback(e)) { _pendingResolution = e; _observation?.Cancel(); }
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!MatchesFeedback(e) || !_seenEvents.Add(e.EventId) || _active is null) return;
            if (State.Phase is not (ExperimentPhase.WAITING_FOR_USER or ExperimentPhase.PAUSED)) return;
            _inactivity?.Dispose(); _inactivity = null;
            if (e.Outcome == UserOutcome.PARTIAL) { Set(ExperimentPhase.WAITING_FOR_USER, "Kısmen çalışıyor. Böyle kullanabilir veya sonraki yönteme geçebilirsiniz."); return; }
            _results.Add(Result(e.Outcome, AutoVerdict.NOT_RUN, "UNKNOWN", "USER_REPORTED", "USER"));
            if (e.Outcome == UserOutcome.RESOLVED)
            {
                SaveUndo("KEPT"); Finish(FinishReason.RESOLVED_USER_REPORTED, "Kullanıcı açıldığını bildirdi; arama durdu.", ResourceDisposition.KEPT_ACTIVE); return;
            }
            if (e.Outcome == UserOutcome.NOT_TESTED) _deferred.Add(_active);
            if (!await RollbackAsync().ConfigureAwait(false)) return;
            if (e.Outcome == UserOutcome.NOT_TESTED) { Set(ExperimentPhase.PAUSED, "Deneme geri alındı; sonuç ölçülmedi.", pause: "USER_UNABLE_TO_TEST"); return; }
            await NextAsync().ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    private bool MatchesFeedback(FeedbackEvent e) => Context is { } c && e.SessionId == c.SessionId && e.AttemptId == c.AttemptId
        && e.OperationId == c.OperationId && e.ContextGeneration == _contextGeneration && e.ModeGeneration == _modeGeneration
        && e.ExpectedStateVersion == State.Version && Enum.IsDefined(e.Outcome);

    public async Task SwitchModeAsync(TestMode mode)
    {
        if (!Enum.IsDefined(mode)) return;
        Interlocked.Increment(ref _modeGeneration); _observation?.Cancel();
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            Mode = mode;
            if (Context is { } c) Context = c with { ModeGeneration = _modeGeneration, DocumentGeneration = ++_documentGeneration };
            if (State.FinishReason is FinishReason.RESOLVED_USER_REPORTED or FinishReason.RESOLVED_AUTO_OBSERVED or FinishReason.PARTIAL_ACCEPTED) return;
            if (_undo is not null)
            {
                if (mode == TestMode.MANUAL) WaitForUser();
                else if (!await EvaluateActiveAsync().ConfigureAwait(false) && !_run!.IsCancellationRequested)
                    await NextAsync().ConfigureAwait(false);
            }
        }
        finally { _serial.Release(); }
    }
    public async Task ContinueManuallyAsync()
    {
        await SwitchModeAsync(TestMode.MANUAL).ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_undo is not null) return;
            if (_clock.GetTimestamp() < _cooldownUntil) return;
            if (State.Phase == ExperimentPhase.RECOVERY_REQUIRED || _goal is null) return;
            _remaining = new(_deferred.Concat(_remaining).Distinct()); _deferred.Clear(); _effects.Clear();
            await NextAsync().ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    public async Task CorrectAutoPassAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State.FinishReason != FinishReason.RESOLVED_AUTO_OBSERVED || _active is null) return;
            _autoCorrection = true; Mode = TestMode.MANUAL; _modeGeneration++;
            _suppressed.Add(CorrectionKey(_goal!, _target!));
            _results.Add(Result(UserOutcome.STILL_PRESENT, AutoVerdict.PASS, "UNKNOWN", "USER_FALSE_POSITIVE_CORRECTION", "USER"));
            if (Context is { } c) Context = c with { ModeGeneration = _modeGeneration };
            if (await RollbackAsync().ConfigureAwait(false)) await NextAsync().ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    public async Task StopAsync(string? pauseReason = null)
    {
        _run?.Cancel(); _observation?.Cancel();
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            _modeGeneration++; _inactivity?.Dispose(); _inactivity = null;
            Set(ExperimentPhase.CANCELLING, "Deneme durduruluyor…");
            if (!await RollbackAsync().ConfigureAwait(false)) return;
            if (pauseReason is not null) { _contextGeneration++; Set(ExperimentPhase.PAUSED, "Deneme geri alındı; yeniden başlangıç kontrolü gerekiyor.", pause: pauseReason); }
            else Finish(FinishReason.CANCELLED, "Deneme durduruldu ve Alpha değişiklikleri geri alındı.");
        }
        finally { _serial.Release(); }
    }

    public async Task AcceptPartialAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State.Phase != ExperimentPhase.WAITING_FOR_USER || _active is null) return;
            _results.Add(Result(UserOutcome.PARTIAL, AutoVerdict.NOT_RUN, "UNKNOWN", "USER_ACCEPTED_PARTIAL", "USER"));
            SaveUndo("KEPT"); Finish(FinishReason.PARTIAL_ACCEPTED, "Kısmî sonuç kullanıcı tarafından kabul edildi; kapsam genişletilmedi.", ResourceDisposition.KEPT_ACTIVE);
        }
        finally { _serial.Release(); }
    }
    public async Task ResumeRemainingAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State.Phase != ExperimentPhase.PAUSED || State.PauseReason != "BUDGET_LIMIT" || _undo is not null) return;
            Budget = new();
            await NextAsync().ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    private static string CorrectionKey(SocialGoal goal, SocialTarget target) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("observer-1.0.0|evaluator-1.0.0|" + JsonSerializer.Serialize(goal, SocialJson.Options) + "|" + target.ContentId)));

    private async Task NextAsync()
    {
        while (_remaining.Count > 0 && !_run!.IsCancellationRequested)
        {
            if (Budget.Exhausted) { Set(ExperimentPhase.PAUSED, "Bu turun sınırına ulaşıldı; kalan yöntemler tüketilmedi.", pause: "BUDGET_LIMIT"); return; }
            var handler = _remaining.Dequeue();
            var d = handler.Definition;
            if (_serverGateObserved && d.NetworkMutation) { _eligibility.Add((d.Id, "UNSUPPORTED_CLIENT", "TARGET_GATE_NETWORK_NOT_INDICATED")); continue; }
            if (d.SchemaVersion != 2 || d.RemoteRelay != "NO" || d.RegionChange != "NO" || d.IdentityCollection != "NO" || d.CredentialExport != "NO"
                || d.EvidenceLabel is "RESEARCH_ONLY" or "RETIRED") { _eligibility.Add((d.Id, "EXCLUDED_BY_PREFERENCE", "CONSTRAINT_UNKNOWN_OR_FORBIDDEN")); continue; }
            if (_effects.Contains(d.EffectKey)) { _eligibility.Add((d.Id, "UNSUPPORTED_CLIENT", "DUPLICATE_EFFECT")); continue; }
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_goal, SocialJson.Options))));
            Context = new(_sessionId, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), _contextGeneration, _modeGeneration,
                ++_documentGeneration, hash, _goal!, _target!, d);
            var eligible = handler.Inspect(Context);
            _eligibility.Add((d.Id, eligible.Status, eligible.ReasonCode));
            if (!eligible.CanApply) continue;
            if (Mode == TestMode.AUTO_BETA && ((!_observer.CanObserve(_goal!) && !d.NetworkMutation) || _autoCorrection)) { _deferred.Add(handler); continue; }
            _effects.Add(d.EffectKey); Budget.Attempts++; _active = handler;
            try
            {
                Set(ExperimentPhase.PREPARING, $"Yöntem {Budget.Attempts}: {d.DisplayName}", candidate: d.Id);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_run.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                _undo = await handler.PrepareAsync(Context, timeout.Token).ConfigureAwait(false);
                if (_undo.OperationId != Context.OperationId || _undo.HandlerId != d.Id) throw new InvalidDataException("UNDO_MISMATCH");
                SaveUndo("APPLYING"); Set(ExperimentPhase.APPLYING, d.DisplayName, ResourceDisposition.TRIAL_ACTIVE, d.Id);
                var apply = handler.ApplyAsync(Context, _undo, timeout.Token);
                try { await apply.WaitAsync(TimeSpan.FromSeconds(15), _run.Token).ConfigureAwait(false); }
                catch (Exception)
                {
                    timeout.Cancel();
                    // A timed-out helper may still mutate. Never roll forward while it can.
                    try { await apply.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (TimeoutException) { Recovery(); return; }
                    catch (Exception) { }
                    throw;
                }
                SaveUndo("ACTIVE");
                if (Mode == TestMode.MANUAL) { WaitForUser(); return; }
                if (await EvaluateActiveAsync().ConfigureAwait(false)) return;
            }
            catch (BudgetExceededException)
            {
                if (await RollbackAsync().ConfigureAwait(false)) Set(ExperimentPhase.PAUSED, "Bu turun veri/istek sınırına ulaşıldı.", pause: "BUDGET_LIMIT"); return;
            }
            catch (OperationCanceledException)
            {
                await RollbackAsync().ConfigureAwait(false); return;
            }
            catch (Exception)
            {
                // Exception text may include a target URL; only a fixed reason is recorded.
                _results.Add(Result(UserOutcome.NOT_TESTED, AutoVerdict.INCONCLUSIVE, "UNKNOWN", "HANDLER_ERROR", "AUTO_EVALUATOR"));
                _deferred.Add(handler);
                if (!await RollbackAsync().ConfigureAwait(false)) return;
            }
        }
        if (_run!.IsCancellationRequested) return;
        if (_deferred.Count > 0) Finish(_observerWasAvailable ? FinishReason.AUTO_INCONCLUSIVE : FinishReason.AUTO_UNAVAILABLE,
            "Otomatik kanıt yetersiz. Manuel inceleme için yöntemler korundu.");
        else Finish(Budget.Attempts == 0 ? FinishReason.NO_APPLICABLE_METHODS : FinishReason.CANDIDATES_EXHAUSTED,
            Budget.Attempts == 0 ? "Bu kapsamda uygulanabilir yöntem bulunamadı." : "Uygun yöntemler tüketildi; bu koşullarda çözüm gözlenmedi.");
    }

    private async Task<bool> EvaluateActiveAsync()
    {
        var c = Context!;
        Set(ExperimentPhase.PROBING, "Seçilen hedefin bağlantı/içerik kanıtı bekleniyor…", ResourceDisposition.TRIAL_ACTIVE, c.Candidate.Id);
        var started = _clock.GetTimestamp();
        var priorActiveSeconds = Budget.ActiveSeconds;
        _observation?.Dispose(); _observation = CancellationTokenSource.CreateLinkedTokenSource(_run!.Token);
        _observation.CancelAfter(TimeSpan.FromSeconds(20));
        ObservationRecord o;
        try { o = await _observer.ObserveAsync(c, _observation.Token).WaitAsync(_observation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (c.ModeGeneration != _modeGeneration || _run.IsCancellationRequested) return true;
            o = ObservationRecord.For(c) with { ObserverError = true };
        }
        catch (Exception) { o = ObservationRecord.For(c) with { ObserverError = true }; }
        Budget.ActiveSeconds = Math.Max(Budget.ActiveSeconds, priorActiveSeconds + _clock.GetElapsedTime(started).TotalSeconds);
        if (c.ModeGeneration != _modeGeneration || c.ContextGeneration != _contextGeneration || _run.IsCancellationRequested) return true;
        if (_pendingResolution is { } human && human.OperationId == c.OperationId && human.ContextGeneration == c.ContextGeneration)
        {
            _results.Add(Result(UserOutcome.RESOLVED, AutoVerdict.NOT_RUN, "UNKNOWN", "USER_REPORTED", "USER"));
            _pendingResolution = null; SaveUndo("KEPT");
            Finish(FinishReason.RESOLVED_USER_REPORTED, "Kullanıcı açıldığını bildirdi; otomatik arama durdu.", ResourceDisposition.KEPT_ACTIVE); return true;
        }
        Set(ExperimentPhase.AUTO_EVALUATING, "Hedef kanıtı değerlendiriliyor…", ResourceDisposition.TRIAL_ACTIVE, c.Candidate.Id);
        var decision = o.Matches(c) ? GoalEvaluator.Evaluate(_goal!, o) : new GoalDecision(AutoVerdict.INCONCLUSIVE, "STALE_OBSERVATION");
        var rateLimited = o.Matches(c) && o.ReasonCodes.Contains("RATE_LIMITED");
        if (rateLimited) decision = new(AutoVerdict.INCONCLUSIVE, "RATE_LIMITED");
        if (decision.Verdict == AutoVerdict.FAIL && o.BlockingGate == "TRUE") _serverGateObserved = true;
        _results.Add(Result(UserOutcome.NOT_TESTED, decision.Verdict,
            decision.Verdict is AutoVerdict.PASS or AutoVerdict.FAIL ? decision.Verdict.ToString() : "UNKNOWN", decision.Reason, "AUTO_EVALUATOR",
            o, _baselineKnown && c.Candidate.NetworkMutation && o.RouteProvenance == "EXACT_TARGET_PATH" ? "OBSERVED_IN_CONTEXT" : "NOT_ESTABLISHED"));
        if (decision.Verdict == AutoVerdict.PASS)
        { SaveUndo("KEPT"); Finish(FinishReason.RESOLVED_AUTO_OBSERVED, "Seçilen işlem otomatik testte çalıştı. Sonuç yalnızca seçilen içerik ve yüzey içindir.", ResourceDisposition.KEPT_ACTIVE); return true; }
        if (decision.Verdict == AutoVerdict.NEEDS_INTERACTION)
        {
            Set(ExperimentPhase.PAUSED, "Seçilen içerikte oynat düğmesine dokunun; ardından otomatik teste devam edin veya manuel sonucu bildirin.",
                ResourceDisposition.TRIAL_ACTIVE, c.Candidate.Id, pause: "USER_ACTION_REQUIRED");
            _inactivity?.Dispose();
            _inactivity = _clock.CreateTimer(_ => _ = StopAsync("USER_INACTIVE"), null, TimeSpan.FromMinutes(15), Timeout.InfiniteTimeSpan);
            return true;
        }
        if (decision.Verdict != AutoVerdict.FAIL) _deferred.Add(_active!);
        if (!await RollbackAsync().ConfigureAwait(false)) return true;
        if (rateLimited)
        {
            var seconds = double.IsFinite(o.RetryAfterSeconds) && o.RetryAfterSeconds > 0 ? Math.Clamp(o.RetryAfterSeconds, 1, 3600) : 300;
            SetCooldown(seconds);
            Set(ExperimentPhase.PAUSED, "Hedef çok fazla istek yanıtı verdi. Bekleme süresi dolmadan yeni deneme yapılmaz.", pause: "COOLDOWN"); return true;
        }
        return false;
    }
    public async Task ResumeObservationAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State.Phase != ExperimentPhase.PAUSED || State.PauseReason != "USER_ACTION_REQUIRED" || _undo is null || Mode != TestMode.AUTO_BETA) return;
            _inactivity?.Dispose(); _inactivity = null;
            if (Budget.Exhausted) { if (await RollbackAsync().ConfigureAwait(false)) Set(ExperimentPhase.PAUSED, "Bu turun sınırına ulaşıldı.", pause: "BUDGET_LIMIT"); return; }
            Context = Context! with { DocumentGeneration = ++_documentGeneration };
            if (!await EvaluateActiveAsync().ConfigureAwait(false)) await NextAsync().ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    private void WaitForUser()
    {
        Set(ExperimentPhase.WAITING_FOR_USER, "Aynı hesap ve seçilen içerikte kontrol edin: sorun hâlâ var mı?", ResourceDisposition.TRIAL_ACTIVE, _active!.Definition.Id);
        _inactivity?.Dispose();
        _inactivity = _clock.CreateTimer(_ => _ = StopAsync("USER_INACTIVE"), null, TimeSpan.FromMinutes(15), Timeout.InfiniteTimeSpan);
    }
    private AttemptResult Result(UserOutcome user, AutoVerdict automatic, string technical, string reason, string decision,
        ObservationRecord? observation = null, string causal = "NOT_ESTABLISHED") => new(Context?.Candidate.Id ?? "unknown", user, automatic,
        technical, reason, decision, causal)
        {
            TestMode = Mode, ModeGeneration = _modeGeneration, ContextGeneration = _contextGeneration,
            IdentityContext = observation?.IdentityContext ?? _goal?.IdentityContext ?? "UNKNOWN",
            ActualSurface = observation?.ActualSurface ?? "UNKNOWN", ObservedCapabilities = observation?.ObservedCapabilities ?? [],
            GoalMatch = automatic == AutoVerdict.PASS && observation is not null ? "MATCHED" : "UNKNOWN",
            EvidenceOrigin = decision == "USER" ? "USER_REPORTED" : observation?.SignalSource ?? "NETWORK_PROBE",
            CandidateVersion = Context?.Candidate.CandidateVersion ?? "1.0.0", BaselineObserved = _baselineKnown ? "PROBLEM_PRESENT" : "UNKNOWN",
            RecordedAtUtc = _clock.GetUtcNow(),
        };
    private async Task<bool> RollbackAsync()
    {
        if (_undo is null) { _active = null; return true; }
        Set(ExperimentPhase.ROLLING_BACK, "Deneme geri alınıyor ve doğrulanıyor…", ResourceDisposition.ROLLBACK_PENDING, _active?.Definition.Id);
        try
        {
            SaveUndo("ROLLING_BACK");
            if (_active is null || !await _active.RollbackAsync(_undo, CancellationToken.None).ConfigureAwait(false)) { Recovery(); return false; }
            _journal.Clear(); _undo = null; _active = null; return true;
        }
        catch (Exception) { Recovery(); return false; }
    }
    private void SaveUndo(string stage)
    {
        if (_undo is null) return;
        _undo = _undo with { Stage = stage };
        _journal.Write(new(_undo, Budget.Attempts, Budget.Requests, Budget.Bytes, Budget.ActiveSeconds, Budget.PageLoads));
    }
    private void Recovery() => Set(ExperimentPhase.RECOVERY_REQUIRED, "Geri alma doğrulanamadı. Yeni deneme engellendi; Durdur / geri al ile yeniden deneyin.", ResourceDisposition.RECOVERY_REQUIRED);
    private void Finish(FinishReason reason, string message, ResourceDisposition disposition = ResourceDisposition.NONE)
    { _inactivity?.Dispose(); _inactivity = null; Set(ExperimentPhase.FINISHED, message, disposition, _active?.Definition.Id, finish: reason); }
    private void Set(ExperimentPhase phase, string message, ResourceDisposition disposition = ResourceDisposition.NONE,
        string? candidate = null, string? pause = null, FinishReason? finish = null)
    { State = new(phase, finish, pause, disposition, State.Version + 1, message, candidate); Changed?.Invoke(); }
}
