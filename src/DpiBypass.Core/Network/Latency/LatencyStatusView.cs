using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DpiBypass.Core.Network;

/// <summary>
/// What the user is told the mode is doing, which is never simply "on" or "off".
/// </summary>
/// <remarks>
/// The distinction the whole feature turns on is between <see cref="Off"/> and
/// <see cref="NoLocalGain"/>. A user who switched the mode on and got no local win has a
/// mode that is on, watching the network and ready to act if the network changes.
/// Rendering that as "off" would be a lie about their own settings and would invite them
/// to keep switching it on to no effect.
/// </remarks>
public enum LatencyModeState
{
    Off = 0,
    Measuring = 1,
    QuickTesting = 2,
    DeepTesting = 3,
    GainApplied = 4,
    NoLocalGain = 5,
    MonitoringOnly = 6,
    UnsupportedAdapter = 7,
    AwaitingRestore = 8,
    Failed = 9,
    Offline = 10,
    NeedsDeepTest = 11,
    TrafficGuardActive = 12,
    Cancelled = 13,
}

/// <summary>One change that was not kept, with the reason and the kind of reason.</summary>
/// <remarks>
/// The cause is part of the record because the card has to separate "measured, no gain"
/// from "never tried" without reading the sentence. Deriving state by searching Turkish
/// text for words like "kötüleşti" breaks the moment the wording is improved, and it was
/// how the headline used to decide whether a change had been rolled back.
/// </remarks>
public sealed record LatencyRejection(string Change, string Reason, LatencyOutcomeCause Cause)
{
    /// <summary>Whether a completed experiment is behind this.</summary>
    public bool WasMeasured => Cause.IsPerformanceEvidence();

    /// <summary>A short label for the cause, for the details list.</summary>
    public string CauseLabel => Cause.Describe();
}

/// <summary>
/// What the user is actually looking at, as one value the card can switch on.
/// </summary>
/// <remarks>
/// Every state that used to share a colour and a sentence is separated here, because the
/// right next step differs for each. The two that mattered most: a successful
/// optimization and a run that only watched used to share the same green "on", and a
/// measurement that could not be completed used to be reported as "no gain found" -
/// which tells a user their connection cannot be improved when in fact nothing was
/// measured.
/// </remarks>
public enum LatencySituation
{
    /// <summary>The mode is off.</summary>
    Off = 0,

    /// <summary>On, but nothing has been measured yet.</summary>
    NotMeasuredYet = 1,

    /// <summary>Something is running now.</summary>
    Working = 2,

    /// <summary>A completed, paired experiment improved something and it is in place.</summary>
    VerifiedGain = 3,

    /// <summary>The gain is in loaded latency only: a send-rate cap is doing the work.</summary>
    LoadedGainOnly = 4,

    /// <summary>Experiments completed and there was no meaningful difference.</summary>
    NoDifference = 5,

    /// <summary>Not enough load or not enough samples; the measurement did not finish.</summary>
    Incomplete = 6,

    /// <summary>Nothing here can be tried right now: no candidate, or permission needed.</summary>
    NotAvailableNow = 7,

    /// <summary>A change was measured, found harmful, and taken back off.</summary>
    RolledBack = 8,

    /// <summary>Something could not be put back and the machine is not as it was.</summary>
    RestoreFailed = 9,

    /// <summary>No usable connection to measure.</summary>
    Offline = 10,

    /// <summary>The user stopped the run.</summary>
    Cancelled = 11,
}

/// <summary>The one thing the card offers to do next.</summary>
public enum LatencyNextAction
{
    /// <summary>Measure the connection: the entry point for everything else.</summary>
    Analyze = 0,

    /// <summary>Benchmark the adapter settings that apply to this target.</summary>
    TryCandidates = 1,

    /// <summary>Run the loaded-latency test, which needs the user to transfer something.</summary>
    LoadTest = 2,

    /// <summary>Measure again, from scratch.</summary>
    Remeasure = 3,

    /// <summary>Put the original settings back.</summary>
    Restore = 4,

    /// <summary>Allow adapter restarts, so the blocked candidates can be measured.</summary>
    AllowRestart = 5,

    /// <summary>Recover from a failed rollback.</summary>
    Recover = 6,

