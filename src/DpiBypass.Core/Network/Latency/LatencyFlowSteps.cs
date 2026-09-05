namespace DpiBypass.Core.Network.Latency;

/// <summary>Where one step of the measurement flow has got to.</summary>
public enum LatencyFlowStepState
{
    /// <summary>Not reached yet.</summary>
    Pending = 0,

    /// <summary>Happening now.</summary>
    Running = 1,

    /// <summary>Finished, with a result.</summary>
    Done = 2,

    /// <summary>Deliberately not attempted, with a reason.</summary>
    Skipped = 3,

    /// <summary>Attempted and could not be completed.</summary>
    Failed = 4,
}

/// <summary>
/// One row of the flow the card shows while it works.
/// </summary>
/// <remarks>
/// The detail is never a fabricated number. A step nothing has been measured for says
/// "ölçülmedi", and one that was skipped says why - both of which are answers, unlike a
/// blank row or a zero.
/// </remarks>
public sealed record LatencyFlowStep(int Ordinal, string Title, LatencyFlowStepState State, string Detail)
{
    /// <summary>A short marker for a screen reader and for a text-only rendering.</summary>
    /// <remarks>
    /// The card colours these rows, and colour must never be the only thing carrying the
    /// state - so each row also has a word for what it is.
    /// </remarks>
    public string StateLabel => State switch
    {
        LatencyFlowStepState.Running => "sürüyor",
        LatencyFlowStepState.Done => "tamamlandı",
        LatencyFlowStepState.Skipped => "atlandı",
        LatencyFlowStepState.Failed => "tamamlanamadı",
        _ => "bekliyor",
    };

    /// <summary>"ok" / "warn" / "error" / "" for the card, alongside the label above.</summary>
    public string Severity => State switch
    {
        LatencyFlowStepState.Done => "ok",
        LatencyFlowStepState.Failed => "error",
        LatencyFlowStepState.Skipped => "warn",
        _ => string.Empty,
    };
}

/// <summary>
/// Turns the latency status into the ordered steps a user can follow.
/// </summary>
/// <remarks>
/// <para>
/// The card already said what state the run was in; what it could not say was where in
/// the process that state sat. "Bağlantı ölçülüyor" for two minutes is indistinguishable
/// from a stuck run, and a result that arrives without the user having seen a baseline
/// being taken reads as an assertion rather than as a measurement.
/// </para>
/// <para>
/// Derived from the status rather than reported by the run, so it cannot drift out of
/// step with what actually happened: every row is a statement about evidence that either
/// exists in the result or does not.
/// </para>
/// </remarks>
public static class LatencyFlowSteps
{
    private const string NotMeasured = "ölçülmedi";

    /// <summary>The six steps, in the order they happen.</summary>
    public static IReadOnlyList<LatencyFlowStep> From(LatencyStatusView status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return
        [
            Target(status),
            Reachability(status),
            Baseline(status),
            Candidates(status),
            Comparison(status),
            Outcome(status),
        ];
    }

    private static LatencyFlowStep Target(LatencyStatusView status)
    {
        if (string.IsNullOrWhiteSpace(status.Target))
        {
            return new(1, "Hedef seçimi", LatencyFlowStepState.Pending, NotMeasured);
        }

        var detail = string.IsNullOrWhiteSpace(status.Protocol)
            ? status.Target
            : $"{status.Target} · {status.Protocol}";

        // The route-reference case is the one that must never be quietly folded in: the
        // number describes the path, not the game's own round trip, and a comparison
        // across the two would be comparing different things.
        return new(
            1,
            "Hedef seçimi",
            LatencyFlowStepState.Done,
            status.RouteReferenceOnly ? $"{detail} (yol referansı)" : detail);
    }

    private static LatencyFlowStep Reachability(LatencyStatusView status)
    {
        if (status.Situation == LatencySituation.Offline || status.State == LatencyModeState.Offline)
        {
            return new(2, "Bağlantı doğrulaması", LatencyFlowStepState.Failed, "ölçülebilir bir bağlantı yok");
        }

        if (status.State == LatencyModeState.UnsupportedAdapter)
        {
            return new(
                2,
                "Bağlantı doğrulaması",
                LatencyFlowStepState.Skipped,
                "bu bağdaştırıcıda desteklenen ayar yok");
        }

        if (Reading(status) is { } reading)
        {
            return new(
                2,
                "Bağlantı doğrulaması",
                LatencyFlowStepState.Done,
                $"{reading.RemoteReplies}/{reading.RemoteAttempts} yanıt · {status.AdapterName}");
        }

        return status.Situation == LatencySituation.Working
            ? new(2, "Bağlantı doğrulaması", LatencyFlowStepState.Running, "hedefe erişim deneniyor")
            : new(2, "Bağlantı doğrulaması", LatencyFlowStepState.Pending, NotMeasured);
    }

    private static LatencyFlowStep Baseline(LatencyStatusView status)
    {
        if (Reading(status) is { } baseline)
        {
            return new(3, "Başlangıç ölçümü", LatencyFlowStepState.Done, Describe(baseline));
        }

        if (status.State is LatencyModeState.Measuring or LatencyModeState.QuickTesting)
        {
            return new(3, "Başlangıç ölçümü", LatencyFlowStepState.Running, "örnekler toplanıyor");
        }

        return status.Situation == LatencySituation.Cancelled
            ? new(3, "Başlangıç ölçümü", LatencyFlowStepState.Skipped, "kullanıcı durdurdu")
            : new(3, "Başlangıç ölçümü", LatencyFlowStepState.Pending, NotMeasured);
    }

