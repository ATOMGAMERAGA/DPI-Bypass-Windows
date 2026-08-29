using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DpiBypass.App.Infrastructure;
using DpiBypass.App.ViewModels;
using DpiBypass.Core.Logging;

namespace DpiBypass.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ThemeManager? _theme;
    private bool _micaApplied;
    private nint _backdropHandle;
    private bool _scrollPending;
    private bool _exiting;

    public MainWindow(MainViewModel viewModel, ThemeManager? theme)
    {
        _viewModel = viewModel;
        _theme = theme;

        InitializeComponent();
        DataContext = viewModel;

#pragma warning disable WPF0001 // Fluent theming is still marked experimental.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

        if (!SystemParameters.ClientAreaAnimation)
        {
            // The page entrance is decorative; the system-wide animation switch wins.
            try
            {
                ((Style)Application.Current.Resources["PageSurfaceStyle"]).Triggers.Clear();
            }
            catch (Exception ex)
            {
                // Losing this only keeps a 180ms fade; say so and carry on.
                AppLog.Error("Sayfa geçiş animasyonu kapatılamadı", ex);
            }
        }

        if (_theme is not null)
        {
            _theme.ThemeChanged += OnThemeChanged;
            _theme.PersonalisationChanged += OnPersonalisationChanged;
        }

        ((INotifyCollectionChanged)_viewModel.LogLines).CollectionChanged += OnLogLinesChanged;
    }

    public event Action? CloseToTrayRequested;

    public event Action? ExitRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Mica needs a window handle, which only exists from here on. When it is not
        // available the solid Fluent background stays, so nothing else has to change.
        _micaApplied = WindowBackdrop.TryApply(this, _theme?.IsDark ?? false);
        _backdropHandle = _micaApplied ? new WindowInteropHelper(this).Handle : nint.Zero;
        AppLog.Info($"Pencere arka planı: {WindowBackdrop.Availability}.");
    }

    /// <summary>
    /// Makes sure the client area is being painted by somebody - the compositor or
    /// the window itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Mica window paints nothing of its own; that is what makes the material
    /// visible. So the instant the material stops being drawn the window becomes a
    /// see-through hole with the controls floating in it, which to anybody who just
    /// double-clicked a shortcut is indistinguishable from the app having failed to
    /// open. Two things cause it and neither reports an error.
    /// </para>
    /// <para>
    /// The first is Windows deciding not to draw it any more - transparency effects
    /// switched off, a high contrast theme, the session moved to Remote Desktop. The
    /// second is subtler: several ordinary WPF property writes rebuild the window
    /// handle, and the replacement handle carries none of the attributes the old one
    /// was given, so the window carries on not painting over a material nobody is
    /// drawing. Raising the window is one of the paths that can do it, which is why
    /// this is checked every time the window is brought to the front as well as on a
    /// timer while it is up.
    /// </para>
    /// </remarks>
    public void EnsureBackgroundIsPainted()
    {
        try
        {
            if (_micaApplied)
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != nint.Zero && handle != _backdropHandle)
                {
                    // New handle: ask for the material again rather than assuming the
                    // old answer still holds.
                    _micaApplied = WindowBackdrop.TryApply(this, _theme?.IsDark ?? false);
                    _backdropHandle = _micaApplied ? handle : nint.Zero;
                }

                if (_micaApplied && WindowBackdrop.DescribeUnavailability() is null)
                {
                    return;
                }
            }

            if (_micaApplied)
            {
                WindowBackdrop.Remove(this);
                _micaApplied = false;
                _backdropHandle = nint.Zero;
                AppLog.Info("Pencere arka planı düz renge alındı.");
            }

            // Nothing is drawing the client area, so the window has to. A fully
            // transparent brush here is the failure being repaired, not a choice.
            if (Background is null or SolidColorBrush { Color.A: 0 })
            {
                SetResourceReference(BackgroundProperty, "AppWindowBackgroundBrush");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Pencere arka planı denetlenemedi", ex);
        }
    }

    private void OnThemeChanged(bool isDark)
    {
        WindowBackdrop.UpdateTitleBarTheme(this, isDark);

        if (!_micaApplied)
        {
            SetResourceReference(BackgroundProperty, "AppWindowBackgroundBrush");
        }
    }

    /// <summary>
    /// Takes the window off the compositor when Windows stops drawing the material.
    /// </summary>
    private void OnPersonalisationChanged() => EnsureBackgroundIsPainted();

    /// <summary>
    /// Keeps the tail of the log in view the way a console would - once per batch of
    /// new lines rather than once per line, and only while the user is actually
    /// reading the tail.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Windows.Controls.ListBox.ScrollIntoView"/> forces a layout
    /// pass, and the engine logs in bursts: opening the driver, measuring a strategy
    /// and rewriting DNS between them produce dozens of lines in a second. One forced
    /// layout each is enough to keep the dispatcher busy for as long as the burst
    /// lasts, and a dispatcher that is never idle is a window that never paints - the
    /// blank rectangle that looks like a hung application. Coalescing to one scroll
    /// per idle moment keeps the behaviour and drops the cost.
    /// <para>
    /// A reader who has scrolled up is reading history; yanking them back down with
    /// every burst makes the page unusable exactly when there is a lot to read. So the
    /// batch scroll fires only when the tail is already on screen (or the list has
    /// never overflowed, which is the same thing). Scrolling back to the bottom is the
    /// user's own action, and the next batch then follows again.
    /// </para>
    /// </remarks>
    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _scrollPending)
        {
            return;
        }

        _scrollPending = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollPending = false;

            if (LogList.Items.Count == 0 || !UserFollowsLogTail())
            {
                return;
            }

            LogList.ScrollIntoView(LogList.Items[^1]);
        }));
    }

    /// <summary>
    /// Whether the log's last line is on screen, i.e. whether new lines may scroll.
    /// The viewer is looked up per batch rather than cached: the page only realises
    /// its template when first visited, and a template rebuild would strand a cached
    /// reference.
    /// </summary>
    private bool UserFollowsLogTail()
    {
        var viewer = FindDescendantScrollViewer(LogList);

        // Not in the visual tree yet (page never visited): nothing to measure, and
        // the console behaviour the window has always had is the right default.
        if (viewer is null)
        {
            return true;
        }

        // Content that fits has no reading position to protect.
        if (viewer.ExtentHeight <= viewer.ViewportHeight + 1)
        {
            return true;
        }

        return viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ExtentHeight - 12;
    }

    private static System.Windows.Controls.ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer viewer)
            {
                return viewer;
            }

            var found = FindDescendantScrollViewer(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
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

        if (_theme is not null)
        {
            _theme.ThemeChanged -= OnThemeChanged;
            _theme.PersonalisationChanged -= OnPersonalisationChanged;
        }

        ((INotifyCollectionChanged)_viewModel.LogLines).CollectionChanged -= OnLogLinesChanged;
        base.OnClosing(e);

        if (!_exiting)
        {
            ExitRequested?.Invoke();
        }
    }
}
