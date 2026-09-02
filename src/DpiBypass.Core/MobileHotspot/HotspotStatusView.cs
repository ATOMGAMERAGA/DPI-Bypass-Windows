using DpiBypass.Core.Network;

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
            TechnicalDetails = run == HotspotRunState.Completed ? BuildDetails(result!) : [],
            Findings = result?.Findings ?? [],
            Report = result?.ToReport() ?? string.Empty,
            Headline = DescribeHeadline(status, run, failure),
            Severity = DescribeSeverity(status, run, result),
            Suggestion = DescribeSuggestion(status, run, result, failure),
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

    private static string DescribeHeadline(HotspotStatus status, HotspotRunState run, string? failure) =>
        (status.VodafoneModeEnabled, run) switch
        {
            (false, _) => "Kapalı",
            (true, HotspotRunState.Running) => $"Açık · {status.NetworkName} kontrol ediliyor",
            (true, HotspotRunState.Failed) => $"Açık · kontrol tamamlanamadı ({failure})",
            (true, HotspotRunState.NotRun) when status.RegisteredHere =>
                $"Açık · {status.NetworkName} kayıtlı · henüz kontrol edilmedi",
            (true, HotspotRunState.NotRun) => $"Açık · {status.NetworkName} kayıtlı değil",
            _ => status.RegisteredHere
                ? $"Açık · {status.NetworkName} kayıtlı"
                : $"Açık · {status.NetworkName} kayıtlı değil",
        };

    private static string DescribeSeverity(
        HotspotStatus status,
        HotspotRunState run,
        HotspotDiagnosticResult? result) => (status.VodafoneModeEnabled, run, result) switch
    {
        (false, _, _) => "off",
        (_, HotspotRunState.Running, _) => "info",
        (_, HotspotRunState.Failed, _) => "warn",
        (_, HotspotRunState.Completed, { HasInternet: false }) => "warn",
        (_, HotspotRunState.Completed, { DnsWorks: false }) => "warn",
        (_, HotspotRunState.Completed, _) => "ok",
        _ => "info",
    };

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
            return "Telefonunuzun paylaşımına bağlıyken \"Bağlantıyı kontrol et\" düğmesine basın.";
        }

        // The diagnostics writes its own remediation when the readings call for one; it
        // knows what it measured and this does not second-guess it.
        return result?.Remediation
            ?? (status.RegisteredHere
                ? null
                : "Bu ağı kaydederseniz, bir dahaki bağlanışınızda kontrol kendiliğinden yapılabilir.");
    }
}
