using System.Text.Json;
using System.Text.Json.Nodes;
using DpiBypass.Core.SocialAccess;
using Xunit;
using DpiBypass.Core.Config;
using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Dns;
using System.IO.Pipes;
using System.Buffers.Binary;

namespace DpiBypass.Tests;

public sealed class SocialAccessTests
{
    [Fact]
    public void ClearingDiagnosticsPreservesCooldownAndObserverCorrectionAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "social-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "history.json");
            var history = new ExperimentHistory(path);
            var correction = new string('a', 64);
            var until = DateTimeOffset.UtcNow.AddMinutes(5);
            history.SuppressedTargets.Add(correction);
            history.Save([], until);
            history.Clear();
            var restarted = new ExperimentHistory(path); restarted.Load();
            Assert.Equal(until, restarted.CooldownUntilUtc);
            Assert.Contains(correction, restarted.SuppressedTargets);
            Assert.Empty(JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(path), SocialJson.Options)!.Entries);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void SharedEvidenceVectorsNeverPromoteUnknownToPass()
    {
        var root = JsonNode.Parse(File.ReadAllText(RepoFiles.Find("tests", "fixtures", "social-access-v2.json")))!;
        foreach (var item in root["cases"]!.AsArray())
        {
            var goal = root["goal"]!.DeepClone();
            var observation = root["observation"]!.DeepClone();
            Merge(goal, item!["goalPatch"]);
            Merge(observation, item["patch"]);
            var parsed = SocialJson.ReadObservation(JsonSerializer.Deserialize<JsonElement>(observation.ToJsonString()));
            var actual = GoalEvaluator.Evaluate(goal.Deserialize<SocialGoal>(SocialJson.Options)!, parsed ?? new ObservationRecord { ObserverError = true });
            Assert.True(actual.Verdict.ToString() == item["expectedVerdict"]!.GetValue<string>(),
                $"{item["id"]}: expected {item["expectedVerdict"]}, got {actual.Verdict}/{actual.Reason}");
        }
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk", true)]
    [InlineData("https://www.youtube.com.evil.test/watch?v=abcdefghijk", false)]
    [InlineData("http://www.youtube.com/watch?v=abcdefghijk", false)]
    [InlineData("https://www.youtube.com@127.0.0.1/watch?v=abcdefghijk", false)]
    [InlineData("https://www.youtube.com:8443/watch?v=abcdefghijk", false)]
    [InlineData("https://www.youtube.com/redirect?q=https://localhost", false)]
    [InlineData("file:///etc/passwd", false)]
    public void TargetsHaveStrictHostAndShapeValidation(string url, bool valid)
        => Assert.Equal(valid, SocialTarget.TryCreate("youtube", url, out _));

    [Fact]
    public void BrowserLauncherRejectsUnvalidatedTargetsBeforeAnyNativeOperation()
        => Assert.Throws<ArgumentException>(() => DesktopUrlLauncher.Open(new("youtube", new Uri("http://127.0.0.1/"), "invalid", "fixture")));

    [Fact]
    public async Task SharedEventSequencesHaveSameStatesAndResourceCounts()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFiles.Find("tests", "fixtures", "social-access-events-v2.json")));
        foreach (var scenario in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            using var temp = new TempDirectory();
            var handlers = scenario.GetProperty("candidates").EnumerateArray().Select(s => new TrialHandler(s.GetString()!)).ToArray();
            var engine = new ExperimentCoordinator(handlers, new SequenceObserver(), new(temp.File("journal.json")));
            await engine.StartAsync(Goal(), Target(), Enum.Parse<TestMode>(scenario.GetProperty("mode").GetString()!), scenario.GetProperty("baselinePresent").GetBoolean());
            FeedbackEvent? previous = null;
            foreach (var item in scenario.GetProperty("events").EnumerateArray())
            {
                var e = item.GetString();
                if (e == "STOP") await engine.StopAsync();
                else if (e == "REPLAY_LAST") await engine.FeedbackAsync(previous!);
                else { previous = engine.CreateFeedback(Enum.Parse<UserOutcome>(e!)); await engine.FeedbackAsync(previous); }
            }
            Assert.Equal(scenario.GetProperty("expectedPhase").GetString(), engine.State.Phase.ToString());
            Assert.Equal(scenario.GetProperty("expectedFinishReason").GetString(), engine.State.FinishReason?.ToString());
            Assert.Equal(scenario.GetProperty("expectedActiveResourceCount").GetInt32(), handlers.Sum(h => h.Applied - h.RolledBack));
            Assert.Equal(scenario.GetProperty("expectedNextCandidate").GetString(), engine.State.Phase == ExperimentPhase.WAITING_FOR_USER ? engine.Context?.Candidate.Id : null);
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task ManualResolutionStopsAndDuplicateFeedbackCannotAdvance()
    {
        using var temp = new TempDirectory();
        var handler = new TrialHandler("first");
        var second = new TrialHandler("second");
        var engine = new ExperimentCoordinator([handler, second], new NullObservationAdapter(),
            new ExperimentJournal(Path.Combine(temp.Path, "journal.json")));
        await engine.StartAsync(Goal(), Target(), TestMode.MANUAL, baselinePresent: true);
        var feedback = engine.CreateFeedback(UserOutcome.RESOLVED);
        await engine.FeedbackAsync(feedback);
        await engine.FeedbackAsync(feedback);
        Assert.Equal(FinishReason.RESOLVED_USER_REPORTED, engine.State.FinishReason);
        Assert.Equal(ResourceDisposition.KEPT_ACTIVE, engine.State.ResourceDisposition);
        Assert.Equal(1, handler.Applied);
        Assert.Equal(0, second.Applied);
        await engine.StopAsync();
        Assert.Equal(1, handler.RolledBack);
    }

    [Fact]
    public async Task RollbackMustFinishBeforeNextCandidateAndFailureBlocksIt()
    {
        using var temp = new TempDirectory();
        var handler = new TrialHandler("first") { RollbackWorks = false };
        var second = new TrialHandler("second");
        var engine = new ExperimentCoordinator([handler, second], new NullObservationAdapter(),
            new ExperimentJournal(Path.Combine(temp.Path, "journal.json")));
        await engine.StartAsync(Goal(), Target(), TestMode.MANUAL, true);
        await engine.FeedbackAsync(engine.CreateFeedback(UserOutcome.STILL_PRESENT));
        Assert.Equal(ExperimentPhase.RECOVERY_REQUIRED, engine.State.Phase);
        Assert.Equal(0, second.Applied);
        Assert.True(File.Exists(Path.Combine(temp.Path, "journal.json")));
    }

    [Fact]
    public async Task AutomaticFailRollsBackAndPassKeepsWithoutUserFeedback()
    {
        using var temp = new TempDirectory();
        var first = new TrialHandler("first");
        var second = new TrialHandler("second");
        var observer = new SequenceObserver();
        var engine = new ExperimentCoordinator([first, second], observer,
            new ExperimentJournal(Path.Combine(temp.Path, "journal.json")));
        await engine.StartAsync(Goal(), Target(), TestMode.AUTO_BETA, true);
        Assert.Equal(1, first.RolledBack);
        Assert.Equal(1, second.Applied);
        Assert.Equal(FinishReason.RESOLVED_AUTO_OBSERVED, engine.State.FinishReason);
        Assert.Equal(UserOutcome.NOT_TESTED, engine.Results.Last().UserOutcome);
        await engine.StopAsync();
    }

    [Fact]
    public async Task UnknownAutomaticCandidatesRemainAvailableForManualReview()
    {
        using var temp = new TempDirectory();
        var first = new TrialHandler("first");
        var engine = new ExperimentCoordinator([first], new NullObservationAdapter(),
            new ExperimentJournal(Path.Combine(temp.Path, "journal.json")));
        await engine.StartAsync(Goal(), Target(), TestMode.AUTO_BETA, true);
        Assert.Equal(FinishReason.AUTO_UNAVAILABLE, engine.State.FinishReason);
        Assert.Equal(0, first.Applied); // unsupported observer is inspected before mutation
        await engine.ContinueManuallyAsync();
        Assert.Equal(1, first.Applied);
        Assert.Equal(ExperimentPhase.WAITING_FOR_USER, engine.State.Phase);
        await engine.StopAsync();
    }

    [Fact]
    public async Task BaselineAlreadyOpenAppliesNothing()
    {
        using var temp = new TempDirectory();
        var handler = new TrialHandler("first");
        var engine = new ExperimentCoordinator([handler], new NullObservationAdapter(),
            new ExperimentJournal(Path.Combine(temp.Path, "journal.json")));
        await engine.StartAsync(Goal(), Target(), TestMode.MANUAL, false);
        Assert.Equal(FinishReason.ALREADY_ACCESSIBLE, engine.State.FinishReason);
        Assert.Equal(0, handler.Applied);
    }

    [Theory]
    [InlineData("{\"TestMode\":{},\"SchemaVersion\":\"new\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void BrokenAlphaSettingsPreserveNormalProtection(string alpha)
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("settings.json"), "{\"DnsMode\":\"PublicResolvers\",\"ManualStrategyId\":\"split2\",\"SocialAccess\":" + alpha + "}");
        var loaded = new ConfigStore(temp.File("settings.json"), temp.File("networks.json")).Load();
        Assert.Equal(DnsMode.PublicResolvers, loaded.DnsMode);
        Assert.Equal("split2", loaded.ManualStrategyId);
        Assert.False(loaded.SocialAccess.Enabled);
        Assert.Equal("MANUAL", loaded.SocialAccess.TestMode);
    }

    [Fact]
    public async Task ExperimentLeaseBlocksNormalTunerAndRestoresOnlyExpectedValue()
    {
        var current = StrategyLibrary.Default;
        using var owner = new StrategyCoordinator(() => current, s => current = s);
        owner.BeginSession("network");
        var lease = owner.AcquireExperiment()!;
        Assert.True(lease.TryWrite(StrategyLibrary.Split2));
        Assert.False(owner.ApplyImmediate("external selection", StrategyLibrary.Disorder2));
        var ran = false;
        await owner.RunAsync(StrategyWorkKind.Automatic, "periodic", (_, _) => { ran = true; return Task.FromResult(true); }, false);
        Assert.False(ran);
        current = StrategyLibrary.DisorderSni; // an external writer changed the field
        Assert.False(owner.ReleaseExperiment(lease, StrategyLibrary.Default, StrategyLibrary.Split2));
        Assert.Equal(StrategyLibrary.DisorderSni, current);
        current = StrategyLibrary.Split2;
        Assert.True(owner.ReleaseExperiment(lease, StrategyLibrary.Default, StrategyLibrary.Split2));
        Assert.Equal(StrategyLibrary.Default, current);
        Assert.False(lease.TryWrite(StrategyLibrary.Split2));
    }

    [Fact]
    public async Task LateAutoPassCannotOverrideManualMode()
    {
        using var temp = new TempDirectory();
        var observer = new BlockingObserver();
        var handler = new TrialHandler("first");
        var engine = new ExperimentCoordinator([handler], observer, new(temp.File("journal.json")));
        var start = engine.StartAsync(Goal(), Target(), TestMode.AUTO_BETA, true);
        await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.SwitchModeAsync(TestMode.MANUAL).WaitAsync(TimeSpan.FromSeconds(5));
        observer.Finish.TrySetResult(ObservationRecord.For(observer.Context!) with { TargetMatched = "TRUE", ContentPresent = "TRUE",
            BlockingGate = "FALSE", FreshEvidence = "TRUE", DocumentVisible = "TRUE", StableSamples = 2, StableDurationMs = 2000,
            ActualSurface = "OFFICIAL_WEB", IdentityContext = "ANONYMOUS", RouteProvenance = "EXACT_TARGET_PATH", ObservedCapabilities = ["VIEW_SELECTED_CONTENT"] });
        await start;
        Assert.Equal(ExperimentPhase.WAITING_FOR_USER, engine.State.Phase);
        Assert.Null(engine.State.FinishReason);
        await engine.StopAsync();
    }

    [Fact]
    public async Task HumanResolutionInterruptsAutomaticObserverAndStopsSearch()
    {
        using var temp = new TempDirectory();
        var observer = new BlockingObserver();
        var first = new TrialHandler("first"); var next = new TrialHandler("next");
        var engine = new ExperimentCoordinator([first, next], observer, new(temp.File("journal.json")));
        var start = engine.StartAsync(Goal(), Target(), TestMode.AUTO_BETA, true);
        await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.FeedbackAsync(engine.CreateFeedback(UserOutcome.RESOLVED)).WaitAsync(TimeSpan.FromSeconds(5));
        await start;
        Assert.Equal(FinishReason.RESOLVED_USER_REPORTED, engine.State.FinishReason);
        Assert.Equal(0, next.Applied);
        await engine.StopAsync();
    }

    [Fact]
    public async Task ManualToAutoFailureContinuesToNextCandidate()
    {
        using var temp = new TempDirectory();
        var first = new TrialHandler("first"); var next = new TrialHandler("next");
        var engine = new ExperimentCoordinator([first, next], new SequenceObserver(), new(temp.File("journal.json")));
        await engine.StartAsync(Goal(), Target(), TestMode.MANUAL, true);
        await engine.SwitchModeAsync(TestMode.AUTO_BETA);
        Assert.Equal(1, first.RolledBack);
        Assert.Equal(1, next.Applied);
        Assert.Equal(FinishReason.RESOLVED_AUTO_OBSERVED, engine.State.FinishReason);
        await engine.StopAsync();
    }

    [Fact]
    public async Task ChangingModeAfterHumanSuccessCannotRestartSearchOrUndoAcceptedSetting()
    {
        using var temp = new TempDirectory(); var first = new TrialHandler("first"); var next = new TrialHandler("next");
        var engine = new ExperimentCoordinator([first, next], new NullObservationAdapter(), new(temp.File("journal.json")));
        await engine.StartAsync(Goal(), Target(), TestMode.MANUAL, true);
        await engine.FeedbackAsync(engine.CreateFeedback(UserOutcome.RESOLVED));
        await engine.SwitchModeAsync(TestMode.AUTO_BETA);
        Assert.Equal(FinishReason.RESOLVED_USER_REPORTED, engine.State.FinishReason);
        Assert.Equal(0, first.RolledBack); Assert.Equal(0, next.Applied);
        await engine.StopAsync();
    }

    [Fact]
    public async Task AllowedGestureResumesSameAttemptWithoutApplyingAgain()
    {
        using var temp = new TempDirectory(); var handler = new TrialHandler("first");
        var engine = new ExperimentCoordinator([handler], new GestureObserver(), new(temp.File("journal.json")));
        await engine.StartAsync(Goal(), Target(), TestMode.AUTO_BETA, true);
        Assert.Equal("USER_ACTION_REQUIRED", engine.State.PauseReason);
        var attempt = engine.Context!.AttemptId;
        Assert.Equal(ResourceDisposition.TRIAL_ACTIVE, engine.State.ResourceDisposition);
        await engine.ResumeObservationAsync();
        Assert.Equal(attempt, engine.Context!.AttemptId); Assert.Equal(1, handler.Applied);
        Assert.Equal(FinishReason.RESOLVED_AUTO_OBSERVED, engine.State.FinishReason);
        await engine.StopAsync();
    }
    private sealed class GestureObserver : IContentObservationAdapter
    {
        private int _samples;
        public bool CanObserve(SocialGoal goal) => true;
        public Task<ObservationRecord> ObserveAsync(ExperimentContext c, CancellationToken t) => Task.FromResult(ObservationRecord.For(c) with {
            TargetMatched = "TRUE", ContentPresent = "TRUE", BlockingGate = "FALSE", FreshEvidence = "TRUE", DocumentVisible = "TRUE",
            ActualSurface = "OFFICIAL_WEB", IdentityContext = "ANONYMOUS", RouteProvenance = "EXACT_TARGET_PATH", StableSamples = 2, StableDurationMs = 2000,
            ObservedCapabilities = ["VIEW_SELECTED_CONTENT"], NeedsInteraction = ++_samples == 1 });
    }

    [Fact]
    public void DiagnosticBudgetRejectsAllocationBeforeOverflow()
    {
        var budget = new ExperimentBudget();
        budget.AddBytes(5 * 1024 * 1024);
        Assert.Throws<BudgetExceededException>(() => budget.AddBytes(1));
        Assert.Equal(5 * 1024 * 1024, budget.Bytes);
        for (var i = 0; i < 24; i++) budget.Request();
        Assert.Throws<BudgetExceededException>(() => budget.Request());
    }

    [Theory]
    [InlineData("Requests", "-1")]
    [InlineData("Attempts", "-1")]
    [InlineData("Bytes", "-1")]
    [InlineData("ActiveSeconds", "-1")]
    [InlineData("PageLoads", "-1")]
    [InlineData("Requests", "25")]
    [InlineData("Stage", "FUTURE_UNKNOWN_STAGE")]
    public async Task InvalidRecoveryCountersOrStageCannotAuthorizeCleanupOrAnotherTrial(string field, string value)
    {
        using var temp = new TempDirectory();
        var journal = new ExperimentJournal(temp.File("journal.json"));
        var handler = new TrialHandler("first");
        var entry = new JournalEntry(new() { HandlerId = "first", OperationId = "fixture-operation", Stage = "ACTIVE" }, 1, 1, 1, 1, 1);
        var data = JsonNode.Parse(JsonSerializer.Serialize(entry, SocialJson.Options))!;
        if (field == "Stage") data["undo"]!["stage"] = value;
        else data[char.ToLowerInvariant(field[0]) + field[1..]] = JsonValue.Create(int.Parse(value));
        File.WriteAllText(temp.File("journal.json"), data.ToJsonString());
        var coordinator = new ExperimentCoordinator([handler], new NullObservationAdapter(), journal);
        await coordinator.RecoverAsync();
        Assert.Equal(ExperimentPhase.RECOVERY_REQUIRED, coordinator.State.Phase);
        Assert.Equal(0, handler.RolledBack);
        Assert.Equal(0, handler.Applied);
        Assert.True(journal.Exists);
    }

    [Fact]
    public async Task ManualInactivityUsesOneDeadlineAndRollsBackWithoutFailureCredit()
    {
        using var temp = new TempDirectory(); var clock = new ManualClock();
        var handler = new TrialHandler("first");
        var engine = new ExperimentCoordinator([handler], new NullObservationAdapter(), new(temp.File("journal.json")), clock);
        await engine.StartAsync(Goal(), Target(), TestMode.MANUAL, true);
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal("USER_INACTIVE", engine.State.PauseReason);
        Assert.Equal(1, handler.RolledBack);
        Assert.Empty(engine.Results);
    }

    [Fact]
    public async Task RealBrowserPipeRejectsStaleAndUnknownFieldsBeforeAcceptingTargetEvidence()
    {
        if (!OperatingSystem.IsWindows()) return;
        var context = new ExperimentContext("s", "a", "o", 1, 1, 1, "goal", Goal(), Target(), new() { Id = "official-web" });
        var bridge = new BrowserObservationBridge(() => context, () => new(ExperimentPhase.PROBING));
        bridge.Start();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var wait = bridge.ObserveAsync(context, timeout.Token);
            var binding = await PipeRequest(new { command = "social.bind" });
            Assert.True(binding["ok"]!.GetValue<bool>());
            var observation = ObservationRecord.For(context) with { TargetMatched = "TRUE", ContentPresent = "TRUE", BlockingGate = "FALSE",
                FreshEvidence = "TRUE", DocumentVisible = "TRUE", ActualSurface = "OFFICIAL_WEB", IdentityContext = "ANONYMOUS",
                RouteProvenance = "EXACT_TARGET_PATH", StableSamples = 2, StableDurationMs = 2000, TabId = 7, ObservedAtMonotonic = 1000,
                ObservedCapabilities = ["VIEW_SELECTED_CONTENT"] };
            var stale = await PipeRequest(new { command = "social.observe", observation = observation with { DocumentGeneration = 0 } });
            Assert.False(stale["ok"]!.GetValue<bool>()); Assert.False(wait.IsCompleted);
            var unknown = JsonSerializer.SerializeToNode(new { command = "social.observe", observation }, SocialJson.Options)!;
            unknown["observation"]!["unexpectedField"] = "synthetic-sensitive-fixture";
            var malformed = await PipeRequest(unknown);
            Assert.Equal("OBSERVER_ERROR", malformed["reason"]!.GetValue<string>()); Assert.False(wait.IsCompleted);
            var accepted = await PipeRequest(new { command = "social.observe", observation });
            Assert.True(accepted["ok"]!.GetValue<bool>());
            Assert.Equal(AutoVerdict.PASS, GoalEvaluator.Evaluate(context.Goal, await wait).Verdict);
        }
        finally { await bridge.StopAsync(); }
    }
    private static async Task<JsonNode> PipeRequest(object message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", BrowserObservationBridge.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, SocialJson.Options); var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await pipe.WriteAsync(prefix, timeout.Token); await pipe.WriteAsync(bytes, timeout.Token); await pipe.FlushAsync(timeout.Token);
        await pipe.ReadExactlyAsync(prefix, timeout.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix); Assert.InRange(length, 2, 16384);
        var response = new byte[length]; await pipe.ReadExactlyAsync(response, timeout.Token);
        return JsonNode.Parse(response)!;
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private readonly List<ClockTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ClockTimer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer; }
        public void Advance(TimeSpan time)
        { _ticks += time.Ticks; foreach (var t in _timers.Where(t => t.Due <= _ticks).ToArray()) { t.Dispose(); t.Callback(t.State); } }
        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback; public object? State { get; } = state;
            public long Due { get; private set; } = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks; return true; }
            public void Dispose() => Due = long.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class BlockingObserver : IContentObservationAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ObservationRecord> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExperimentContext? Context;
        public bool CanObserve(SocialGoal goal) => true;
        public Task<ObservationRecord> ObserveAsync(ExperimentContext c, CancellationToken token)
        { Context = c; Started.TrySetResult(); return Finish.Task; } // intentionally ignores cancellation
    }

    private static void Merge(JsonNode destination, JsonNode? patch)
    {
        if (patch is null) return;
        foreach (var pair in patch.AsObject()) destination[pair.Key] = pair.Value?.DeepClone();
    }

    private static SocialGoal Goal() => new() { PlatformId = "youtube", IdentityContext = "ANONYMOUS",
        AllowedSurfaces = ["OFFICIAL_WEB"], PublicFallbackAllowed = true };
    private static SocialTarget Target() { SocialTarget.TryCreate("youtube", "https://www.youtube.com/watch?v=abcdefghijk", out var value); return value!; }

    private sealed class TrialHandler(string id) : IExperimentHandler
    {
        public CandidateDefinition Definition { get; } = new() { Id = id, EffectKey = id, DisplayName = id };
        public int Applied { get; private set; }
        public int RolledBack { get; private set; }
        public bool RollbackWorks { get; init; } = true;
        public Eligibility Inspect(ExperimentContext context) => Eligibility.Eligible;
        public Task<UndoRecord> PrepareAsync(ExperimentContext context, CancellationToken token)
            => Task.FromResult(new UndoRecord { HandlerId = id, OperationId = context.OperationId });
        public Task ApplyAsync(ExperimentContext context, UndoRecord undo, CancellationToken token) { Applied++; return Task.CompletedTask; }
        public Task<bool> RollbackAsync(UndoRecord undo, CancellationToken token) { RolledBack++; return Task.FromResult(RollbackWorks); }
    }
    private sealed class SequenceObserver : IContentObservationAdapter
    {
        private int _n;
        public bool CanObserve(SocialGoal goal) => true;
        public Task<ObservationRecord> ObserveAsync(ExperimentContext c, CancellationToken t) => Task.FromResult(
            ObservationRecord.For(c) with { TargetMatched = "TRUE", ContentPresent = "TRUE", BlockingGate = ++_n == 1 ? "TRUE" : "FALSE",
                FreshEvidence = "TRUE", DocumentVisible = "TRUE", ActualSurface = "OFFICIAL_WEB", IdentityContext = "ANONYMOUS",
                RouteProvenance = "EXACT_TARGET_PATH", StableSamples = 2, StableDurationMs = 2000,
                ObservedCapabilities = ["VIEW_SELECTED_CONTENT"] });
    }
}
