using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DpiBypass.Core.SocialAccess;

public enum TestMode { MANUAL, AUTO_BETA }
public enum ExperimentPhase { DISABLED, IDLE, BASELINING, SELECTING, PREPARING, APPLYING, PROBING, AUTO_EVALUATING, WAITING_FOR_USER, CANCELLING, ROLLING_BACK, PAUSED, FINISHED, RECOVERY_REQUIRED }
public enum FinishReason { RESOLVED_USER_REPORTED, RESOLVED_AUTO_OBSERVED, PARTIAL_ACCEPTED, ALREADY_ACCESSIBLE, CANDIDATES_EXHAUSTED, NO_APPLICABLE_METHODS, AUTO_INCONCLUSIVE, AUTO_UNAVAILABLE, CANCELLED }
public enum ResourceDisposition { NONE, TRIAL_ACTIVE, KEPT_ACTIVE, ROLLBACK_PENDING, RECOVERY_REQUIRED }
public enum AutoVerdict { NOT_RUN, PASS, FAIL, INCONCLUSIVE, UNSUPPORTED, NEEDS_INTERACTION, CANCELLED }
public enum UserOutcome { STILL_PRESENT, RESOLVED, PARTIAL, NOT_TESTED }
public enum ProblemClass { NETWORK_UNREACHABLE, DNS_FAILURE_OR_INTERFERENCE, TLS_OR_SNI_INTERFERENCE_SUSPECTED, QUIC_OR_UDP_PATH_FAILURE, MEDIA_PATH_FAILURE, POSSIBLE_THROTTLING, LOGIN_REQUIRED, AGE_OR_ID_VERIFICATION_REQUIRED, BOT_CHALLENGE, ACCOUNT_RESTRICTION, CONTENT_NOT_AVAILABLE, RATE_LIMITED, CAPTIVE_PORTAL, SERVICE_OUTAGE_SUSPECTED, MIXED, UNKNOWN }
public static class SocialJson
{
    public static ObservationRecord? ReadObservation(JsonElement value)
    {
        try { return value.Deserialize<ObservationRecord>(Options); }
        catch (JsonException) { return null; }
    }
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }, MaxDepth = 12,
    };
}

[JsonConverter(typeof(SocialPreferencesConverter))]
public sealed record SocialPreferences
{
    public int SchemaVersion { get; init; } = 2;
    public bool Enabled { get; set; }
    public string TestMode { get; set; } = "MANUAL";
    public bool AllowNetworkChanges { get; set; }
    public bool AllowMeteredRenderer { get; set; }
}

// A malformed Alpha field must not make ConfigStore discard normal protection settings.
public sealed class SocialPreferencesConverter : JsonConverter<SocialPreferences>
{
    public override SocialPreferences Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return new();
        var r = doc.RootElement;
        bool Flag(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var mode = r.TryGetProperty("TestMode", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == "AUTO_BETA" ? "AUTO_BETA" : "MANUAL";
        if (r.TryGetProperty("SchemaVersion", out var version) && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var n) || n != 2)) return new();
        return new() { Enabled = Flag("Enabled"), TestMode = mode, AllowNetworkChanges = Flag("AllowNetworkChanges"), AllowMeteredRenderer = false };
    }
    public override void Write(Utf8JsonWriter w, SocialPreferences v, JsonSerializerOptions options)
    {
        w.WriteStartObject(); w.WriteNumber("SchemaVersion", 2); w.WriteBoolean("Enabled", v.Enabled);
        w.WriteString("TestMode", v.TestMode == "AUTO_BETA" ? "AUTO_BETA" : "MANUAL");
        w.WriteBoolean("AllowNetworkChanges", v.AllowNetworkChanges); w.WriteBoolean("AllowMeteredRenderer", false); w.WriteEndObject();
    }
}

public sealed record SocialGoal
{
    public string OriginalSurface { get; init; } = "OFFICIAL_NATIVE_APP";
    public string PlatformId { get; init; } = "youtube";
    public ProblemClass ProblemClass { get; init; } = ProblemClass.UNKNOWN;
    public string IdentityContext { get; init; } = "SAME_ACCOUNT";
    public string[] AllowedSurfaces { get; init; } = ["OFFICIAL_WEB"];
    public string[] RequiredCapabilities { get; init; } = ["VIEW_SELECTED_CONTENT"];
    public bool PublicFallbackAllowed { get; init; }
}

