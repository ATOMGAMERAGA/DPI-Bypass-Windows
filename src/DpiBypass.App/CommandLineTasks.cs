using System.IO;
using System.Runtime.InteropServices;
using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Ipc;
using DpiBypass.Core.Logging;
using DpiBypass.Core.Startup;

namespace DpiBypass.App;

/// <summary>
/// Everything this executable does without opening a window: the installer's
/// housekeeping, and the command line.
/// </summary>
/// <remarks>
/// <para>
/// Keeping the installer jobs in the app rather than duplicating them as installer
/// script means the uninstaller restores DNS using exactly the same code path that
/// changed it, with the same snapshot file - so there is no second implementation
/// to drift.
/// </para>
/// <para>
/// The query and control verbs talk to the running instance over a named pipe,
/// because the engine is a system-wide packet filter owned by one process: a second
/// copy of the executable can read the settings file but has no idea what the
/// running one has measured, chosen, or counted.
/// </para>
/// </remarks>
internal static class CommandLineTasks
{
    /// <summary>
    /// The verbs that do their work without a window. Anything else - including
    /// <c>--minimized</c> - is a normal launch.
    /// </summary>
    private static readonly HashSet<string> HeadlessVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "install-autostart", "uninstall-autostart", "restore-dns",
        "strategies", "isps", "version", "v", "help", "h", "?",
        "status", "test", "search", "domains", "enable", "disable", "vodafone",
    };

    /// <summary>
    /// Answers, without doing any work, whether this launch is a headless job.
    /// </summary>
    /// <remarks>
    /// Startup has to know which path it is on before it decides whether to build a
    /// window, and it has to know synchronously: asking the question by starting the
    /// job and waiting for the answer is what used to put installer housekeeping and
    /// pipe round-trips in front of the UI.
    /// </remarks>
    public static bool IsHeadlessVerb(string[] args)
        => args.Length > 0 && HeadlessVerbs.Contains(NormaliseVerb(args[0]));

    private static string NormaliseVerb(string argument) => argument.TrimStart('-', '/').ToLowerInvariant();

    /// <summary>Returns true when the argument was a headless job and the process should exit.</summary>
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        var verb = NormaliseVerb(args[0]);
        var argument = args.Length > 1 ? args[1] : null;

        switch (verb)
        {
            // --- installer and uninstaller ---------------------------------------
            case "install-autostart":
                await InstallAutoStartAsync().ConfigureAwait(false);
                return true;

            case "uninstall-autostart":
                await DisableAutoStartAsync().ConfigureAwait(false);
                return true;

            case "restore-dns":
                await RestoreDnsAsync().ConfigureAwait(false);
                return true;

            // --- catalogue listings, which need no running instance ---------------
            case "strategies":
                WriteConsole(ControlCommands.DescribeStrategies());
                return true;

            case "isps":
                WriteConsole(ControlCommands.DescribeIsps());
                return true;

            case "version":
            case "v":
                WriteConsole($"{AppPaths.ProductName} {typeof(CommandLineTasks).Assembly.GetName().Version?.ToString(3) ?? "-"}");
                return true;

            case "help":
            case "h":
            case "?":
                WriteConsole(HelpText);
                return true;

            // --- talk to the running instance -------------------------------------
            case "status":
                return await SendAsync(ControlProtocol.Commands.Status).ConfigureAwait(false);

            case "test":
                return await SendAsync(ControlProtocol.Commands.Test, argument).ConfigureAwait(false);

            case "search":
                return await SendAsync(ControlProtocol.Commands.Search).ConfigureAwait(false);

            case "domains":
                return await SendAsync(ControlProtocol.Commands.Domains).ConfigureAwait(false);

            case "enable":
                return await SendAsync(ControlProtocol.Commands.Enable).ConfigureAwait(false);

            case "disable":
                return await SendAsync(ControlProtocol.Commands.Disable).ConfigureAwait(false);

            case "vodafone":
                return await SendAsync(ResolveVodafoneCommand(argument)).ConfigureAwait(false);

            // --minimized and anything else fall through to the window.
            default:
                return false;
        }
    }

    private static string ResolveVodafoneCommand(string? argument) => argument?.Trim().ToLowerInvariant() switch
    {
        "on" or "ac" or "aç" => ControlProtocol.Commands.VodafoneOn,
        "off" or "kapat" => ControlProtocol.Commands.VodafoneOff,
        _ => ControlProtocol.Commands.VodafoneStatus,
    };

    private static async Task<bool> SendAsync(string command, string? argument = null)
    {
        // Searching and re-tuning run real handshakes, so allow for them.
        var timeout = command is ControlProtocol.Commands.Search or ControlProtocol.Commands.Test
            ? TimeSpan.FromSeconds(90)
            : TimeSpan.FromSeconds(10);

        var response = await ControlClient
            .SendAsync(new ControlRequest { Command = command, Argument = argument }, timeout)
            .ConfigureAwait(false);

        if (response is null)
        {
            WriteConsole(
                $"{AppPaths.ProductName} çalışmıyor. Önce uygulamayı başlatın, sonra bu komutu yeniden çalıştırın.");
            return true;
        }

        WriteConsole(response.Text);
        return true;
    }

    private const string HelpText = """
        DPI Bypass — komut satırı

          DpiBypass.exe status              genel durum
          DpiBypass.exe test [alanadı]      erişimi sına (varsayılan: discord.com)
          DpiBypass.exe search              yöntemi yeniden ara
          DpiBypass.exe domains             korunan alan adlarını listele
          DpiBypass.exe strategies          yöntem kataloğu
          DpiBypass.exe isps                operatör profilleri
          DpiBypass.exe enable | disable    korumayı aç / kapat
          DpiBypass.exe vodafone [on|off]   hotspot TTL düzeltmesi (argümansız: durum)
          DpiBypass.exe restore-dns         DNS ayarlarını geri yükle
          DpiBypass.exe version             sürüm

        Durum ve denetim komutları çalışan uygulamaya bağlanır; uygulama kapalıysa
        bunu söyler. Argümansız çalıştırmak pencereyi açar.
        """;

    /// <summary>
    /// Prints to the console that launched us.
    /// </summary>
    /// <remarks>
    /// This is a WinExe so it has no console of its own; without attaching to the
    /// parent's, every command would appear to do nothing at all. Falling back to a
    /// dialog covers being launched from Explorer, where there is no console to
    /// attach to.
    /// </remarks>
    private static void WriteConsole(string text)
    {
        var attached = false;

        try
        {
            attached = AttachConsole(AttachParentProcess);
        }
        catch (Exception)
        {
            // Not on Windows, or no parent console.
        }

        if (attached)
        {
            try
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine(text);
                Console.Out.Flush();
                return;
            }
            catch (IOException)
            {
                // Redirected to something that went away.
            }
            finally
            {
                FreeConsole();
            }
        }

        try
        {
            System.Windows.MessageBox.Show(text, AppPaths.ProductName);
        }
        catch (Exception)
        {
            // Headless: nothing more we can do.
        }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    /// <summary>
    /// Turns autostart on because the installer's checkbox was ticked.
    /// </summary>
    /// <remarks>
    /// The choice is written to the settings file, not just acted on. The app
    /// reconciles the two at every launch - a setting that says "on" with no task
    /// registered puts the task back - so acting without recording the decision would
    /// let the next launch undo whatever the installer just did.
    /// </remarks>
    private static async Task InstallAutoStartAsync()
    {
        var store = new ConfigStore();
        var settings = store.Load();

        settings.StartWithWindows = true;
        store.Save(settings);

        await new AutoStartManager(log: AppLog.InfoSink)
            .EnableAsync(settings.StartMinimised)
            .ConfigureAwait(false);
    }

    /// <summary>Turns autostart off, recording the choice for the same reason.</summary>
    private static async Task DisableAutoStartAsync()
    {
        var store = new ConfigStore();
        var settings = store.Load();

        settings.StartWithWindows = false;
        store.Save(settings);

        await new AutoStartManager(log: AppLog.InfoSink).DisableAsync().ConfigureAwait(false);
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
