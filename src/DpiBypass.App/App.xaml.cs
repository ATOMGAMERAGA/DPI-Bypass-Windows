using System.IO;
using System.Windows;
using System.Windows.Threading;
using DpiBypass.App.Infrastructure;
using DpiBypass.App.ViewModels;
using DpiBypass.Core;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Ipc;
using DpiBypass.Core.Logging;
using DpiBypass.Core.Startup;

namespace DpiBypass.App;

public partial class App : Application
{
    /// <summary>
    /// How long a launch waits for the running copy to put its window up before it
    /// starts asking whether that copy is alive at all.
    /// </summary>
    /// <remarks>
    /// The running copy answers this from its activation listener thread using plain
    /// window-manager calls, so a healthy instance replies in milliseconds however
    /// busy its UI thread is. A second of silence therefore means something is really
    /// wrong, not that the other copy is mid-way through a measurement.
    /// </remarks>
    private static readonly TimeSpan FirstHandover = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The second, longer wait, given only to a copy that has proved it is alive.
    /// A busy instance is worth waiting for; a dead one is not.
    /// </summary>
    private static readonly TimeSpan BusyHandover = TimeSpan.FromSeconds(4);

    /// <summary>How long the running copy gets to answer its control channel.</summary>
    private static readonly TimeSpan LivenessProbe = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// How long a copy that is about to be ended gets to put the machine's DNS back.
    /// </summary>
    private static readonly TimeSpan StandDownTimeout = TimeSpan.FromSeconds(6);

    private SingleInstance? _instance;
    private ControlServer? _control;
    private ProtectionService? _service;
    private ThemeManager? _theme;
    private TrayIcon? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private DispatcherTimer? _visibilityWatchdog;
    private StartupPlan _plan = new(StartupVisibility.ShowWindow, "başlatılıyor");
    private bool _shuttingDown;
    private bool _windowEverShown;

    /// <summary>
    /// The main window's handle, readable from any thread.
    /// </summary>
    /// <remarks>
    /// Kept here so the activation listener can raise the window without going
    /// through the dispatcher. See <see cref="OnActivationRequested"/>.
    /// </remarks>
    private volatile nint _windowHandle;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF installs its SynchronizationContext when the dispatcher loop starts, and
        // that happens only once OnStartup has returned. Until then the context is
        // absent, so the continuation after any await here is handed to a thread pool
        // thread instead of coming back to the UI thread - and a window built on a
        // thread pool thread belongs to a dispatcher nobody ever runs. It never draws,
        // never reaches the taskbar, and never reports an error, while the process
        // stays alive holding the single instance lock. Installing the context first
        // keeps the whole of startup on the thread that owns the UI.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));

        PinWorkingDirectory();

