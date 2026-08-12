using System.IO;
using System.Windows;
using System.Windows.Threading;
using DpiBypass.App.Infrastructure;
using DpiBypass.App.ViewModels;
using DpiBypass.Core;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Ipc;
using DpiBypass.Core.Logging;

namespace DpiBypass.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private ControlServer? _control;
    private ProtectionService? _service;
    private ThemeManager? _theme;
    private TrayIcon? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _shuttingDown;

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
    /// Gets the window on screen. Everything that can be slow is queued behind it.
    /// </summary>
    /// <remarks>
    /// This method is deliberately synchronous from end to end. Anything awaited here
    /// runs before WPF starts pumping messages, which means an unpainted window, a
    /// tray icon the shell never gets told about, and an app that looks hung for as
    /// long as the work takes. Opening the driver, rewriting DNS and measuring
    /// strategies all belong after the loop is running, not in front of it.
    /// </remarks>
    private void Start(StartupEventArgs e)
    {
        AppPaths.MigrateLegacyState();
        AppLog.Initialise();
        AppLog.Info($"{AppPaths.ProductName} başlatılıyor · {AppPaths.Author}");

        // The installer, the uninstaller and the command line share this executable.
        if (CommandLineTasks.IsHeadlessVerb(e.Args))
        {
            _ = RunHeadlessAsync(e.Args);
            return;
        }

        _instance = SingleInstance.Acquire();
        if (!TryContinueAsPrimary())
        {
            Shutdown();
            return;
        }

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

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Gözlenmeyen görev hatası", args.Exception);
            args.SetObserved();
        };
        SessionEnding += OnSessionEnding;

#pragma warning disable WPF0001 // Fluent theming is still marked experimental.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

        _theme = new ThemeManager(this);
        _theme.Apply();

        _service = new ProtectionService();
        _viewModel = new MainViewModel(_service, Dispatcher);

        var trayReady = TryCreateTrayIcon();

        _window = new MainWindow(_viewModel, _theme);
        _window.CloseToTrayRequested += () =>
        {
            _window.Hide();
            _tray?.Notify(AppPaths.ProductName, "Uygulama tepside çalışmaya devam ediyor.");
        };
        _window.ExitRequested += () => _ = ShutdownAsync();

        // A second launch (Start menu, desktop shortcut) arrives here.
        _instance.ActivationRequested += OnActivationRequested;
        _instance.BeginListening();

        // The command line drives this instance rather than guessing from the
        // settings file what the running engine has decided.
        var commands = new ControlCommands(_service);
        _control = new ControlServer(request => commands.HandleAsync(request), AppLog.InfoSink);
        _control.Start();

        // Starting in the tray is only a sane thing to do when there is a tray icon to
        // come back through. Without one the window is the only way into the app, so a
        // failed icon means the window is shown whatever the setting says.
        var startHidden = trayReady
            && e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
            && _service.Settings.StartMinimised;

        if (!startHidden)
        {
            _window.Show();
        }

        if (_service.Settings.StartEngineOnLaunch)
        {
            // Background priority puts this behind layout and rendering, so the window
            // is drawn and interactive before the engine starts taking its time.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = StartEngineAsync()));
        }
    }

    /// <summary>
    /// Decides whether this process should build the UI. False means a healthy
    /// instance took the request and this launch is finished.
    /// </summary>
    private bool TryContinueAsPrimary()
    {
        if (_instance!.IsPrimary)
        {
            return true;
        }

        if (_instance.SignalExistingInstance())
        {
            AppLog.Info("Uygulama zaten çalışıyor; pencereyi öne getirmesi istendi.");
            return false;
        }

        // The lock is held by a copy that will not answer - hung, or half torn down.
        // Handing the user the "look in the notification area" message would be a lie:
        // there is nothing there to click, and every later launch would say the same.
        AppLog.Warning("Çalışan kopya yanıt vermedi; bu kopya devralıyor.");
        return _instance.TryTakeOver(AppLog.InfoSink);
    }

    /// <summary>
    /// Answers another launch that asked for the window. Runs on the activation
    /// listener thread.
    /// </summary>
    private bool OnActivationRequested()
    {
        // Waiting for the UI thread to finish is the whole point: what this returns is
        // what the waiting launch is told, so a stuck dispatcher has to report failure
        // rather than let the request disappear into a queue that is not being drained.
        var operation = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(ShowMainWindow));
        return operation.Wait(TimeSpan.FromSeconds(4)) == DispatcherOperationStatus.Completed;
    }

    private bool TryCreateTrayIcon()
    {
        try
        {
            var tray = new TrayIcon();
            tray.OpenRequested += ShowMainWindow;
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
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    /// <summary>
    /// Last resort for a startup failure: say something the user can act on and leave
    /// a file behind, because at this point there is no window and no log page.
    /// </summary>
    private static void ReportFatal(Exception exception)
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
