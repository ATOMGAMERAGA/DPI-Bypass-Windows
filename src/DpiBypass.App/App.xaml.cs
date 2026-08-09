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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Startup runs before the dispatcher loop, so an exception escaping here kills
        // the process without a window, a tray icon or a log line - which is exactly
        // what "the app does not open" looks like from the outside. Everything is
        // wrapped so that a failure is reported instead of being silent.
        try
        {
            await StartAsync(e).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportFatal(ex);
            Shutdown(1);
        }
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        AppPaths.MigrateLegacyState();
        AppLog.Initialise();
        AppLog.Info($"{AppPaths.ProductName} başlatılıyor · {AppPaths.Author}");

        // The installer, the uninstaller and the command line share this executable.
        if (await CommandLineTasks.TryRunAsync(e.Args).ConfigureAwait(true))
        {
            Shutdown();
            return;
        }

        _instance = SingleInstance.Acquire();
        if (!_instance.IsPrimary)
        {
            // Hand the click to the copy that is already running rather than exiting
            // without a trace. It is normally sitting in the tray, started minimised
            // by the logon task, so this is the usual way the window gets opened.
            var delivered = _instance.SignalExistingInstance();
            AppLog.Info(delivered
                ? "Uygulama zaten çalışıyor; pencereyi göstermesi istendi."
                : "Uygulama zaten çalışıyor ancak yanıt vermedi.");

            if (!delivered)
            {
                MessageBox.Show(
                    $"{AppPaths.ProductName} zaten çalışıyor ancak pencere açılamadı.\n\n"
                        + "Bildirim alanındaki (saatin yanındaki) simgeye çift tıklayın.",
                    AppPaths.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

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

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowMainWindow;
        _tray.ToggleRequested += () => _viewModel.ToggleCommand.Execute(null);
        _tray.TestRequested += () => _viewModel.TestCommand.Execute(null);
        _tray.ExitRequested += () => _ = ShutdownAsync();

        _viewModel.StateChanged += () => _tray?.Update(_viewModel.StatusHeadline, _viewModel.StatusDetail, _viewModel.IsRunning);

        _window = new MainWindow(_viewModel, _theme);
        _window.CloseToTrayRequested += () =>
        {
            _window.Hide();
            _tray?.Notify(AppPaths.ProductName, "Uygulama tepside çalışmaya devam ediyor.");
        };
        _window.ExitRequested += () => _ = ShutdownAsync();

        // A second launch (Start menu, desktop shortcut) arrives here.
        _instance.ActivationRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);
        _instance.BeginListening();

        // The command line drives this instance rather than guessing from the
        // settings file what the running engine has decided.
        var commands = new ControlCommands(_service);
        _control = new ControlServer(request => commands.HandleAsync(request), AppLog.InfoSink);
        _control.Start();

        var startHidden = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
            && _service.Settings.StartMinimised;

        if (!startHidden)
        {
            _window.Show();
        }

        if (_service.Settings.StartEngineOnLaunch)
        {
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
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
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
        _service?.StopAsync().GetAwaiter().GetResult();
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

        try
        {
            _service?.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Nothing more we can do on the way down.
        }
    }
}
