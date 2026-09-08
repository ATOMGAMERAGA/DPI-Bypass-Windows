using System.Collections.Frozen;

namespace DpiBypass.Core.Apps;

/// <summary>
/// The hosts the Lunar Client launcher's advertisement slot is filled from.
/// </summary>
/// <remarks>
/// <para>
/// Lunar Client's launcher has been an Overwolf application since August 2024, and the
/// single advertisement in the bottom right of the launcher window is sold and rendered
/// by Overwolf. Moonsworth's own support article names the two companies involved:
/// Overwolf serves the slot, and GeoEdge screens the creatives. The launcher offers no
/// way to turn the slot off, so the only thing a machine can do about it is refuse to
/// resolve the hosts the slot is fetched from.
/// </para>
/// <para>
/// Which is exactly what this list is, and it is deliberately narrow. It holds three
/// kinds of name and nothing else: the Overwolf advertising and telemetry hosts, the
/// exchanges an Overwolf slot is filled from, and the measurement companies that count
/// the impression afterwards. Lunar's own service - <c>api.lunarclientprod.com</c>, the
/// asset and cosmetic servers, <c>www.lunarclient.com</c>, and every Minecraft server the
/// game connects to - is not in it and must never be: blocking any of those would break
/// signing in, and a launcher that cannot sign in is not an improvement on a launcher
/// with an advertisement in it.
/// </para>
/// <para>
/// Every entry covers itself and everything under it, so <c>googlesyndication.com</c>
/// also covers <c>pagead2.googlesyndication.com</c>. Where a parent domain has non
/// advertising uses the entry names the single host instead: <c>mrkt.forgecdn.net</c> is
/// Overwolf's marketing CDN, while the rest of <c>forgecdn.net</c> serves mod downloads
/// and is left alone.
/// </para>
/// </remarks>
public static class LunarAdBlock
{
    /// <summary>
    /// The names answered locally instead of being resolved, newest evidence first.
    /// </summary>
    /// <remarks>
    /// Sources, so the next person to touch this can check rather than guess: the
    /// <c>overwolf.com</c> telemetry hosts and <c>analytics.lunarclientprod.com</c> are
    /// carried by Hagezi's Pro list and, for the two oldest, by StevenBlack's; the
    /// exchange and measurement names are on every mainstream DNS blocklist; and each
    /// host was checked to exist at the time of writing, which is how the ones that only
    /// looked plausible - <c>ads.lunarclient.com</c> and friends, all of them nothing but
    /// the Cloudflare wildcard on <c>lunarclient.com</c> - stayed out of it.
    /// </remarks>
    private static readonly string[] BlockedDomains =
    [
        // --- Overwolf: the company that sells and renders the launcher's slot --------
        "ads.overwolf.com",
        "tracking.overwolf.com",
        "analyticsnew.overwolf.com",
        "analyticssec.overwolf.com",
        "newlog.overwolf.com",
        "apps-errors.overwolf.com",
        "client-errors.overwolf.com",

        // Overwolf's marketing CDN. Named as a single host on purpose: the rest of
        // forgecdn.net is CurseForge's mod download edge and has nothing to do with ads.
        "mrkt.forgecdn.net",

        // --- Moonsworth's own analytics endpoint ------------------------------------
        // The launcher's product telemetry. Not the API it signs in through, and not the
        // asset server the cosmetics come from; those are untouched by design.
        "analytics.lunarclientprod.com",

        // --- GeoEdge: the creative screening Moonsworth's support article names -------
        "geoedge.be",

        // --- The exchanges an Overwolf slot is filled from ---------------------------
        // Blocking these is what leaves the slot empty rather than merely unmeasured:
        // the creative itself comes from one of them, whatever the launcher asked for.
        "googlesyndication.com",
        "doubleclick.net",
        "adtrafficquality.google",
        "pubmatic.com",
        "openx.net",
        "smartadserver.com",
        "adnxs.com",
        "criteo.com",
        "casalemedia.com",
        "rubiconproject.com",
        "3lift.com",
        "sharethrough.com",
        "adsrvr.org",
        "amazon-adsystem.com",

        // --- Impression measurement and brand safety ---------------------------------
        "moatads.com",
        "doubleverify.com",
        "adsafeprotected.com",
        "scorecardresearch.com",
    ];

    /// <summary>
    /// The subset written into the machine's hosts file, as literal host names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hosts entry matches one name exactly - it says nothing about anything under it -
    /// so only the names that are already whole hosts can go in one. That is precisely the
    /// Lunar and Overwolf side of the list, which is the part this feature is named after;
    /// the exchanges are registrable domains whose creatives arrive from a subdomain
    /// nobody can enumerate, and they are left to the resolver, where a suffix match works.
    /// </para>
    /// <para>
    /// GeoEdge appears here as its two script hosts rather than as the registrable domain
    /// the resolver blocks, for the same reason.
    /// </para>
    /// </remarks>
    private static readonly string[] HostsFileEntries =
    [
        "ads.overwolf.com",
        "tracking.overwolf.com",
        "analyticsnew.overwolf.com",
        "analyticssec.overwolf.com",
        "newlog.overwolf.com",
        "apps-errors.overwolf.com",
        "client-errors.overwolf.com",
        "mrkt.forgecdn.net",
        "analytics.lunarclientprod.com",
        "rumcdn.geoedge.be",
        "grumi-ip.geoedge.be",
    ];

    private static readonly FrozenSet<string> Lookup =
        BlockedDomains.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> SpanLookup =
        Lookup.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Everything the resolver refuses, matched by suffix.</summary>
    public static IReadOnlyList<string> Domains => BlockedDomains;

    /// <summary>The literal host names the hosts file layer writes.</summary>
    public static IReadOnlyList<string> HostsFileNames => HostsFileEntries;

    /// <summary>
    /// Whether a name is one of the advertising hosts, or sits under one.
    /// </summary>
    /// <remarks>
    /// Walks the name one label at a time and asks the set about each suffix, so the work
    /// is a handful of hash lookups over spans of the caller's string with nothing
    /// allocated. A trailing root dot is tolerated because a DNS question carries the
    /// name in wire form and callers differ on whether they put it back.
    /// </remarks>
    public static bool Blocks(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var remaining = name.AsSpan().TrimEnd('.');

        while (!remaining.IsEmpty)
        {
            if (SpanLookup.Contains(remaining))
            {
                return true;
            }

            var dot = remaining.IndexOf('.');
            if (dot < 0)
            {
                return false;
            }

            remaining = remaining[(dot + 1)..];
        }

        return false;
    }
}
