using System.Windows;
using System.Windows.Threading;
using AtomDpi.App.Infrastructure;
using AtomDpi.App.ViewModels;
using AtomDpi.Core;
using AtomDpi.Core.Interop;
using AtomDpi.Core.Logging;

namespace AtomDpi.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\AtomDpiBypass.SingleInstance";

    private Mutex? _instanceMutex;
    private ProtectionService? _service;
    private ThemeManager? _theme;
    private TrayIcon? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _shuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLog.Initialise();
        AppLog.Info($"{AppPaths.ProductName} başlatılıyor · {AppPaths.Author}");

        // The installer and uninstaller reuse this executable for their housekeeping.
        if (await CommandLineTasks.TryRunAsync(e.Args).ConfigureAwait(true))
        {
            Shutdown();
            return;
        }

        if (!ClaimSingleInstance())
        {
            AppLog.Info("Uygulama zaten çalışıyor; bu örnek kapatılıyor.");
            Shutdown();
            return;
        }

        // The packet driver is unopenable without elevation, so ask for it once and
        // hand over to the elevated copy rather than starting up half working.
        if (!Elevation.IsElevated)
        {
            var arguments = string.Join(' ', e.Args);
            if (Elevation.TryRelaunchElevated(arguments))
            {
                Shutdown();
                return;
            }

            MessageBox.Show(
                "Atom DPI Bypass, ağ sürücüsünü açabilmek için yönetici hakları gerektirir.\n\n"
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

    private bool ClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // Another session already owns it.
            return false;
        }
        catch (Exception)
        {
            // If the mutex cannot be created at all, better to run than to refuse.
            return true;
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
            _instanceMutex?.Dispose();
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
