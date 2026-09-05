using DpiBypass.Core.Network;
using DpiBypass.Core.Vodafone;

namespace DpiBypass.Core.MobileHotspot;

/// <summary>
/// What one check found, at the level of detail a user acts on.
/// </summary>
/// <remarks>
/// Five outcomes rather than a pass/fail pair, because collapsing them is how a working
/// connection ends up looking broken. "IPv6 is not configured on this network" is normal
/// on most mobile links; "IPv6 is configured and traffic does not pass" is a real fault;
/// "we did not measure it" is neither. Painting all three red tells the user to go and
/// fix something that is not wrong.
/// </remarks>
public enum HotspotCheckState
{
    /// <summary>Checked and working.</summary>
    Ok = 0,

    /// <summary>Working, with something worth knowing about.</summary>
    Warning = 1,

    /// <summary>Checked and not working. This is the only failure.</summary>
    Failed = 2,

    /// <summary>Present but deliberately not in use here. Not a fault.</summary>
    NotUsed = 3,

    /// <summary>Not measured on this pass.</summary>
    NotMeasured = 4,

    /// <summary>Nothing here can answer the question. Not a fault either.</summary>
    NotSupported = 5,
}

/// <summary>One line of the result, as a value rather than as a line of a text report.</summary>
public sealed record HotspotCheckCard
{
    public required string Title { get; init; }

    public required HotspotCheckState State { get; init; }

    /// <summary>The short answer: "çalışıyor", "42 ms", "Bilinmiyor".</summary>
    public required string Value { get; init; }

    /// <summary>One sentence of context, when there is one worth giving.</summary>
    public string? Detail { get; init; }
}

/// <summary>Whether a check has been run on the network we are looking at.</summary>
public enum HotspotRunState
{
    /// <summary>Never run here.</summary>
    NotRun = 0,

    /// <summary>Running now.</summary>
    Running = 1,

    /// <summary>Finished, and the result below is from this network.</summary>
    Completed = 2,

    /// <summary>Started and could not finish.</summary>
    Failed = 3,
}

/// <summary>
/// Everything the Vodafone Sınırsız Modu card shows, as structured values.
/// </summary>
/// <remarks>
/// Exists so the card never renders <see cref="HotspotDiagnosticResult.ToReport"/> into
/// its main area and never reads that report back to work out what happened. The report
/// is written for a person to read in a support thread; deriving the UI from it means
/// improving a sentence changes what the interface believes.
/// </remarks>
public sealed record HotspotStatusView
{
    public required bool ModeEnabled { get; init; }

    /// <summary>Whether moving to a remembered network runs the checks by itself.</summary>
    public required bool AutoCheckEnabled { get; init; }

    /// <summary>Whether the network we are on now is one of the remembered ones.</summary>
    public required bool RegisteredHere { get; init; }

    public required int RegisteredNetworks { get; init; }

    public required string NetworkName { get; init; }

    public required string AdapterName { get; init; }

    public required HotspotRunState Run { get; init; }

    /// <summary>When the shown result was measured, when there is one.</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>The short summary cards: internet, DNS, connection quality.</summary>
    public IReadOnlyList<HotspotCheckCard> Cards { get; init; } = [];

    /// <summary>Addresses, MTU, adapter, VPN: the "Teknik ayrıntılar" section.</summary>
    public IReadOnlyList<HotspotCheckCard> TechnicalDetails { get; init; } = [];

    /// <summary>One line saying what the mode is doing on this network.</summary>
    public required string Headline { get; init; }

    /// <summary>Whether the TTL rewrite is installed on the adapter under us.</summary>
    /// <remarks>
    /// The card distinguishes this from <see cref="ModeEnabled"/> on purpose. A switch
    /// that is on while nothing is running is the exact state this feature spent a
    /// release in, and the only way a user can tell the difference is if the interface
    /// says so.
    /// </remarks>
    public required bool RewriteActive { get; init; }

    /// <summary>The TTL outgoing packets are being rewritten to.</summary>
    public required int TtlValue { get; init; }

    /// <summary>Packets the rule has actually changed.</summary>
    public long RewrittenPackets { get; init; }

    /// <summary>Outbound IPv6 packets dropped on the shared adapter.</summary>
    public long DroppedIPv6Packets { get; init; }

    /// <summary>One line about the rewrite itself, shown whether or not a check has run.</summary>
    public required string RewriteLine { get; init; }