public sealed record SocialTarget(string PlatformId, Uri Url, string ContentId, string TargetRef)
{
    public static bool TryCreate(string platform, string raw, out SocialTarget? target)
    {
        target = null;
        if (raw.Length > 2048 || raw.Any(char.IsControl) || !Uri.TryCreate(raw, UriKind.Absolute, out var u)
            || u.Scheme != "https" || !u.IsDefaultPort || u.UserInfo.Length != 0 || u.Fragment.Length != 0) return false;
        var h = u.IdnHost.ToLowerInvariant();
        string? id = null;
        string? clean = null;
        if (platform == "youtube" && h is "www.youtube.com" or "m.youtube.com" or "youtube.com")
        {
            var q = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2)).Where(x => x.Length == 2 && x[0] == "v").ToArray();
            if (u.AbsolutePath == "/watch" && q.Length == 1 && Regex.IsMatch(q[0][1], "^[A-Za-z0-9_-]{11}$"))
            { id = q[0][1]; clean = $"https://{h}/watch?v={id}"; }
            else if (Regex.IsMatch(u.AbsolutePath, "^/shorts/[A-Za-z0-9_-]{11}/?$"))
            { id = u.AbsolutePath.Split('/')[2]; clean = $"https://{h}/shorts/{id}"; }
        }
        if (platform == "instagram" && h is "instagram.com" or "www.instagram.com")
        {
            var match = Regex.Match(u.AbsolutePath, "^/(p|reel)/([A-Za-z0-9_-]{1,64})/?$");
            if (match.Success) { id = match.Groups[2].Value; clean = $"https://www.instagram.com/{match.Groups[1].Value}/{id}/"; }
        }
        if (id is null || clean is null) return false;
        target = new(platform, new Uri(clean), id, Guid.NewGuid().ToString("N")); return true;
    }
    public static bool IsAllowedProbe(Uri uri) => TryCreate(uri.Host.Contains("instagram", StringComparison.Ordinal) ? "instagram" : "youtube", uri.AbsoluteUri, out _);
}

public sealed record CandidateDefinition
{
    public int SchemaVersion { get; init; } = 2;
    public string Id { get; init; } = "";
    public string CandidateVersion { get; init; } = "1.0.0";
    public string HandlerId { get; init; } = "";
    public string Family { get; init; } = "A1";
    public string EffectKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Surface { get; init; } = "OFFICIAL_WEB";
    public string IdentityContext { get; init; } = "UNKNOWN";
    public string ScopeNote { get; init; } = "";
    public bool NetworkMutation { get; init; }
    public string RemoteRelay { get; init; } = "NO";
    public string RegionChange { get; init; } = "NO";
    public string IdentityCollection { get; init; } = "NO";
    public string CredentialExport { get; init; } = "NO";
    public string EvidenceLabel { get; init; } = "MAINTAINER_DOCUMENTED";
    public string Source { get; init; } = "";
    public string ReviewedAt { get; init; } = "2026-10-17";
    public string RollbackKind { get; init; } = "OWNED_RESOURCE";
}
public sealed record Eligibility(string Status, string ReasonCode)
{
    public static readonly Eligibility Eligible = new("ELIGIBLE", "SUPPORTED");
    public bool CanApply => Status == "ELIGIBLE";
}
public sealed record ExperimentContext(string SessionId, string AttemptId, string OperationId, long ContextGeneration,
    long ModeGeneration, long DocumentGeneration, string GoalHash, SocialGoal Goal, SocialTarget Target, CandidateDefinition Candidate)
{
    public string TargetRef => Target.TargetRef;
}
public sealed record FeedbackEvent(string SessionId, string AttemptId, string OperationId, long ContextGeneration,
    long ModeGeneration, string EventId, long ExpectedStateVersion, UserOutcome Outcome);
public sealed record UndoRecord
{
    public int SchemaVersion { get; init; } = 2;
    public string HandlerId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string? Before { get; init; }
    public string? Installed { get; init; }
    public string Stage { get; init; } = "PREPARED";
}
public sealed record ExperimentState(ExperimentPhase Phase = ExperimentPhase.IDLE, FinishReason? FinishReason = null,
    string? PauseReason = null, ResourceDisposition ResourceDisposition = ResourceDisposition.NONE, long Version = 0,
    string Message = "Kapalı. Deneyler yalnızca Başlat ile çalışır.", string? CandidateId = null);
