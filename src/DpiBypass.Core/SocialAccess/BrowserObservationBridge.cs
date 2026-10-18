using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DpiBypass.Core.SocialAccess;

/// <summary>Same-user local channel. The browser can submit evidence, never privileged commands.</summary>
public sealed class BrowserObservationBridge(Func<ExperimentContext?> context, Func<ExperimentState> state) : IContentObservationAdapter
{
    public const string PipeName = "DpiBypass.SocialObserver.v2";
    private readonly Lock _gate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _server;
    private DateTimeOffset _lastHello;
    private TaskCompletionSource<ObservationRecord>? _pending;
    private ObservationRecord? _last;
    private ExperimentContext? _observing;
    private int? _tab;
    private double _lastTimestamp;
    public void Start()
    {
        if (_server is { IsCompleted: false }) return;
        _lifetime = new(); _server = Task.Run(() => ServeAsync(_lifetime.Token));
    }
    public bool CanObserve(SocialGoal goal) => _server is { IsCompleted: false }
        && DateTimeOffset.UtcNow - _lastHello < TimeSpan.FromMinutes(2)
        && goal.PlatformId == "youtube" && goal.AllowedSurfaces.Contains("OFFICIAL_WEB")
        && goal.RequiredCapabilities.All(x => x is "VIEW_SELECTED_CONTENT" or "PLAY_MEDIA");
    public async Task<ObservationRecord> ObserveAsync(ExperimentContext c, CancellationToken token)
    {
        TaskCompletionSource<ObservationRecord> pending;
        lock (_gate)
        {
            _observing = c; _last = null; _tab = null; _lastTimestamp = -1;
            pending = _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { return await pending.Task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { lock (_gate) return _last ?? ObservationRecord.For(c) with { ObserverError = true }; }
        finally { lock (_gate) { if (ReferenceEquals(_pending, pending)) { _pending = null; _observing = null; } } }
    }
    private object Handle(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
            return new { ok = false, reason = "INVALID_MESSAGE" };
        if (command.GetString() == "social.bind" && message.EnumerateObject().Count() == 1)
        {
            _lastHello = DateTimeOffset.UtcNow;
            var c = context();
            var current = state();
            if (c is null || current.Phase is not (ExperimentPhase.PROBING or ExperimentPhase.AUTO_EVALUATING))
                return new { ok = false, reason = "NO_ACTIVE_ATTEMPT", finished = current.Phase is ExperimentPhase.FINISHED or ExperimentPhase.PAUSED
                    or ExperimentPhase.RECOVERY_REQUIRED or ExperimentPhase.IDLE or ExperimentPhase.DISABLED or ExperimentPhase.WAITING_FOR_USER,
                    keepActive = current.ResourceDisposition == ResourceDisposition.KEPT_ACTIVE || current.Phase == ExperimentPhase.WAITING_FOR_USER
                        && current.ResourceDisposition == ResourceDisposition.TRIAL_ACTIVE || current.Phase == ExperimentPhase.PAUSED
                        && current.PauseReason == "USER_ACTION_REQUIRED" && current.ResourceDisposition == ResourceDisposition.TRIAL_ACTIVE };
            return new { ok = true, binding = new { schemaVersion = 2, c.SessionId, c.AttemptId, c.OperationId,
                c.ContextGeneration, c.ModeGeneration, c.DocumentGeneration, c.GoalHash, c.TargetRef,
                goal = c.Goal, targetUrl = c.Target.Url.AbsoluteUri, observerVersion = "1.0.0", evaluatorVersion = "1.0.0", allowNavigation = true } };
        }
        if (command.GetString() != "social.observe" || message.EnumerateObject().Count() != 2
            || !message.TryGetProperty("observation", out var element)) return new { ok = false, reason = "UNSUPPORTED_MESSAGE" };
        ObservationRecord? o;
        try { o = SocialJson.ReadObservation(element); if (o is null) return new { ok = false, reason = "OBSERVER_ERROR" }; }
        catch (Exception) { return new { ok = false, reason = "OBSERVER_ERROR" }; }
        lock (_gate)
        {
            if (o is null || _observing is not { } c || !o.Matches(c) || _pending is null
                || state().Phase != ExperimentPhase.PROBING || o.TabId < 0 || !double.IsFinite(o.ObservedAtMonotonic)
                || o.ObservedAtMonotonic <= _lastTimestamp || (_tab.HasValue && _tab != o.TabId))
                return new { ok = false, finished = state().Phase is ExperimentPhase.FINISHED or ExperimentPhase.PAUSED };
            if (o.RendererLoadCount is < 0 or > 6) return new { ok = false, reason = "BUDGET_LIMIT" };
            if (c.Candidate.NetworkMutation) o = o with { RouteProvenance = "UNVERIFIED" };
            _tab ??= o.TabId; _lastTimestamp = o.ObservedAtMonotonic; _last = o;
            var decision = GoalEvaluator.Evaluate(c.Goal, o);
            if (decision.Verdict is AutoVerdict.PASS or AutoVerdict.FAIL or AutoVerdict.NEEDS_INTERACTION) _pending.TrySetResult(o);
            return new { ok = true, finished = decision.Verdict == AutoVerdict.PASS, keepActive = decision.Verdict == AutoVerdict.PASS };
        }
    }
    private async Task ServeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var length = new byte[4]; await pipe.ReadExactlyAsync(length, timeout.Token).ConfigureAwait(false);
                var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(length);
                if (size is < 2 or > 16384) continue;
                var bytes = new byte[size]; await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                object response;
                try { using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 12 }); response = Handle(doc.RootElement); }
                catch (Exception) { response = new { ok = false, reason = "OBSERVER_ERROR" }; }
                var result = JsonSerializer.SerializeToUtf8Bytes(response, SocialJson.Options);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, result.Length);
                await pipe.WriteAsync(length, timeout.Token).ConfigureAwait(false);
                await pipe.WriteAsync(result, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception) { /* Disconnected/malformed client; never log raw input. */ }
        }
    }
    public async Task StopAsync()
    {
        _lifetime?.Cancel();
        lock (_gate) { _pending?.TrySetCanceled(); _pending = null; _observing = null; }
        if (_server is not null) await _server.ConfigureAwait(false);
        _lifetime?.Dispose(); _lifetime = null; _server = null;
    }
}