    /// <summary>Colour for <see cref="RewriteLine"/>: same vocabulary as <see cref="Severity"/>.</summary>
    public required string RewriteSeverity { get; init; }

    /// <summary>"off", "ok", "warn", "attention" or "info": colour only.</summary>
    public required string Severity { get; init; }

    /// <summary>The single suggested next step, when there is one.</summary>
    public string? Suggestion { get; init; }

    /// <summary>The findings the diagnostics itself produced, in its own words.</summary>
    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>The full human-readable report, for the details section and support.</summary>
    public string Report { get; init; } = string.Empty;

    /// <summary>
    /// Whether a previous build actually left something behind that needs removing.
    /// </summary>
    /// <remarks>
    /// The cleanup entry point stays available, but it only earns a place in the card
    /// when there is something to clean. Offering "remove the old TTL sub-feature" to
    /// every user on a clean install is a developer's migration note wearing a button.
    /// </remarks>
    public required bool LegacyCleanupAvailable { get; init; }

    /// <summary>Whether the current network can be added to the remembered list.</summary>
    public bool CanRememberThisNetwork => !RegisteredHere && !string.IsNullOrWhiteSpace(NetworkName);

    /// <summary>
    /// The state before anything has been read: off, unknown network, nothing measured.
    /// </summary>
    /// <remarks>
    /// Exists so a consumer can hold a non-null instance from construction and never has
    /// to null-check the whole card while it is still being wired up.
    /// </remarks>
    public static readonly HotspotStatusView Empty = new()
    {
        ModeEnabled = false,
        AutoCheckEnabled = false,
        RegisteredHere = false,
        RegisteredNetworks = 0,
        NetworkName = string.Empty,
        AdapterName = string.Empty,
        Run = HotspotRunState.NotRun,
        Headline = "Kapalı",
        Severity = "off",
        RewriteActive = false,
        TtlValue = TtlFixSettings.DefaultTimeToLive,
        RewriteLine = "TTL düzeltmesi kapalı.",
        RewriteSeverity = "off",
        LegacyCleanupAvailable = false,
    };

    public static HotspotStatusView From(
        HotspotStatus status,
        bool busy = false,
        string? failure = null,
        bool legacyResidue = false)
    {
        ArgumentNullException.ThrowIfNull(status);

        var result = status.LastResult;
        var run = (busy, failure, result) switch
        {
            (true, _, _) => HotspotRunState.Running,
            (_, not null, _) => HotspotRunState.Failed,
            (_, _, not null) => HotspotRunState.Completed,
            _ => HotspotRunState.NotRun,
        };

        return new HotspotStatusView
        {
            ModeEnabled = status.VodafoneModeEnabled,
            AutoCheckEnabled = status.DiagnosticsEnabled,
            RegisteredHere = status.RegisteredHere,
            RegisteredNetworks = status.RegisteredNetworks,
            NetworkName = status.NetworkName,
            AdapterName = status.AdapterName,
            Run = run,
            CheckedAt = run == HotspotRunState.Completed ? result!.CompletedAt : null,
            Cards = run == HotspotRunState.Completed ? BuildCards(result!) : [],

            // The rewrite row leads the technical details whether or not a check has run:
            // it is the one line that answers "is this doing anything", and it is
            // available the moment the mode is switched on.
            TechnicalDetails = run == HotspotRunState.Completed
                ? [RewriteCard(status), .. BuildDetails(result!)]
                : [RewriteCard(status)],
            Findings = result?.Findings ?? [],
            Report = result?.ToReport() ?? string.Empty,
            Headline = DescribeHeadline(status, run, failure),
            Severity = DescribeSeverity(status, run, result),
            Suggestion = DescribeSuggestion(status, run, result, failure),
            RewriteActive = status.TtlActive,
            TtlValue = status.TtlValue,
            RewrittenPackets = status.RewrittenPackets,
            DroppedIPv6Packets = status.DroppedIPv6Packets,
            RewriteLine = DescribeRewrite(status),
            RewriteSeverity = DescribeRewriteSeverity(status),
            LegacyCleanupAvailable = legacyResidue,
        };
    }

