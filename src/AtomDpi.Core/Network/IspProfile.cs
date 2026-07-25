using AtomDpi.Core.Engine;

namespace AtomDpi.Core.Network;

public enum AccessKind
{
    Unknown = 0,
    Fixed = 1,
    Mobile = 2,
    Hotspot = 3,
    FixedWireless = 4,
}

/// <summary>
/// One Turkish operator (or access product) plus the order in which its filters
/// are usually beaten. The tuner still measures for real - this ordering only
/// decides what gets measured first, which is the difference between finding a
/// working recipe in two seconds and in twenty.
/// </summary>
public sealed record IspProfile
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public AccessKind Kind { get; init; } = AccessKind.Unknown;

    /// <summary>Reverse DNS suffixes that identify this operator.</summary>
    public string[] ReverseDnsHints { get; init; } = [];

    /// <summary>Autonomous system numbers announced by this operator.</summary>
    public int[] AsnHints { get; init; } = [];

    /// <summary>Case insensitive fragments that show up in the Wi-Fi network name.</summary>
    public string[] SsidHints { get; init; } = [];

    /// <summary>Strategy ids to try, best first.</summary>
    public required string[] PreferredStrategies { get; init; }
}

public static class IspCatalog
{
    // Mobile and hotspot networks in Turkey inspect harder than fixed lines, so
    // they lead with the decoy based recipes; fixed lines usually fall to a plain
    // SNI split, which is the cheapest thing we can do.
    private static readonly string[] MobileOrder =
    [
        "fake-ttl-split-sni", "fake-badseq-split-sni", "fake-ttl6-split-sni", "disorder-sni",
        "fake-badsum-disorder-sni", "multisplit-sni", "aggressive-combo", "split-sni",
        "fake-ttl8-split2", "oob-split-sni", "disorder2", "split2",
    ];

    private static readonly string[] FixedOrder =
    [
        "split-sni", "fake-ttl-split-sni", "disorder-sni", "fake-badseq-split-sni",
        "multisplit-sni", "split2", "fake-ttl6-split-sni", "fake-badsum-disorder-sni",
        "disorder2", "oob-split-sni", "fake-ttl8-split2", "aggressive-combo",
    ];

    private static readonly string[] HotspotOrder =
    [
        "fake-badseq-split-sni", "fake-ttl-split-sni", "aggressive-combo", "multisplit-sni",
        "disorder-sni", "fake-badsum-disorder-sni", "fake-ttl6-split-sni", "split-sni",
        "oob-split-sni", "disorder2", "split2", "fake-ttl8-split2",
    ];

    public static readonly IspProfile TurkTelekomMobile = new()
    {
        Id = "tt-mobile",
        DisplayName = "Türk Telekom (Mobil)",
        Kind = AccessKind.Mobile,
        ReverseDnsHints = ["mobil.ttnet.com.tr", "ttmobil.com.tr", "avea.com.tr"],
        AsnHints = [47331, 9121, 34984],
        SsidHints = [],
        PreferredStrategies = MobileOrder,
    };

    public static readonly IspProfile TurkTelekomHome = new()
    {
        Id = "tt-home",
        DisplayName = "Türk Telekom Evde İnternet",
        Kind = AccessKind.Fixed,
        ReverseDnsHints = ["ttnet.com.tr", "turktelekom.com.tr", "ttnet.net.tr"],
        AsnHints = [9121, 47331],
        SsidHints = ["ttnet", "turktelekom", "türk telekom", "tt_"],
        PreferredStrategies = FixedOrder,
    };

    public static readonly IspProfile TurkTelekomHotspot = new()
    {
        Id = "tt-hotspot",
        DisplayName = "Türk Telekom Hotspot",
        Kind = AccessKind.Hotspot,
        ReverseDnsHints = ["hotspot.ttnet.com.tr"],
        AsnHints = [9121],
        SsidHints = ["tt wi-fi", "ttwifi", "türk telekom wifi", "turk telekom wifi", "ttnet hotspot"],
        PreferredStrategies = HotspotOrder,
    };

    public static readonly IspProfile Redbox = new()
    {
        Id = "tt-redbox",
        DisplayName = "Redbox (Türk Telekom)",
        Kind = AccessKind.FixedWireless,
        ReverseDnsHints = ["redbox.com.tr"],
        AsnHints = [9121, 47331],
        SsidHints = ["redbox"],
        PreferredStrategies = MobileOrder,
    };

    public static readonly IspProfile TurkcellMobile = new()
    {
        Id = "turkcell-mobile",
        DisplayName = "Turkcell (Mobil)",
        Kind = AccessKind.Mobile,
        ReverseDnsHints = ["turkcell.com.tr", "mobil.turkcell.com.tr"],
        AsnHints = [16135, 47524],
        SsidHints = [],
        PreferredStrategies = MobileOrder,
    };

