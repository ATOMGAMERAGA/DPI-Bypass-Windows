using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AtomDpi.App.Infrastructure;
using AtomDpi.App.ViewModels;

namespace AtomDpi.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ThemeManager _theme;
    private bool _micaApplied;
    private bool _exiting;

    public MainWindow(MainViewModel viewModel, ThemeManager theme)
    {
        _viewModel = viewModel;
        _theme = theme;

        InitializeComponent();
        DataContext = viewModel;

#pragma warning disable WPF0001 // Fluent theming is still marked experimental.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

        _theme.ThemeChanged += OnThemeChanged;
        ((INotifyCollectionChanged)_viewModel.LogLines).CollectionChanged += OnLogLinesChanged;
    }

    public event Action? CloseToTrayRequested;

    public event Action? ExitRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Mica needs a window handle, which only exists from here on. When it is not
        // available the solid Fluent background stays, so nothing else has to change.
        _micaApplied = WindowBackdrop.TryApply(this, _theme.IsDark);
    }

    private void OnThemeChanged(bool isDark)
    {
        WindowBackdrop.UpdateTitleBarTheme(this, isDark);

        if (!_micaApplied)
        {
            SetResourceReference(BackgroundProperty, "AppWindowBackgroundBrush");
        }
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
        {
            return;
        }

        // Keep the tail visible, the way a console would.
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        ExitRequested?.Invoke();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _viewModel.MinimiseToTrayOnClose)
        {
            // Closing the window is not the same as ending protection; the engine keeps
            // running and the tray icon stays the way back in.
            e.Cancel = true;
            CloseToTrayRequested?.Invoke();
            return;
        }

        _theme.ThemeChanged -= OnThemeChanged;
        ((INotifyCollectionChanged)_viewModel.LogLines).CollectionChanged -= OnLogLinesChanged;
        base.OnClosing(e);

        if (!_exiting)
        {
            ExitRequested?.Invoke();
        }
    }
}