    private static IReadOnlyList<HotspotCheckCard> BuildCards(HotspotDiagnosticResult result) =>
    [
        new()
        {
            Title = "İnternet erişimi",
            State = result.HasInternet ? HotspotCheckState.Ok : HotspotCheckState.Failed,
            Value = result.HasInternet ? "Çalışıyor" : "Trafik geçmiyor",
            Detail = result.HasInternet
                ? null
                : "Adres alınmış olsa bile paketler karşı tarafa ulaşmıyor.",
        },
        new()
        {
            Title = "Ad çözümleme (DNS)",
            State = result.DnsWorks ? HotspotCheckState.Ok : HotspotCheckState.Failed,
            Value = result.DnsWorks ? "Çalışıyor" : "Ad çözülemiyor",
            Detail = result.DnsWorks ? null : "Site adları IP adresine çevrilemiyor.",
        },
        QualityCard(result),
        new()
        {
            Title = "Plan / hotspot hakkı",

            // Nothing here can establish what a subscription includes, so this is not a
            // check that failed - it is a question this application does not answer.
            State = HotspotCheckState.NotSupported,
            Value = HotspotDiagnosticResult.PlanEntitlement,
            Detail = "Tarife ve hotspot hakkı ölçümle belirlenemez; bu uygulama tahmin yürütmez.",
        },
    ];

    /// <summary>Latency, jitter and loss as one card, or an honest "not measured".</summary>
    private static HotspotCheckCard QualityCard(HotspotDiagnosticResult result)
    {
        if (result.MedianRttMs is not { } median)
        {
            return new HotspotCheckCard
            {
                Title = "Bağlantı kalitesi",
                State = HotspotCheckState.NotMeasured,
                Value = "Ölçülemedi",
                Detail = "Hedef yanıt vermediği için gecikme ölçülemedi.",
            };
        }

        // Loss is only shown when something counted packets. A passive instrument reports
        // null, and printing that as "%0 kayıp" would be a number nobody measured.
        var loss = result.PacketLossPercent is { } value
            ? $" · kayıp %{value:F1}"
            : " · kayıp ölçülmedi";

        var state = (median, result.PacketLossPercent) switch
        {
            (> 250, _) => HotspotCheckState.Warning,
            (_, > 5) => HotspotCheckState.Warning,
            _ => HotspotCheckState.Ok,
        };

        return new HotspotCheckCard
        {
            Title = "Bağlantı kalitesi",
            State = state,
            Value = $"{median:F0} ms{loss}",
            Detail = result.P95RttMs is { } p95 ? $"En kötü %5 dilim: {p95:F0} ms." : null,
        };
    }

    private static IReadOnlyList<HotspotCheckCard> BuildDetails(HotspotDiagnosticResult result)
    {
        var details = new List<HotspotCheckCard>
        {
            AddressCard("IPv4", result.HasIpv4, result.Ipv4Works),
            AddressCard("IPv6", result.HasIpv6, result.Ipv6Works),
            new()
            {
                Title = "Adres türü",
                State = HotspotCheckState.Ok,
                Value = DescribeAddressKind(result.AddressKind),
            },
            new()
            {
                Title = "Bağdaştırıcı",
                State = HotspotCheckState.Ok,
                Value = result.AdapterName,
            },
            new()
            {
                Title = "VPN / tünel",

                // Detection is best effort in both directions, so neither answer is a
                // fault and neither is presented as one.
                State = result.VpnAdapterActive ? HotspotCheckState.Warning : HotspotCheckState.Ok,
                Value = result.VpnAdapterActive ? "Etkin olabilir" : "Saptanmadı",
                Detail = "Tespit en iyi çabadır; kesin değildir.",
            },
            new()
            {
                Title = "Operatör",
                State = result.CarrierHint is null ? HotspotCheckState.NotSupported : HotspotCheckState.Ok,
                Value = result.CarrierHint ?? "Bilinmiyor",
                Detail = result.CarrierHint is null
                    ? "Windows bu bağlantı için operatör adı bildirmiyor."
                    : null,
            },
        };

        details.Add(result.LargestUnfragmentedPayload is { } payload
            ? new HotspotCheckCard
            {
                Title = "MTU",
                State = result.MtuLooksReduced == true ? HotspotCheckState.Warning : HotspotCheckState.Ok,
                Value = $"{payload + 28} bayt",
                Detail = result.MtuLooksReduced == true
                    ? "1500'ün altında; bazı bağlantılarda parçalanma sorunları görülebilir."
                    : null,
            }
            : new HotspotCheckCard
            {
                Title = "MTU",
                State = HotspotCheckState.NotMeasured,
                Value = "Ölçülemedi",
            });

        return details;
    }