public sealed record AttemptResult(string CandidateId, UserOutcome UserOutcome, AutoVerdict AutoVerdict,
    string TechnicalOutcome, string ReasonCode, string DecisionBy, string CausalAttribution = "NOT_ESTABLISHED")
{
    public TestMode TestMode { get; init; }
    public long ModeGeneration { get; init; }
    public long ContextGeneration { get; init; }
    public string IdentityContext { get; init; } = "UNKNOWN";
    public string ActualSurface { get; init; } = "UNKNOWN";
    public string[] ObservedCapabilities { get; init; } = [];
    public string GoalMatch { get; init; } = "UNKNOWN";
    public string EvidenceOrigin { get; init; } = "USER_REPORTED";
    public string EvaluatorVersion { get; init; } = "1.0.0";
    public string CandidateVersion { get; init; } = "1.0.0";
    public string PerformanceOutcome { get; init; } = "NOT_MEASURED";
    public string BaselineObserved { get; init; } = "UNKNOWN";
    public DateTimeOffset RecordedAtUtc { get; init; }
}
public interface IExperimentHandler
{
    CandidateDefinition Definition { get; }
    Eligibility Inspect(ExperimentContext context);
    Task<UndoRecord> PrepareAsync(ExperimentContext context, CancellationToken token);
    Task ApplyAsync(ExperimentContext context, UndoRecord undo, CancellationToken token);
    // True means cleanup AND verification completed; false preserves the recovery journal.
    Task<bool> RollbackAsync(UndoRecord undo, CancellationToken token);
}
public interface IContentObservationAdapter
{
    bool CanObserve(SocialGoal goal);
    Task<ObservationRecord> ObserveAsync(ExperimentContext context, CancellationToken token);
}
public sealed class NullObservationAdapter : IContentObservationAdapter
{
    public bool CanObserve(SocialGoal goal) => false;
    public Task<ObservationRecord> ObserveAsync(ExperimentContext context, CancellationToken token)
        => Task.FromResult(ObservationRecord.For(context) with { ObserverError = true });
}

public sealed record ObservationRecord
{
    public int SchemaVersion { get; init; } = 2;
    public string SessionId { get; init; } = "";
    public string AttemptId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public long ContextGeneration { get; init; }
    public long ModeGeneration { get; init; }
    public long DocumentGeneration { get; init; }
    public string GoalHash { get; init; } = "";
    public string TargetRef { get; init; } = "";
    public int TabId { get; init; }
    public string ObserverId { get; init; } = "youtube-dom";
    public string ObserverVersion { get; init; } = "1.0.0";
    public string EvaluatorVersion { get; init; } = "1.0.0";
    public double ObservedAtMonotonic { get; init; }
    public double ObservationWindow { get; init; }
    public string SignalSource { get; init; } = "WEB_OBSERVER";
    public string ActualSurface { get; init; } = "UNKNOWN";
    public string IdentityContext { get; init; } = "UNKNOWN";
    public string IdentityEvidence { get; init; } = "UNKNOWN";
    public string RouteProvenance { get; init; } = "UNVERIFIED";
    public string TargetMatched { get; init; } = "UNKNOWN";
    public string ContentPresent { get; init; } = "UNKNOWN";
    public string BlockingGate { get; init; } = "UNKNOWN";
    public string MediaProgress { get; init; } = "UNKNOWN";
    public string FreshEvidence { get; init; } = "UNKNOWN";
    public string DocumentVisible { get; init; } = "UNKNOWN";
    public AutoVerdict AutoVerdict { get; init; } = AutoVerdict.INCONCLUSIVE;
    public string[] ReasonCodes { get; init; } = [];
    public string[] ObservedCapabilities { get; init; } = [];
    public int StableSamples { get; init; }
    public double StableDurationMs { get; init; }
    public double MediaProgressSeconds { get; init; }
    public bool Seeking { get; init; }
    public bool Advertisement { get; init; }
    public bool Preview { get; init; }
    public bool ObserverError { get; init; }
    public bool NeedsInteraction { get; init; }
    public string BudgetAccuracy { get; init; } = "UNKNOWN";
    public int RendererLoadCount { get; init; }
    public bool CacheUncertain { get; init; }
    public double RetryAfterSeconds { get; init; }
    public static ObservationRecord For(ExperimentContext c) => new() { SessionId = c.SessionId, AttemptId = c.AttemptId,
        OperationId = c.OperationId, ContextGeneration = c.ContextGeneration, ModeGeneration = c.ModeGeneration,
        DocumentGeneration = c.DocumentGeneration, GoalHash = c.GoalHash, TargetRef = c.TargetRef };
    public bool Matches(ExperimentContext c) => SchemaVersion == 2 && SessionId == c.SessionId && AttemptId == c.AttemptId
        && OperationId == c.OperationId && ContextGeneration == c.ContextGeneration && ModeGeneration == c.ModeGeneration
        && DocumentGeneration == c.DocumentGeneration && GoalHash == c.GoalHash && TargetRef == c.TargetRef;
}
