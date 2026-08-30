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
        "status", "test", "search", "domains", "enable", "disable", "hotspot", "vodafone", "latency",
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
                // Long, because the answer it is waiting for is worth waiting for. A
                // running copy that has not finished starting keeps saying so, and this
                // returns the moment its window is up; a machine with nothing running
                // is answered immediately, without spending any of the budget. The
                // short timeout this used to have expired against a first launch that
                // was still loading and reported a working installation as broken.
                // 0 = a rendered window, 1 = a primary copy answered but its window
                // failed, 2 = no primary copy. The installer may start an app only for
                // the last case; treating a broken-but-live copy as "nobody" made it
                // launch a second long recovery cycle and doubled failed install time.
                Environment.ExitCode = (int)SingleInstance.RequestVisibleWindow(HealthCheckTimeout(argument));
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

            case "hotspot":
                return await SendAsync(ResolveHotspotCommand(argument)).ConfigureAwait(false);

            // Original product spelling retained alongside the generic hotspot alias.
            case "vodafone":
                return await SendAsync(ResolveVodafoneCommand(argument)).ConfigureAwait(false);

            case "latency":
                return await RunLatencyAsync(args).ConfigureAwait(false);

            // --minimized and anything else fall through to the window.
            default:
                return false;
        }
    }

    /// <summary>
    /// How long <c>--health-check</c> waits for a window, optionally given in seconds
    /// on the command line so a caller can choose its own budget.
    /// </summary>
    private static TimeSpan HealthCheckTimeout(string? argument)
        => int.TryParse(argument, out var seconds) && seconds is > 0 and <= 600
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(60);

    private static string ResolveHotspotCommand(string? argument) => argument?.Trim().ToLowerInvariant() switch
    {
        "diagnose" or "tanila" or "tanıla" or "test" => ControlProtocol.Commands.HotspotDiagnose,
        "cleanup" or "temizle" => ControlProtocol.Commands.HotspotCleanup,
        _ => ControlProtocol.Commands.HotspotStatus,
    };

    private static string ResolveVodafoneCommand(string? argument) => argument?.Trim().ToLowerInvariant() switch
    {
        "on" or "ac" or "aç" => ControlProtocol.Commands.VodafoneOn,
        "off" or "kapat" => ControlProtocol.Commands.VodafoneOff,
        "diagnose" or "tanila" or "tanıla" or "test" => ControlProtocol.Commands.HotspotDiagnose,
        "cleanup" or "temizle" => ControlProtocol.Commands.HotspotCleanup,
        _ => ControlProtocol.Commands.VodafoneStatus,
    };

    /// <summary>
    /// The <c>latency</c> verb family.
    /// </summary>
    /// <remarks>
    /// Every command that existed before still means exactly what it did. The additions
    /// are new sub-verbs and flags, so a script written against the old surface keeps
    /// working without change.
    /// </remarks>
    private static async Task<bool> RunLatencyAsync(string[] args)
    {
        var sub = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : string.Empty;
        var flags = args.Skip(2).Select(value => value.Trim()).ToArray();

        switch (sub)
        {
            case "on":
            case "ac":
            case "aç":
                return await SendAsync(ControlProtocol.Commands.LatencyOn).ConfigureAwait(false);

            case "off":
            case "kapat":
                return await SendAsync(ControlProtocol.Commands.LatencyOff).ConfigureAwait(false);

            case "test":
                return await RunLatencyMeasurementAsync(TargetFlag(flags)).ConfigureAwait(false);

            case "optimize":
                return await SendAsync(HasFlag(flags, "--deep")
                    ? ControlProtocol.Commands.LatencyDeepTest
                    : ControlProtocol.Commands.LatencyQuickTest).ConfigureAwait(false);

            case "loaded-test":
            case "deep-test":
                return await SendAsync(ControlProtocol.Commands.LatencyDeepTest).ConfigureAwait(false);

            case "retest":
                return await SendAsync(ControlProtocol.Commands.LatencyRetest).ConfigureAwait(false);

            case "report":
                return await SendAsync(ControlProtocol.Commands.LatencyReport).ConfigureAwait(false);

            case "target":
                return await SendAsync(ControlProtocol.Commands.LatencyTarget, TargetFlag(flags) ?? args.ElementAtOrDefault(2))
                    .ConfigureAwait(false);

            case "profiles":
                return await RunLatencyProfilesAsync(flags).ConfigureAwait(false);

            case "restore":
                return await RestoreLatencyAsync().ConfigureAwait(false);

            case "status":
            default:
                return await SendAsync(HasFlag(flags, "--json")
                    ? ControlProtocol.Commands.LatencyStatusJson
                    : ControlProtocol.Commands.LatencyStatus).ConfigureAwait(false);
        }
    }

    /// <summary>Reads <c>--target host[:port]</c>, or a bare value after the sub-verb.</summary>
    private static string? TargetFlag(IReadOnlyList<string> flags)
    {
        for (var index = 0; index < flags.Count; index++)
        {
            if (string.Equals(flags[index], "--target", StringComparison.OrdinalIgnoreCase)
                && index + 1 < flags.Count)
            {
                return flags[index + 1];
            }

            if (flags[index].StartsWith("--target=", StringComparison.OrdinalIgnoreCase))
            {
                return flags[index]["--target=".Length..];
            }
        }

        return flags.FirstOrDefault(flag => !flag.StartsWith("--", StringComparison.Ordinal));
    }

    private static bool HasFlag(IReadOnlyList<string> flags, string name)
        => flags.Any(flag => string.Equals(flag, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Measures without a running instance, because measuring changes nothing.
    /// </summary>
    /// <remarks>
    /// Every other latency verb goes through the running copy, which owns the adapter
    /// state. This one deliberately does not: a user diagnosing a connection should not
    /// have to start the app first to find out what their ping is.
    /// </remarks>
    private static async Task<bool> RunLatencyMeasurementAsync(string? target)
    {
        var network = NetworkFingerprint.Capture();
        if (!network.IsOnline)
        {
            WriteConsole("Aktif internet bağlantısı bulunamadı.");
            return true;
        }

        var spec = LatencyTargetSpec.Reference;
        if (!string.IsNullOrWhiteSpace(target)
            && !LatencyTargetSpec.TryParse(target, out spec, out var error))
        {
            WriteConsole(error ?? "Hedef ayrıştırılamadı.");
            return true;
        }

        var resolver = new LatencyTargetResolver(log: AppLog.InfoSink);
        var resolution = await resolver.ResolveAsync(spec).ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            WriteConsole(resolution.Failure ?? "Ölçüm hedefi çözümlenemedi.");
            return true;
        }

        // Survey first so the benchmark pass and any later comparison all use the one
        // endpoint that actually answers on this network.
        var probe = new LatencyProbe();
        LatencyEndpoint endpoint = resolution.Endpoints[0];
        LatencyMeasurement? survey = null;

        foreach (var candidate in resolution.Endpoints)
        {
            survey = await probe
                .MeasureAsync(network, LatencyProbeRequest.Survey.For(candidate))
                .ConfigureAwait(false);
            endpoint = candidate;

            if (survey.HasRemoteConnectivity)
            {
                break;
            }
        }

        var measurement = survey is { HasRemoteConnectivity: true }
            ? await probe
                .MeasureAsync(network, LatencyProbeRequest.Benchmark.For(endpoint))
                .ConfigureAwait(false)
            : survey!;

        WriteConsole(LatencyReport.Measurement(
            network,
            measurement,
            LatencyPathAnalysis.Describe(measurement),
            endpoint,
            resolution.Notice));

        return true;
    }

    private static async Task<bool> RunLatencyProfilesAsync(IReadOnlyList<string> flags)
    {
        if (!flags.Any(flag => string.Equals(flag, "clear", StringComparison.OrdinalIgnoreCase)))
        {
            WriteConsole("Kullanım: DpiBypass.exe latency profiles clear");
            return true;
        }

        var response = await ControlClient.SendAsync(
            new ControlRequest { Command = ControlProtocol.Commands.LatencyProfilesClear },
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        if (response is not null)
        {
            WriteConsole(response.Text);
            return true;
        }

        // No running copy: the cache is a plain file, and removing it can never leave a
        // machine in a changed state, so it is safe to do from here.
        try
        {
            if (File.Exists(AppPaths.LatencyProfilesFile))
            {
                File.Delete(AppPaths.LatencyProfilesFile);
                WriteConsole("Kayıtlı gecikme sonuçları silindi.");
            }
            else
            {
                WriteConsole("Silinecek kayıtlı gecikme sonucu yoktu.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteConsole($"Kayıtlı sonuçlar silinemedi: {ex.Message}");
        }

        return true;
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

        // The snapshot covers everything a completed run recorded. The sweep covers the
        // one case it cannot: a policy created by a build that died before it could
        // write the record. During uninstall there is no second chance to notice.
        var removed = await new LoadedLatencyLane(log: AppLog.InfoSink)
            .ClearOwnedPoliciesAsync()
            .ConfigureAwait(false);

        WriteConsole(removed > 0
            ? $"{result.StatusLine}{Environment.NewLine}{removed} QoS ilkesi kaldırıldı."
            : result.StatusLine);

        return true;
    }

    private static async Task<bool> SendAsync(string command, string? argument = null)
    {
        // Searching and re-tuning run real handshakes, so allow for them.
        // The deep test waits for the user to start a transfer, so it gets the longest
        // budget of anything here; the others only have to outlast a benchmark.
        var timeout = command switch
        {
            ControlProtocol.Commands.LatencyDeepTest => TimeSpan.FromMinutes(6),
            ControlProtocol.Commands.Search
                or ControlProtocol.Commands.Test
                or ControlProtocol.Commands.LatencyOn
                or ControlProtocol.Commands.LatencyOff
                or ControlProtocol.Commands.LatencyQuickTest
                or ControlProtocol.Commands.LatencyRetest => TimeSpan.FromMinutes(4),
            _ => TimeSpan.FromSeconds(10),
        };

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
          DpiBypass.exe vodafone [on|off]   Vodafone Sınırsız Modu (güvenli tanılama)
          DpiBypass.exe vodafone diagnose   Vodafone bağlantısını incele
          DpiBypass.exe hotspot diagnose    mobil paylaşım bağlantısını incele (hiçbir şeyi değiştirmez)
          DpiBypass.exe hotspot cleanup     yalnız eski TTL alt özelliğini temizle
          DpiBypass.exe hotspot             hotspot durumunu göster
          DpiBypass.exe latency status [--json]
                                            düşük-gecikme durumunu göster (--json: kararlı şema)
          DpiBypass.exe latency on | off    ölçümlü düşük-gecikme modunu aç / kapat
          DpiBypass.exe latency test [--target ana_bilgisayar[:port]]
                                            hiçbir ayar değiştirmeden RTT/jitter ölç
          DpiBypass.exe latency target <hedef>
                                            ölçüm hedefini kalıcı olarak ayarla (boş: genel referans)
          DpiBypass.exe latency optimize --quick | --deep
                                            hızlı ölçüm ya da yük altında derin test
          DpiBypass.exe latency loaded-test yük altında derin test (siz indirme/gönderim başlatın)
          DpiBypass.exe latency retest      kayıtlı sonucu yok sayıp baştan ölç
          DpiBypass.exe latency report      son tam raporu yazdır
          DpiBypass.exe latency profiles clear
                                            kayıtlı per-ağ sonuçlarını sil
          DpiBypass.exe latency restore     özgün NIC ayarlarını kurtar
          DpiBypass.exe restore-dns         DNS ayarlarını geri yükle
          DpiBypass.exe version             sürüm

          DpiBypass.exe --show              pencereyi her koşulda aç
          DpiBypass.exe --minimized         tepside başlat (oturum açma görevi bunu kullanır)
          DpiBypass.exe --health-check [sn] çalışan kopyanın penceresini açmasını bekle
                                            (0 = açıldı, 1 = pencere hatası, 2 = uygulama yok)
          DpiBypass.exe --ui-selftest       arayüzü sınayıp çıkar; ağ motorunu açmaz
                                            (0 = pencere gerçekten çizildi, 1 = çizilmedi)

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
        => TryStartRecoveryProcess(
            ["--dns-watchdog", Environment.ProcessId.ToString()],
            "DNS crash watchdog");

    /// <summary>
    /// Launches the same separately named copy to put DNS back right now, rather than
    /// when this process exits.
    /// </summary>
    /// <remarks>
    /// Used by the fatal paths. A process that is already going down is the worst
    /// possible place to spend thirty seconds shelling out to PowerShell: the wait is
    /// what the user experiences as the application hanging before it disappears, and
    /// it delays the crash log and the error dialog that are the only account of what
    /// happened. Handing the job to a process that is not dying costs nothing and is
    /// more likely to finish it.
    /// </remarks>
    public static bool TryStartDnsRecovery()
        => TryStartRecoveryProcess(["--restore-dns"], "DNS recovery helper");

    private static bool TryStartRecoveryProcess(string[] arguments, string what)
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
            AppLog.Warning($"{what} could not start: executable not found.");
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

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            AppLog.Info($"{what} started (PID {process.Id}).");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"{what} could not start", ex);
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
