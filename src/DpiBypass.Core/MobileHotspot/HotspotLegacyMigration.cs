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

/// <summary>What one migration pass found and did.</summary>
public sealed record HotspotMigrationResult
{
    public static readonly HotspotMigrationResult NothingToDo = new()
    {
        Changed = false,
        LegacyWasEnabled = false,
        ClearedNetworks = 0,
        Summary = "Eski hotspot yapılandırması bulunamadı.",
    };

    public required bool Changed { get; init; }

    /// <summary>Whether the retired TTL rewrite was switched on in the file.</summary>
    public required bool LegacyWasEnabled { get; init; }

    public required int ClearedNetworks { get; init; }

    public required string Summary { get; init; }
}

/// <summary>
/// Retires the old hotspot TTL rewrite from a settings file, once and for good.
/// </summary>
/// <remarks>
/// <para>
/// Earlier builds shipped a mode that rewrote the TTL of every outgoing packet on a
/// tethered adapter and dropped its IPv6, so an operator's tethering counter would not
/// recognise the traffic. That mechanism is gone. What is left here is the part that
/// still matters to somebody upgrading: making sure a file written by one of those
/// builds can never switch it back on, and that nothing about it is left behind.
/// </para>
/// <para>
/// The migration is deterministic and idempotent by construction. It reads only the
/// legacy fields, writes a fixed result, and running it again on its own output changes
/// nothing - which is what makes it safe to run on every load rather than once, from a
/// marker that a hand-edited or restored file might not carry.
/// </para>
/// </remarks>
public static class HotspotLegacyMigration
{
    /// <summary>
    /// Clears the legacy state and, when it was in use, turns on the diagnostics that
    /// replaced it.
    /// </summary>
    /// <param name="state">The legacy fields as loaded, mutated in place.</param>
    /// <param name="now">Timestamp for the migration marker.</param>
    public static HotspotMigrationResult Apply(IHotspotLegacyState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        var networks = state.LegacyNetworks;
        var wasEnabled = state.LegacyTtlFixEnabled;
        var hadNetworks = networks.Count > 0;

        // Belt and braces: even a file that already carries the marker has the switch
        // forced off, so a hand edit or a restored backup cannot bring the rewrite back.
        state.LegacyTtlFixEnabled = false;

        if (!wasEnabled && !hadNetworks)
        {
            return HotspotMigrationResult.NothingToDo;
        }

        var cleared = networks.Count;
        networks.Clear();

        // The user was relying on this feature, so the safe replacement is switched on
        // in its place rather than leaving them with nothing where something used to be.
        if (wasEnabled)
        {
            state.DiagnosticsEnabled = true;
        }

        state.LegacyMigratedAt ??= now;

        return new HotspotMigrationResult
        {
            Changed = true,
            LegacyWasEnabled = wasEnabled,
            ClearedNetworks = cleared,
            Summary = wasEnabled
                ? $"Eski hotspot TTL modu kapatıldı ve {cleared} ağ kaydı temizlendi; "
                    + "yerine mobil hotspot tanılaması açıldı."
                : $"Kullanılmayan {cleared} eski hotspot ağ kaydı temizlendi.",
        };
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

    bool DiagnosticsEnabled { get; set; }

    DateTimeOffset? LegacyMigratedAt { get; set; }
}
