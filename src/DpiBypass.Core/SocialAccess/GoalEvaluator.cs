namespace DpiBypass.Core.SocialAccess;

public sealed record GoalDecision(AutoVerdict Verdict, string Reason);
public static class GoalEvaluator
{
    public static GoalDecision Evaluate(SocialGoal goal, ObservationRecord o)
    {
        GoalDecision Unknown(string reason) => new(AutoVerdict.INCONCLUSIVE, reason);
        if (goal.PlatformId is not ("youtube" or "instagram") || goal.IdentityContext is not ("ANONYMOUS" or "SAME_ACCOUNT")
            || o.IdentityEvidence is not ("UNKNOWN" or "USER_BOUND_SESSION" or "PLATFORM_SIGNAL")
            || !double.IsFinite(o.ObservedAtMonotonic) || o.ObservedAtMonotonic < 0 || !double.IsFinite(o.ObservationWindow) || o.ObservationWindow < 0
            || !double.IsFinite(o.MediaProgressSeconds) || o.MediaProgressSeconds < 0 || o.RendererLoadCount is < 0 or > 6)
            return Unknown("INVALID_OBSERVATION");
        if (o.SchemaVersion != 2 || o.ObserverId != "youtube-dom" || o.ObserverVersion != "1.0.0" || o.EvaluatorVersion != "1.0.0" || o.ObserverError
            || o.ObservedCapabilities is null || goal.AllowedSurfaces is null || goal.RequiredCapabilities is null)
            return Unknown("OBSERVER_ERROR");
        if (goal.RequiredCapabilities.Length == 0 || goal.RequiredCapabilities.Any(c => c is not ("VIEW_SELECTED_CONTENT" or "PLAY_MEDIA" or "VIEW_ACCOUNT_FEED"))
            || goal.AllowedSurfaces.Length == 0 || goal.AllowedSurfaces.Any(s => s is not ("OFFICIAL_WEB" or "OFFICIAL_NATIVE_APP" or "CONTROLLED_WEB_VIEW" or "LOCAL_ALTERNATIVE_CLIENT")))
            return Unknown("INVALID_GOAL");
        if (o.TargetMatched != "TRUE" || o.DocumentVisible != "TRUE") return Unknown("TARGET_OR_DOCUMENT_UNVERIFIED");
        if (!goal.AllowedSurfaces.Contains(o.ActualSurface) || goal.IdentityContext != o.IdentityContext)
            return Unknown("GOAL_SCOPE_MISMATCH");
        if (goal.IdentityContext == "SAME_ACCOUNT" && o.IdentityEvidence is not ("USER_BOUND_SESSION" or "PLATFORM_SIGNAL"))
            return Unknown("IDENTITY_UNKNOWN");
        if (o.NeedsInteraction) return new(AutoVerdict.NEEDS_INTERACTION, "USER_GESTURE_REQUIRED");
        if (o.Advertisement || o.Preview || o.Seeking) return Unknown("NON_TARGET_MEDIA");
        if (o.BlockingGate == "TRUE") return new(AutoVerdict.FAIL, "TARGET_BLOCKING_GATE");
        if (o.BlockingGate != "FALSE" || o.ContentPresent != "TRUE") return Unknown("UNRECOGNIZED_LAYOUT");
        if (o.FreshEvidence != "TRUE" || o.CacheUncertain) return Unknown("CACHE_UNCERTAIN");
        if (o.RouteProvenance is not ("EXACT_TARGET_PATH" or "EQUIVALENT_ADAPTER_PATH")) return Unknown("PATH_UNVERIFIED");
        if (o.StableSamples < 2 || !double.IsFinite(o.StableDurationMs) || o.StableDurationMs < 2000)
            return Unknown("STABILITY_UNPROVEN");
        if (goal.RequiredCapabilities.Any(capability => !o.ObservedCapabilities.Contains(capability))) return Unknown("CAPABILITY_UNPROVEN");
        if (goal.ProblemClass == ProblemClass.POSSIBLE_THROTTLING) return Unknown("PERFORMANCE_NOT_MEASURED");
        if ((goal.RequiredCapabilities.Contains("PLAY_MEDIA") || goal.ProblemClass == ProblemClass.MEDIA_PATH_FAILURE) && (o.MediaProgress != "TRUE"
            || !double.IsFinite(o.MediaProgressSeconds) || o.MediaProgressSeconds < 3)) return Unknown("MEDIA_NOT_PROGRESSING");
        return new(AutoVerdict.PASS, "GOAL_OBSERVED");
    }
}