        // Startup runs before the dispatcher loop, so an exception escaping here kills
        // the process without a window, a tray icon or a log line - which is exactly
        // what "the app does not open" looks like from the outside. Everything is
        // wrapped so that a failure is reported instead of being silent.
        try
        {
            Start(e);
        }
        catch (Exception ex)
        {
            ReportFatal(ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Moves the process off whatever directory it was launched from, onto its own.
    /// </summary>
    /// <remarks>
    /// A process inherits the current directory of whoever started it, and the things
    /// that start this one are not careful with it: the installer runs the app from a
    /// temporary folder it deletes as it exits, and the one line installer runs it
    /// from a shell whose directory may not even exist by then. A current directory
    /// that has been deleted is not a harmless detail on Windows - every
    /// <c>CreateProcess</c> made from it fails with "the system cannot find the path
    /// specified", so registering the logon task and configuring DNS both report a
    /// path error that has nothing to do with either. It is also where the loader
    /// looks first for a DLL or an executable named without a path, which is a
    /// planting risk in a process running as administrator. The install folder cannot
    /// be deleted while the running executable is in it, so it is always a valid
    /// answer.
    /// </remarks>
    private static void PinWorkingDirectory()
    {
        try
        {
            var install = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(install) && Directory.Exists(install))
            {
                Directory.SetCurrentDirectory(install);
            }
        }
        catch (Exception)
        {
            // Nothing here is fatal; the helpers all resolve absolute paths anyway.
        }
    }

    /// <summary>
    /// Gets the window on screen. Everything that can be slow is queued behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is deliberately synchronous from end to end. Anything awaited here
    /// runs before WPF starts pumping messages, which means an unpainted window, a
    /// tray icon the shell never gets told about, and an app that looks hung for as
    /// long as the work takes. Opening the driver, rewriting DNS and measuring
    /// strategies all belong after the loop is running, not in front of it.
    /// </para>
    /// <para>
    /// It is also deliberately phased. Only the first phase can stop the app: a
    /// window and the model behind it. Everything after that - the palette, the
    /// notification area icon, the control channel, the engine - is something the app
    /// is better off without than dead over, so each is taken on its own and a
    /// failure is logged and stepped past. Losing the whole application to a theme
    /// file that would not load is how a working installation ends up showing an
    /// error dialog and nothing else.
    /// </para>
    /// </remarks>
    private void Start(StartupEventArgs e)
    {
        // Timed and logged because "it took ages to open" is otherwise unanswerable
        // after the fact: the numbers say whether the wait was the handover, the
        // window, or something queued behind it.
        var clock = System.Diagnostics.Stopwatch.StartNew();

        AppPaths.MigrateLegacyState();
        AppLog.Initialise();
        AppLog.Info($"{AppPaths.ProductName} başlatılıyor · {AppPaths.Author}");
        AppLog.Info($"Sürüm {typeof(App).Assembly.GetName().Version?.ToString(4) ?? "-"} · "
            + $"klasör: {AppPaths.InstallDirectory} · komut satırı: {(e.Args.Length == 0 ? "(yok)" : string.Join(' ', e.Args))}");

        // The installer, the uninstaller and the command line share this executable.
        if (CommandLineTasks.IsHeadlessVerb(e.Args))
        {
            InstallExceptionHandlers();
            _ = RunHeadlessAsync(e.Args);
            return;
        }

        _instance = SingleInstance.Acquire();
        if (!TryContinueAsPrimary())
        {
            AppLog.Info($"Bu kopya devretti ({clock.ElapsedMilliseconds} ms).");
            AppLog.Shutdown();
            Shutdown();
            return;
        }

        AppLog.Info($"Tek örnek denetimi tamamlandı ({clock.ElapsedMilliseconds} ms).");

        // The packet driver is unopenable without elevation. The manifest asks for it,
        // so reaching this branch means the manifest was bypassed somehow; ask once and
        // hand over rather than starting up half working.
        if (!Elevation.IsElevated)
        {
            // Release the instance lock first, or the elevated copy we are about to
            // start would see it held and exit straight back out.
            _instance.Dispose();
            _instance = null;

            if (Elevation.TryRelaunchElevated(string.Join(' ', e.Args)))
            {
                Shutdown();
                return;
            }

            MessageBox.Show(
                $"{AppPaths.ProductName}, ağ sürücüsünü açabilmek için yönetici hakları gerektirir.\n\n"
                    + "Uygulamayı sağ tıklayıp \"Yönetici olarak çalıştır\" seçeneğiyle başlatın.",
                AppPaths.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        InstallExceptionHandlers();

        // --- phase one: the parts without which there is nothing to show -----------
        _service = new ProtectionService();
        _viewModel = new MainViewModel(_service, Dispatcher);

        // The palette is applied before the window is built so the first frame is
        // already the right colour, but a palette that will not load is a cosmetic
        // problem: the Fluent theme underneath it still renders every control.
        TryApplyTheme();

        var trayReady = TryCreateTrayIcon();

        if (!TryCreateWindow())
        {
            // No window, but the engine and the notification area icon are still
            // usable, so the app keeps protecting the connection instead of vanishing.
            if (_tray is null)
            {
                throw new InvalidOperationException(
                    "Uygulama penceresi oluşturulamadı ve bildirim alanı simgesi de kullanılamıyor.");
            }

            _tray.Notify(
                AppPaths.ProductName,
                "Pencere açılamadı; koruma bildirim alanı simgesinden yönetilebilir.",
                warning: true);
        }

        // Listening starts here rather than with the rest of the background work: the
        // window it answers with now exists, and until this is up a second launch
        // gets no answer at all - which is the one situation that ends with a healthy
        // instance being taken over and killed. It costs one thread.
        Guarded("Etkinleştirme dinleyicisi", () =>
        {
            // A second launch (Start menu, desktop shortcut) arrives here.
            _instance!.ActivationRequested += OnActivationRequested;
            _instance.BeginListening();
        });

        _plan = StartupPlan.Decide(
            e.Args,
            _service.Settings.StartMinimised,
            _service.Settings.HasShownWindow,
            trayReady);

        AppLog.Info($"Açılış kararı: {(_plan.ShowsWindow ? "pencere gösteriliyor" : "tepside")} ({_plan.Reason}).");

        if (_plan.ShowsWindow)
        {
            ShowMainWindow();
        }

        AppLog.Info($"Arayüz hazır ({clock.ElapsedMilliseconds} ms).");

        // Whatever was decided above, something has to be reachable once the message
        // loop is running: a window on screen, or an icon in the notification area.
        // The watchdog is the last line of defence for the failure this app has been
        // bitten by more than once - a live process the user cannot get to.
        StartVisibilityWatchdog();

        // --- phase two: everything the window does not depend on --------------------
        // Queued behind the first frame rather than run in front of it, and each part
        // guarded on its own so one refusing has no say over the others.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartBackgroundServices));
    }

    /// <summary>
    /// Brings up the parts of the app that the window does not need in order to
    /// appear: the control channel and the engine.
    /// </summary>
    private void StartBackgroundServices()
    {
        if (_shuttingDown)
        {
            return;
        }

        Guarded("Denetim kanalı", () =>
        {
            // The command line drives this instance rather than guessing from the
            // settings file what the running engine has decided.
            var commands = new ControlCommands(_service!);
            _control = new ControlServer(request => commands.HandleAsync(request), AppLog.InfoSink);
            _control.Start();
        });

        // Low-latency mode owns its own NetworkMonitor and is intentionally not tied
        // to whether the DPI engine is enabled.
        Guarded("Ping düşürme", () => _ = StartIndependentFeaturesAsync());

        if (_service!.Settings.StartEngineOnLaunch)
        {
            // The engine re-applies the redirect itself on the way up, reusing the
            // snapshot a previous run left behind, so a leftover is repaired rather
            // than restored here. If it fails to start it puts the resolvers back as
            // part of tearing down, which is the case the recovery below is for.
            Guarded("Koruma", () => _ = StartEngineAsync());
        }
        else
        {
            // Not starting the engine means nobody is going to re-apply the DNS
            // settings this session, and a run that ended without restoring them - a
            // crash, a hard power off, a copy that was taken over - leaves the machine
            // resolving against a loopback proxy that is not listening. That is a
            // machine with no name resolution at all, which reads as the app having
            // broken the internet.
            Guarded("DNS kurtarma", () => _ = RestorePendingDnsAsync());
        }
    }

    /// <summary>Runs one startup step, logging rather than propagating a failure.</summary>
    private static void Guarded(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            AppLog.Error($"{what} başlatılamadı", ex);
        }
    }

    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Gözlenmeyen görev hatası", args.Exception);
            args.SetObserved();
        };
        SessionEnding += OnSessionEnding;

        // The last line of defence for the one failure the user cannot work around on
        // their own. While the engine runs, every name on this machine is resolved by
        // this process, so a copy that exits without putting the resolvers back leaves
        // the whole computer unable to look anything up - and nothing on screen
        // explains it. This runs on an orderly exit however it was reached, including
        // one the code above did not plan for.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreDnsOnExit();
    }

