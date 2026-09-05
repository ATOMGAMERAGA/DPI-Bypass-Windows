using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DpiBypass.App.Infrastructure;
using DpiBypass.App.ViewModels;
using DpiBypass.Core.Logging;
using DpiBypass.Core.Startup;

namespace DpiBypass.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ThemeManager? _theme;
    private bool _micaApplied;
    private bool _backdropSuppressed;
    private nint _backdropHandle;
    private bool _scrollPending;
    private bool _exiting;
    private bool _detached;
    private bool _contentRenderedSeen;

    public MainWindow(MainViewModel viewModel, ThemeManager? theme)
    {
        _viewModel = viewModel;
        _theme = theme;

        StartupTrace.Mark("MainWindow ctor · InitializeComponent başladı");
        InitializeComponent();
        StartupTrace.Mark("MainWindow ctor · InitializeComponent bitti");
        DataContext = viewModel;

#pragma warning disable WPF0001 // Fluent theming is still marked experimental.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

        if (_theme is not null)
        {
            _theme.ThemeChanged += OnThemeChanged;
            _theme.PersonalisationChanged += OnPersonalisationChanged;
        }

        ((INotifyCollectionChanged)_viewModel.VisibleLogLines).CollectionChanged += OnLogLinesChanged;

        // Hidden in the notification area, the only thing the counter timer produces is
        // formatted text nobody can see. Protection and the network watch are untouched -
        // this covers presentation and nothing else - and coming back re-reads at once so
        // the window is current the moment it is on screen.
        IsVisibleChanged += OnWindowVisibilityChanged;

        Loaded += OnWindowLoaded;
        Readiness = WindowReadiness.Created;
        StartupTrace.Mark("MainWindow ctor bitti");
    }

    public event Action? CloseToTrayRequested;

    public event Action? ExitRequested;

    /// <summary>Raised on the UI thread the first time a frame reaches the screen.</summary>
    public event Action? FirstFrameRendered;

    /// <summary>
    /// How far this window has actually got. The only value that means the user can
    /// see something is <see cref="WindowReadiness.Rendered"/>.
    /// </summary>
    public WindowReadiness Readiness { get; private set; } = WindowReadiness.None;

    /// <summary>Whether the client area is currently handed to the compositor.</summary>
    public bool BackdropActive => _micaApplied;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        Readiness = WindowReadiness.SourceInitialized;
        StartupTrace.Mark($"SourceInitialized · HWND=0x{new WindowInteropHelper(this).Handle:X}");

        // Only the title bar's light/dark rendering here. The material itself is a
        // separate, later decision: see EnableBackdrop.
        WindowBackdrop.UpdateTitleBarTheme(this, _theme?.IsDark ?? false);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        if (Readiness < WindowReadiness.Rendered)
        {
            StartupTrace.Mark($"Activated · hazırlık={Readiness}");
        }
    }

    /// <summary>
    /// The first real frame. Everything that treats the window as successful hangs off
    /// this, and nothing hangs off <c>Show()</c> returning.
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (!_contentRenderedSeen)
        {
            // Traced whether or not it is the signal that won the race, because "did
            // ContentRendered arrive at all" is the first question anybody reading a
            // startup log about a missing window needs answered.
            _contentRenderedSeen = true;
            StartupTrace.Mark("ContentRendered");
        }

        MarkRendered("ContentRendered");
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (Readiness < WindowReadiness.Loaded)
        {
            Readiness = WindowReadiness.Loaded;
        }

        StartupTrace.Mark("Loaded");

        // Loaded only ever fires on a window that has been shown, so this subscription
        // is scoped to a window that is genuinely on its way up.
        CompositionTarget.Rendering += OnComposition;
    }

    /// <summary>
    /// The corroborating first-frame signal: WPF composing a frame while this window is
    /// visible and has a real size.
    /// </summary>
    /// <remarks>
    /// <c>ContentRendered</c> is the primary signal and the right one, but the whole
    /// startup now hangs off first-frame confirmation, and hanging it off exactly one
    /// event would trade the old failure for a new one - a window that is perfectly fine
    /// and never confirmed would be put through recovery it does not need. This is an
    /// independent witness: the render loop ran, with this window visible and laid out.
    /// Whichever arrives first ends the wait, and both unsubscribe immediately, so a
    /// per-frame handler never outlives the frame it was waiting for.
    /// </remarks>
    private void OnComposition(object? sender, EventArgs e)
    {
        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        MarkRendered("CompositionTarget.Rendering");
    }

    private void MarkRendered(string signal)
    {
        CompositionTarget.Rendering -= OnComposition;

        if (Readiness >= WindowReadiness.Rendered)
        {
            // Shown again after being hidden. The first frame is already accounted for.
            return;
        }

        Readiness = WindowReadiness.Rendered;
        StartupTrace.Mark($"ilk kare çizildi · {signal}");

        try
        {
            FirstFrameRendered?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("İlk kare bildirimi işlenemedi", ex);
        }
    }

    /// <summary>
    /// Asks Windows for the Mica material, if it is safe to ask for it here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only once the window has been confirmed on screen, and that ordering is
    /// the fix rather than a detail. Turning the material on means telling WPF to stop
    /// painting the client area, because the compositor is going to paint it instead -
    /// so when the compositor does not, the window becomes a transparent hole with the
    /// controls floating in it. Doing that during <c>OnSourceInitialized</c>, as this
    /// used to, put the failure in front of the very first frame: the app's first ever
    /// window was the one at risk of being invisible, on a machine where nobody had
    /// seen the app before and had no reason to look in the notification area.
    /// </para>
    /// <para>
    /// Now the first frame is always opaque and always drawn by WPF. The material is a
    /// second step applied to a window already proved reachable, and it is dropped
    /// again the moment anything about it is in doubt.
    /// </para>
    /// </remarks>
    public void EnableBackdrop()
    {
        if (_micaApplied || _backdropSuppressed || Readiness < WindowReadiness.Rendered)
        {
            return;
        }

        try
        {
            _micaApplied = WindowBackdrop.TryApply(this, _theme?.IsDark ?? false);
            _backdropHandle = _micaApplied ? new WindowInteropHelper(this).Handle : nint.Zero;
            AppLog.Info($"Pencere arka planı: {WindowBackdrop.Availability}.");
        }
        catch (Exception ex)
        {
            _micaApplied = false;
            _backdropHandle = nint.Zero;
            AppLog.Error("Pencere arka planı uygulanamadı", ex);
        }
    }

    /// <summary>
    /// Takes the client area back off the compositor for good and paints it here.
    /// </summary>
    /// <remarks>
    /// The escape hatch for the one failure that cannot be detected from inside this
    /// process: DWM accepting the backdrop request and then not drawing the material.
    /// Nothing Windows will answer distinguishes that from a window that is being drawn
    /// perfectly, so the recovery is driven by the user - a second launch of an app
    /// whose window this process believes is already in front of them - and by the
    /// visibility watchdog. Suppressed rather than merely removed, so nothing turns it
    /// back on later in the same session.
    /// </remarks>
    public void DisableBackdrop(string reason)
    {
        _backdropSuppressed = true;

        if (!_micaApplied)
        {
            RestoreOpaqueBackground();
            return;
        }

        try
        {
            WindowBackdrop.Remove(this);
        }
        catch (Exception ex)
        {
            AppLog.Error("Pencere arka planı kaldırılamadı", ex);
        }

        _micaApplied = false;
        _backdropHandle = nint.Zero;
        RestoreOpaqueBackground();
        AppLog.Warning($"Pencere arka planı düz renge alındı: {reason}.");
    }

    /// <summary>Forces a layout and paint pass. Used only by recovery.</summary>
    public void ForceRedraw()
    {
        try
        {
            InvalidateVisual();
            UpdateLayout();
        }
        catch (Exception ex)
        {
            AppLog.Error("Pencere yeniden çizilemedi", ex);
        }
    }

    /// <summary>
    /// Unhooks everything this window subscribed to and closes it without the
    /// close-to-tray or exit behaviour, so a replacement can be built.
    /// </summary>
    public void CloseForReplacement()
    {
        _exiting = true;
        CloseToTrayRequested = null;
        ExitRequested = null;
        FirstFrameRendered = null;

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("Pencere kapatılamadı", ex);
            Detach();
        }
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

            RestoreOpaqueBackground();
        }
        catch (Exception ex)
        {
            AppLog.Error("Pencere arka planı denetlenemedi", ex);
        }
    }

    /// <summary>
    /// Puts the window's own background brush back when nothing else is painting the
    /// client area.
    /// </summary>
    /// <remarks>
    /// A fully transparent brush here is the failure being repaired, not a choice: it
    /// is what the backdrop path leaves behind, and a window carrying it while nobody
    /// draws the material is the see-through hole this whole file guards against.
    /// </remarks>
    private void RestoreOpaqueBackground()
    {
        if (Background is null or SolidColorBrush { Color.A: 0 })
        {
            SetResourceReference(BackgroundProperty, "AppWindowBackgroundBrush");
        }
    }

    /// <summary>Unhooks the subscriptions this window owns. Safe to call twice.</summary>
    private void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;

        if (_theme is not null)
        {
            _theme.ThemeChanged -= OnThemeChanged;
            _theme.PersonalisationChanged -= OnPersonalisationChanged;
        }

        Loaded -= OnWindowLoaded;
        CompositionTarget.Rendering -= OnComposition;
        ((INotifyCollectionChanged)_viewModel.VisibleLogLines).CollectionChanged -= OnLogLinesChanged;
        IsVisibleChanged -= OnWindowVisibilityChanged;
    }

    private void OnThemeChanged(bool isDark)
    {
        WindowBackdrop.UpdateTitleBarTheme(this, isDark);

        if (!_micaApplied)
        {
            // Re-pointed rather than left alone: the backdrop path replaces this with a
            // local transparent brush, and a window that came back from it has no
            // dynamic reference left to follow the new palette.
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
    /// <summary>
    /// Scrolls the settings page to the section a jump link names.
    /// </summary>
    /// <remarks>
    /// The Traffic Guard section is inside an expander that may be collapsed, and bringing
    /// a collapsed element into view scrolls to wherever its zero-height placeholder sits.
    /// So the expander is opened first, and the scroll is queued behind the layout pass
    /// that opening it causes - otherwise the position is measured against a page that has
    /// not grown yet.
    /// </remarks>
    private void OnJumpToSection(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FrameworkElement target })
        {
            return;
        }

        for (var ancestor = VisualTreeHelper.GetParent(target); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is Expander { IsExpanded: false } expander)
            {
                expander.IsExpanded = true;
            }
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            target.BringIntoView();

            // Focus follows the scroll, so a keyboard user lands in the section rather
            // than having the page move under a caret that is still where it was.
            target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }));
    }

    private void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        => _viewModel.SetPresentationActive(e.NewValue is true);

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

    /// <summary>
    /// Records which page the user moved to.
    /// </summary>
    /// <remarks>
    /// <c>SelectionChanged</c> bubbles, so every combo box and list on every page raises
    /// it through this handler as well. Only the rail's own transitions are interesting,
    /// and logging the rest would bury them - hence the source check rather than a
    /// filter on the arguments.
    /// </remarks>
    private void OnNavigationSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, NavigationTabs))
        {
            return;
        }

        var to = e.AddedItems.Count > 0 ? DescribeTab(e.AddedItems[0]) : "-";
        var from = e.RemovedItems.Count > 0 ? DescribeTab(e.RemovedItems[0]) : null;

        AppLog.Info(from is null ? $"Sekme açıldı: {to}" : $"Sekme değişti: {from} → {to}");
    }

    /// <summary>The automation name each navigation item carries, for logs and readers.</summary>
    private static string DescribeTab(object? item) => item is System.Windows.Controls.TabItem tab
        && System.Windows.Automation.AutomationProperties.GetName(tab) is { Length: > 0 } name
        ? name
        : "?";

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

        Detach();
        base.OnClosing(e);

        if (!_exiting)
        {
            ExitRequested?.Invoke();
        }
    }
}
