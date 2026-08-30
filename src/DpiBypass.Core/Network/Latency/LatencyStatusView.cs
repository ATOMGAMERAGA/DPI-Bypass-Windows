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

/// <summary>One change that was measured and turned down, with the reason it was.</summary>
public sealed record LatencyRejection(string Change, string Reason);

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

    public LatencyMeasurement? Idle { get; init; }

    public LatencyMeasurement? UploadLoaded { get; init; }

    public LatencyMeasurement? DownloadLoaded { get; init; }

    public LatencyPathAnalysis? Path { get; init; }

    public IReadOnlyList<string> Applied { get; init; } = [];

    public IReadOnlyList<LatencyRejection> Rejected { get; init; } = [];

    public IReadOnlyList<string> Notices { get; init; } = [];

    public TrafficGuardState? TrafficGuard { get; init; }

    public LatencyDelta? Improvement { get; init; }

    public bool IsBusy => State is LatencyModeState.Measuring
        or LatencyModeState.QuickTesting
        or LatencyModeState.DeepTesting;

    /// <summary>Whether the switch should read as on, whatever the outcome was.</summary>
    public bool ModeEnabled { get; init; }

    public static LatencyStatusView From(bool modeEnabled, LatencyOptimizationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var state = MapState(modeEnabled, result);

        return new LatencyStatusView
        {
            State = state,
            ModeEnabled = modeEnabled,
            Headline = DescribeHeadline(state, result),
            Severity = DescribeSeverity(state),
            Detail = result.StatusLine,
            Target = result.TargetLabel,
            Protocol = result.TargetProtocol,
            RouteReferenceOnly = result.RouteReferenceOnly,
            AdapterName = result.AdapterName,
            Idle = result.After ?? result.Before,
            UploadLoaded = result.UploadLoaded,
            DownloadLoaded = result.DownloadLoaded,
            Path = result.Path,
            Applied = result.AppliedChanges,
            Rejected = [.. result.Verdicts.Where(verdict => !verdict.Accepted)
                .Select(verdict => new LatencyRejection(verdict.Description, verdict.Reason))],
            Notices = result.Notices,
            TrafficGuard = result.TrafficGuard,
            Improvement = result.VerifiedImprovement,
        };
    }

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

    /// <summary>The reason a change was taken back off, when one was.</summary>
    private static string? Rolled(LatencyOptimizationResult result)
    {
        var rolled = result.Verdicts.FirstOrDefault(verdict =>
            !verdict.Accepted
            && (verdict.Reason.Contains("paket kaybı", StringComparison.OrdinalIgnoreCase)
                || verdict.Reason.Contains("kötüleşti", StringComparison.OrdinalIgnoreCase)
                || verdict.Reason.Contains("bağlantı koptu", StringComparison.OrdinalIgnoreCase)));

        return rolled?.Reason;
    }

    /// <summary>How much of the delay is beyond the operator, when the split is known.</summary>
    private static double? WanShare(LatencyOptimizationResult result)
        => result.Path is { Bottleneck: LatencyBottleneck.WanRoute, RemotePathMs: { } remote } && remote > 0
            ? remote
            : null;

    private static string DescribeSeverity(LatencyModeState state) => state switch
    {
        LatencyModeState.GainApplied or LatencyModeState.TrafficGuardActive => "ok",
        LatencyModeState.Off => "off",
        LatencyModeState.Failed or LatencyModeState.Offline or LatencyModeState.AwaitingRestore => "warn",
        _ => "info",
    };

    /// <summary>The stable machine-readable form, for scripts and support requests.</summary>
    public string ToJson()
    {
        var json = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["state"] = State.ToString(),
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
            ["uploadLoaded"] = Measurement(UploadLoaded),
            ["downloadLoaded"] = Measurement(DownloadLoaded),
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
            })]),
            ["notices"] = new JsonArray([.. Notices.Select(entry => (JsonNode)JsonValue.Create(entry)!)]),
            ["improvement"] = Improvement is null
                ? null
                : new JsonObject
                {
                    ["medianMs"] = Round(Improvement.MedianMs),
                    ["p95Ms"] = Round(Improvement.P95Ms),
                    ["p99Ms"] = Round(Improvement.P99Ms),
                    ["jitterMs"] = Round(Improvement.JitterMs),
                    ["lossPercent"] = Round(Improvement.LossPercent),
                },
            ["trafficGuard"] = TrafficGuard is null
                ? null
                : new JsonObject
                {
                    ["status"] = TrafficGuard.Status.ToString(),
                    ["summary"] = TrafficGuard.Summary,
                    ["policyName"] = TrafficGuard.PolicyName,
                    ["throttleBitsPerSecond"] = TrafficGuard.ThrottleBitsPerSecond,
                    ["application"] = TrafficGuard.ThrottledApplication,
                    ["uploadQueueingBeforeMs"] = Round(TrafficGuard.UploadQueueingBeforeMs),
                    ["uploadQueueingAfterMs"] = Round(TrafficGuard.UploadQueueingAfterMs),
                    ["conflicts"] = new JsonArray(
                        [.. TrafficGuard.Conflicts.Select(entry => (JsonNode)JsonValue.Create(entry)!)]),
                },
        };

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject? Measurement(LatencyMeasurement? measurement) => measurement is null
        ? null
        : new JsonObject
        {
            ["endpoint"] = measurement.RemoteEndpoint,
            ["protocol"] = measurement.Protocol,
            ["replies"] = measurement.RemoteReplies,
            ["attempts"] = measurement.RemoteAttempts,
            ["minimumMs"] = Round(measurement.MinimumRttMs),
            ["medianMs"] = Round(measurement.MedianRttMs),
            ["p95Ms"] = Round(measurement.P95RttMs),

            // Reported, but null when the sample is too small for the name to mean
            // anything: a p99 from forty replies is the worst sample, not a percentile.
            ["p99Ms"] = measurement.RemoteReplies >= 100 ? Round(measurement.P99RttMs) : null,
            ["jitterMs"] = Round(measurement.JitterMs),
            ["packetLossPercent"] = Round(measurement.PacketLossPercent),
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
        }.Where(part => part is not null));
}