    /// <summary>
    /// Puts the machine's resolvers back if this process is still holding them.
    /// </summary>
    /// <remarks>
    /// Deliberately blunt: no state to consult, no gate to take, and a hard timeout,
    /// because Windows gives a process a couple of seconds at most here and the
    /// alternative to finishing is a machine with no name resolution.
    /// </remarks>
    private void RestoreDnsOnExit()
    {
        if (_service is null || _service.State == ProtectionState.Stopped)
        {
            return;
        }

        try
        {
            Task.Run(() => _service.StopAsync()).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // On the way down with nowhere left to report it. The snapshot on disk is
            // what the next launch, and the uninstaller, restore from.
        }

        AppLog.Shutdown();
    }

    private void TryApplyTheme()
    {
        try
        {
#pragma warning disable WPF0001 // Fluent theming is still marked experimental.
            ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

            _theme = new ThemeManager(this);
            _theme.Apply();
        }
        catch (Exception ex)
        {
            // The window is built with whatever survived. Its own null check covers a
            // manager that never got as far as being constructed.
            AppLog.Error("Renk paleti uygulanamadı", ex);
        }
    }

    private bool TryCreateWindow()
    {
        try
        {
            _window = new MainWindow(_viewModel!, _theme);
            _window.CloseToTrayRequested += OnCloseToTray;
            _window.ExitRequested += () => _ = ShutdownAsync();
            _window.HandleReady += handle => _windowHandle = handle;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Pencere oluşturulamadı", ex);
            _window = null;
            return false;
        }
    }

