using System.Globalization;

namespace DpiBypass.Core.Network.Latency;

/// <summary>The short state word on the ping card.</summary>
public enum LatencyHeadlineState
{
    /// <summary>The switch is off.</summary>
    Off = 0,

    /// <summary>Measuring the connection.</summary>
    Measuring = 1,

    /// <summary>Trying settings and verifying them.</summary>
    Applying = 2,

    /// <summary>A verified improvement is in place.</summary>
    Active = 3,

    /// <summary>Nothing beat what was already there, so nothing was changed.</summary>
    Unchanged = 4,

    /// <summary>Something needs the user: a failed rollback, no connection, a blocked run.</summary>
    NeedsAttention = 5,
}

/// <summary>
/// The whole ping card, as text, decided in one place.
/// </summary>
/// <remarks>
/// <para>
/// Three numbers - before, after, what came off - plus one sentence and one line saying
/// what was measured and when. Everything that is not those lives in the collapsed detail
/// section.
/// </para>
/// <para>
/// The rules here exist because the card previously implied things the measurements did
/// not support. Before and after are the same statistic of the same comparison, so the
/// difference between them is the difference the settings made and not the drift of the
/// link over the run. A gain is only shown when a paired experiment verified it and the
/// change is still applied. A steadier connection gets its own sentence rather than being
/// converted into milliseconds off the ping. Nothing that was not measured is printed as a
/// number, and nothing is ever printed as 0 ms to fill a gap.
/// </para>
/// </remarks>
public sealed record LatencyHeadline
{
    /// <summary>What is shown when there is no number: never a zero.</summary>
    public const string NotMeasured = "—";

    /// <summary>The state word: "Ölçülüyor", "Etkin", and so on.</summary>
    public required string Status { get; init; }

    public required LatencyHeadlineState State { get; init; }

    /// <summary>The starting point, formatted, or <see cref="NotMeasured"/>.</summary>
    public required string Before { get; init; }

    /// <summary>The verified result, formatted, or <see cref="NotMeasured"/>.</summary>
    public required string After { get; init; }

    /// <summary>What came off, formatted, or <see cref="NotMeasured"/>.</summary>
    public required string Gain { get; init; }

    /// <summary>
    /// Whether the gain is a real, verified, still-applied reduction.
    /// </summary>
    /// <remarks>
    /// The only thing that may be coloured as a win. False covers every other case,
    /// including a rejected candidate's difference, a stability-only result, and a run that
    /// has not finished.
    /// </remarks>
    public required bool ShowsReduction { get; init; }

    /// <summary>The same gain as a share of the baseline, when there is a valid one.</summary>
    /// <remarks>
    /// Secondary by design, and null rather than zero when the baseline cannot carry a
    /// percentage - a baseline of 0 ms has no meaningful share, and neither has one that
    /// was never measured.
    /// </remarks>
    public string? Percent { get; init; }

    /// <summary>One sentence about what happened.</summary>
    public required string Explanation { get; init; }

    /// <summary>
    /// Set when the tail or the variation improved but the typical round trip did not.
    /// </summary>
    /// <remarks>
    /// Its own line precisely so it is never added to the ping gain. "Steadier" and "lower"
    /// are two different results and the card says which one it has.
    /// </remarks>
    public string? StabilityNote { get; init; }

    /// <summary>What was measured.</summary>
    public required string TargetLine { get; init; }

    /// <summary>When, and whether it is a live reading or a past comparison.</summary>
    public required string MeasuredAtLine { get; init; }

    /// <summary>The idle round trip as it stands now, formatted, or <see cref="NotMeasured"/>.</summary>
    /// <remarks>
    /// Labelled apart from the before/after pair. That pair is the history of one
    /// comparison; this is what the connection reads at the moment, and on a rolled-back
    /// run it is the only number on the card that means anything.
    /// </remarks>
    public required string CurrentPing { get; init; }

    /// <summary>Builds the card from a status view and the clock.</summary>
    public static LatencyHeadline From(LatencyStatusView status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);

