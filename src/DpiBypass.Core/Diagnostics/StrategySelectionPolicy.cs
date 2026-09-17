namespace DpiBypass.Core.Diagnostics;

/// <summary>How much of a measurement there was to decide on.</summary>
public enum SelectionConfidence
{
    /// <summary>Not enough samples to tell candidates apart. The incumbent stays.</summary>
    Insufficient = 0,

    /// <summary>Reachability and latency were measured; throughput was not.</summary>
    LatencyOnly = 1,

    /// <summary>Reachability, latency and a sustained transfer were all measured.</summary>
    WithThroughput = 2,
}

/// <summary>Why one candidate was preferred, in terms a user can check.</summary>
/// <param name="StrategyId">The winner, or null when nothing should change.</param>
/// <param name="Reason">One sentence. Never empty, including when nothing changed.</param>
/// <param name="Confidence">How much was measured.</param>
/// <param name="Changed">Whether this is a different profile from the incumbent.</param>
/// <param name="MeasuredAt">When the measurements it is based on were taken.</param>
/// <param name="Eliminated">
/// Candidates that were measured and rejected, with why - so "it picked the slow one" can
/// be answered rather than argued about.
/// </param>
public sealed record StrategySelection(
    string? StrategyId,
    string Reason,
    SelectionConfidence Confidence,
    bool Changed,
    DateTimeOffset MeasuredAt,
    IReadOnlyList<StrategyRejection> Eliminated)
{
    public bool HasWinner => StrategyId is { Length: > 0 };
}

/// <summary>One measured candidate that did not win, and why.</summary>
public sealed record StrategyRejection(string StrategyId, string StrategyName, string Reason);

/// <summary>The thresholds the policy decides by. All of them are starting points.</summary>
/// <param name="MinimumSamplesPerCandidate">
/// Below this many probes a candidate has not been measured, it has been sampled once and
/// guessed at.
/// </param>
/// <param name="MaximumFailureRate">
/// How much flakiness a candidate may show and still be considered. A recipe that gets
/// through four times in five is not a recipe that works; it is one that will drop a call.
/// </param>
/// <param name="SpeedFloorRatio">
/// The share of the best measured sustained speed a candidate has to keep to stay in the
/// running. 0.95 is a starting point chosen to sit outside the noise of a short transfer,
/// not a law: it is here to be tuned against real measurements, and the field exists so
/// that tuning it is a change to one number.
/// </param>
/// <param name="MinimumImprovementMs">
/// How much faster a challenger has to be before the profile is worth changing at all.
/// </param>
/// <param name="MinimumImprovementRatio">
/// And how much faster in relative terms, so the absolute figure does not become
/// meaningless on a link where everything is either 8 ms or 300 ms.
/// </param>
/// <param name="MinimumDwell">
/// How long a working profile is left alone before a challenger may replace it, when the
/// challenger is merely better rather than necessary.
/// </param>
public readonly record struct SelectionThresholds(
    int MinimumSamplesPerCandidate,
    double MaximumFailureRate,
    double SpeedFloorRatio,
    double MinimumImprovementMs,
    double MinimumImprovementRatio,
    TimeSpan MinimumDwell)
{
    /// <summary>
    /// The values this application starts from. Not universal constants.
    /// </summary>
    /// <remarks>
    /// Every one of them is an engineering judgement about this app's traffic and this
    /// app's users, and each is here rather than buried in a comparison so that changing
    /// one is a change to a number with a test beside it.
    /// </remarks>
    public static SelectionThresholds Default { get; } = new(
        MinimumSamplesPerCandidate: 3,
        MaximumFailureRate: 0.2,
        SpeedFloorRatio: 0.95,
        MinimumImprovementMs: 3,
        MinimumImprovementRatio: 0.08,
        MinimumDwell: TimeSpan.FromMinutes(10));
}