    private void OnCloseToTray()
    {
        _window?.Hide();

        // Windows 11 files an icon it has not seen before behind the overflow
        // chevron, so "it is in the tray" is not, on its own, directions anybody can
        // follow. Say where to look, and what to do when it is not there.
        _tray?.Notify(
            AppPaths.ProductName,
            "Koruma çalışmaya devam ediyor. Pencereyi geri getirmek için saatin yanındaki "
                + "ok (^) altında bulunan simgeye tıklayın ya da kısayolu yeniden çalıştırın.");
    }

    /// <summary>
    /// Checks, once the message loop is running, that this process actually put
    /// something on screen - and puts the window up if it did not.
    /// </summary>
    /// <remarks>
    /// Everything before this point can succeed and still leave nothing for the user:
    /// the shell can drop a notification area icon it was offered while it was still
    /// starting up, and a window can be shown into a compositor state that never
    /// paints it. Neither reports an error. Rather than trust the startup path, the
    /// app looks at the result twice - soon, and then once more after the desktop has
    /// settled - and shows the window if it cannot account for itself.
    /// </remarks>
    private void StartVisibilityWatchdog()
    {
        var checks = 0;

        _visibilityWatchdog = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(4),
        };

        _visibilityWatchdog.Tick += (_, _) =>
        {
            checks++;

            var windowUp = _window is { IsVisible: true };

            // "Never got there", not "is not there now": a user who closed the window
            // to the tray in the first seconds meant it, and having it climb back out
            // would be its own kind of broken.
            if (!windowUp && _plan.ShowsWindow && !_windowEverShown)
            {
                AppLog.Warning("Pencere görünmüyor; yeniden gösteriliyor.");
                ShowMainWindow();
            }
            else if (!windowUp)
            {
                // Hidden on purpose: make sure the way back in is really there.
                _tray?.EnsureVisible();
            }
            else
            {
                // On screen, but a window handed to a compositor that has stopped
                // drawing the material is a see-through hole with the controls
                // floating in it - running, reachable, and indistinguishable from an
                // app that failed to open. The window paints itself again if so.
                _window?.EnsureBackgroundIsPainted();
            }

            if (checks >= 2)
            {
                // The one line in the log that answers "was it ever on screen?".
                AppLog.Info(
                    $"Görünürlük denetimi: pencere {(_windowEverShown ? "gösterildi" : "gizli")} · "
                        + $"bildirim alanı simgesi {(_tray is null ? "yok" : "var")} · arka plan: {WindowBackdrop.Availability}.");

                _visibilityWatchdog!.Stop();
                _visibilityWatchdog = null;
            }
        };