    private static LatencyFlowStep Candidates(LatencyStatusView status)
    {
        if (status.State == LatencyModeState.UnsupportedAdapter)
        {
            return new(4, "Aday denemeleri", LatencyFlowStepState.Skipped, "denenebilecek ayar yok");
        }

        if (status.State is LatencyModeState.DeepTesting or LatencyModeState.QuickTesting)
        {
            return new(4, "Aday denemeleri", LatencyFlowStepState.Running, "adaylar ölçülüyor");
        }

        var measured = status.Rejected.Count(rejection => rejection.WasMeasured);

        // A candidate an obstacle blocked is a separate row from one that was measured and
        // lost. Reporting the two together is what turned "you have not allowed adapter
        // restarts" into "there is nothing here to gain" - the same sentence for a thing
        // the user can fix in one click and a thing they cannot fix at all.
        var blocked = status.Rejected.Count - measured;

        if (status.Applied.Count > 0)
        {
            return new(
                4,
                "Aday denemeleri",
                LatencyFlowStepState.Done,
                $"{status.Applied.Count} uygulandı, {measured} ölçüldü ve elendi"
                    + (blocked > 0 ? $", {blocked} şimdilik denenemedi" : string.Empty));
        }

        if (status.Rejected.Count > 0)
        {
            return blocked > 0 && measured == 0
                ? new(4, "Aday denemeleri", LatencyFlowStepState.Skipped, $"{blocked} aday şimdilik denenemedi")
                : new(
                    4,
                    "Aday denemeleri",
                    LatencyFlowStepState.Done,
                    $"{measured} ölçüldü ve elendi"
                        + (blocked > 0 ? $", {blocked} şimdilik denenemedi" : string.Empty));
        }

        return new(4, "Aday denemeleri", LatencyFlowStepState.Pending, NotMeasured);
    }

    private static LatencyFlowStep Comparison(LatencyStatusView status)
    {
        if (status.Improvement is { } improvement)
        {
            // The metric that moved is named, so a median gain cannot be read into a run
            // that only steadied the delay variation.
            return new(
                5,
                "Karşılaştırma",
                LatencyFlowStepState.Done,
                $"{status.ImprovedMetric ?? "ortanca"} · "
                    + FormattableString.Invariant($"ortanca {improvement.MedianMs:F1} ms, ")
                    + FormattableString.Invariant($"p99 {improvement.P99Ms:F1} ms, ")
                    + FormattableString.Invariant($"dalgalanma {improvement.JitterMs:F1} ms"));
        }

        return status.Situation switch
        {
            LatencySituation.NoDifference => new(5, "Karşılaştırma", LatencyFlowStepState.Done, "anlamlı fark yok"),
            LatencySituation.RolledBack => new(5, "Karşılaştırma", LatencyFlowStepState.Done, "geriledi"),
            LatencySituation.Incomplete => new(
                5,
                "Karşılaştırma",
                LatencyFlowStepState.Failed,
                "karşılaştırma tamamlanamadı"),
            LatencySituation.Working => new(5, "Karşılaştırma", LatencyFlowStepState.Pending, "denemeler sürüyor"),
            _ => new(5, "Karşılaştırma", LatencyFlowStepState.Pending, NotMeasured),
        };
    }

    private static LatencyFlowStep Outcome(LatencyStatusView status) => status.Situation switch
    {
        LatencySituation.VerifiedGain or LatencySituation.LoadedGainOnly => new(
            6,
            "Kabul / geri alma",
            LatencyFlowStepState.Done,
            $"{status.Applied.Count} ayar uygulandı ve yerinde"),
        LatencySituation.RolledBack => new(6, "Kabul / geri alma", LatencyFlowStepState.Done, "geri alındı"),
        LatencySituation.NoDifference => new(
            6,
            "Kabul / geri alma",
            LatencyFlowStepState.Done,
            "değişiklik saklanmadı"),
        LatencySituation.RestoreFailed => new(
            6,
            "Kabul / geri alma",
            LatencyFlowStepState.Failed,
            "geri alınamadı; kurtarma gerekiyor"),
        LatencySituation.Cancelled => new(
            6,
            "Kabul / geri alma",
            LatencyFlowStepState.Skipped,
            "durduruldu; ayarlar değiştirilmedi"),
        LatencySituation.Working => new(6, "Kabul / geri alma", LatencyFlowStepState.Pending, "sonuç bekleniyor"),
        _ => new(6, "Kabul / geri alma", LatencyFlowStepState.Pending, NotMeasured),
    };

    /// <summary>The idle reading a run actually took, whichever field carries it.</summary>
    private static LatencyMeasurement? Reading(LatencyStatusView status)
        => status.IdleBefore ?? status.Idle ?? status.UploadLoaded ?? status.DownloadLoaded;

    private static string Describe(LatencyMeasurement measurement)
    {
        // Loss is printed only when the instrument measured it. A passive series counts
        // observations rather than attempts, so writing a zero there would be inventing
        // a result out of the absence of one.
        var loss = measurement.PacketLossPercent is { } percent
            ? FormattableString.Invariant($", kayıp {percent:F1}%")
            : ", kayıp ölçülmedi";

        return FormattableString.Invariant($"ortanca {measurement.MedianRttMs:F1} ms, ")
            + FormattableString.Invariant($"p99 {measurement.P99RttMs:F1} ms, ")
            + FormattableString.Invariant($"dalgalanma {measurement.JitterMs:F1} ms{loss}")
            + FormattableString.Invariant($" · {measurement.RemoteReplies} örnek");
    }
}
