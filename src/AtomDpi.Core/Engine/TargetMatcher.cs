using AtomDpi.Core.Net;

namespace AtomDpi.Core.Engine;

/// <summary>What the user asked us to cover. Surfaced directly in the main window.</summary>
public enum ProtectionScope
{
    /// <summary>Only traffic belonging to Discord, or aimed at a Discord hostname.</summary>
    DiscordOnly = 0,

    /// <summary>Discord plus every installed browser.</summary>
    DiscordAndBrowsers = 1,

    /// <summary>Every TCP flow on the machine.</summary>
    Everything = 2,
}

/// <summary>
/// Decides whether a given first-data-packet is one we should rewrite. Keeping this
/// decision cheap matters: it runs on the packet path.
/// </summary>
public sealed class TargetMatcher
{
    /// <summary>Every hostname Discord's client, CDN, gateway and voice servers use.</summary>
    public static readonly string[] DiscordDomains =
    [
        "discord.com",
        "discordapp.com",
        "discordapp.net",
        "discord.gg",
        "discord.media",
        "discordcdn.com",
        "discord.co",
        "discord.dev",
        "discordstatus.com",
        "dis.gd",
        "discord-attachments-uploads-prd.storage.googleapis.com",
    ];

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

    public bool ShouldProtect(string? hostName, string? imagePath)
    {
        if (hostName is not null && IsExcluded(hostName))
        {
            return false;
        }

        return Scope switch
        {
            ProtectionScope.Everything => true,
            ProtectionScope.DiscordAndBrowsers =>
                IsTargetDomain(hostName) || IsDiscord(imagePath) || IsBrowser(imagePath),
            _ => IsDiscordDomain(hostName) || IsExtraDomain(hostName) || IsDiscord(imagePath),
        };
    }

    private bool IsExcluded(string hostName) => Matches(ExcludedDomains, hostName);

    private bool IsExtraDomain(string? hostName) => hostName is not null && Matches(ExtraDomains, hostName);

    public bool IsTargetDomain(string? hostName) => IsDiscordDomain(hostName) || IsExtraDomain(hostName);

    public static bool IsDiscordDomain(string? hostName)
    {
        if (string.IsNullOrEmpty(hostName))
        {
            return false;
        }

        foreach (var domain in DiscordDomains)
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