    /// <summary>
    /// An address family's state, where "not configured" is not a failure.
    /// </summary>
    private static HotspotCheckCard AddressCard(string title, bool configured, bool works) => new()
    {
        Title = title,
        State = (configured, works) switch
        {
            (false, _) => HotspotCheckState.NotUsed,
            (true, true) => HotspotCheckState.Ok,
            _ => HotspotCheckState.Failed,
        },
        Value = (configured, works) switch
        {
            (false, _) => "Bu ağda kullanılmıyor",
            (true, true) => "Çalışıyor",
            _ => "Adres var, trafik geçmiyor",
        },
        Detail = configured || title != "IPv6"
            ? null
            : "Mobil bağlantıların çoğunda normaldir.",
    };

    private static string DescribeAddressKind(HotspotAddressKind kind) => kind switch
    {
        HotspotAddressKind.Public => "Genel IP",
        HotspotAddressKind.Private => "Özel aralık (NAT arkasında)",
        HotspotAddressKind.SharedAddressSpace => "Paylaşılan adres alanı (100.64/10)",
        HotspotAddressKind.Mixed => "Birden çok IPv4 adres sınıfı",
        _ => "Belirlenemedi",
    };

    /// <summary>
    /// The one line the card leads with, and the one word the user is looking for.
    /// </summary>
    /// <remarks>
    /// "Aktif" is reserved for the state the feature exists to reach: the mode is on and
    /// this is one of the networks the user registered. It used to be unreachable in
    /// practice - the card only ever read "Açık · … kayıtlı değil", because the network
    /// identity it compared against was never filled in unless the engine was running -
    /// so the mode looked as though it had done nothing until every button on the card
    /// had been pressed by hand.
    /// </remarks>
    private static string DescribeHeadline(HotspotStatus status, HotspotRunState run, string? failure)
    {
        if (!status.VodafoneModeEnabled)
        {
            return "Kapalı";
        }

        var name = string.IsNullOrWhiteSpace(status.NetworkName) ? "bilinmeyen ağ" : status.NetworkName;
        var lead = (status.RegisteredHere, status.TtlActive) switch
        {
            // "Aktif" means the rewrite is running. It is not a synonym for the switch
            // being on: a rule that failed to install - no administrator rights, no
            // driver - leaves the user with a mode that says it is working and is not.
            (true, true) => $"Aktif · {name} · TTL {status.TtlValue}",
            (true, false) when status.TtlFailure is { } reason => $"Açık · {name} · TTL kuralı kurulamadı ({reason})",
            (true, false) => $"Açık · {name} · TTL kuralı kurulmadı",
            _ => $"Açık · {name} kayıtlı değil",
        };

        return run switch
        {
            HotspotRunState.Running => $"{lead} · kontrol ediliyor…",
            HotspotRunState.Failed => $"{lead} · kontrol tamamlanamadı ({failure})",
            HotspotRunState.Completed => $"{lead} · kontrol tamamlandı",
            _ when status.RegisteredHere => $"{lead} · henüz kontrol edilmedi",
            _ => lead,
        };
    }

    /// <summary>The rewrite in one sentence, with the counter that proves it.</summary>
    /// <remarks>
    /// The packet count is the difference between "the rule is installed" and "the rule
    /// is doing something". An installed rule that has rewritten nothing means the wrong
    /// adapter or no traffic, and both are worth seeing rather than inferring.
    /// </remarks>
    private static string DescribeRewrite(HotspotStatus status)
    {
        if (!status.VodafoneModeEnabled)
        {
            return "TTL düzeltmesi kapalı.";
        }

        if (!status.RegisteredHere)
        {
            return "Bu ağ kayıtlı değil; TTL düzeltmesi yalnızca kaydettiğiniz ağlarda çalışır.";
        }

        if (!status.TtlActive)
        {
            return status.TtlFailure is { } reason
                ? $"TTL kuralı kurulamadı: {reason}"
                : "TTL kuralı henüz kurulmadı.";
        }

        var ipv6 = status.DroppedIPv6Packets > 0
            ? $" · {status.DroppedIPv6Packets:N0} IPv6 paketi düşürüldü"
            : string.Empty;

        return $"Giden paketler TTL {status.TtlValue} ile yollanıyor · "
            + $"{status.RewrittenPackets:N0} paket düzeltildi{ipv6}";
    }

