namespace AtomDpi.Core;

/// <summary>Where the app keeps its state, logs and settings.</summary>
public static class AppPaths
{
    public const string ProductName = "Atom DPI Bypass";

    public const string Author = "Atom Gamer Arda A.G.A";

    /// <summary>
    /// State lives under ProgramData rather than the user profile: the engine runs
    /// elevated and may start before anyone signs in, so a per-user folder would
    /// either be unreachable or belong to the wrong account.
    /// </summary>
    public static string StateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ProductName);

    public static string LogDirectory { get; } = Path.Combine(StateDirectory, "logs");

    public static string SettingsFile { get; } = Path.Combine(StateDirectory, "settings.json");

    public static string ProfilesFile { get; } = Path.Combine(StateDirectory, "networks.json");

    public static string InstallDirectory => AppContext.BaseDirectory;

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
