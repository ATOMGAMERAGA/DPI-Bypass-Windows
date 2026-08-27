namespace DpiBypass.Core;

/// <summary>Where the app keeps its state, logs and settings.</summary>
public static class AppPaths
{
    public const string ProductName = "DPI Bypass";

    public const string Author = "Atom Gamer Arda A.G.A";

    /// <summary>The folder used before the app was renamed. Migrated from once, on first run.</summary>
    private const string LegacyProductName = "Atom DPI Bypass";

    /// <summary>
    /// State lives under ProgramData rather than the user profile: the engine runs
    /// elevated and may start before anyone signs in, so a per-user folder would
    /// either be unreachable or belong to the wrong account.
    /// </summary>
    public static string StateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ProductName);

    public static string LegacyStateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        LegacyProductName);

    public static string LogDirectory { get; } = Path.Combine(StateDirectory, "logs");

    public static string SettingsFile { get; } = Path.Combine(StateDirectory, "settings.json");

    public static string ProfilesFile { get; } = Path.Combine(StateDirectory, "networks.json");

    /// <summary>Exact original NIC values retained until every change is restored.</summary>
    public static string LatencySnapshotFile { get; } = Path.Combine(StateDirectory, "latency-snapshot.json");

    /// <summary>Domains the app worked out are blocked, learned while running.</summary>
    public static string LearnedDomainsFile { get; } = Path.Combine(StateDirectory, "learned-domains.json");

    public static string InstallDirectory => AppContext.BaseDirectory;

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// Copies settings written under the old product name across, once.
    /// </summary>
    /// <remarks>
    /// Only the files we own are moved, and only when the new one does not exist, so
    /// running this twice is harmless and a user who already reconfigured the renamed
    /// build never has their choices overwritten by a stale file. The old folder is
    /// left on disk for the uninstaller to remove - deleting a ProgramData tree we no
    /// longer fully understand is not worth the risk.
    /// </remarks>
    public static void MigrateLegacyState()
    {
        try
        {
            if (!Directory.Exists(LegacyStateDirectory) || string.Equals(
                    LegacyStateDirectory,
                    StateDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(StateDirectory);

            foreach (var name in new[]
            {
                "settings.json", "networks.json", "dns-snapshot.json", "latency-snapshot.json", "learned-domains.json",
            })
            {
                var source = Path.Combine(LegacyStateDirectory, name);
                var destination = Path.Combine(StateDirectory, name);

                if (File.Exists(source) && !File.Exists(destination))
                {
                    File.Copy(source, destination);
                }
            }
        }
        catch (Exception)
        {
            // Migration is a convenience; defaults are always usable.
        }
    }
}
