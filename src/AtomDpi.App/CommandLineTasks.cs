using AtomDpi.Core;
using AtomDpi.Core.Config;
using AtomDpi.Core.Dns;
using AtomDpi.Core.Logging;
using AtomDpi.Core.Startup;

namespace AtomDpi.App;

/// <summary>
/// Headless jobs the installer and uninstaller invoke on the same executable.
/// </summary>
/// <remarks>
/// Keeping these in the app rather than duplicating them as installer script means
/// the uninstaller restores DNS using exactly the same code path that changed it,
/// with the same snapshot file - so there is no second implementation to drift.
/// </remarks>
internal static class CommandLineTasks
{
    /// <summary>Returns true when the argument was a headless job and the process should exit.</summary>
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        var verb = args[0].TrimStart('-', '/').ToLowerInvariant();

        switch (verb)
        {
            case "install-autostart":
                await InstallAutoStartAsync().ConfigureAwait(false);
                return true;

            case "uninstall-autostart":
                await new AutoStartManager(log: AppLog.InfoSink).DisableAsync().ConfigureAwait(false);
                return true;

            case "restore-dns":
                await RestoreDnsAsync().ConfigureAwait(false);
                return true;

            default:
                return false;
        }
    }

    private static async Task InstallAutoStartAsync()
    {
        var settings = new ConfigStore().Load();
        var manager = new AutoStartManager(log: AppLog.InfoSink);

        if (settings.StartWithWindows)
        {
            await manager.EnableAsync(settings.StartMinimised).ConfigureAwait(false);
        }
    }

    private static async Task RestoreDnsAsync()
    {
        var configurator = new DnsConfigurator(AppPaths.StateDirectory, AppLog.InfoSink);

        if (!configurator.HasPendingRestore)
        {
            AppLog.Info("Geri yüklenecek bir DNS anlık görüntüsü yok.");
            return;
        }

        await configurator.RestoreAsync().ConfigureAwait(false);
    }
}
