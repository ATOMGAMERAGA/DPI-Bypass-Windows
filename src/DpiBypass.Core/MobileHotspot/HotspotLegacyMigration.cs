using System.Text.Json;
using DpiBypass.Core.Vodafone;

namespace DpiBypass.Core.MobileHotspot;

/// <summary>
/// A network an older build recorded under the pre-Vodafone field names.
/// </summary>
/// <remarks>
/// Kept only so a settings file written by one of those builds can still be read. The
/// migration moves each entry into <see cref="VodafoneModeNetwork"/> and empties this
/// list; nothing reads it to decide behaviour.
/// </remarks>
public sealed record LegacyHotspotNetwork
{
    public required string Key { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string AdapterName { get; init; } = string.Empty;

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A network the user associated with Vodafone Sınırsız Modu.
/// </summary>
/// <remarks>
/// The mode only installs its rule on one of these. Registration is per network because
/// the rewrite is only meaningful where something is counting hops: on a home router it
/// changes the user's traffic and buys them nothing.
/// </remarks>
public sealed record VodafoneModeNetwork
{
    public required string Key { get; init; }

    /// <summary>
    /// The wireless name the network was remembered under, when it had one.
    /// </summary>
    /// <remarks>
    /// Stored separately from <see cref="Key"/> because the key is not stable for the
    /// networks this feature is about. A fingerprint mixes in the access point's MAC,
    /// and a phone sharing its connection hands out a new one every time the hotspot is
    /// switched off and on - Android and iOS both randomise it - so a network the user
    /// registered yesterday arrives today under a key nothing has ever seen. Matching on
    /// the name as well is what makes "my saved network" mean the network the user saved
    /// rather than one particular session of it.
    /// </remarks>
    public string Ssid { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string AdapterName { get; init; } = string.Empty;

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>What one migration pass found and did.</summary>
public sealed record HotspotMigrationResult
{
    public static readonly HotspotMigrationResult NothingToDo = new()
    {
        Changed = false,
        LegacyWasEnabled = false,
        MigratedNetworks = 0,
        VodafoneIdentityRestored = false,
        Summary = "Eski TTL yapılandırması bulunamadı.",
    };

    public required bool Changed { get; init; }

    /// <summary>Whether the mode was switched on under the old field name.</summary>
    public required bool LegacyWasEnabled { get; init; }

    /// <summary>How many legacy registrations were carried into the current list.</summary>
    public required int MigratedNetworks { get; init; }

    /// <summary>Whether a PR #11-era settings file had its Vodafone identity restored.</summary>
    public required bool VodafoneIdentityRestored { get; init; }

    public required string Summary { get; init; }
}

/// <summary>
/// Folds a settings file written under the old field names into the current ones.
/// </summary>
/// <remarks>
/// <para>
/// The hotspot TTL rewrite predates the Vodafone naming and was stored as
/// <c>HotspotTtlFix</c>, <c>HotspotTtlNetworks</c>, <c>HotspotTtlValue</c> and
/// <c>HotspotDropIPv6</c>. One intermediate build then deleted the mechanism outright
/// and had this pass erase those fields on every load. Both of those files have to end
/// up in the same place: the mode switched on where the user had switched it on, the
/// networks they registered still registered, and the TTL and IPv6 choices they made
/// still theirs.
/// </para>
/// <para>
/// The migration is deterministic and idempotent by construction. It reads only the
/// legacy fields, moves what they hold into the current ones, and running it again on
/// its own output changes nothing. A separate restoration marker also upgrades settings
/// already processed by the erasing build without re-enabling the mode after a user
/// later switches it off.
/// </para>
/// </remarks>
public static class HotspotLegacyMigration
{
    /// <summary>
    /// Whether an older build actually left anything behind on this machine.
    /// </summary>
    /// <remarks>
    /// A pure read, so the card can offer the cleanup only when there is something to
    /// migrate rather than showing every user on a clean install a button about field
    /// names they never had.
    /// </remarks>
    public static bool HasResidue(IHotspotLegacyState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.LegacyTtlFixEnabled
            || state.LegacyTtlValue is not null
            || state.LegacyDropIpv6 is not null
            || state.LegacyNetworks.Count > 0;
    }

    /// <summary>
    /// Moves the legacy fields into the current model and clears them.
    /// </summary>
    /// <param name="state">The settings as loaded, mutated in place.</param>
    /// <param name="now">Timestamp for the migration marker.</param>
    public static HotspotMigrationResult Apply(IHotspotLegacyState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        var networks = state.LegacyNetworks;
        var wasEnabled = state.LegacyTtlFixEnabled;
        var hadNetworks = networks.Count > 0;
        var hadLegacyOptions = state.LegacyTtlValue is not null || state.LegacyDropIpv6 is not null;
        var restoresPr11Identity = state.VodafoneIdentityRestoredAt is null
            && state.LegacyMigratedAt is not null;

        // The user's own tuning, carried across rather than reset to the default. Read
        // before the fields are cleared, and only applied when the old file actually
        // carried a usable value.
        if (ReadTtl(state.LegacyTtlValue) is { } ttl)
        {
            state.VodafoneTtl = ttl;
        }

        if (ReadBool(state.LegacyDropIpv6) is { } dropIpv6)
        {
            state.VodafoneDropIpv6 = dropIpv6;
        }

        state.LegacyTtlFixEnabled = false;
        state.LegacyTtlValue = null;
        state.LegacyDropIpv6 = null;

        if (!wasEnabled && !hadNetworks && !hadLegacyOptions && !restoresPr11Identity)
        {
            return HotspotMigrationResult.NothingToDo;
        }

        var migrated = 0;
        foreach (var network in networks.Where(network => !string.IsNullOrWhiteSpace(network.Key)))
        {
            if (state.VodafoneNetworks.Any(existing => string.Equals(
                    existing.Key,
                    network.Key,
                    StringComparison.Ordinal)))
            {
                migrated++;
                continue;
            }

            state.VodafoneNetworks.Add(new VodafoneModeNetwork
            {
                Key = network.Key,
                DisplayName = network.DisplayName ?? string.Empty,
                AdapterName = network.AdapterName ?? string.Empty,
                AddedAt = network.AddedAt,
            });
            migrated++;
        }

        networks.Clear();

        // The old switch selected the whole feature, mechanism included, so an upgrade
        // leaves the user with the mode they had switched on rather than with a toggle
        // they have to find again.
        if (wasEnabled)
        {
            state.VodafoneModeEnabled = true;
            state.DiagnosticsEnabled = true;
        }
        else if (restoresPr11Identity && state.DiagnosticsEnabled)
        {
            // PR #11 erased the original switch and used diagnostics=true as its only
            // surviving indication that the old feature had been active.
            state.VodafoneModeEnabled = true;
        }

        if (wasEnabled || hadNetworks || hadLegacyOptions)
        {
            state.LegacyMigratedAt ??= now;
        }

        state.VodafoneIdentityRestoredAt ??= now;

        return new HotspotMigrationResult
        {
            Changed = true,
            LegacyWasEnabled = wasEnabled,
            MigratedNetworks = migrated,
            VodafoneIdentityRestored = wasEnabled || restoresPr11Identity,
            Summary = BuildSummary(wasEnabled, hadNetworks, hadLegacyOptions, restoresPr11Identity, migrated),
        };
    }

    /// <summary>
    /// A legacy TTL, when the file holds one this build can use.
    /// </summary>
    /// <remarks>
    /// The field is a raw <see cref="JsonElement"/> because a hand-edited file can put
    /// anything there. A value outside the usable range is dropped rather than carried:
    /// the current default is a better answer than a number that would rewrite the
    /// engine's own low-TTL decoys.
    /// </remarks>
    private static int? ReadTtl(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Number } element
            || !element.TryGetInt32(out var ttl))
        {
            return null;
        }

        return ttl == TtlFixSettings.CoerceTimeToLive(ttl) ? ttl : null;
    }

    private static bool? ReadBool(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string BuildSummary(
        bool wasEnabled,
        bool hadNetworks,
        bool hadLegacyOptions,
        bool restoredPr11Identity,
        int migratedNetworks)
    {
        if (wasEnabled)
        {
            return $"Vodafone Sınırsız Modu eski ayar dosyasından geri yüklendi; {migratedNetworks} "
                + "ağ kaydı taşındı.";
        }

        if (hadNetworks)
        {
            return $"{migratedNetworks} Vodafone ağ kaydı eski alan adlarından taşındı.";
        }

        if (hadLegacyOptions)
        {
            return "Eski TTL/IPv6 seçenekleri güncel ayarlara taşındı.";
        }

        return restoredPr11Identity
            ? "Vodafone Sınırsız Modu kimliği ve tanılama ayarı geri yüklendi."
            : HotspotMigrationResult.NothingToDo.Summary;
    }
}

/// <summary>
/// The fields the migration reads and writes.
/// </summary>
/// <remarks>
/// An interface rather than the settings type itself so the migration can be tested and
/// reasoned about without the rest of the configuration coming with it.
/// </remarks>
public interface IHotspotLegacyState
{
    bool LegacyTtlFixEnabled { get; set; }

    List<LegacyHotspotNetwork> LegacyNetworks { get; }

    JsonElement? LegacyTtlValue { get; set; }

    JsonElement? LegacyDropIpv6 { get; set; }

    bool VodafoneModeEnabled { get; set; }

    List<VodafoneModeNetwork> VodafoneNetworks { get; }

    bool DiagnosticsEnabled { get; set; }

    /// <summary>The TTL outgoing packets are rewritten to.</summary>
    int VodafoneTtl { get; set; }

    /// <summary>Whether outbound IPv6 is dropped on the shared adapter.</summary>
    bool VodafoneDropIpv6 { get; set; }

    DateTimeOffset? LegacyMigratedAt { get; set; }

    DateTimeOffset? VodafoneIdentityRestoredAt { get; set; }
}