        var state = StateFor(status);
        var current = status.Idle;

        // A before/after is only offered when both halves exist, describe the same
        // comparison, and the change that produced it is still in place. IdleAfter is null
        // on every run that kept nothing, which is what stops a rejected candidate's
        // reading being drawn as the "after" of anything.
        var pair = status is { IdleBefore: { } before, IdleAfter: { } after }
            && before.ComparableWith(after)
            && status.Applied.Count > 0
            ? (Before: before, After: after)
            : ((LatencyMeasurement Before, LatencyMeasurement After)?)null;

        // Only a median win is a reduction. A stability-only result has an after value and
        // a verified experiment behind it, and still must not print a millisecond gain.
        var reduction = pair is { } shown
            && status.Situation == LatencySituation.VerifiedGain
            && status.GainKind == LatencyGainKind.Median
            && shown.Before.MedianRttMs > shown.After.MedianRttMs
            ? shown.Before.MedianRttMs - shown.After.MedianRttMs
            : (double?)null;

        return new LatencyHeadline
        {
            State = state,
            Status = StatusWord(state),
            Before = pair is { } p ? Milliseconds(p.Before.MedianRttMs) : NotMeasured,
            After = pair is { } q ? Milliseconds(q.After.MedianRttMs) : NotMeasured,
            Gain = reduction is { } gain ? $"{Milliseconds(gain)} azalma" : NotMeasured,
            ShowsReduction = reduction is not null,
            Percent = PercentOf(reduction, pair?.Before.MedianRttMs),
            StabilityNote = StabilityNoteFor(status),
            Explanation = ExplanationFor(status, state, reduction),
            CurrentPing = current is null ? NotMeasured : Milliseconds(current.MedianRttMs),
            TargetLine = TargetLineFor(status),
            MeasuredAtLine = MeasuredAtLineFor(status, current, pair is not null, now),
        };
    }

    private static LatencyHeadlineState StateFor(LatencyStatusView status) => status.Situation switch
    {
        LatencySituation.Off => LatencyHeadlineState.Off,
        LatencySituation.Working when status.Applied.Count > 0 => LatencyHeadlineState.Applying,
        LatencySituation.Working => LatencyHeadlineState.Measuring,
        LatencySituation.NotMeasuredYet => LatencyHeadlineState.Measuring,
        LatencySituation.VerifiedGain or LatencySituation.LoadedGainOnly => LatencyHeadlineState.Active,
        LatencySituation.NoDifference or LatencySituation.RolledBack => LatencyHeadlineState.Unchanged,

        // Everything the user might have to do something about. A failed rollback is the
        // important one and it is never softened into "unchanged": the machine is not as it
        // was found.
        LatencySituation.RestoreFailed
            or LatencySituation.Offline
            or LatencySituation.Incomplete
            or LatencySituation.NotAvailableNow => LatencyHeadlineState.NeedsAttention,
        _ => LatencyHeadlineState.Unchanged,
    };

    private static string StatusWord(LatencyHeadlineState state) => state switch
    {
        LatencyHeadlineState.Off => "Kapalı",
        LatencyHeadlineState.Measuring => "Ölçülüyor",
        LatencyHeadlineState.Applying => "İyileştirme uygulanıyor",
        LatencyHeadlineState.Active => "Etkin",
        LatencyHeadlineState.Unchanged => "Mevcut ayarlar korundu",
        _ => "İlgilenilmesi gerekiyor",
    };

    /// <summary>The steadiness line, when steadiness is what improved.</summary>
    private static string? StabilityNoteFor(LatencyStatusView status)
        => status.GainKind == LatencyGainKind.Stability && status.Applied.Count > 0
            ? "Bağlantı daha kararlı: ping ortalaması aynı kaldı, ani sıçramalar azaldı."
            : null;

    private static string ExplanationFor(
        LatencyStatusView status,
        LatencyHeadlineState state,
        double? reduction) => state switch
    {
        LatencyHeadlineState.Off => "Ping optimizasyonu kapalı. Açtığınızda hedefi kendisi seçer ve "
            + "yalnız ölçerek doğruladığı iyileştirmeleri uygular.",
        LatencyHeadlineState.Measuring => "Bağlantınız ölçülüyor. Bu sırada uygulamayı kullanmaya "
            + "devam edebilirsiniz.",
        LatencyHeadlineState.Applying => "Aday ayarlar sırayla deneniyor; yalnız ölçülerek doğrulananlar kalıyor.",

        LatencyHeadlineState.Active when reduction is not null =>
            "Doğrulanmış bir düşüş uygulandı ve hâlâ etkin.",
        LatencyHeadlineState.Active when status.GainKind == LatencyGainKind.Stability =>
            "Ping ortalaması değişmedi; doğrulanan iyileşme bağlantının kararlılığında.",
        LatencyHeadlineState.Active => "Bir iyileştirme uygulandı ve etkin.",

        // The sentence the reported card should have shown.
        LatencyHeadlineState.Unchanged => status.Remeasure == LatencyRemeasureState.Failed
            ? "Belirgin bir düşüş doğrulanmadı; mevcut ayarlarınız korundu. Bağlantı şu anda "
                + "yeniden ölçülemedi."
            : "Belirgin bir düşüş doğrulanmadı; mevcut ayarlarınız korundu.",

        _ => status.Suggestion.Length > 0
            ? status.Suggestion
            : "Ölçüm tamamlanamadı. Ayrıntılar bölümünde ne yapıldığı yazıyor.",
    };

    private static string TargetLineFor(LatencyStatusView status)
    {
        if (status.Target.Length == 0)
        {
            return "Ölçülen hedef: henüz belirlenmedi.";
        }

        var line = $"Ölçülen hedef: {status.Target}";

        if (status.Protocol.Length > 0)
        {
            line += $" · {status.Protocol}";
        }

        // Never silently presented as the application's own round trip.
        return status.RouteReferenceOnly
            ? line + " · yalnız rota referansı, uygulamanın kendi süresi değil"
            : line;
    }

    /// <summary>
    /// When the numbers were taken, and which kind of number they are.
    /// </summary>
    /// <remarks>
    /// A before/after pair is history: it describes a comparison that finished, possibly
    /// some time ago and possibly on another network. A current reading is now. Labelling
    /// them the same way is how a gain measured on one connection gets read as a property
    /// of the one the user is on.
    /// </remarks>
    private static string MeasuredAtLineFor(
        LatencyStatusView status,
        LatencyMeasurement? current,
        bool hasPair,
        DateTimeOffset now)
    {
        if (status.Remeasure == LatencyRemeasureState.InProgress)
        {
            return "Yeniden ölçülüyor…";
        }

        if (status.Remeasure == LatencyRemeasureState.Failed && current is null)
        {
            return "Şu anki ping ölçülemedi.";
        }

        if (current is null)
        {
            return "Henüz ölçüm yok.";
        }

        var age = Age(now - current.MeasuredAt);

        return hasPair
            ? $"Karşılaştırma {age} alındı · şu anki ping bu ölçümden"
            : $"Şu anki ping {age} ölçüldü";
    }

    private static string Age(TimeSpan elapsed) => elapsed switch
    {
        { TotalSeconds: < 90 } => "az önce",
        { TotalMinutes: < 60 } => $"{elapsed.TotalMinutes:F0} dakika önce",
        { TotalHours: < 24 } => $"{elapsed.TotalHours:F0} saat önce",
        _ => $"{elapsed.TotalDays:F0} gün önce",
    };

    /// <summary>The gain as a share of the baseline, or null when that would be meaningless.</summary>
    private static string? PercentOf(double? gain, double? baseline)
        => gain is { } value && baseline is { } start && start > 0
            ? string.Create(CultureInfo.CurrentCulture, $"%{value / start * 100:F0}")
            : null;

    private static string Milliseconds(double value)
        => string.Create(CultureInfo.CurrentCulture, $"{value:F0} ms");
}
