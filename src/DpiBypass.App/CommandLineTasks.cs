using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DpiBypass.App.Infrastructure;
using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Ipc;
using DpiBypass.Core.Logging;
using DpiBypass.Core.Network;
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
        "install-autostart", "uninstall-autostart", "restore-dns", "dns-watchdog", "health-check",
        "strategies", "isps", "version", "v", "help", "h", "?",
        "status", "test", "search", "domains", "enable", "disable", "vodafone", "latency",
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

            case "dns-watchdog":
                await RunDnsWatchdogAsync(argument).ConfigureAwait(false);
                return true;

            case "health-check":
                Environment.ExitCode = SingleInstance.RequestVisibleWindow(TimeSpan.FromSeconds(12)) ? 0 : 1;
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

            case "latency":
                return await RunLatencyAsync(argument).ConfigureAwait(false);

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

    private static async Task<bool> RunLatencyAsync(string? argument)
    {
        switch (argument?.Trim().ToLowerInvariant())
        {
            case "on":
            case "ac":
            case "aç":
                return await SendAsync(ControlProtocol.Commands.LatencyOn).ConfigureAwait(false);

            case "off":
            case "kapat":
                return await SendAsync(ControlProtocol.Commands.LatencyOff).ConfigureAwait(false);

            case "test":
            {
                var network = NetworkFingerprint.Capture();
                if (!network.IsOnline)
                {
                    WriteConsole("Aktif internet bağlantısı bulunamadı.");
                    return true;
                }

                var measurement = await new LatencyProbe().MeasureAsync(network).ConfigureAwait(false);
                WriteConsole(LatencyOptimizer.FormatMeasurement(network, measurement));
                return true;
            }

            case "restore":
                return await RestoreLatencyAsync().ConfigureAwait(false);

            default:
                return await SendAsync(ControlProtocol.Commands.LatencyStatus).ConfigureAwait(false);
        }
    }

    private static async Task<bool> RestoreLatencyAsync()
    {
        // Prefer the owner process so its monitor is stopped as well as the settings
        // being restored. During uninstall there is deliberately no owner process,
        // so the exact same snapshot is recovered locally.
        var response = await ControlClient.SendAsync(
            new ControlRequest { Command = ControlProtocol.Commands.LatencyRestore },
            TimeSpan.FromSeconds(45)).ConfigureAwait(false);

        if (response is not null)
        {
            WriteConsole(response.Text);
            return true;
        }

        var store = new ConfigStore();
        var settings = store.Load();
        settings.LowLatencyMode = false;
        store.Save(settings);

        await using var optimizer = new LatencyOptimizer(log: AppLog.InfoSink);
        var result = await optimizer.RestoreAsync().ConfigureAwait(false);
        WriteConsole(result.StatusLine);
        return true;
    }

    private static async Task<bool> SendAsync(string command, string? argument = null)
    {
        // Searching and re-tuning run real handshakes, so allow for them.
        var timeout = command is ControlProtocol.Commands.Search or ControlProtocol.Commands.Test
                or ControlProtocol.Commands.LatencyOn or ControlProtocol.Commands.LatencyOff
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
          DpiBypass.exe latency status      düşük-gecikme durumunu göster
          DpiBypass.exe latency on | off    ölçümlü düşük-gecikme modunu aç / kapat
          DpiBypass.exe latency test        hiçbir ayar değiştirmeden RTT/jitter ölç
          DpiBypass.exe latency restore     özgün NIC ayarlarını kurtar
          DpiBypass.exe restore-dns         DNS ayarlarını geri yükle
          DpiBypass.exe version             sürüm

          DpiBypass.exe --show              pencereyi her koşulda aç
          DpiBypass.exe --minimized         tepside başlat (oturum açma görevi bunu kullanır)

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

    /// <summary>
    /// Launches a differently named copy which survives a crash/end-task of the UI
    /// process and restores DNS as soon as its owner disappears unexpectedly.
    /// </summary>
    public static bool TryStartDnsWatchdog()
    {
        var recoveryExecutable = Path.Combine(AppContext.BaseDirectory, "DpiBypass.Recovery.exe");
        if (!File.Exists(recoveryExecutable))
        {
            // Developer builds may not have run the publish target yet. The installed
            // payload always carries the separately named copy.
            recoveryExecutable = Environment.ProcessPath ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(recoveryExecutable) || !File.Exists(recoveryExecutable))
        {
            AppLog.Warning("DNS crash watchdog could not start: executable not found.");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = recoveryExecutable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--dns-watchdog");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            AppLog.Info($"DNS crash watchdog started (PID {process.Id}).");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("DNS crash watchdog could not start", ex);
            return false;
        }
    }

    private static async Task RunDnsWatchdogAsync(string? parentPidText)
    {
        if (!int.TryParse(parentPidText, out var parentPid) || parentPid <= 0)
        {
            Environment.ExitCode = 2;
            return;
        }

        try
        {
            using var parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // It exited between Process.Start and this copy opening the handle.
        }
        catch (InvalidOperationException)
        {
            // Already gone; recovery below is still the right operation.
        }

        // A normal shutdown restores and removes the snapshot before the process exits.
        // A crash leaves it behind. Give filesystem buffers a moment to settle, then
        // retry transient adapter/PowerShell failures without ever deleting the source.
        await Task.Delay(500).ConfigureAwait(false);

        var configurator = new DnsConfigurator(AppPaths.StateDirectory, AppLog.InfoSink);
        for (var attempt = 1; attempt <= 3 && configurator.HasPendingRestore; attempt++)
        {
            try
            {
                AppLog.Warning($"Owner process ended with DNS redirected; recovery attempt {attempt}.");
                await configurator.RestoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"DNS crash recovery attempt {attempt} failed", ex);
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2)).ConfigureAwait(false);
                }
            }
        }

        Environment.ExitCode = configurator.HasPendingRestore ? 1 : 0;
    }
}