    public static readonly IspProfile Superonline = new()
    {
        Id = "superonline",
        DisplayName = "Turkcell Superonline",
        Kind = AccessKind.Fixed,
        ReverseDnsHints = ["superonline.net", "turkcell.net.tr"],
        AsnHints = [34984, 197328],
        SsidHints = ["superonline", "turkcell"],
        PreferredStrategies = FixedOrder,
    };

    public static readonly IspProfile Superbox = new()
    {
        Id = "superbox",
        DisplayName = "Superbox (Turkcell FWA)",
        Kind = AccessKind.FixedWireless,
        ReverseDnsHints = ["superbox.turkcell.com.tr"],
        AsnHints = [16135, 34984],
        SsidHints = ["superbox"],
        PreferredStrategies = MobileOrder,
    };

    public static readonly IspProfile TurkcellHotspot = new()
    {
        Id = "turkcell-hotspot",
        DisplayName = "Turkcell Hotspot",
        Kind = AccessKind.Hotspot,
        ReverseDnsHints = ["wifi.turkcell.com.tr"],
        AsnHints = [16135, 34984],
        SsidHints = ["turkcell wi-fi", "turkcellwifi", "superonline wi-fi"],
        PreferredStrategies = HotspotOrder,
    };

    public static readonly IspProfile VodafoneMobile = new()
    {
        Id = "vodafone-mobile",
        DisplayName = "Vodafone (Mobil)",
        Kind = AccessKind.Mobile,
        ReverseDnsHints = ["vodafone.com.tr", "mobil.vodafone.com.tr"],
        AsnHints = [15897, 197207],
        SsidHints = [],
        PreferredStrategies = MobileOrder,
    };

    public static readonly IspProfile VodafoneHome = new()
    {
        Id = "vodafone-home",
        DisplayName = "Vodafone Evde İnternet",
        Kind = AccessKind.Fixed,
        ReverseDnsHints = ["vodafone.net.tr", "vodafonenet.com.tr"],
        AsnHints = [15897],
        SsidHints = ["vodafone"],
        PreferredStrategies = FixedOrder,
    };

    public static readonly IspProfile VodafoneHotspot = new()
    {
        Id = "vodafone-hotspot",
        DisplayName = "Vodafone Hotspot",
        Kind = AccessKind.Hotspot,
        ReverseDnsHints = ["wifi.vodafone.com.tr"],
        AsnHints = [15897],
        SsidHints = ["vodafone wi-fi", "vodafonewifi", "vodafone hotspot"],
        PreferredStrategies = HotspotOrder,
    };

    public static readonly IspProfile TurkNet = new()
    {
        Id = "turknet",
        DisplayName = "TurkNet",
        Kind = AccessKind.Fixed,
        ReverseDnsHints = ["turk.net", "turknet.com.tr"],
        AsnHints = [12735],
        SsidHints = ["turknet"],
        PreferredStrategies = FixedOrder,
    };

    public static readonly IspProfile Unknown = new()
    {
        Id = "unknown",
        DisplayName = "Diğer / Bilinmiyor",
        Kind = AccessKind.Unknown,
        PreferredStrategies = [.. StrategyLibrary.All.Where(s => !s.IsPassthrough).Select(s => s.Id)],
    };

    public static readonly IReadOnlyList<IspProfile> All =
    [
        TurkTelekomMobile,
        TurkTelekomHome,
        TurkTelekomHotspot,
        Redbox,
        TurkcellMobile,
        Superonline,
        Superbox,
        TurkcellHotspot,
        VodafoneMobile,
        VodafoneHome,
        VodafoneHotspot,
        TurkNet,
        Unknown,
    ];

    public static IspProfile ById(string? id)
        => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Unknown;

    /// <summary>
    /// Scores every profile against what we observed and returns the best match.
    /// Reverse DNS is the strongest signal, then ASN, then the Wi-Fi name; the
    /// access kind only breaks ties between an operator's own products.
    /// </summary>
    public static IspProfile Match(string? reverseDns, int? asn, string? ssid, AccessKind kind)
    {
        IspProfile? best = null;
        var bestScore = 0;

        foreach (var profile in All)
        {
            if (ReferenceEquals(profile, Unknown))
            {
                continue;
            }

            var score = 0;

            if (!string.IsNullOrEmpty(reverseDns))
            {
                foreach (var hint in profile.ReverseDnsHints)
                {
                    if (reverseDns.EndsWith(hint, StringComparison.OrdinalIgnoreCase)
                        || reverseDns.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        // Longer suffixes are more specific: "mobil.ttnet.com.tr" beats "ttnet.com.tr".
                        score += 40 + hint.Length;
                    }
                }
            }

            if (asn is not null && profile.AsnHints.Contains(asn.Value))
            {
                score += 25;
            }

            if (!string.IsNullOrEmpty(ssid))
            {
                foreach (var hint in profile.SsidHints)
                {
                    if (ssid.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 20 + hint.Length;
                    }
                }
            }

            if (score > 0 && profile.Kind == kind)
            {
                score += 15;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = profile;
            }
        }

        return best ?? Unknown;
    }
}
