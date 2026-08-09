using DpiBypass.Core.Net;

namespace DpiBypass.Core.Engine;

/// <summary>What the user asked us to cover. Surfaced directly in the main window.</summary>
public enum ProtectionScope
{
    /// <summary>Only traffic belonging to Discord, or aimed at a Discord hostname.</summary>
    DiscordOnly = 0,

    /// <summary>Discord plus every installed browser.</summary>
    DiscordAndBrowsers = 1,

    /// <summary>Every TCP flow on the machine.</summary>
    Everything = 2,

    /// <summary>
    /// Every domain known or discovered to be filtered, whichever program asked for
    /// it. The equivalent of the Linux build's "smart" mode.
    /// </summary>
    BlockedSites = 3,
}

/// <summary>
/// Decides whether a given first-data-packet is one we should rewrite. Keeping this
/// decision cheap matters: it runs on the packet path.
/// </summary>
public sealed class TargetMatcher
{
    /// <summary>Every hostname Discord's client, CDN, gateway and voice servers use.</summary>
    public static readonly string[] DiscordDomains = BlockedSiteCatalog.Discord;

    /// <summary>Discord plus the other domains filtered on Turkish networks.</summary>
    public static readonly string[] KnownBlockedDomains = BlockedSiteCatalog.All;

    /// <summary>Executable names we treat as browsers.</summary>
    public static readonly string[] BrowserExecutables =
    [
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe",
        "vivaldi.exe", "browser.exe", "yandex.exe", "chromium.exe", "thorium.exe",
        "librewolf.exe", "waterfox.exe", "floorp.exe", "zen.exe", "palemoon.exe",
        "maxthon.exe", "ucbrowser.exe", "iexplore.exe", "msedgewebview2.exe", "tor.exe",
    ];

    /// <summary>Executable names that mean "this is Discord".</summary>
    public static readonly string[] DiscordExecutables =
    [
        "discord.exe", "discordptb.exe", "discordcanary.exe", "discorddevelopment.exe",
    ];

    public ProtectionScope Scope { get; set; } = ProtectionScope.DiscordOnly;

    /// <summary>Extra hostnames the user added by hand.</summary>
    public HashSet<string> ExtraDomains { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hostnames that must never be touched, even in system-wide mode.</summary>
    public HashSet<string> ExcludedDomains { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hostnames the discovery pass found to be filtered on this machine.</summary>
    public HashSet<string> LearnedDomains { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One hostname to leave completely untouched, and one to always rewrite,
    /// whatever the scope says.
    /// </summary>
    /// <remarks>
    /// These exist so the discovery pass can measure a single site both ways without
    /// disturbing anything else. Swapping the engine's global strategy would work too,
    /// but it would drop protection for every other connection on the machine for the
    /// several seconds a probe takes - which is not an acceptable price for a
    /// background measurement. Both are volatile: the packet path reads them on every
    /// handshake and the probe clears them when it finishes.
    /// </remarks>
    public volatile string? ProbePassthroughHost;

    public volatile string? ProbeForcedHost;

    public bool ShouldProtect(string? hostName, string? imagePath)
    {
        if (hostName is not null && IsExcluded(hostName))
        {
            return false;
        }

        if (hostName is not null)
        {
            // Checked before the scope so a probe measures what it means to measure.
            if (string.Equals(hostName, ProbePassthroughHost, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(hostName, ProbeForcedHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return Scope switch
        {
            ProtectionScope.Everything => true,
            ProtectionScope.BlockedSites => IsBlockedSite(hostName) || IsDiscord(imagePath),
            ProtectionScope.DiscordAndBrowsers =>
                IsBlockedSite(hostName) || IsDiscord(imagePath) || IsBrowser(imagePath),
            _ => IsDiscordDomain(hostName) || IsExtraDomain(hostName) || IsLearnedDomain(hostName) || IsDiscord(imagePath),
        };
    }

    private bool IsExcluded(string hostName) => Matches(ExcludedDomains, hostName);

    private bool IsExtraDomain(string? hostName) => hostName is not null && Matches(ExtraDomains, hostName);

    private bool IsLearnedDomain(string? hostName) => hostName is not null && Matches(LearnedDomains, hostName);

    public bool IsTargetDomain(string? hostName) => IsBlockedSite(hostName);

    /// <summary>Anything on the shipped list, discovered by the app, or added by the user.</summary>
    public bool IsBlockedSite(string? hostName)
        => IsKnownBlockedDomain(hostName) || IsExtraDomain(hostName) || IsLearnedDomain(hostName);

    public static bool IsDiscordDomain(string? hostName) => MatchesAny(hostName, BlockedSiteCatalog.Discord);

    public static bool IsKnownBlockedDomain(string? hostName) => MatchesAny(hostName, KnownBlockedDomains);

    private static bool MatchesAny(string? hostName, string[] domains)
    {
        if (string.IsNullOrEmpty(hostName))
        {
            return false;
        }

        foreach (var domain in domains)
        {
            if (IsDomainMatch(hostName, domain))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsDiscord(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            return false;
        }

        var fileName = WindowsPath.FileName(imagePath);
        foreach (var executable in DiscordExecutables)
        {
            if (string.Equals(fileName, executable, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Squirrel installs launch from ...\Discord\app-1.0.9999\Discord.exe and the
        // updater itself also opens sockets, so fall back to a path check.
        return imagePath.Contains("\\discord", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBrowser(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            return false;
        }

        var fileName = WindowsPath.FileName(imagePath);
        foreach (var executable in BrowserExecutables)
        {
            if (string.Equals(fileName, executable, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(HashSet<string> set, string hostName)
    {
        if (set.Count == 0)
        {
            return false;
        }

        foreach (var candidate in set)
        {
            if (IsDomainMatch(hostName, candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Exact match or a proper subdomain - never a bare substring.</summary>
    private static bool IsDomainMatch(string hostName, string domain)
    {
        if (hostName.Equals(domain, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return hostName.Length > domain.Length
            && hostName[hostName.Length - domain.Length - 1] == '.'
            && hostName.EndsWith(domain, StringComparison.OrdinalIgnoreCase);
    }
}
