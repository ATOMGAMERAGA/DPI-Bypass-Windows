namespace DpiBypass.Core.MobileHotspot;

/// <summary>
/// A network an older build had the TTL rewrite switched on for.
/// </summary>
/// <remarks>
/// Kept only so an existing settings file can be recognised and cleaned up. Nothing
/// reads this list to decide behaviour any more; the migration empties it.
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
/// The network identity remains useful for legitimate per-network diagnostics even
/// though the old TTL packet rewrite is no longer available. Keeping it in a separate
/// current-model list lets migration remove only the obsolete mechanism without
/// throwing away the user's remembered networks.
/// </remarks>
public sealed record VodafoneModeNetwork
{
    public required string Key { get; init; }

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

    /// <summary>Whether the retired TTL rewrite was switched on in the file.</summary>
    public required bool LegacyWasEnabled { get; init; }

    /// <summary>How many legacy registrations were preserved in the safe mode.</summary>
    public required int MigratedNetworks { get; init; }

    /// <summary>Whether a PR #11-era settings file had its Vodafone identity restored.</summary>
    public required bool VodafoneIdentityRestored { get; init; }

    public required string Summary { get; init; }
}

/// <summary>
/// Retires only the old hotspot TTL rewrite while preserving the surrounding feature.
/// </summary>
/// <remarks>
/// <para>
/// Earlier builds shipped a mode that rewrote the TTL of every outgoing packet on a
/// tethered adapter and dropped its IPv6, so an operator's tethering counter would not
/// recognise the traffic. That mechanism is gone. What is left here is the part that
/// still matters to somebody upgrading: making sure a file written by one of those
/// builds can never switch it back on. The feature identity, remembered networks and
/// legitimate diagnostics are retained.
/// </para>
/// <para>
/// The migration is deterministic and idempotent by construction. It reads only the
/// legacy fields, migrates reusable state, and running it again on its own output changes
/// nothing. A separate restoration marker also upgrades settings already processed by
/// PR #11 without re-enabling the mode after a user later switches it off.
/// </para>
/// </remarks>
public static class HotspotLegacyMigration
{
    /// <summary>
    /// Disables the obsolete rewrite and moves reusable network registrations into the
    /// safe Vodafone mode.
    /// </summary>
    /// <param name="state">The legacy fields as loaded, mutated in place.</param>
    /// <param name="now">Timestamp for the migration marker.</param>
    /// <summary>
    /// Whether an older build actually left anything behind on this machine.
    /// </summary>
    /// <remarks>
    /// A pure read, so the card can offer the cleanup only when there is something to
    /// clean rather than showing every user on a clean install a button about a
    /// sub-feature they never had.
    /// </remarks>
    public static bool HasResidue(IHotspotLegacyState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.LegacyTtlFixEnabled
            || state.LegacyTtlValue is not null
            || state.LegacyDropIpv6 is not null
            || state.LegacyNetworks.Count > 0;
    }

    public static HotspotMigrationResult Apply(IHotspotLegacyState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        var networks = state.LegacyNetworks;
        var wasEnabled = state.LegacyTtlFixEnabled;
        var hadNetworks = networks.Count > 0;
        var hadLegacyOptions = state.LegacyTtlValue is not null || state.LegacyDropIpv6 is not null;
        var restoresPr11Identity = state.VodafoneIdentityRestoredAt is null
            && state.LegacyMigratedAt is not null;

        // Belt and braces: even a file that already carries the marker has the switch
        // forced off, so a hand edit or a restored backup cannot bring the rewrite back.
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

        // The old switch selected the whole user-facing feature as well as the unsafe
        // implementation detail. Preserve that intent, but only for the diagnostic mode.
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

    private static string BuildSummary(
        bool wasEnabled,
        bool hadNetworks,
        bool hadLegacyOptions,
        bool restoredPr11Identity,
        int migratedNetworks)
    {
        if (wasEnabled)
        {
            return $"Eski TTL yeniden yazımı kapatıldı; {migratedNetworks} Vodafone ağ kaydı "
                + "güvenli tanılama moduna taşındı.";
        }

        if (hadNetworks)
        {
            return $"{migratedNetworks} Vodafone ağ kaydı korundu; kullanılmayan TTL alanları temizlendi.";
        }

        if (hadLegacyOptions)
        {
            return "Eski TTL/IPv6 seçenekleri temizlendi; Vodafone modu ve diğer tercihler korundu.";
        }

        return restoredPr11Identity
            ? "Vodafone Sınırsız Modu kimliği ve güvenli tanılama ayarı geri yüklendi."
            : HotspotMigrationResult.NothingToDo.Summary;
    }

}

/// <summary>
/// The legacy fields the migration touches.
/// </summary>
/// <remarks>
/// An interface rather than the settings type itself so the migration can be tested and
/// reasoned about without the rest of the configuration coming with it.
/// </remarks>
public interface IHotspotLegacyState
{
    bool LegacyTtlFixEnabled { get; set; }

    List<LegacyHotspotNetwork> LegacyNetworks { get; }

    System.Text.Json.JsonElement? LegacyTtlValue { get; set; }

    System.Text.Json.JsonElement? LegacyDropIpv6 { get; set; }

    bool VodafoneModeEnabled { get; set; }

    List<VodafoneModeNetwork> VodafoneNetworks { get; }

    bool DiagnosticsEnabled { get; set; }

    DateTimeOffset? LegacyMigratedAt { get; set; }

    DateTimeOffset? VodafoneIdentityRestoredAt { get; set; }
}