    /// <summary>Nothing to do but read the result.</summary>
    ViewResult = 7,
}

/// <summary>
/// The whole latency picture in one structured value: what the UI binds to and what
/// <c>latency status --json</c> prints.
/// </summary>
/// <remarks>
/// The JSON shape is a contract for whoever automates against it, so fields are added
/// rather than renamed and every one of them is present even when empty.
/// </remarks>
public sealed record LatencyStatusView
{
    public const int SchemaVersion = 1;

    public required LatencyModeState State { get; init; }

    /// <summary>The one line that answers "what is this doing right now".</summary>
    public required string Headline { get; init; }

    /// <summary>"off", "ok", "warn" or "info": colour only, never the meaning.</summary>
    public required string Severity { get; init; }

    /// <summary>The full report, as the status panel and the CLI print it.</summary>
    public string Detail { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public string Protocol { get; init; } = string.Empty;

    /// <summary>True when the number measures the route rather than the app's own RTT.</summary>
    public bool RouteReferenceOnly { get; init; }

    public string AdapterName { get; init; } = string.Empty;

    /// <summary>
    /// The idle round trip: the link quiet, nothing transferring.
    /// </summary>
    /// <remarks>
    /// Only ever a measurement whose own load counters say the link was idle. It used to
    /// be filled with <c>After ?? Before</c>, and the deep test put its loaded window in
    /// <c>After</c> - so a card could report the 140 ms measured mid-upload as the user's
    /// idle ping.
    /// </remarks>
    public LatencyMeasurement? Idle { get; init; }

    /// <summary>The idle round trip measured again with the final settings, when there is one.</summary>
    public LatencyMeasurement? IdleAfter { get; init; }

    /// <summary>
    /// The idle baseline, kept alongside <see cref="IdleAfter"/> so a before/after can be
    /// shown from two real measurements rather than reconstructed from a delta.
    /// </summary>
    public LatencyMeasurement? IdleBefore { get; init; }

    public LatencyMeasurement? UploadLoaded { get; init; }

    /// <summary>The loaded window measured again after a cap was applied, when there is one.</summary>
    public LatencyMeasurement? UploadLoadedAfter { get; init; }

    public LatencyMeasurement? DownloadLoaded { get; init; }

    public LatencyPathAnalysis? Path { get; init; }

    public IReadOnlyList<string> Applied { get; init; } = [];

    public IReadOnlyList<LatencyRejection> Rejected { get; init; } = [];

    public IReadOnlyList<string> Notices { get; init; } = [];

    public TrafficGuardState? TrafficGuard { get; init; }

    /// <summary>
    /// The gain an independent paired experiment measured. Never a start-to-finish diff.
    /// </summary>
    public LatencyDelta? Improvement { get; init; }

    /// <summary>Which metric that experiment improved, when it named one.</summary>
    public string? ImprovedMetric { get; init; }

    /// <summary>
    /// The plain difference between the first and last readings of the run.
    /// </summary>
    /// <remarks>
    /// Offered for context and never as a causal claim: everything that drifted between
    /// the two readings is in this number as well as anything the settings did.
    /// </remarks>
    public LatencyDelta? BaselineComparison { get; init; }

    /// <summary>What this run tried and what it did not, with a reason for each.</summary>
    public IReadOnlyList<LatencyLaneReport> Lanes { get; init; } = [];

    /// <summary>What the user is looking at, as a value rather than as a sentence.</summary>
    public required LatencySituation Situation { get; init; }

    /// <summary>The single thing worth doing next.</summary>
    public required LatencyNextAction NextAction { get; init; }

    /// <summary>One sentence saying what that next step is and why.</summary>
    public string Suggestion { get; init; } = string.Empty;

    /// <summary>What the run established about the link's own ceiling, per direction.</summary>
    public LinkCapacityEstimate Capacity { get; init; } = LinkCapacityEstimate.Unknown;

    /// <summary>Bytes of the user's own traffic the run watched go past.</summary>
    public long DataUsedBytes { get; init; }

    public bool IsBusy => State is LatencyModeState.Measuring
        or LatencyModeState.QuickTesting
        or LatencyModeState.DeepTesting;

    /// <summary>Whether the switch should read as on, whatever the outcome was.</summary>
    public bool ModeEnabled { get; init; }

    public static LatencyStatusView From(bool modeEnabled, LatencyOptimizationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var state = MapState(modeEnabled, result);
        var situation = MapSituation(modeEnabled, result, state);
        var action = NextActionFor(situation, result);

        // Idle means idle. A measurement whose own counters say the link was busy is a
        // loaded window whatever field it arrived in, and is not shown as the idle ping.
        var idleAfter = IdleOnly(result.After);

        return new LatencyStatusView
        {
            State = state,
            Situation = situation,
            NextAction = action,
            Suggestion = DescribeSuggestion(situation, action, result),
            ModeEnabled = modeEnabled,
            Headline = DescribeHeadline(state, result),
            Severity = DescribeSeverity(situation),
            Detail = result.StatusLine,
            Target = result.TargetLabel,
            Protocol = result.TargetProtocol,
            RouteReferenceOnly = result.RouteReferenceOnly,
            AdapterName = result.AdapterName,
            Idle = idleAfter ?? IdleOnly(result.Before),
            IdleAfter = idleAfter,
            IdleBefore = IdleOnly(result.Before),
            UploadLoaded = result.UploadLoaded,
            UploadLoadedAfter = result.UploadLoadedAfter ?? result.TrafficGuard?.LoadedAfter,
            DownloadLoaded = result.DownloadLoaded,
            Path = result.Path,
            Applied = result.AppliedChanges,
            Rejected = [.. result.Verdicts.Where(verdict => !verdict.Accepted)
                .Select(verdict => new LatencyRejection(verdict.Description, verdict.Reason, verdict.Cause))],
            Notices = result.Notices,
            Lanes = result.Lanes,
            TrafficGuard = result.TrafficGuard,
            Improvement = result.VerifiedImprovement,
            ImprovedMetric = result.ImprovedMetric,
            BaselineComparison = result.BaselineComparison,
            Capacity = result.Capacity,
            DataUsedBytes = result.DataUsedBytes,
        };
    }

    /// <summary>The measurement, but only if the link was actually idle for it.</summary>
    /// <remarks>
    /// <see cref="LatencyLoadState.Unknown"/> counts as idle: a machine whose counters
    /// could not be read has not told us the link was busy, and refusing to show its one
    /// measurement would leave the card empty on hardware that simply has no counters.
    /// </remarks>
    private static LatencyMeasurement? IdleOnly(LatencyMeasurement? measurement)
        => measurement is null || measurement.Load.IsLoaded ? null : measurement;

    /// <summary>
    /// Which of the real situations this result is, from its fields rather than its prose.
    /// </summary>
    private static LatencySituation MapSituation(
        bool modeEnabled,
        LatencyOptimizationResult result,
        LatencyModeState state)
    {
        if (state is LatencyModeState.Measuring or LatencyModeState.QuickTesting or LatencyModeState.DeepTesting)
        {
            return LatencySituation.Working;
        }

        // A rollback that did not complete outranks everything: the machine is not in a
        // state the user chose, and saying anything else first would bury that. A failure
        // that did put everything back is an ordinary failure and says so, because the
        // recovery action it would otherwise offer has nothing to recover.
        if (result.RestoreFailed)
        {
            return LatencySituation.RestoreFailed;
        }

        return result.Status switch
        {
            LatencyOptimizationStatus.Failed => LatencySituation.Incomplete,
            LatencyOptimizationStatus.Disabled when !modeEnabled => LatencySituation.Off,
            LatencyOptimizationStatus.Disabled => LatencySituation.NotMeasuredYet,
            LatencyOptimizationStatus.Offline => LatencySituation.Offline,
            LatencyOptimizationStatus.Cancelled => LatencySituation.Cancelled,
            LatencyOptimizationStatus.Restoring => LatencySituation.Working,
            LatencyOptimizationStatus.Active => LatencySituation.VerifiedGain,
            LatencyOptimizationStatus.TrafficGuardActive => LatencySituation.LoadedGainOnly,
            LatencyOptimizationStatus.Unsupported => LatencySituation.NotAvailableNow,

            // The distinction the old build could not make. A run that measured nothing -
            // because the candidates needed permission, or the load never appeared - is
            // incomplete, not a finding that the connection cannot be improved.
            LatencyOptimizationStatus.NoGain when WasRolledBack(result) => LatencySituation.RolledBack,
            LatencyOptimizationStatus.NoGain when NothingWasMeasured(result) => LatencySituation.NotAvailableNow,
            LatencyOptimizationStatus.NoGain => LatencySituation.NoDifference,
            LatencyOptimizationStatus.NeedsDeepTest => LatencySituation.Incomplete,
            LatencyOptimizationStatus.MonitoringOnly when IncompleteLoadRun(result) => LatencySituation.Incomplete,
            LatencyOptimizationStatus.MonitoringOnly => LatencySituation.NoDifference,
            _ => LatencySituation.NotMeasuredYet,
        };
    }

    /// <summary>Whether a change was applied, measured as harmful, and taken back off.</summary>
    /// <remarks>
    /// Read from the verdict causes, not from the wording of the reason. The previous
    /// build searched the Turkish text for "kötüleşti", "paket kaybı" and "bağlantı
    /// koptu", so rewording a message silently changed which state the card showed.
    /// </remarks>
    private static bool WasRolledBack(LatencyOptimizationResult result)
        => result.Verdicts.Any(verdict => verdict.Cause is LatencyOutcomeCause.MeasuredRegression
            or LatencyOutcomeCause.ConnectivityLost);

    /// <summary>Whether the run reached a verdict on anything at all.</summary>
    private static bool NothingWasMeasured(LatencyOptimizationResult result)
        => result.Verdicts.Count > 0
        && result.Verdicts.All(verdict => !verdict.Cause.IsPerformanceEvidence());

    /// <summary>Whether the loaded lane started and did not gather enough to conclude.</summary>
    private static bool IncompleteLoadRun(LatencyOptimizationResult result)
        => result.Lanes.Any(lane => lane.Lane == LatencyLane.LoadedLatency
            && lane.State == LatencyLaneState.Incomplete);

    /// <summary>The one action the card offers, chosen from the situation and the lanes.</summary>
    private static LatencyNextAction NextActionFor(LatencySituation situation, LatencyOptimizationResult result)
        => situation switch
        {
            LatencySituation.Off or LatencySituation.NotMeasuredYet => LatencyNextAction.Analyze,
            LatencySituation.Working => LatencyNextAction.ViewResult,
            LatencySituation.VerifiedGain => LatencyNextAction.Restore,
            LatencySituation.LoadedGainOnly => LatencyNextAction.ViewResult,
            LatencySituation.RolledBack => LatencyNextAction.ViewResult,
            LatencySituation.RestoreFailed => LatencyNextAction.Recover,
            LatencySituation.Offline or LatencySituation.Cancelled => LatencyNextAction.Remeasure,
            LatencySituation.Incomplete => LatencyNextAction.LoadTest,

            // Permission is the only obstacle the user can lift from this card, so when
            // it is what stopped the run it is what the card offers.
            LatencySituation.NotAvailableNow when NeedsPermission(result) => LatencyNextAction.AllowRestart,

            // Otherwise point at the lane that has not been run. A machine with no
            // adapter candidate still has the loaded test, which is where the large
            // numbers usually are.
            LatencySituation.NotAvailableNow or LatencySituation.NoDifference
                when HasUntriedLoadLane(result) => LatencyNextAction.LoadTest,
            LatencySituation.NoDifference => LatencyNextAction.Remeasure,
            _ => LatencyNextAction.Analyze,
        };

    private static bool NeedsPermission(LatencyOptimizationResult result)
        => result.Verdicts.Any(verdict => verdict.Cause == LatencyOutcomeCause.AwaitingPermission);

    private static bool HasUntriedLoadLane(LatencyOptimizationResult result)
        => result.Lanes.Any(lane => lane.Lane == LatencyLane.LoadedLatency
            && lane.State == LatencyLaneState.Available);

    /// <summary>
    /// One sentence, built from the fields, saying what to do next and why.
    /// </summary>
    /// <remarks>
    /// Built here rather than in the view model so the CLI and the card cannot drift, and
    /// composed from structured values so no consumer has to parse a report to act.
    /// </remarks>
    private static string DescribeSuggestion(
        LatencySituation situation,
        LatencyNextAction action,
        LatencyOptimizationResult result) => (situation, action) switch
    {
        (LatencySituation.Off, _) => "Bağlantınızı ölçmek için önce bu özelliği açın.",
        (LatencySituation.NotMeasuredYet, _) => "Başlamak için \"Bağlantımı analiz et\" düğmesine basın.",
        (LatencySituation.Working, _) => "Ölçüm sürüyor; dilediğiniz an durdurabilirsiniz.",

        (LatencySituation.VerifiedGain, _) =>
            "Ayarlar uygulandı. Bir sorun görürseniz \"Ayarları geri al\" ile eski haline dönebilirsiniz.",

        (LatencySituation.LoadedGainOnly, _) => result.TrafficGuard?.ThrottleBitsPerSecond is { } cap
            ? $"Kazanç, gönderim hızı {cap / 1_000_000d:F1} Mbit/s ile sınırlandığı sürece geçerlidir."
            : "Kazanç yalnız gönderim sırasında geçerlidir.",

        (LatencySituation.NoDifference, LatencyNextAction.LoadTest) =>
            "Boştaki ping değişmedi. Asıl fark genelde bir şey gönderirken oluşur; yük altında test edin.",
        (LatencySituation.NoDifference, _) =>
            "Bu testte belirgin bir iyileşme çıkmadı. Koşullar değiştiğinde yeniden ölçebilirsiniz.",

        (LatencySituation.Incomplete, _) =>
            "Ölçüm tamamlanmadı. Testi başlatıp istenen aktarımı yaptığınızda sonuç çıkacaktır.",

        (LatencySituation.NotAvailableNow, LatencyNextAction.AllowRestart) =>
            "Bazı ayarlar yalnız ağ kartı yeniden başlatılınca etkinleşir. İzin verirseniz ölçülebilirler; "
                + "bağlantınız birkaç saniye kesilir.",
        (LatencySituation.NotAvailableNow, LatencyNextAction.LoadTest) =>
            "Bu bilgisayarda değiştirilebilecek bir ağ kartı ayarı yok, ama yük altındaki gecikme ölçülebilir.",
        (LatencySituation.NotAvailableNow, _) =>
            "Şu anda denenebilecek bir ayar yok; bağlantı ölçümü yine de kullanılabilir.",

        (LatencySituation.RolledBack, _) =>
            "Ayar sonucu kötüleştirdiği için eski değerine döndürüldü; makinede kalıcı bir değişiklik yok.",
        (LatencySituation.RestoreFailed, _) =>
            "Bazı ayarlar eski haline döndürülemedi. \"Ayarları geri al\" ile hedefli kurtarmayı çalıştırın.",
        (LatencySituation.Offline, _) => "Ölçüm için çalışan bir internet bağlantısı gerekiyor.",
        (LatencySituation.Cancelled, _) => "Ölçüm durduruldu; makine ölçümden önceki durumunda.",
        _ => string.Empty,
    };

    /// <summary>
    /// The state, which depends on what the user asked for as well as what was found.
    /// </summary>
    /// <remarks>
    /// A result that found nothing is only "off" when the user actually turned it off.
    /// With the mode on it is monitoring, which is a different thing and is shown as one.
    /// </remarks>
    private static LatencyModeState MapState(bool modeEnabled, LatencyOptimizationResult result) => result.Status switch
    {
        LatencyOptimizationStatus.Disabled => modeEnabled ? LatencyModeState.MonitoringOnly : LatencyModeState.Off,
        LatencyOptimizationStatus.Measuring => LatencyModeState.Measuring,
        LatencyOptimizationStatus.Optimizing => LatencyModeState.Measuring,
        LatencyOptimizationStatus.QuickTesting => LatencyModeState.QuickTesting,
        LatencyOptimizationStatus.LoadTesting => LatencyModeState.DeepTesting,
        LatencyOptimizationStatus.Active => LatencyModeState.GainApplied,
        LatencyOptimizationStatus.TrafficGuardActive => LatencyModeState.TrafficGuardActive,
        LatencyOptimizationStatus.NoGain => modeEnabled ? LatencyModeState.NoLocalGain : LatencyModeState.Off,
        LatencyOptimizationStatus.MonitoringOnly => LatencyModeState.MonitoringOnly,
        LatencyOptimizationStatus.NeedsDeepTest => LatencyModeState.NeedsDeepTest,
        LatencyOptimizationStatus.Unsupported => LatencyModeState.UnsupportedAdapter,
        LatencyOptimizationStatus.Offline => LatencyModeState.Offline,
        LatencyOptimizationStatus.Restoring => LatencyModeState.AwaitingRestore,
        LatencyOptimizationStatus.Cancelled => LatencyModeState.Cancelled,
        _ => LatencyModeState.Failed,
    };

    private static string DescribeHeadline(LatencyModeState state, LatencyOptimizationResult result)
    {
        var prefix = state == LatencyModeState.Off ? "Kapalı" : "Açık";

        return state switch
        {
            LatencyModeState.Off => "Kapalı",
            LatencyModeState.Measuring => "Açık · ölçülüyor",
            LatencyModeState.QuickTesting => "Açık · hızlı test yapılıyor",
            LatencyModeState.DeepTesting => "Açık · yük altında derin test yapılıyor",

            LatencyModeState.GainApplied when result.VerifiedImprovement is { } gain =>
                $"{prefix} · NIC ayarı medianı {gain.MedianMs:F1} ms, p95'i {gain.P95Ms:F1} ms azalttı",
            LatencyModeState.GainApplied => $"{prefix} · doğrulanmış NIC değişikliği uygulandı",

            LatencyModeState.TrafficGuardActive when result.TrafficGuard?.ImprovementMs is { } removed =>
                $"{prefix} · Traffic Guard gönderim kuyruklanmasını {removed:F0} ms azalttı",
            LatencyModeState.TrafficGuardActive => $"{prefix} · Traffic Guard etkin",

            LatencyModeState.NoLocalGain when Rolled(result) is { } reason =>
                $"{prefix} · müdahale geri alındı · {reason}",
            LatencyModeState.NoLocalGain when WanShare(result) is { } wan =>
                $"{prefix} · gecikmenin {wan:F0} ms'i ISP/WAN rotasında; yerel ayar bunu değiştiremez",
            LatencyModeState.NoLocalGain =>
                $"{prefix} · ağ izleniyor · yerel olarak uygulanabilir kazanç bulunamadı",

            LatencyModeState.MonitoringOnly when WanShare(result) is { } wan =>
                $"{prefix} · yalnız izleme · gecikmenin {wan:F0} ms'i ISP/WAN rotasında",
            LatencyModeState.MonitoringOnly => $"{prefix} · yalnız izleme ve tanılama",

            LatencyModeState.NeedsDeepTest => "Derin test gerekli · yalnız boşta bağlantı ölçüldü",
            LatencyModeState.UnsupportedAdapter =>
                "Desteklenen NIC adayı yok · hedef ve yük tanılaması kullanılabilir",
            LatencyModeState.Offline => "Bağlantı yok · ölçüm yapılamıyor",
            LatencyModeState.AwaitingRestore => "Geri yükleme bekliyor",
            LatencyModeState.Cancelled => "İptal edildi · özgün ayarlar geri alındı",
            _ => "Başarısız · ayrıntı için rapora bakın",
        };
    }

    /// <summary>
    /// The reason a change was taken back off, when one was.
    /// </summary>
    /// <remarks>
    /// Selected on the verdict's cause. The previous version searched the reason text for
    /// "paket kaybı", "kötüleşti" and "bağlantı koptu", which made the card's state
    /// depend on the exact Turkish wording of a diagnostic message.
    /// </remarks>
    private static string? Rolled(LatencyOptimizationResult result)
        => result.Verdicts
            .FirstOrDefault(verdict => verdict.Cause is LatencyOutcomeCause.MeasuredRegression
                or LatencyOutcomeCause.ConnectivityLost)
            ?.Reason;

    /// <summary>How much of the delay is beyond the operator, when the split is known.</summary>
    private static double? WanShare(LatencyOptimizationResult result)
        => result.Path is { Bottleneck: LatencyBottleneck.WanRoute, RemotePathMs: { } remote } && remote > 0
            ? remote
            : null;

    /// <summary>
    /// Colour only, and never the only thing carrying the meaning.
    /// </summary>
    /// <remarks>
    /// Driven from the situation rather than the mode state so a successful optimization
    /// and a run that only watched cannot share the same green: one changed the machine
    /// for the better and the other did nothing, and a colour that says "success" for
    /// both is the card telling the user something that is not true.
    /// </remarks>
    private static string DescribeSeverity(LatencySituation situation) => situation switch
    {
        LatencySituation.VerifiedGain or LatencySituation.LoadedGainOnly => "ok",
        LatencySituation.Off => "off",
        LatencySituation.RestoreFailed or LatencySituation.Offline => "warn",
        LatencySituation.Incomplete or LatencySituation.NotAvailableNow => "attention",
        _ => "info",
    };

    /// <summary>The stable machine-readable form, for scripts and support requests.</summary>
    public string ToJson()
    {
        var json = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["state"] = State.ToString(),
            ["situation"] = Situation.ToString(),
            ["nextAction"] = NextAction.ToString(),
            ["suggestion"] = Suggestion,
            ["modeEnabled"] = ModeEnabled,
            ["headline"] = Headline,
            ["severity"] = Severity,
            ["adapter"] = AdapterName,
            ["target"] = new JsonObject
            {
                ["label"] = Target,
                ["protocol"] = Protocol,
                ["routeReferenceOnly"] = RouteReferenceOnly,
            },
            ["idle"] = Measurement(Idle),
            ["idleAfter"] = Measurement(IdleAfter),
            ["uploadLoaded"] = Measurement(UploadLoaded),
            ["uploadLoadedAfter"] = Measurement(UploadLoadedAfter),
            ["downloadLoaded"] = Measurement(DownloadLoaded),
            ["lanes"] = new JsonArray([.. Lanes.Select(lane => (JsonNode)new JsonObject
            {
                ["lane"] = lane.Lane.ToString(),
                ["state"] = lane.State.ToString(),
                ["detail"] = lane.Detail,
            })]),
            ["path"] = Path is null
                ? null
                : new JsonObject
                {
                    ["bottleneck"] = Path.Bottleneck.ToString(),
                    ["localLinkMs"] = Path.LocalLinkMs,
                    ["remotePathMs"] = Path.RemotePathMs,
                    ["uploadQueueingMs"] = Path.UploadQueueingMs,
                    ["downloadQueueingMs"] = Path.DownloadQueueingMs,
                    ["locallyImprovable"] = Path.LocallyImprovable,
                    ["trafficGuardApplicable"] = Path.TrafficGuardApplicable,
                    ["summary"] = Path.Summary,
                },
            ["applied"] = new JsonArray([.. Applied.Select(entry => (JsonNode)JsonValue.Create(entry)!)]),
            ["rejected"] = new JsonArray([.. Rejected.Select(entry => (JsonNode)new JsonObject
            {
                ["change"] = entry.Change,
                ["reason"] = entry.Reason,

                // The machine-readable half of the reason: a consumer can tell "measured
                // and useless" from "never tried" without reading Turkish prose.
                ["cause"] = entry.Cause.ToString(),
                ["measured"] = entry.WasMeasured,
            })]),
            ["notices"] = new JsonArray([.. Notices.Select(entry => (JsonNode)JsonValue.Create(entry)!)]),
            // The verified gain, which comes from the paired confirmation experiment.
            ["improvement"] = Delta(Improvement),
            ["improvedMetric"] = ImprovedMetric,

            // The plain first-to-last difference. Reported separately and never merged
            // with the above, because nothing establishes that the change caused it.
            ["baselineComparison"] = Delta(BaselineComparison),
            ["capacity"] = new JsonObject
            {
                ["uplinkKbps"] = Round(Capacity.UplinkKbps),
                ["downlinkKbps"] = Round(Capacity.DownlinkKbps),
                ["uplinkConfidence"] = Capacity.UplinkConfidence.ToString(),
                ["downlinkConfidence"] = Capacity.DownlinkConfidence.ToString(),
                ["uplinkObservedAt"] = Capacity.UplinkObservedAt?.ToString("O", CultureInfo.InvariantCulture),
                ["downlinkObservedAt"] = Capacity.DownlinkObservedAt?.ToString("O", CultureInfo.InvariantCulture),
            },
            ["dataUsedBytes"] = DataUsedBytes,
            ["trafficGuard"] = TrafficGuard is null
                ? null
                : new JsonObject
                {
                    ["status"] = TrafficGuard.Status.ToString(),
                    ["summary"] = TrafficGuard.Summary,
                    ["policyName"] = TrafficGuard.PolicyName,
                    ["throttleBitsPerSecond"] = TrafficGuard.ThrottleBitsPerSecond,
                    ["application"] = TrafficGuard.ThrottledApplication,
                    ["policyMatch"] = TrafficGuard.PolicyMatch,
                    ["mode"] = TrafficGuard.Mode.ToString(),
                    ["uploadQueueingBeforeMs"] = Round(TrafficGuard.UploadQueueingBeforeMs),
                    ["uploadQueueingAfterMs"] = Round(TrafficGuard.UploadQueueingAfterMs),
                    ["loadedP95BeforeMs"] = Round(TrafficGuard.LoadedP95BeforeMs),
                    ["loadedP95AfterMs"] = Round(TrafficGuard.LoadedP95AfterMs),
                    ["retainedThroughputShare"] = Round(TrafficGuard.RetainedThroughputShare),
                    ["trials"] = new JsonArray(
                        [.. TrafficGuard.Trials.Select(entry => (JsonNode)JsonValue.Create(entry)!)]),
                    ["conflicts"] = new JsonArray(
                        [.. TrafficGuard.Conflicts.Select(entry => (JsonNode)JsonValue.Create(entry)!)]),
                },
        };

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject? Delta(LatencyDelta? delta) => delta is null
        ? null
        : new JsonObject
        {
            ["medianMs"] = Round(delta.MedianMs),
            ["p95Ms"] = Round(delta.P95Ms),
            ["p99Ms"] = Round(delta.P99Ms),
            ["jitterMs"] = Round(delta.JitterMs),
            ["lossPercent"] = Round(delta.LossPercent),
        };

    private static JsonObject? Measurement(LatencyMeasurement? measurement) => measurement is null
        ? null
        : new JsonObject
        {
            ["endpoint"] = measurement.RemoteEndpoint,
            ["protocol"] = measurement.Protocol,
            ["replies"] = measurement.RemoteReplies,
            ["attempts"] = measurement.RemoteAttempts,

            // Passive observations send nothing, so their attempt count is zero and their
            // loss is null rather than a plausible-looking zero percent.
            ["source"] = measurement.Source.ToString(),
            ["minimumMs"] = Round(measurement.MinimumRttMs),
            ["medianMs"] = Round(measurement.MedianRttMs),
            ["p95Ms"] = Round(measurement.P95RttMs),

            // Reported, but null when the sample is too small for the name to mean
            // anything: a p99 from forty replies is the worst sample, not a percentile.
            ["p99Ms"] = measurement.RemoteReplies >= 100 ? Round(measurement.P99RttMs) : null,
            ["jitterMs"] = Round(measurement.JitterMs),
            ["packetLossPercent"] = Round(measurement.PacketLossPercent),
            ["packetLossMeasured"] = measurement.LossMeasured,

            // Reported so a consumer can tell a real difference from a rounding artefact:
            // nothing below this is something the instrument could see.
            ["clockResolutionMs"] = Round(measurement.ClockResolutionMs),
            ["gatewayMedianMs"] = Round(measurement.GatewayMedianRttMs),
            ["loadState"] = measurement.Load.State.ToString(),
            ["uplinkKbps"] = Round(measurement.Load.UplinkKbps),
            ["downlinkKbps"] = Round(measurement.Load.DownlinkKbps),
        };

    private static JsonNode? Round(double? value) => value is { } number && double.IsFinite(number)
        ? JsonValue.Create(Math.Round(number, 2, MidpointRounding.AwayFromZero))
        : null;

    /// <summary>A compact one-line form for the text status command.</summary>
    public string ToCompactLine() => string.Join(
        " · ",
        new[]
        {
            Headline,
            string.IsNullOrWhiteSpace(Target) ? null : $"hedef {Target}",
            Idle is null ? null : $"boşta median {Idle.MedianRttMs.ToString("F1", CultureInfo.InvariantCulture)} ms",
            Path?.UploadQueueingMs is { } upload
                ? $"gönderim kuyruğu {upload.ToString("F0", CultureInfo.InvariantCulture)} ms"
                : null,
            string.IsNullOrWhiteSpace(Suggestion) ? null : Suggestion,
        }.Where(part => part is not null));
}
