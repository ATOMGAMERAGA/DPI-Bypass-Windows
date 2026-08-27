using Microsoft.Win32;

namespace DpiBypass.Core.Apps;

public sealed record InstalledApp(string Name, string ExecutablePath, string? Version, bool IsRunning);

/// <summary>
/// Finds the Discord builds and browsers installed on this machine so the UI can say
/// "Discord bulundu" instead of asking the user to tell us.
/// </summary>
public static class DiscordDetector
{
    private static readonly (string Folder, string Executable, string Label)[] SquirrelBuilds =
    [
        ("Discord", "Discord.exe", "Discord"),
        ("DiscordPTB", "DiscordPTB.exe", "Discord PTB"),
        ("DiscordCanary", "DiscordCanary.exe", "Discord Canary"),
        ("DiscordDevelopment", "DiscordDevelopment.exe", "Discord Development"),
    ];

    private static readonly (string Executable, string Label, string[] RelativePaths)[] KnownBrowsers =
    [
        ("chrome.exe", "Google Chrome", [@"Google\Chrome\Application\chrome.exe"]),
        ("msedge.exe", "Microsoft Edge", [@"Microsoft\Edge\Application\msedge.exe"]),
        ("firefox.exe", "Mozilla Firefox", [@"Mozilla Firefox\firefox.exe"]),
        ("brave.exe", "Brave", [@"BraveSoftware\Brave-Browser\Application\brave.exe"]),
        ("opera.exe", "Opera", [@"Opera\opera.exe"]),
        ("vivaldi.exe", "Vivaldi", [@"Vivaldi\Application\vivaldi.exe"]),
        ("browser.exe", "Yandex Browser", [@"Yandex\YandexBrowser\Application\browser.exe"]),
        ("zen.exe", "Zen Browser", [@"Zen Browser\zen.exe"]),
    ];

    /// <summary>Every Discord channel we can find, running or not.</summary>
    public static IReadOnlyList<InstalledApp> FindDiscord()
    {
        var found = new List<InstalledApp>();
        var running = RunningProcessNames();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var (folder, executable, label) in SquirrelBuilds)
        {
            var root = Path.Combine(localAppData, folder);
            if (!SafeDirectoryExists(root))
            {
                continue;
            }

            // Squirrel keeps the real binary in app-<version>\ and updates by adding
            // a new folder, so take the highest version present.
            var versioned = SafeEnumerateDirectories(root, "app-*")
                .OrderByDescending(ParseVersionFolder)
                .FirstOrDefault();

            var candidate = versioned is null ? null : Path.Combine(versioned, executable);
            if (candidate is not null && File.Exists(candidate))
            {
                found.Add(new InstalledApp(
                    label,
                    candidate,
                    Path.GetFileName(versioned!)["app-".Length..],
                    IsRunning(running, executable)));
                continue;
            }

            // Update.exe based launch, or a portable copy sitting in the root.
            var direct = Path.Combine(root, executable);
            if (File.Exists(direct))
            {
                found.Add(new InstalledApp(label, direct, null, IsRunning(running, executable)));
            }
        }

        foreach (var path in FindStoreDiscord())
        {
            found.Add(new InstalledApp("Discord (Microsoft Store)", path, null, IsRunning(running, "Discord.exe")));
        }

        return found;
    }

    public static bool IsDiscordInstalled() => FindDiscord().Count > 0;

    public static IReadOnlyList<InstalledApp> FindBrowsers()
    {
        var found = new List<InstalledApp>();
        var running = RunningProcessNames();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        }.Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();

        foreach (var (executable, label, relativePaths) in KnownBrowsers)
        {
            var path = relativePaths
                .SelectMany(relative => roots.Select(root => Path.Combine(root, relative)))
                .FirstOrDefault(File.Exists)
                ?? ResolveFromAppPaths(executable);

            if (path is not null)
            {
                found.Add(new InstalledApp(label, path, null, IsRunning(running, executable)));
            }
        }

        return found;
    }

    /// <summary>
    /// The shell's App Paths key is how Windows itself resolves "chrome.exe", so it
    /// catches installs in places we would not think to look.
    /// </summary>
    private static string? ResolveFromAppPaths(string executable)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}");
                if (key?.GetValue(null) is string value)
                {
                    var trimmed = value.Trim('"');
                    if (File.Exists(trimmed))
                    {
                        return trimmed;
                    }
                }
            }
            catch (Exception)
            {
                // Registry access is best effort.
            }
        }

        return null;
    }

    private static IReadOnlyList<string> FindStoreDiscord()
    {
        var found = new List<string>();

        try
        {
            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps");

            foreach (var directory in SafeEnumerateDirectories(windowsApps, "*Discord*"))
            {
                var candidate = Path.Combine(directory, "Discord.exe");
                if (File.Exists(candidate))
                {
                    found.Add(candidate);
                }
            }
        }
        catch (Exception)
        {
            // A store build we cannot see is one we do not report; the desktop builds
            // found above are still worth returning.
        }

        return found;
    }

    /// <summary>
    /// The matching sub-directories of <paramref name="root"/>, or nothing when they
    /// cannot be listed.
    /// </summary>
    /// <remarks>
    /// The result is materialised inside the try on purpose.
    /// <see cref="Directory.EnumerateDirectories(string, string)"/> is lazy, so a
    /// <c>try</c> around the call catches nothing at all: the access check happens
    /// when the caller starts walking the sequence, by which time this method has
    /// returned and its handler is gone. That matters here because one of the roots
    /// is <c>%ProgramFiles%\WindowsApps</c>, which denies enumeration to
    /// administrators on a stock Windows install - so the "which Discord is
    /// installed" lookup threw on essentially every machine, took the browser lookup
    /// queued behind it with it, and left both summaries on the status page reading
    /// "Aranıyor…" for as long as the app was open.
    /// </remarks>
    internal static IReadOnlyList<string> SafeEnumerateDirectories(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root) ? [.. Directory.EnumerateDirectories(root, pattern)] : [];
        }
        catch (Exception)
        {
            // WindowsApps in particular denies enumeration on many machines.
            return [];
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Version ParseVersionFolder(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
            && Version.TryParse(name["app-".Length..], out var version)
            ? version
            : new Version(0, 0);
    }

    /// <summary>
    /// The names of everything running right now, taken once per detection pass.
    /// </summary>
    /// <remarks>
    /// This used to be a <c>GetProcessesByName</c> call per candidate - a full process
    /// enumeration for each of four Discord builds and eight browsers, twelve sweeps
    /// of the process table to answer twelve yes/no questions. Worse, the
    /// <see cref="System.Diagnostics.Process"/> objects it hands back own native
    /// handles and none of them were ever disposed, so each pass leaked one handle per
    /// running process on the machine - and a browser alone accounts for dozens.
    /// </remarks>
    private static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        System.Diagnostics.Process[] processes;
        try
        {
            processes = System.Diagnostics.Process.GetProcesses();
        }
        catch (Exception)
        {
            return names;
        }

        foreach (var process in processes)
        {
            try
            {
                names.Add(process.ProcessName);
            }
            catch (Exception)
            {
                // A process that ended between the enumeration and this read.
            }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }

    private static bool IsRunning(HashSet<string> running, string executable)
    {
        try
        {
            return running.Contains(Path.GetFileNameWithoutExtension(executable));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