        _visibilityWatchdog.Start();
    }

    /// <summary>
    /// Records that the window has been seen, so later launches are allowed to start
    /// in the notification area.
    /// </summary>
    private void RememberWindowWasShown()
    {
        _windowEverShown = true;

        var service = _service;
        if (service is null || service.Settings.HasShownWindow)
        {
            return;
        }

        try
        {
            service.Settings.HasShownWindow = true;
            service.SaveSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error("Ayar kaydedilemedi", ex);
        }
    }

    /// <summary>
    /// Decides whether this process should build the UI. False means a healthy
    /// instance took the request and this launch is finished.
    /// </summary>
    /// <remarks>
    /// The hard case is the copy that is running, healthy, and busy. A start-up sweep
    /// measures strategies over real handshakes, and while it is doing that the
    /// dispatcher can be slow to answer. Treating "slow" as "dead" is expensive in a
    /// way nothing on screen explains: the running copy owns the driver handles and
    /// has pointed the machine's resolvers at its own DNS proxy, so ending it takes
    /// the connection with it and the replacement has to build all of it again. So
    /// liveness is established over the control channel - which is served from the
    /// thread pool and answers whatever the UI thread is doing - before this launch
    /// is allowed to conclude that nobody is home.
    /// </remarks>
    private bool TryContinueAsPrimary()
    {
        if (_instance!.IsPrimary)
        {
            return true;
        }

        // A copy on another user's desktop would answer this launch, raise its window
        // where this user cannot see it, and leave the shortcut looking dead.
        var located = SingleInstance.LocateInstances();
        if (located.OnAnotherDesktop && !located.OnThisDesktop)
        {
            AppLog.Warning("Uygulama başka bir Windows oturumunda çalışıyor.");
            MessageBox.Show(
                $"{AppPaths.ProductName} bu bilgisayarda başka bir kullanıcı oturumunda çalışıyor.\n\n"
                    + "Koruma tüm bilgisayar için tek bir kopyadan yürütülür. Uygulamayı burada "
                    + "kullanmak için diğer oturumdan kapatın ya da o oturumu kapatın.",
                AppPaths.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        // Windows will not let a background process take the foreground on its own.
        // This launch has the right and is about to exit, so it hands it over first;
        // without this the running copy shows its window behind everything the user
        // is looking at, which reads as the shortcut having done nothing at all.
        WindowActivation.AllowForegroundHandover();

        if (_instance.SignalExistingInstance(FirstHandover))
        {
            AppLog.Info("Uygulama zaten çalışıyor; pencereyi öne getirmesi istendi.");
            return false;
        }

        var alive = RunningInstanceAnswers();
        if (alive)
        {
            AppLog.Info("Çalışan kopya meşgul ama ayakta; pencereyi açması için daha uzun bekleniyor.");

            if (_instance.SignalExistingInstance(BusyHandover))
            {
                return false;
            }
        }

        // The lock is held by a copy that will not answer - hung, or half torn down.
        // Handing the user the "look in the notification area" message would be a lie:
        // there is nothing there to click, and every later launch would say the same.
        AppLog.Warning("Çalışan kopya yanıt vermedi; bu kopya devralıyor.");

        if (alive)
        {
            // It is about to be ended, and it is the process holding the machine's DNS
            // redirect. Asking it to stand down first is the difference between a
            // takeover the user does not notice and one that leaves them with no name
            // resolution until this copy has finished starting. It answered the pipe a
            // moment ago, so this is worth a short wait and no more.
            StandDownRunningInstance();
        }

        return _instance.TryTakeOver(AppLog.InfoSink);
    }

    /// <summary>
    /// Asks the copy that is about to be ended to put the machine's settings back.
    /// </summary>
    private static void StandDownRunningInstance()
    {
        try
        {
            var stop = Task.Run(() => ControlClient.SendAsync(
                new ControlRequest { Command = ControlProtocol.Commands.Disable },
                StandDownTimeout));

            if (stop.Wait(StandDownTimeout + TimeSpan.FromSeconds(1)) && stop.Result is { Ok: true })
            {
                AppLog.Info("Çalışan kopya korumayı bıraktı; DNS ayarları geri alındı.");
                return;
            }

            AppLog.Warning("Çalışan kopya korumayı bırakamadı; DNS bu kopya tarafından yeniden yapılandırılacak.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Çalışan kopyaya durma isteği gönderilemedi", ex);
        }
    }

    /// <summary>
    /// Asks the copy holding the instance lock for its status over the control pipe.
    /// True means it is alive, whatever its window is doing.
    /// </summary>
    private static bool RunningInstanceAnswers()
    {
        try
        {
            // On the thread pool, because this runs on the UI thread of a launch that
            // has no window yet and must not deadlock against its own dispatcher.
            var probe = Task.Run(() => ControlClient.SendAsync(
                new ControlRequest { Command = ControlProtocol.Commands.Status },
                LivenessProbe));

            return probe.Wait(LivenessProbe + TimeSpan.FromSeconds(1)) && probe.Result is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Answers another launch that asked for the window. Runs on the activation
    /// listener thread.
    /// </summary>
    private bool OnActivationRequested()
    {
        // The fast path, and the one that runs almost every time: the window is
        // already up and only needs raising, which is a window-manager call this
        // thread can make itself. Answering here rather than through the dispatcher is
        // what keeps a launch from concluding that a merely busy copy is a dead one -
        // and the launch that concludes that kills the copy, taking the packet driver
        // and the machine's DNS redirect down with it.
        if (WindowActivation.TryRaiseHandle(_windowHandle))
        {
            // The WPF side still gets told, so the window is un-minimised properly and
            // its backdrop is rechecked. Nobody waits for it.
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => ShowMainWindow()));
            return true;
        }

        // Hidden to the notification area, or not built yet. This one genuinely needs
        // the UI thread, because it is a WPF Show() rather than a raise. What this
        // returns is what the waiting launch is told, and the answer is the window's,
        // not the dispatcher's - an operation that completed while failing to raise
        // the window is still a launch that showed the user nothing.
        var shown = false;
        var operation = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => shown = ShowMainWindow()));

        // Matched to the second wait on the other side, so a busy dispatcher gets to
        // finish rather than being written off while the launch is still waiting.
        return operation.Wait(BusyHandover) == DispatcherOperationStatus.Completed && shown;
    }

    private bool TryCreateTrayIcon()
    {
        try
        {
            var tray = new TrayIcon();
            tray.OpenRequested += () => ShowMainWindow();
            tray.ToggleRequested += () => _viewModel?.ToggleCommand.Execute(null);
            tray.TestRequested += () => _viewModel?.TestCommand.Execute(null);
            tray.ExitRequested += () => _ = ShutdownAsync();

            _tray = tray;
            _viewModel!.StateChanged += () =>
                _tray?.Update(_viewModel.StatusHeadline, _viewModel.StatusDetail, _viewModel.IsRunning);

            return true;
        }
        catch (Exception ex)
        {
            // An icon the shell refuses is a degraded app, not a dead one.
            AppLog.Error("Bildirim alanı simgesi oluşturulamadı", ex);
            _tray = null;
            return false;
        }
    }

    private async Task RunHeadlessAsync(string[] args)
    {
        try
        {
            await CommandLineTasks.TryRunAsync(args).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Komut çalıştırılamadı", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    private async Task StartEngineAsync()
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            await _service.StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Koruma otomatik başlatılamadı", ex);
            _tray?.Notify(AppPaths.ProductName, ex.Message, warning: true);

            // Starting tears itself down on failure, DNS included, but a teardown that
            // was itself interrupted would leave the machine pointed at a proxy that is
            // not there. Checking costs a file test and covers the case that hurts.
            await RestorePendingDnsAsync().ConfigureAwait(true);
        }
    }

    private async Task StartIndependentFeaturesAsync()
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            await _service.StartIndependentFeaturesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Bağımsız ağ özellikleri başlatılamadı", ex);
            _tray?.Notify(AppPaths.ProductName, "Ping düşürme başlatılamadı; NIC ayarları değiştirilmedi.", warning: true);
        }
    }

    /// <summary>
    /// Puts the machine's resolvers back when a previous run left them redirected and
    /// this one is not going to take them over.
    /// </summary>
    private async Task RestorePendingDnsAsync()
    {
        try
        {
            var configurator = new DnsConfigurator(AppPaths.StateDirectory, AppLog.InfoSink);
            if (!configurator.HasPendingRestore)
            {
                return;
            }

            // The engine owns the resolvers whenever it is running, and it re-applies
            // them itself on the way up. Undoing a redirect it has just installed
            // would be worse than the leftover this is here to clean up.
            if (_service is not { State: ProtectionState.Stopped })
            {
                return;
            }

            AppLog.Warning("Önceki çalıştırmadan kalan DNS yönlendirmesi bulundu; geri alınıyor.");
            await configurator.RestoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("DNS ayarları geri yüklenemedi", ex);
        }
    }

    /// <summary>
    /// Puts the window in front of the user. Returns whether it is genuinely there.
    /// </summary>
    /// <remarks>
    /// The return value is not decoration: it is what a second launch is told, and
    /// that launch takes over when the answer is no. Reporting success because
    /// <c>Show()</c> did not throw is how a wedged copy keeps every later launch
    /// from ever producing a window.
    /// </remarks>
    private bool ShowMainWindow()
    {
        if (_window is null)
        {
            return false;
        }

        var shown = WindowActivation.BringToFront(_window);

        if (shown)
        {
            RememberWindowWasShown();

            // Raising a window can rebuild its handle, and a rebuilt handle has none
            // of the backdrop state the old one was given - which leaves a window
            // that paints nothing over a material nobody is drawing.
            _window.EnsureBackgroundIsPainted();
        }
        else
        {
            AppLog.Warning("Pencere öne getirilemedi.");
        }

        return shown;
    }

    /// <summary>
    /// Last resort for a startup failure: say something the user can act on and leave
    /// a file behind, because at this point there is no window and no log page.
    /// </summary>
    internal static void ReportFatal(Exception exception)
    {
        var detail = exception.ToString();

        try
        {
            AppLog.Error("Uygulama başlatılamadı", exception);
        }
        catch (Exception)
        {
            // The logger itself may be what failed.
        }

        var crashPath = Path.Combine(Path.GetTempPath(), "dpibypass-crash.log");
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            crashPath = Path.Combine(AppPaths.LogDirectory, "crash.log");
        }
        catch (Exception)
        {
            // Fall back to the temp folder, which is always writable.
        }

        try
        {
            File.AppendAllText(crashPath, $"{DateTimeOffset.Now:O}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing more we can do on the way down.
        }

        try
        {
            // Flushed before the dialog, not after: the dialog blocks until the user
            // dismisses it, and a user who kills the process at that point would take
            // the explanation with them.
            AppLog.Shutdown();
        }
        catch (Exception)
        {
            // The logger itself may be what failed.
        }

        try
        {
            MessageBox.Show(
                $"{AppPaths.ProductName} başlatılamadı.\n\n{exception.Message}\n\nAyrıntılar: {crashPath}",
                AppPaths.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // A headless session cannot show a dialog; the file is still written.
        }
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Logging off or shutting down: DNS has to go back before the session dies or
        // the machine boots pointing at a loopback proxy that is not running.
        AppLog.Info("Oturum kapanıyor; ayarlar geri alınıyor.");
        StopServiceSynchronously();
    }

    /// <summary>
    /// Stops the service from a context that cannot await, without deadlocking.
    /// </summary>
    /// <remarks>
    /// Blocking the UI thread on the service's own task would wait forever: its
    /// continuations are queued to this very thread. Running it on the thread pool
    /// gives it a scheduler that is free to make progress, and the timeout keeps a
    /// wedged stop from holding up the logoff Windows is going to force anyway.
    /// </remarks>
    private void StopServiceSynchronously()
    {
        var service = _service;
        if (service is null)
        {
            return;
        }

        try
        {
            Task.Run(() => service.StopAsync()).Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            AppLog.Error("Koruma durdurulamadı", ex);
        }
    }

    private async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        try
        {
            _visibilityWatchdog?.Stop();
            _visibilityWatchdog = null;
            _window?.Hide();
            _viewModel?.Detach();

            if (_control is not null)
            {
                await _control.DisposeAsync().ConfigureAwait(true);
                _control = null;
            }

            if (_service is not null)
            {
                await _service.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Kapatma sırasında hata", ex);
        }
        finally
        {
            _tray?.Dispose();
            _theme?.Dispose();
            _instance?.Dispose();
            AppLog.Info("Kapatıldı.");

            // The writer is a background thread, so without this the last few lines -
            // the ones that say why the app is closing - never reach the file.
            AppLog.Shutdown();
            Shutdown();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Arayüz hatası", e.Exception);

        // A UI glitch is not a reason to drop protection and leave DNS redirected.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLog.Error("Beklenmeyen hata", exception);
        }

        StopServiceSynchronously();
    }
}
