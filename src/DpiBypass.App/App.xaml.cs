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
    private static readonly TimeSpan FirstHandover = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The second, longer wait, given only to a copy that has proved it is alive.
    /// A busy instance is worth waiting for; a dead one is not.
    /// </summary>
    private static readonly TimeSpan BusyHandover = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The whole budget given to a copy that keeps saying it is still starting.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. This is the wait that covers the first run after an
    /// installation, where the copy being waited for is loading a self-contained
    /// runtime, reading its settings and opening the packet driver on a machine that
    /// is still busy with the installer that started it. Every second spent here is a
    /// second not spent killing a healthy instance, which is the failure this is for.
    /// </remarks>
    private static readonly TimeSpan StartingHandover = TimeSpan.FromSeconds(90);

    /// <summary>How long the running copy gets to answer its control channel.</summary>
    private static readonly TimeSpan LivenessProbe = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// The budget a dying process gets for putting the machine's DNS back before it
    /// stops being able to say anything at all.
    /// </summary>
    /// <remarks>
    /// Small, and that is the fix. The recovery used to be given thirty seconds ahead
    /// of the crash report, so a failure to start showed the user nothing for the best
    /// part of a minute and then vanished - "it hung and then it crashed" is precisely
    /// what that looks like from outside. The separately named watchdog process does
    /// the same work without a deadline and outlives this one, so blocking here buys
    /// very little and costs the only account of what went wrong.
    /// </remarks>
    private static readonly TimeSpan FatalRecoveryBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the window is given to draw its first frame before the watchdog starts
    /// treating the silence as a fault, and the backoff after that.
    /// </summary>
    /// <remarks>
    /// The first entry is generous because a healthy window does not use it at all:
    /// <c>ContentRendered</c> ends the wait the moment it arrives, so these intervals
    /// are only ever spent by a window that is already in trouble.
    /// </remarks>
    private static readonly TimeSpan[] HealthCheckSchedule =
    [
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(8),
    ];

    /// <summary>
    /// How close together two launches have to be for the second to count as the user
    /// saying the window is not really there.
    /// </summary>
    private static readonly TimeSpan RedundantActivationWindow = TimeSpan.FromMinutes(1);

    /// <summary>How long the UI self test waits for a window before reporting failure.</summary>
    private static readonly TimeSpan SelfTestBudget = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Runs the whole normal startup, reports whether a real window reached the screen,
    /// and exits with 0 or 1.
    /// </summary>
    /// <remarks>
    /// Nothing is bypassed by it: the same elevation check, the same instance lock, the
    /// same window. All it changes is that the packet driver, DNS and the control
    /// channel are left alone - a window can be verified without any of them, and
    /// verifying a window is no reason to take over a machine's name resolution - and
    /// that the process ends with a verdict instead of waiting for the user. Its report
    /// goes to the same log folder as everything else.
    /// </remarks>
    private const string SelfTestSwitch = "--ui-selftest";

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
    private bool _dnsWatchdogStarted;

    /// <summary>
    /// Whether the app currently intends the user to be looking at the window. Set by
    /// the startup plan and by every activation request; cleared when the user closes
    /// the window to the notification area, which is the one case where "not on screen"
    /// is the right answer rather than a fault.
    /// </summary>
    private bool _windowWanted;

    /// <summary>Whether a confirmed, reachable frame has been seen in this process.</summary>
    private bool _windowConfirmed;

    private int _recoveryAttempts;
    private bool _recreationSpent;
    private bool _failureReported;

    /// <summary>
    /// Launches that asked for a window this process already believed was on screen.
    /// More than one in a row is the user telling us they cannot see it.
    /// </summary>
    private int _redundantActivations;

    private DateTime _lastRedundantActivation;

    private bool _selfTest;

    /// <summary>What this process exits with. Only the self test sets anything else.</summary>
    private int _exitCode;

    /// <summary>
    /// Whether start-up has finished and the dispatcher loop is running, so an
    /// activation request can be answered for real instead of with "still starting".
    /// Read from the activation listener thread, written once and never cleared.
    /// </summary>
    private volatile bool _startupComplete;

    /// <summary>
    /// Whether this copy has finished trying to get its window on screen and failed.
    /// Read from the activation listener thread: it is what turns a later launch from
    /// "wait for the running copy" into "take over from it".
    /// </summary>
    private volatile bool _recoveryExhausted;

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

        StartupTrace.Mark("App.OnStartup girildi");

        PinWorkingDirectory();
        StartupTrace.Mark($"çalışma klasörü sabitlendi: {AppContext.BaseDirectory}");

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
        AppPaths.MigrateLegacyState();
        AppLog.Initialise();
        StartupTrace.Mark("günlük hazır");
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

        _selfTest = StartupPlan.HasSwitch(e.Args, SelfTestSwitch);

        // The logon task now fires on its own trigger rather than waiting to be started
        // by the Run key, which is what makes autostart survive a registry entry going
        // missing. The Windows Startup Apps switch has to keep working in spite of that,
        // so a launch Windows made stands down when Windows has recorded it as off. A
        // launch a person made is never affected.
        if (StartupPlan.StartedByWindows(e.Args) && AutoStartManager.IsTurnedOffInWindows())
        {
            AppLog.Info("Otomatik başlatma Windows'ta kapatılmış; bu açılış atlanıyor.");
            Shutdown();
            return;
        }

        _instance = SingleInstance.Acquire();
        StartupTrace.Mark($"tek örnek kilidi alındı · birincil={_instance.IsPrimary}");

        if (!TryContinueAsPrimary())
        {
            StartupTrace.Mark("ikincil kopya · devredildi, çıkılıyor");
            Shutdown();
            return;
        }

        StartupTrace.Mark("bu kopya birincil");

        // Before anything that takes time, and that is the whole point. Everything
        // below - the elevation check, the service, the palette, the window - happens
        // in the seconds during which a second launch is deciding whether anyone is
        // home, and until this thread is running the honest answer to that question
        // never gets sent. A launch that hears nothing takes over and ends this
        // process, which is a window appearing and vanishing again during an install.
        // Listening from here means the answer is "on its way" from the first moment.
        Guarded("Etkinleştirme dinleyicisi", () =>
        {
            // A second launch (Start menu, desktop shortcut) arrives here.
            _instance!.ActivationRequested += OnActivationRequested;
            _instance.BeginListening();
            StartupTrace.Mark("etkinleştirme dinleyicisi çalışıyor");
        });

        // The packet driver is unopenable without elevation. The manifest asks for it,
        // so reaching this branch means the manifest was bypassed somehow; ask once and
        // hand over rather than starting up half working.
        StartupTrace.Mark($"yükseltme durumu · yönetici={Elevation.IsElevated}");

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
        StartupTrace.Mark("ProtectionService kurucusu başladı");
        _service = new ProtectionService();
        StartupTrace.Mark("ProtectionService kurucusu bitti");

        StartupTrace.Mark("MainViewModel kurucusu başladı");
        _viewModel = new MainViewModel(_service, Dispatcher);
        StartupTrace.Mark("MainViewModel kurucusu bitti");

        // The palette is applied before the window is built so the first frame is
        // already the right colour, but a palette that will not load is a cosmetic
        // problem: the Fluent theme underneath it still renders every control.
        StartupTrace.Mark("tema başlatılıyor");
        TryApplyTheme();
        ApplyMotionPreference();
        StartupTrace.Mark($"tema hazır · koyu={_theme?.IsDark}");

        StartupTrace.Mark("bildirim alanı simgesi başlatılıyor");
        var trayReady = TryCreateTrayIcon();
        StartupTrace.Mark($"bildirim alanı simgesi hazır · var={trayReady}");

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

        _plan = _selfTest
            ? new StartupPlan(StartupVisibility.ShowWindow, "arayüz kendi kendini sınıyor")
            : StartupPlan.Decide(
                e.Args,
                _service.Settings.StartMinimised,
                _service.Settings.HasShownWindow,
                trayReady);

        AppLog.Info($"Açılış kararı: {(_plan.ShowsWindow ? "pencere gösteriliyor" : "tepside")} ({_plan.Reason}).");
        StartupTrace.Mark($"açılış kararı · {(_plan.ShowsWindow ? "pencere" : "tepsi")} ({_plan.Reason})");

        _windowWanted = _plan.ShowsWindow;

        if (_plan.ShowsWindow)
        {
            ShowMainWindow();
        }

        // Whatever was decided above, something has to be reachable once the message
        // loop is running: a window on screen, or an icon in the notification area.
        // The watchdog is the last line of defence for the failure this app has been
        // bitten by more than once - a live process the user cannot get to.
        StartVisibilityWatchdog();

        // Answering activation requests for real starts here, and not one line sooner.
        // Until OnStartup returns, the dispatcher is not pumping messages, so a request
        // answered by hopping onto it would wait out its whole timeout and report this
        // perfectly healthy process as one that cannot produce a window. Queued at Send
        // so it is the first thing the loop does once it is running; up to that moment
        // the listener keeps saying "still starting", which is the truth.
        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => _startupComplete = true));

        StartDispatcherHeartbeat();

        StartupTrace.Mark("açılış tamamlandı · ileti döngüsüne geçiliyor");

        if (_selfTest)
        {
            // The self test exists to answer one question - does this build put a real
            // window on screen - so it deliberately never opens the driver, rewrites
            // DNS or touches the network.
            AppLog.Info("Arayüz kendi kendini sınama kipi: ağ motoru başlatılmıyor.");
            StartSelfTestDeadline();
            return;
        }

        // --- phase two: everything the window does not depend on --------------------
        // Queued behind the first frame rather than run in front of it, and each part
        // guarded on its own so one refusing has no say over the others.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartBackgroundServices));
    }

    /// <summary>
    /// Measures how long the dispatcher took to get to a queued item, once.
    /// </summary>
    /// <remarks>
    /// The one number that separates "the window is broken" from "the UI thread is
    /// blocked", and they need opposite fixes. Queued at background priority so it sits
    /// behind layout and render: if this reports a few milliseconds the loop is healthy
    /// and a missing window is the window's problem, and if it reports seconds - or
    /// never arrives at all - something in front of it is holding the thread.
    /// </remarks>
    private void StartDispatcherHeartbeat()
    {
        var queued = System.Diagnostics.Stopwatch.StartNew();

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            StartupTrace.Mark($"ileti döngüsü nabzı · kuyruktan {queued.Elapsed.TotalMilliseconds:0.0} ms sonra çalıştı")));
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

        StartupTrace.Mark("arka plan servisleri başlatılıyor");

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

        // Which network we are on is not the engine's business either, and treating it
        // as though it were is what left Vodafone Sınırsız Modu reporting the user's own
        // saved hotspot as an unknown network until protection happened to be running.
        Guarded("Ağ takibi", () => _ = StartNetworkAwarenessAsync());

        if (_service!.Settings.StartEngineOnLaunch)
        {
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

    /// <summary>
    /// Honours "turn off animations in Windows" before any page style is resolved.
    /// </summary>
    /// <remarks>
    /// A swap rather than an edit, and it has to happen here. The window used to clear
    /// the entrance trigger from the style after <c>InitializeComponent</c>, which could
    /// not work twice over: a style is sealed the first time it is applied, so mutating
    /// its trigger collection throws, and the pages had already resolved the style by
    /// then anyway. Replacing the resource before the window is built means every page
    /// simply picks up the version without the animation.
    /// </remarks>
    private void ApplyMotionPreference()
    {
        try
        {
            if (SystemParameters.ClientAreaAnimation)
            {
                return;
            }

            if (Resources["PageSurfaceStaticStyle"] is Style still)
            {
                Resources["PageSurfaceStyle"] = still;
                AppLog.Info("Sistem animasyonları kapalı; sayfa geçiş animasyonu kullanılmıyor.");
            }
        }
        catch (Exception ex)
        {
            // A 180ms fade is not worth a failed start.
            AppLog.Error("Sayfa geçiş animasyonu tercihi uygulanamadı", ex);
        }
    }

    private bool TryCreateWindow()
    {
        try
        {
            _window = new MainWindow(_viewModel!, _theme);
            _window.CloseToTrayRequested += OnCloseToTray;
            _window.ExitRequested += () => _ = ShutdownAsync();
            _window.FirstFrameRendered += OnFirstFrameRendered;
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
        // The one case where a window that is not on screen is correct rather than
        // broken. Everything that judges visibility reads this.
        _windowWanted = false;
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
    /// Watches, once the message loop is running, for a window that is meant to be on
    /// screen and is not - and gets it there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything before this point can succeed and still leave nothing for the user.
    /// The shell drops a notification area icon it was offered while it was still
    /// starting up. A window is shown into a compositor state that never paints it. A
    /// window carries coordinates from a monitor that has since been unplugged. DWM
    /// cloaks a window and keeps reporting it as visible. None of those report an
    /// error, and the old version of this check could not see any of them: it asked
    /// <c>IsVisible</c>, which is true in every one of those cases, and concluded the
    /// app was fine.
    /// </para>
    /// <para>
    /// So the question is now asked properly - <see cref="WindowHealthEvaluator"/> gets
    /// what WPF, the window manager and DWM each say - and it has three answers rather
    /// than two. A reachable window ends the watch. A window the user deliberately put
    /// away ends it too, once the way back in has been checked. Only the third answer
    /// starts recovery, and that recovery is bounded: a few escalating attempts, then a
    /// message the user can act on. A watchdog that shows and activates a window on a
    /// timer for ever is not a fix, it is a second bug.
    /// </para>
    /// </remarks>
    private void StartVisibilityWatchdog()
    {
        var checks = 0;

        // Background, not ApplicationIdle: still below input, layout and render, so it
        // can never get in front of the first frame - but not starved either, and a
        // check that never runs is a watchdog that does not exist.
        _visibilityWatchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = HealthCheckSchedule[0],
        };

        _visibilityWatchdog.Tick += (_, _) =>
        {
            if (_shuttingDown)
            {
                StopVisibilityWatchdog();
                return;
            }

            checks++;

            var observation = Observe();
            var report = WindowHealthEvaluator.Evaluate(observation);

            switch (report.Health)
            {
                case WindowHealth.Reachable:
                    ConfirmWindowReachable(observation, report);
                    return;

                case WindowHealth.HiddenOnPurpose:
                    // Hidden because that is what was asked for: make sure the way back
                    // in is really there, then stop watching.
                    _tray?.EnsureVisible();
                    LogVisibility(observation, report);
                    StopVisibilityWatchdog();
                    return;

                default:
                    Recover(observation, report);
                    break;
            }

            if (_visibilityWatchdog is not null)
            {
                _visibilityWatchdog.Interval = HealthCheckSchedule[Math.Min(checks, HealthCheckSchedule.Length - 1)];
            }
        };

        _visibilityWatchdog.Start();
    }

    /// <summary>Puts the watch back on for a window that is wanted and is not up.</summary>
    private void EnsureVisibilityWatchdog()
    {
        if (_visibilityWatchdog is null && !_recoveryExhausted && !_shuttingDown)
        {
            StartVisibilityWatchdog();
        }
    }

    private void StopVisibilityWatchdog()
    {
        _visibilityWatchdog?.Stop();
        _visibilityWatchdog = null;
    }

    /// <summary>
    /// Gathers what WPF, the window manager and DWM each say about the window.
    /// </summary>
    private WindowObservation Observe()
    {
        var window = _window;
        if (window is null)
        {
            return WindowObservation.Missing(_windowWanted, _tray is not null);
        }

        var native = WindowInspector.Inspect(window);

        return new WindowObservation(
            WindowExists: true,
            WantsToBeVisible: _windowWanted,
            Readiness: window.Readiness,
            WpfVisible: window.IsVisible,
            HasHandle: native.HasHandle,
            NativeVisible: native.Visible,
            Minimised: native.Minimised || window.WindowState == WindowState.Minimized,
            Cloak: native.Cloak,
            OnScreen: native.OnScreen,
            TrayAvailable: _tray is not null);
    }

    /// <summary>
    /// The window drew its first frame. Everything that treats the app as having been
    /// seen hangs off here, and nothing hangs off <c>Show()</c> returning.
    /// </summary>
    private void OnFirstFrameRendered()
    {
        var observation = Observe();
        var report = WindowHealthEvaluator.Evaluate(observation);

        if (report.IsReachable)
        {
            ConfirmWindowReachable(observation, report);
            return;
        }

        // A frame was drawn into something the user still cannot get to - cloaked, or
        // off every attached monitor. The watchdog owns that from here.
        AppLog.Warning($"İlk kare çizildi ama pencere erişilebilir değil: {report.Reason}.");
    }

    /// <summary>
    /// Records the one outcome that counts: a real window, on a real monitor, that the
    /// user can reach.
    /// </summary>
    private void ConfirmWindowReachable(WindowObservation observation, WindowHealthReport report)
    {
        StopVisibilityWatchdog();

        if (!_windowConfirmed)
        {
            _windowConfirmed = true;
            _recoveryAttempts = 0;
            StartupTrace.Mark($"pencere doğrulandı · {report.Reason}");
            LogVisibility(observation, report);

            // Both of these are queued rather than run here. This is called from inside
            // the first render, and neither writing a settings file nor negotiating with
            // the compositor belongs in front of the frame the user is waiting for.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                RememberWindowWasShown(observation);

                // The material is asked for only now, on a window already proved to be
                // on screen, so a machine where it is accepted and then never drawn can
                // no longer cost the user the first frame.
                EnableWindowBackdrop();
            }));
        }

        CompleteSelfTest(success: true, observation, report);
    }

    /// <summary>Runs the next bounded recovery step for a window that will not appear.</summary>
    private void Recover(WindowObservation observation, WindowHealthReport report)
    {
        var step = WindowRecovery.Next(observation, _recoveryAttempts, _recreationSpent);

        AppLog.Warning(
            $"Pencere erişilemiyor ({report.Reason}); kurtarma adımı {step} "
            + $"({_recoveryAttempts + 1}/{WindowRecovery.MaxAttempts}).");
        AppLog.Warning($"Pencere durumu · {DescribeWindow(observation)}");

        switch (step)
        {
            case WindowRecoveryStep.None:
                return;

            case WindowRecoveryStep.GiveUp:
                GiveUpOnWindow(observation, report);
                return;

            case WindowRecoveryStep.Recreate:
                _recreationSpent = true;
                _recoveryAttempts++;
                RecreateWindow();
                return;
        }

        // Reveal, Reposition and OpaqueFallback all end in a raise; they differ in what
        // they put right first.
        _recoveryAttempts++;

        if (step == WindowRecoveryStep.Reposition)
        {
            WindowActivation.EnsureOnScreen(_window!);
        }
        else if (step == WindowRecoveryStep.OpaqueFallback)
        {
            // The client area may have been handed to a compositor that is not drawing
            // it. Taking it back costs a visual effect and nothing else.
            _window!.DisableBackdrop("pencere görünür değil");
            _window.ForceRedraw();
        }

        ShowMainWindow();
    }

    /// <summary>
    /// Builds a replacement window, once, for a handle that has never drawn a frame.
    /// </summary>
    /// <remarks>
    /// The last step, and the only one that can lose anything. Everything the window
    /// shows lives in the view model, which outlives it, so the replacement comes up
    /// with the same state - but the old handle and whatever was wrong with it are
    /// gone. Deliberately not reachable more than once per process: a window that is
    /// rebuilt on a timer is an application that flashes on screen for ever.
    /// </remarks>
    private void RecreateWindow()
    {
        AppLog.Warning("Pencere hiç çizilmedi; tek seferlik olarak yeniden oluşturuluyor.");
        StartupTrace.Mark("pencere yeniden oluşturuluyor");

        try
        {
            var old = _window;
            _window = null;

            if (old is not null)
            {
                old.FirstFrameRendered -= OnFirstFrameRendered;
                old.CloseForReplacement();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Eski pencere kapatılamadı", ex);
        }

        if (!TryCreateWindow())
        {
            return;
        }

        // The replacement never asks for the compositor material: whatever the old one
        // could not draw, this one is going to draw itself.
        _window!.DisableBackdrop("yeniden oluşturulan pencere düz renkle açılıyor");
        ShowMainWindow();
    }

    /// <summary>
    /// The end of the line: no usable window, and the user has to be told rather than
    /// left with a process they cannot reach.
    /// </summary>
    private void GiveUpOnWindow(WindowObservation observation, WindowHealthReport report)
    {
        StopVisibilityWatchdog();
        _recoveryExhausted = true;

        if (_failureReported)
        {
            return;
        }

        _failureReported = true;

        AppLog.Error($"Arayüz başlatılamadı: {report.Reason}");
        AppLog.Error($"Pencere durumu · {DescribeWindow(observation)}");
        StartupTrace.Dump(report.Reason);
        WriteUiDiagnostics(observation, report);

        _tray?.Notify(
            AppPaths.ProductName,
            $"Pencere açılamadı ({report.Reason}). Koruma çalışıyor; ayrıntılar: {AppPaths.LogDirectory}",
            warning: true);

        ShowStartupFailureDialog(report.Reason);
        CompleteSelfTest(success: false, observation, report);
    }

    /// <summary>
    /// Says what happened, on a thread of its own.
    /// </summary>
    /// <remarks>
    /// A modal dialog on the UI thread would stop the dispatcher, which is the one
    /// thing that must keep running: it is what lets a later launch be answered, the
    /// notification area icon respond, and the engine's continuations complete. Its own
    /// STA thread costs nothing and keeps the process usable while the message is up.
    /// </remarks>
    private static void ShowStartupFailureDialog(string reason)
    {
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    MessageBox.Show(
                        $"{AppPaths.ProductName} penceresi açılamadı.\n\n{reason}\n\n"
                            + "Koruma arka planda çalışmaya devam ediyor ve bildirim alanı simgesinden "
                            + "yönetilebilir.\n\nTanılama kayıtları:\n"
                            + AppPaths.LogDirectory,
                        AppPaths.ProductName,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch (Exception)
                {
                    // A session with no interactive desktop cannot show a dialog. The
                    // log file and the notification area message are still there.
                }
            })
            {
                IsBackground = true,
                Name = "DpiBypass.StartupFailure",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("Başlatma hatası iletisi gösterilemedi", ex);
        }
    }

    /// <summary>
    /// Leaves the window state and the startup timeline in a file of their own, so the
    /// evidence survives even when the daily log is rotated or crowded.
    /// </summary>
    private static void WriteUiDiagnostics(WindowObservation observation, WindowHealthReport report)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var path = Path.Combine(AppPaths.LogDirectory, "ui-diagnostics.log");

            var text = new System.Text.StringBuilder()
                .AppendLine($"{DateTimeOffset.Now:O} · arayüz açılamadı")
                .AppendLine($"karar: {report}")
                .AppendLine($"pencere: {observation}")
                .AppendLine($"arka plan: {WindowBackdrop.Availability}")
                .AppendLine("açılış izlemesi:");

            foreach (var mark in StartupTrace.Timeline)
            {
                text.Append("    ").AppendLine(mark);
            }

            File.AppendAllText(path, text.AppendLine().ToString());
        }
        catch (Exception ex)
        {
            AppLog.Error("Arayüz tanılama dosyası yazılamadı", ex);
        }
    }

    /// <summary>The one line in the log that answers "was it ever on screen?".</summary>
    private void LogVisibility(WindowObservation observation, WindowHealthReport report)
        => AppLog.Info(
            $"Görünürlük denetimi: {report.Reason} · hazırlık {observation.Readiness} · "
            + $"bildirim alanı simgesi {(_tray is null ? "yok" : "var")} · "
            + $"arka plan: {WindowBackdrop.Availability}.");

    /// <summary>Everything about the window worth putting in a log line, in one string.</summary>
    private string DescribeWindow(WindowObservation observation)
    {
        var window = _window;

        var description = $"{observation} · WindowState={window?.WindowState.ToString() ?? "-"} · "
            + $"Visibility={window?.Visibility.ToString() ?? "-"} · "
            + $"ShowInTaskbar={window?.ShowInTaskbar.ToString() ?? "-"} · "
            + $"arka plan={WindowBackdrop.Availability}";

        // The native side adds the HWND, its rectangle and the monitors it was compared
        // against, which is the part that explains an off-screen or cloaked window.
        return window is null ? description : $"{description} · {WindowInspector.Inspect(window)}";
    }

    /// <summary>Turns on the compositor material, unless this machine has ruled it out.</summary>
    private void EnableWindowBackdrop()
    {
        if (_window is null)
        {
            return;
        }

        if (_service?.Settings.DisableWindowBackdrop == true)
        {
            _window.DisableBackdrop("ayarlarda kapatılmış");
            return;
        }

        _window.EnableBackdrop();
    }

    /// <summary>
    /// Records that the window has genuinely been in front of the user, so later
    /// launches are allowed to start in the notification area.
    /// </summary>
    /// <remarks>
    /// Written only after a confirmed frame on a reachable window, and that is the
    /// whole point of the check. This used to be saved the instant <c>Show()</c>
    /// returned, which is true of a window that never draws - so one failed first run
    /// told every later logon that the user had already seen the app, and the logon
    /// task was free to start it in the notification area for ever after. A first run
    /// that fails must leave no trace that says it succeeded.
    /// </remarks>
    private void RememberWindowWasShown(WindowObservation observation)
    {
        if (!WindowHealthEvaluator.MayRecordWindowShown(observation))
        {
            return;
        }

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
    /// <para>
    /// The hard case is the copy that is running, healthy, and busy. A start-up sweep
    /// measures strategies over real handshakes, and while it is doing that the
    /// dispatcher can be slow to answer. Treating "slow" as "dead" is expensive in a
    /// way nothing on screen explains: the running copy owns the driver handles and
    /// has pointed the machine's resolvers at its own DNS proxy, so ending it takes
    /// the connection with it and the replacement has to build all of it again.
    /// </para>
    /// <para>
    /// The harder case, and the one this got wrong, is the copy that has been running
    /// for two seconds. An installation starts the app, and something else - the
    /// installer's own launch, a shortcut, the script that drove the install - starts
    /// it again immediately afterwards. The first copy is still loading its runtime,
    /// so it had no window, no control channel and nothing to say, and the second
    /// copy read that silence as death and killed it. That is a window appearing and
    /// disappearing again seconds after an install finishes. A starting copy now says
    /// so from the moment it takes the lock, and a launch that hears it waits.
    /// </para>
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

        var handover = _instance.SignalExistingInstance(FirstHandover);

        switch (InstanceHandover.Decide(handover, startupBudgetSpent: false))
        {
            case LaunchAction.Exit:
                AppLog.Info("Uygulama zaten çalışıyor; pencereyi öne getirmesi istendi.");
                return false;

            case LaunchAction.WaitForStartup:
                AppLog.Info("Çalışan kopya henüz açılıyor; penceresini açması bekleniyor.");

                handover = _instance.AwaitWindow(StartingHandover);
                if (InstanceHandover.Decide(handover, startupBudgetSpent: true) == LaunchAction.Exit)
                {
                    return false;
                }

                // Either it stopped answering, or it is still saying "starting" a minute
                // and a half later, which is no longer a start-up. Both fall through to
                // the checks below: a copy that is genuinely wedged before it ever gets a
                // window still has to be recoverable, or every later launch says the same
                // thing and the user never gets the app at all.
                AppLog.Warning("Çalışan kopya beklendi ama pencere açılmadı; devralma denetimlerine geçiliyor.");
                break;
        }

        if (RunningInstanceAnswers())
        {
            AppLog.Info("Çalışan kopya meşgul ama ayakta; pencereyi açması için daha uzun bekleniyor.");

            if (_instance.SignalExistingInstance(BusyHandover) == HandoverReply.WindowShown)
            {
                return false;
            }
        }

        // The lock is held by a copy that will not answer - hung, or half torn down.
        // Handing the user the "look in the notification area" message would be a lie:
        // there is nothing there to click, and every later launch would say the same.
        AppLog.Warning("Çalışan kopya yanıt vermedi; bu kopya devralıyor.");
        if (_instance.TryTakeOver(AppLog.InfoSink))
        {
            return true;
        }

        MessageBox.Show(
            $"{AppPaths.ProductName} uygulamasının yanıt vermeyen bir kopyası kapatılamadı.\n\n"
                + "İkinci bir ağ motoru başlatılmadı. Görev Yöneticisi'nden DPI Bypass işlemini "
                + "sonlandırıp uygulamayı yeniden açın.",
            AppPaths.ProductName,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
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
    /// <remarks>
    /// Three answers, because the launch on the other side does three different things
    /// with them. This listener is running from the moment the lock is taken, which is
    /// well before there is a window or a dispatcher loop to ask, so the common answer
    /// during the first seconds is "still starting" - and it has to be given without
    /// touching the dispatcher, which is not pumping yet and would simply time out.
    /// </remarks>
    private HandoverReply OnActivationRequested()
    {
        if (_shuttingDown)
        {
            return HandoverReply.NoAnswer;
        }

        if (!_startupComplete)
        {
            // Still building the window, or the dispatcher loop has not started yet.
            // Either way the launch on the other side should wait rather than conclude
            // that this process is not serving anyone.
            return HandoverReply.Starting;
        }

        // Waiting for the UI thread to finish is the whole point: what this returns is
        // what the waiting launch is told, so a stuck dispatcher has to report failure
        // rather than let the request disappear into a queue that is not being drained.
        // And the answer is the window's, not the dispatcher's - an operation that
        // completed while the window is still not on screen is a launch that showed the
        // user nothing, and it now says so rather than claiming success.
        var reply = HandoverReply.NoAnswer;
        var operation = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => reply = ActivateForLaunch()));

        // Longer than the first wait on the other side and matched to the second one,
        // so a busy dispatcher gets to finish rather than being written off while the
        // launch that asked is still waiting for it. A dispatcher that still did not
        // finish is reported as no answer rather than as "starting": start-up is over,
        // so this is a copy that is stuck rather than one that is on its way, and the
        // launch on the other side has its own second chance for a merely busy
        // instance before it acts on the answer.
        var completed = operation.Wait(BusyHandover) == DispatcherOperationStatus.Completed;

        return completed ? reply : HandoverReply.NoAnswer;
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
        var exitCode = 0;
        try
        {
            await CommandLineTasks.TryRunAsync(args).ConfigureAwait(true);
            exitCode = Environment.ExitCode;
        }
        catch (Exception ex)
        {
            AppLog.Error("Komut çalıştırılamadı", ex);
            exitCode = 1;
        }
        finally
        {
            Shutdown(exitCode);
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
            if (_service.Settings.DnsMode != DnsMode.SystemDefault && !_dnsWatchdogStarted)
            {
                // System DNS is about to be changed. Do not make a process-local DNS
                // proxy a single point of failure unless a separately named recovery
                // process is already watching this owner.
                if (!CommandLineTasks.TryStartDnsWatchdog())
                {
                    throw new InvalidOperationException(
                        "DNS çökme koruması başlatılamadı; internet ayarları güvenlik için değiştirilmedi.");
                }

                _dnsWatchdogStarted = true;
            }

            await _service.StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Koruma otomatik başlatılamadı", ex);
            _tray?.Notify(AppPaths.ProductName, ex.Message, warning: true);
        }
    }

    /// <summary>
    /// Starts watching the network, and runs the safe hotspot checks once if we came
    /// up already sitting on a registered one.
    /// </summary>
    private async Task StartNetworkAwarenessAsync()
    {
        var service = _service;
        if (service is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => service.RunStartupHotspotCheckAsync()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Ağ durumu okunamadı", ex);
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
    /// Puts the window in front of the user, and reports whether the raise itself ran.
    /// </summary>
    /// <remarks>
    /// Deliberately not a claim that anything is on screen. Whether the user can see
    /// the window is settled by <see cref="WindowHealthEvaluator"/> once a frame has
    /// been drawn, because that is a different question with a different answer and
    /// conflating the two is the bug this whole path was rebuilt around.
    /// </remarks>
    private void ShowMainWindow()
    {
        if (_window is null)
        {
            AppLog.Warning("Gösterilecek pencere yok.");
            return;
        }

        _windowWanted = true;

        StartupTrace.Mark($"ShowMainWindow · hazırlık={_window.Readiness}");
        var outcome = WindowActivation.Raise(_window);
        StartupTrace.Mark(
            $"ShowMainWindow bitti · adımlar={(outcome.Completed ? "tamam" : "hata")} · ön plan={outcome.Foreground}");

        if (!outcome.Completed)
        {
            return;
        }

        // Raising a window can rebuild its handle, and a rebuilt handle has none of the
        // backdrop state the old one was given - which leaves a window that paints
        // nothing over a material nobody is drawing.
        _window.EnsureBackgroundIsPainted();
    }

    /// <summary>
    /// Answers a second launch that asked for the window, from the UI thread.
    /// </summary>
    /// <remarks>
    /// The repeat-request check is the escape hatch for the one failure this process
    /// cannot see for itself. A window whose client area is handed to the compositor is
    /// invisible if the compositor does not draw the material, and nothing Windows will
    /// answer distinguishes that from a window being drawn perfectly - so the evidence
    /// has to come from the user, and it does: they launched the app again while this
    /// process believed its window was reachable and in front of them. That only makes
    /// sense if they cannot see it, so the material goes, permanently, on this machine.
    /// A cosmetic loss, reversible by hand in settings.json, in exchange for the app
    /// being reachable at all.
    /// </remarks>
    private HandoverReply ActivateForLaunch()
    {
        NoteRedundantActivation(Observe());

        ShowMainWindow();

        var observation = Observe();
        var reply = InstanceHandover.ReplyFor(observation, _startupComplete, _recoveryExhausted);

        if (reply != HandoverReply.WindowShown)
        {
            AppLog.Warning(
                $"Etkinleştirme isteği yanıtı: {reply} · {WindowHealthEvaluator.Evaluate(observation).Reason}");

            // A launch asked for a window and did not get one. That is the same fault
            // the startup watchdog exists for, so it goes back on - otherwise a copy
            // that has been sitting in the notification area since boot has nothing
            // watching it, and every later launch waits out its budget and takes over.
            EnsureVisibilityWatchdog();
        }

        return reply;
    }

    /// <summary>
    /// Counts launches that asked for a window this process already believed was on
    /// screen, and acts on the second one.
    /// </summary>
    /// <remarks>
    /// The evidence is the user's behaviour, because nothing else can supply it. A
    /// window whose client area has been handed to the compositor is invisible when the
    /// compositor does not draw the material, and Windows reports that window as
    /// visible, uncloaked, on a monitor and focused - identical to a window being drawn
    /// perfectly. Someone launching the application again, twice, while this process
    /// says its window is already up and not minimised, is telling us the one thing we
    /// cannot measure. So the material goes, on this machine, permanently.
    /// </remarks>
    private void NoteRedundantActivation(WindowObservation before)
    {
        if (_window is not { BackdropActive: true }
            || before.Minimised
            || !WindowHealthEvaluator.Evaluate(before).IsReachable)
        {
            _redundantActivations = 0;
            return;
        }

        var now = DateTime.UtcNow;
        _redundantActivations = now - _lastRedundantActivation <= RedundantActivationWindow
            ? _redundantActivations + 1
            : 1;
        _lastRedundantActivation = now;

        if (_redundantActivations < 2)
        {
            return;
        }

        AppLog.Warning(
            "Uygulama, penceresi zaten açık ve önde görünürken yeniden başlatıldı; "
            + "pencere arka planı kalıcı olarak kapatılıyor.");

        _window.DisableBackdrop("kullanıcı pencereyi göremiyor");
        _window.ForceRedraw();
        PersistBackdropOptOut();
    }

    /// <summary>Remembers that this machine must never be offered the material again.</summary>
    private void PersistBackdropOptOut()
    {
        var service = _service;
        if (service is null || service.Settings.DisableWindowBackdrop)
        {
            return;
        }

        try
        {
            service.Settings.DisableWindowBackdrop = true;
            service.SaveSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error("Pencere arka planı ayarı kaydedilemedi", ex);
        }
    }

    /// <summary>Ends the UI self test with a verdict the caller can read from the exit code.</summary>
    private void CompleteSelfTest(bool success, WindowObservation observation, WindowHealthReport report)
    {
        if (!_selfTest)
        {
            return;
        }

        _selfTest = false;

        if (success && _window is not null)
        {
            try
            {
                UiLayoutSelfTest.Run(_window);
            }
            catch (Exception ex)
            {
                success = false;
                AppLog.Error("Arayüz yerleşim sınaması başarısız", ex);
            }
        }

        AppLog.Info(
            $"Arayüz sınaması {(success ? "BAŞARILI" : "BAŞARISIZ")} · {report} · {DescribeWindow(observation)}");

        if (!success)
        {
            StartupTrace.Dump("arayüz sınaması");
            WriteUiDiagnostics(observation, report);
        }

        // Queued rather than immediate so this can be called from inside the very
        // handlers the shutdown tears down, and routed through the normal teardown so
        // the instance lock and the service are released the way they always are.
        _exitCode = success ? 0 : 1;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = ShutdownAsync()));
    }

    /// <summary>Stops the self test from waiting for a window that is never going to come.</summary>
    private void StartSelfTestDeadline()
    {
        var deadline = new DispatcherTimer(DispatcherPriority.Background) { Interval = SelfTestBudget };

        deadline.Tick += (_, _) =>
        {
            deadline.Stop();

            if (!_selfTest)
            {
                return;
            }

            var observation = Observe();
            CompleteSelfTest(success: false, observation, WindowHealthEvaluator.Evaluate(observation));
        };

        deadline.Start();
    }

    /// <summary>
    /// Last resort for a startup failure: say something the user can act on and leave
    /// a file behind, because at this point there is no window and no log page.
    /// </summary>
    private static void ReportFatal(Exception exception)
    {
        var detail = exception.ToString();

        // A failure before the service was constructed cannot go through its normal
        // teardown, so a snapshot left by a previous run has to be recovered - but not
        // in front of the report. Handed to the separately named recovery process,
        // which is not on its way down and has no deadline, so this one can get on
        // with the only thing it can still do: say what happened, promptly.
        var recovering = StartExternalDnsRecovery();

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

        if (!recovering)
        {
            // The helper could not be started - a payload missing its recovery copy,
            // or a machine that will not launch it. Doing it here is the fallback, and
            // it happens after the report rather than in front of it.
            TryRestorePendingDnsAfterFatal(FatalRecoveryBudget);
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
    private void StopServiceSynchronously(TimeSpan? budget = null)
    {
        var service = _service;
        if (service is null)
        {
            return;
        }

        try
        {
            Task.Run(() => service.StopAsync()).Wait(budget ?? TimeSpan.FromSeconds(8));
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
            StopVisibilityWatchdog();
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

            // The file writer batches, so the closing lines - which are the ones that
            // explain why the app is going away - are still queued at this point.
            AppLog.Shutdown();
            Shutdown(_exitCode);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Arayüz hatası", e.Exception);

        // A UI glitch is not a reason to drop protection and leave DNS redirected.
        e.Handled = true;
    }

    /// <summary>
    /// Last rites for a process the CLR is about to end anyway.
    /// </summary>
    /// <remarks>
    /// Everything here is on a short leash on purpose. This used to stop the service
    /// for up to eight seconds and then block for up to thirty more putting DNS back,
    /// which is most of a minute during which the app is on screen, frozen, and then
    /// gone - "it said it was starting and forty seconds later it crashed" is the
    /// shape of that, and none of it helped the user. The recovery is handed to the
    /// separately named process that exists for it, which is not dying and has no
    /// deadline, and the in-process attempt keeps only enough of a budget to win the
    /// race when it can.
    /// </remarks>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLog.Error("Beklenmeyen hata", exception);
        }

        StartExternalDnsRecovery();
        StopServiceSynchronously(FatalRecoveryBudget);
        TryRestorePendingDnsAfterFatal(FatalRecoveryBudget);

        // The crash itself is the last thing worth reading in the file, so the queued
        // batch is flushed before the runtime finishes tearing the process down. Short
        // leash like everything else here.
        AppLog.Shutdown();
    }

    /// <summary>
    /// Hands the DNS restore to a process that is not the one going down.
    /// </summary>
    /// <remarks>
    /// The recovery executable is a copy of this one under a different name, so it is
    /// unaffected by whatever is ending this process - and it does the restore with no
    /// timeout at all, which is what makes it safe to keep the deadlines here small.
    /// The engine's normal watchdog is already doing this for a crash that happens
    /// once protection is running; this covers the failures that happen before it.
    /// </remarks>
    /// <returns>
    /// False when there is nothing to hand over, or nothing to hand it to. The caller
    /// falls back to doing it here, on a short budget.
    /// </returns>
    private static bool StartExternalDnsRecovery()
    {
        try
        {
            var configurator = new DnsConfigurator(AppPaths.StateDirectory, AppLog.InfoSink);
            if (!configurator.HasPendingRestore)
            {
                // Nothing was redirected, so there is nothing for either path to undo.
                return true;
            }

            return CommandLineTasks.TryStartDnsRecovery();
        }
        catch (Exception)
        {
            // The in-process attempt is the fallback, and the logon task's next launch
            // reconciles a snapshot that outlives both.
            return false;
        }
    }

    private static void TryRestorePendingDnsAfterFatal(TimeSpan budget)
    {
        try
        {
            var configurator = new DnsConfigurator(AppPaths.StateDirectory, AppLog.InfoSink);
            if (configurator.HasPendingRestore)
            {
                Task.Run(() => configurator.RestoreAsync(CancellationToken.None)).Wait(budget);
            }
        }
        catch (Exception ex)
        {
            try
            {
                AppLog.Error("Kritik hata sonrası DNS geri yüklenemedi", ex);
            }
            catch (Exception)
            {
                // The watchdog process will make the same attempt after this exits.
            }
        }
    }
}