/// <summary>
/// Picks the profile to run, out of what was measured.
/// </summary>
/// <remarks>
/// <para>
/// The goal is "the best verified balance among the candidates tested on this network",
/// and not a promise of the lowest ping or the highest speed anywhere. Those are different
/// claims and only the first one is true.
/// </para>
/// <para>
/// The order matters and it is: reach, then stability, then speed, then latency. A profile
/// that cannot reach the targets is not a candidate however fast it is; one that drops one
/// connection in four is not a candidate however low its median; one that costs a fifth of
/// the link's throughput is not worth two milliseconds; and only among what is left does
/// the lower latency win.
/// </para>
/// <para>
/// The last rule is the one that keeps the app from thrashing. Two profiles whose results
/// are inside each other's noise will alternate for ever if every sweep simply picks the
/// current best, and each switch costs a reconnect. So a challenger has to be better by a
/// margin, in both absolute and relative terms, and a profile that is working is left alone
/// for a while before being replaced by one that is merely better.
/// </para>
/// </remarks>
public static class StrategySelectionPolicy
{
    /// <param name="measurements">What each candidate measured. May be empty.</param>
    /// <param name="targets">The hosts a candidate has to reach.</param>
    /// <param name="incumbentId">The profile running now, or null when there is none.</param>
    /// <param name="incumbentSince">When the incumbent was installed, for the dwell rule.</param>
    /// <param name="now">The clock, injected so the dwell rule can be tested.</param>
    /// <param name="thresholds">The numbers to decide by.</param>
    public static StrategySelection Choose(
        IReadOnlyList<StrategyMeasurement> measurements,
        IReadOnlyList<ProbeTarget> targets,
        string? incumbentId,
        DateTimeOffset? incumbentSince,
        DateTimeOffset now,
        SelectionThresholds? thresholds = null)
    {
        var limits = thresholds ?? SelectionThresholds.Default;
        var measuredAt = measurements.Count == 0 ? now : measurements.Max(m => m.MeasuredAt);
        var rejected = new List<StrategyRejection>();

        if (measurements.Count == 0)
        {
            return Keep(incumbentId, "Hiçbir profil ölçülmedi; mevcut ayar korunuyor.", SelectionConfidence.Insufficient, measuredAt, rejected);
        }

        // --- 1. Reach and stability. Nothing else is asked of a profile that fails here.
        var eligible = new List<StrategyMeasurement>();
        foreach (var candidate in measurements)
        {
            if (candidate.Attempts < limits.MinimumSamplesPerCandidate)
            {
                rejected.Add(new StrategyRejection(
                    candidate.StrategyId,
                    candidate.StrategyName,
                    $"Yeterli örnek alınamadı ({candidate.Attempts}/{limits.MinimumSamplesPerCandidate})."));
                continue;
            }

            if (!candidate.ReachedEveryRequiredTarget(targets))
            {
                var missing = string.Join(", ", candidate.MissingRequiredTargets(targets));
                rejected.Add(new StrategyRejection(
                    candidate.StrategyId,
                    candidate.StrategyName,
                    $"Gerekli hedeflere ulaşamadı: {missing}."));
                continue;
            }

            if (candidate.FailureRate > limits.MaximumFailureRate)
            {
                rejected.Add(new StrategyRejection(
                    candidate.StrategyId,
                    candidate.StrategyName,
                    $"Kararsız: denemelerin %{candidate.FailureRate * 100:F0}'ı başarısız."));
                continue;
            }

            eligible.Add(candidate);
        }

        if (eligible.Count == 0)
        {
            return Keep(
                incumbentId,
                "Ölçülen profillerin hiçbiri gerekli hedeflere kararlı biçimde ulaşamadı; mevcut ayar korunuyor.",
                SelectionConfidence.Insufficient,
                measuredAt,
                rejected);
        }

        // --- 2. Speed, when it was measured. A profile that costs a meaningful share of
        //        the link's throughput is out, however good its latency looks.
        var measuredSpeeds = eligible
            .Where(candidate => candidate.Throughput is not null)
            .ToArray();

        var confidence = measuredSpeeds.Length >= 2
            ? SelectionConfidence.WithThroughput
            : SelectionConfidence.LatencyOnly;

        if (confidence == SelectionConfidence.WithThroughput)
        {
            var best = measuredSpeeds.Max(candidate => candidate.Throughput!.DownlinkMbps);
            var floor = best * limits.SpeedFloorRatio;

            var kept = new List<StrategyMeasurement>();
            foreach (var candidate in eligible)
            {
                // A candidate nobody measured the speed of is not eliminated by a speed
                // rule: the answer is unknown, not bad, and treating the two the same is
                // how an untested option quietly becomes an unusable one.
                if (candidate.Throughput is not { } throughput)
                {
                    kept.Add(candidate);
                    continue;
                }

                if (throughput.DownlinkMbps < floor)
                {
                    rejected.Add(new StrategyRejection(
                        candidate.StrategyId,
                        candidate.StrategyName,
                        $"Hızı anlamlı biçimde düşürüyor: {throughput.DownlinkMbps:F1} Mb/sn, "
                            + $"en iyisi {best:F1} Mb/sn."));
                    continue;
                }

                kept.Add(candidate);
            }

            if (kept.Count > 0)
            {
                eligible = kept;
            }
        }

        // --- 3. Among what is left: the lowest connect time, then the steadiest.
        var ranked = eligible
            .OrderBy(candidate => candidate.MedianConnectMs ?? double.MaxValue)
            .ThenBy(candidate => candidate.JitterMs ?? double.MaxValue)
            .ToList();

        var winner = ranked[0];

        foreach (var loser in ranked.Skip(1))
        {
            rejected.Add(new StrategyRejection(
                loser.StrategyId,
                loser.StrategyName,
                Describe(loser, winner)));
        }

        // --- 4. Hysteresis. Changing profile costs a reconnect, so a challenger has to be
        //        worth one.
        if (incumbentId is { Length: > 0 } && !string.Equals(incumbentId, winner.StrategyId, StringComparison.Ordinal))
        {
            var incumbent = eligible.FirstOrDefault(candidate =>
                string.Equals(candidate.StrategyId, incumbentId, StringComparison.Ordinal));

            if (incumbent is not null)
            {
                var gain = (incumbent.MedianConnectMs ?? double.MaxValue) - (winner.MedianConnectMs ?? double.MaxValue);
                var relative = incumbent.MedianConnectMs is > 0
                    ? gain / incumbent.MedianConnectMs.Value
                    : 1d;

                if (gain < limits.MinimumImprovementMs || relative < limits.MinimumImprovementRatio)
                {
                    return Keep(
                        incumbentId,
                        $"Mevcut profil korunuyor: aday yalnızca {gain:F1} ms daha hızlı ve bu fark "
                            + "ölçüm belirsizliğinin içinde kalıyor.",
                        confidence,
                        measuredAt,
                        rejected);
                }

                if (incumbentSince is { } since && now - since < limits.MinimumDwell)
                {
                    var remaining = limits.MinimumDwell - (now - since);
                    return Keep(
                        incumbentId,
                        $"Mevcut profil çalışıyor ve {remaining.TotalMinutes:F0} dakika daha korunacak; "
                            + "yakın sonuçlar arasında sürekli geçiş yapılmıyor.",
                        confidence,
                        measuredAt,
                        rejected);
                }
            }
        }

        var changed = !string.Equals(incumbentId, winner.StrategyId, StringComparison.Ordinal);

        return new StrategySelection(
            winner.StrategyId,
            Rationale(winner, confidence, targets, changed),
            confidence,
            changed,
            measuredAt,
            rejected);
    }