    private static string DescribeRewriteSeverity(HotspotStatus status) => status switch
    {
        { VodafoneModeEnabled: false } => "off",
        { RegisteredHere: false } => "info",
        { TtlActive: true } => "ok",
        { TtlFailure: not null } => "attention",
        _ => "warn",
    };

    /// <summary>The rewrite as a details row, so the state is readable as a value too.</summary>
    private static HotspotCheckCard RewriteCard(HotspotStatus status) => new()
    {
        Title = "TTL düzeltmesi",
        State = status switch
        {
            { VodafoneModeEnabled: false } => HotspotCheckState.NotUsed,
            { RegisteredHere: false } => HotspotCheckState.NotUsed,
            { TtlActive: true } => HotspotCheckState.Ok,
            _ => HotspotCheckState.Failed,
        },
        Value = status switch
        {
            { VodafoneModeEnabled: false } => "Kapalı",
            { RegisteredHere: false } => "Bu ağda kullanılmıyor",
            { TtlActive: true } => $"TTL {status.TtlValue} · {status.RewrittenPackets:N0} paket",
            _ => "Kurulamadı",
        },
        Detail = status is { VodafoneModeEnabled: true, RegisteredHere: true, TtlActive: true }
            ? $"Telefon bir düşürdüğünde operatöre {status.TtlValue - 1} gider."
            : status.TtlFailure,
    };

    private static string DescribeSeverity(
        HotspotStatus status,
        HotspotRunState run,
        HotspotDiagnosticResult? result)
    {
        if (!status.VodafoneModeEnabled)
        {
            return "off";
        }

        // Switched on for this network and not actually rewriting anything outranks
        // whatever the connection checks found: the feature is not doing its job, and
        // a green card over a dead rule is the fault this release is fixing.
        if (status is { RegisteredHere: true, TtlActive: false })
        {
            return status.TtlFailure is null ? "warn" : "attention";
        }

        return (run, result) switch
        {
            (HotspotRunState.Running, _) => "info",
            (HotspotRunState.Failed, _) => "warn",
            (HotspotRunState.Completed, { HasInternet: false }) => "warn",
            (HotspotRunState.Completed, { DnsWorks: false }) => "warn",
            (HotspotRunState.Completed, _) => "ok",

            // On, registered and rewriting: the mode is doing its job even before the
            // first check has produced numbers.
            (HotspotRunState.NotRun, _) when status.RegisteredHere => "ok",
            _ => "info",
        };
    }

    private static string? DescribeSuggestion(
        HotspotStatus status,
        HotspotRunState run,
        HotspotDiagnosticResult? result,
        string? failure)
    {
        if (!status.VodafoneModeEnabled)
        {
            return "Bu bölümü açtığınızda bağlı olduğunuz ağı kontrol edebilirsiniz.";
        }

        // Before anything the connection checks found: the mode is on for this network
        // and is not rewriting, so this is the only next step worth offering.
        if (status is { RegisteredHere: true, TtlActive: false })
        {
            // The driver-specific half of the advice comes from the failure itself, which
            // is written where the driver is opened. This file is part of a subsystem
            // that must never touch the packet path, and naming the driver here would
            // read as though it did.
            return status.TtlFailure is null
                ? "TTL kuralı henüz kurulmadı. Ağ değiştirdiyseniz \"Bu ağı kaydet\" ile "
                    + "yeniden deneyebilirsiniz."
                : $"{status.TtlFailure} Uygulamayı yönetici olarak çalıştırdığınızdan emin olun.";
        }

        if (run == HotspotRunState.Running)
        {
            return null;
        }

        if (run == HotspotRunState.Failed)
        {
            return failure;
        }

        if (run == HotspotRunState.NotRun)
        {
            return status.RegisteredHere
                ? "Bu ağ kayıtlı; kontrol kendiliğinden çalışır. Hemen sonuç görmek için "
                    + "\"Bağlantıyı kontrol et\" düğmesini kullanabilirsiniz."
                : "Telefonunuzun paylaşımına bağlıyken \"Bu ağı kaydet\" deyin ya da "
                    + "\"Bağlantıyı kontrol et\" düğmesine basın.";
        }

        // The diagnostics writes its own remediation when the readings call for one; it
        // knows what it measured and this does not second-guess it.
        return result?.Remediation
            ?? (status.RegisteredHere
                ? null
                : "Bu ağı kaydederseniz, bir dahaki bağlanışınızda kontrol kendiliğinden yapılabilir.");
    }
}
