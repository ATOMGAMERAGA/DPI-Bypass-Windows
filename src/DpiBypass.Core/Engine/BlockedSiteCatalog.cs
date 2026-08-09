namespace DpiBypass.Core.Engine;

/// <summary>
/// The domains that are known to be filtered in Turkey and are worth desyncing by
/// default, whichever program asked for them.
/// </summary>
/// <remarks>
/// This is the Windows half of the list the Linux build ships. Keeping the set
/// explicit rather than "protect everything" is what makes the low-impact scopes
/// meaningful: a machine on the blocked-sites scope only ever has these handshakes
/// rewritten, so nothing else on the system can be affected by a bad recipe.
/// </remarks>
public static class BlockedSiteCatalog
{
    /// <summary>Every hostname Discord's client, CDN, gateway and voice servers use.</summary>
    public static readonly string[] Discord =
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

    /// <summary>Other domains commonly blocked by DPI on Turkish networks.</summary>
    public static readonly string[] Other =
    [
        "roblox.com",
        "rbxcdn.com",
        "instagram.com",
        "cdninstagram.com",
        "wattpad.com",
        "onlyfans.com",
        "rutracker.org",
        "1337x.to",
        "thepiratebay.org",
        "nyaa.si",
        "pornhub.com",
        "xvideos.com",
        "redtube.com",
        "xhamster.com",
    ];

    /// <summary>Discord plus the rest of the default list.</summary>
    public static readonly string[] All = [.. Discord, .. Other];
}