    /// <summary>The sentence shown beside the active profile.</summary>
    private static string Rationale(
        StrategyMeasurement winner,
        SelectionConfidence confidence,
        IReadOnlyList<ProbeTarget> targets,
        bool changed)
    {
        var required = StrategyTargets.RequiredCount(targets);
        var latency = winner.MedianConnectMs is { } connect ? $"{connect:F0} ms bağlanma" : "ölçülemedi";
        var jitter = winner.JitterMs is { } jitterMs ? $", ±{jitterMs:F0} ms dalgalanma" : string.Empty;

        var speed = confidence == SelectionConfidence.WithThroughput && winner.Throughput is { } throughput
            ? $", {throughput.DownlinkMbps:F1} Mb/sn sürdürülebilir indirme"
            : ", hız testi yapılmadı";

        var verb = changed ? "seçildi" : "korundu";

        return $"{winner.StrategyName} {verb}: {required} gerekli hedefin tamamına ulaştı, "
            + $"{winner.Successes}/{winner.Attempts} deneme başarılı ({latency}{jitter}{speed}).";
    }

    private static string Describe(StrategyMeasurement loser, StrategyMeasurement winner)
    {
        if (loser.MedianConnectMs is not { } theirs || winner.MedianConnectMs is not { } ours)
        {
            return "Karşılaştırılabilir ölçüm elde edilemedi.";
        }

        var difference = theirs - ours;

        return difference > 0.05
            ? $"{difference:F0} ms daha yavaş bağlanıyor."
            : $"Gecikmesi eşit; dalgalanması daha yüksek (±{loser.JitterMs ?? 0:F0} ms).";
    }

    private static StrategySelection Keep(
        string? incumbentId,
        string reason,
        SelectionConfidence confidence,
        DateTimeOffset measuredAt,
        IReadOnlyList<StrategyRejection> rejected)
        => new(incumbentId, reason, confidence, Changed: false, measuredAt, rejected);
}
