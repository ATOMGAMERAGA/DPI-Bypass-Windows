using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DpiBypass.App.ViewModels;
using DpiBypass.Core.Logging;
using DpiBypass.Core.Onboarding;
using TabControl = System.Windows.Controls.TabControl;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace DpiBypass.App.Infrastructure;

/// <summary>Exercises real WPF layout on the Windows CI runner without running network commands.</summary>
internal static class UiLayoutSelfTest
{
    public static void Run(MainWindow window)
    {
        var tabs = (TabControl)window.FindName("NavigationTabs");
        var originalTab = tabs.SelectedIndex;
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        var deferred = tabs.Items.Cast<TabItem>()
            .Where(tab => DeferredTabContent.GetTemplate(tab) is not null).ToArray();
        Require(deferred.Length == 4, "Expected four deferred navigation pages.");
        Require(deferred.All(tab => tab.Content is null), "Unvisited pages were built before the first frame.");
        var realised = new Dictionary<TabItem, object>();
        var palettes = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary();
        palettes.Add(palette);
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            {
                palette.Source = new Uri($"Theme/{theme}.xaml", UriKind.Relative);
                // 760x560 is the window's own minimum, which is what a 1366x768 laptop
                // at 125% text scaling has room for: 614 usable DIP of height. The old
                // minimum of 620 did not fit on that machine at all.
                foreach (var width in new[] { 760d, 820d, 1080d })
                {
                    window.Width = width;
                    window.Height = width switch { 760d => 560d, 820d => 620d, _ => 780d };
                    for (var i = 0; i < tabs.Items.Count; i++)
                    {
                        tabs.SelectedIndex = i;
                        window.UpdateLayout();
                        var tab = (TabItem)tabs.Items[i];
                        Require(tab.Content is FrameworkElement, "Selected page has no controls.");
                        Require(ReferenceEquals(((FrameworkElement)tab.Content).DataContext, window.DataContext),
                            "Page lost its view model.");
                        if (realised.TryGetValue(tab, out var previous))
                            Require(ReferenceEquals(previous, tab.Content), "Revisiting a page recreated its controls.");
                        else
                            realised.Add(tab, tab.Content);

                        // The settings shortcuts use ElementName inside a template's
                        // namescope. Verify they still point to their section controls.
                        foreach (var shortcut in Descendants<Button>(window)
                            .Where(button => button.ReadLocalValue(FrameworkElement.TagProperty)
                                is System.Windows.Data.BindingExpression))
                            Require(shortcut.Tag is FrameworkElement, "Section shortcut lost its target.");

                        // No button is accidentally stretched by a layout bug. The one
                        // control that is deliberately large - the connection ring's
                        // button - is exempt by name rather than by raising the bound for
                        // everything, which would retire the check for the other forty.
                        foreach (var button in Descendants<Button>(window)
                            .Where(b => b.IsVisible && b.Name != "ConnectButton"))
                        {
                            Require(button.ActualHeight is > 0 and <= 64,
                                $"Unexpected button height: {button.Content} ({button.ActualHeight})");
                        }
                    }

                    tabs.SelectedIndex = 4; // Settings contains the latency action.
                    window.UpdateLayout();
                    VerifyLatencyProgress(window, $"{theme}-{width:0}");

                    // The two surfaces that are new and cannot be checked by reading
                    // markup: the connection control and the greeting. Both are rendered
                    // at every size and palette, so a review has real frames of them.
                    tabs.SelectedIndex = 0;
                    window.UpdateLayout();
                    VerifyConnectionControl(window);
                    SaveFrame(window, $"status-{theme}-{width:0}");

                    tabs.SelectedIndex = 4;
                    window.UpdateLayout();
                    SaveFrame(window, $"settings-{theme}-{width:0}");

                    VerifyWelcome(window, $"{theme}-{width:0}");

                    // Every capsule in the window, against real measured heights rather
                    // than against the markup that asked for them.
                    VerifyBadgeShapes(window, $"{theme}-{width:0}");
                }
            }
            // One page of badges on their own, so the shape can be looked at rather than
            // hunted for in a screenshot of the whole window.
            foreach (var theme in new[] { "Light", "Dark" })
            {
                palette.Source = new Uri($"Theme/{theme}.xaml", UriKind.Relative);
                SaveBadgeGallery(theme);
            }

            AppLog.Info(
                "Arayüz yerleşimi doğrulandı: 6 sekme, 3 pencere boyutu, 2 palet; "
                + "ilerleme alanı sabit, karşılama ve bağlantı denetimi çizildi, "
                + "kapsül rozetleri ölçülen yüksekliğe göre doğrulandı.");
        }
        finally
        {
            palettes.Remove(palette);
            window.Width = originalWidth;
            window.Height = originalHeight;
            tabs.SelectedIndex = originalTab;
        }
    }

    /// <summary>
    /// Checks that every badge marked as a capsule is drawn as one.
    /// </summary>
    /// <remarks>
    /// The bug this covers is that WPF does not read a large corner radius the way CSS
    /// does. It clamps each axis on its own and never to <c>min(width, height) / 2</c>, so
    /// "round the ends off" written as a big number draws an ellipse inscribed in the
    /// badge - no straight edge anywhere, and points where the blunt ends should be. The
    /// only radius that is a capsule is half the height the stroke leaves behind, and it
    /// has to follow the measured height because these badges size themselves to their
    /// text. See Infrastructure/PillShape.cs.
    ///
    /// Asserted here, on a laid-out window at three sizes and both palettes, because the
    /// unit tests can only resolve the geometry WPF would produce - they cannot lay a
    /// badge out and read back how tall it turned out to be.
    /// </remarks>
    private static void VerifyBadgeShapes(MainWindow window, string scenario)
    {
        var badges = Descendants<Border>(window)
            .Where(border => PillShape.GetIsPill(border) && border.IsVisible && border.ActualHeight > 0)
            .ToArray();

        Require(badges.Length > 0, $"No capsule badges were laid out at {scenario}.");

        foreach (var badge in badges)
        {
            var radius = badge.CornerRadius;
            var expected = (badge.ActualHeight - badge.BorderThickness.Top) / 2;

            Require(
                Math.Abs(radius.TopLeft - expected) < 0.51,
                $"A capsule {badge.ActualWidth:0.#}x{badge.ActualHeight:0.#} at {scenario} carries "
                    + $"radius {radius.TopLeft:0.##}; a capsule needs {expected:0.##}.");

            // Uniform, or Border takes its complex render path and the corners stop
            // matching each other.
            Require(
                radius.TopLeft == radius.TopRight
                && radius.TopLeft == radius.BottomLeft
                && radius.TopLeft == radius.BottomRight,
                $"A capsule at {scenario} has mismatched corners: {radius}.");

            // A capsule needs somewhere to put its straight edge. A badge narrower than it
            // is tall is a circle at best and an ellipse at worst, whatever radius it has.
            Require(
                badge.ActualWidth >= badge.ActualHeight - 0.51,
                $"A capsule at {scenario} is {badge.ActualWidth:0.#} wide and {badge.ActualHeight:0.#} tall, "
                    + "so it has no straight edge to round off.");
        }
    }

    /// <summary>
    /// The ping card holds still while a run starts, and its detail section still builds.
    /// </summary>
    /// <remarks>
    /// The card is now one switch and three figures; the target pickers, the per-metric
    /// tiles and the manual re-runs moved into the expander below it. So the figures are
    /// what must not move when a run starts, and the moved controls are checked after the
    /// expander is opened - collapsed content has no visual tree to look at.
    /// </remarks>
    private static void VerifyLatencyProgress(MainWindow window, string scenario)
    {
        var panel = (FrameworkElement)FindPageElement(window, "LatencyProgressPanel");
        var slot = (FrameworkElement)FindPageElement(window, "LatencyProgressSlot");
        var figures = (FrameworkElement)FindPageElement(window, "PingFigures");
        var title = (TextBlock)FindPageElement(window, "LatencyProgressLabel");
        var section = (FrameworkElement)FindPageElement(window, "LatencySection");
        var idleHint = (FrameworkElement)FindPageElement(window, "LatencyIdleHint");
        var statusWord = (TextBlock)FindPageElement(window, "PingStatusWord");
        var switchControl = (FrameworkElement)FindPageElement(window, "PingSwitch");

        var oldHintVisibility = idleHint.Visibility;
        var oldVisibility = panel.Visibility;
        var oldText = title.Text;
        var oldStatus = statusWord.Text;
        try
        {
            panel.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
            statusWord.SetCurrentValue(TextBlock.TextProperty, "Kapalı");
            window.UpdateLayout();

            var before = figures.TranslatePoint(new Point(), section);
            var figuresSize = figures.RenderSize;
            var slotSize = slot.RenderSize;
            var switchSize = switchControl.RenderSize;
            Require(slotSize.Height is > 0 and <= 80, "Progress slot must remain compact.");
            Require(figuresSize.Height is > 0, "The three figures did not lay out.");

            // A long update and the longest status word must not push the figures down or
            // squeeze the switch.
            idleHint.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            panel.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            title.SetCurrentValue(TextBlock.TextProperty,
                "Daha iyi bağlantı yolu aranıyor; ağ kartı seçenekleri ve bağlantı kalitesi ölçülüyor…");
            statusWord.SetCurrentValue(TextBlock.TextProperty, "İyileştirme uygulanıyor");
            window.UpdateLayout();

            var after = figures.TranslatePoint(new Point(), section);
            Require(Math.Abs(before.Y - after.Y) < 1, "Starting a run moved the three figures.");
            Require(figures.RenderSize == figuresSize, "The figures row changed size while busy.");
            Require(slot.RenderSize == slotSize, "The progress panel changed size.");
            Require(switchControl.RenderSize == switchSize, "The switch changed size while busy.");

            // Open the details and make sure everything that moved in there still builds -
            // its templates and StaticResource references are only resolved on expansion.
            var details = Descendants<Expander>(section).First();
            details.SetCurrentValue(Expander.IsExpandedProperty, true);
            window.UpdateLayout();

            foreach (var name in new[] { "LatencyPrimaryButton", "LatencyResultCards" })
            {
                Require(
                    Descendants<FrameworkElement>(details).Any(element => element.Name == name),
                    $"{name} is not reachable from the details section at {scenario}.");
            }

            var action = Descendants<Button>(details).First(button => button.Name == "LatencyPrimaryButton");
            Require(action.ActualHeight is > 0 and <= 64, "The detail action has an unexpected height.");

            details.SetCurrentValue(Expander.IsExpandedProperty, false);
            section.BringIntoView(new Rect(0, 0, section.ActualWidth, 400));
            window.UpdateLayout();
            SaveFrame(window, scenario);
        }
        finally
        {
            idleHint.SetCurrentValue(UIElement.VisibilityProperty, oldHintVisibility);
            panel.SetCurrentValue(UIElement.VisibilityProperty, oldVisibility);
            title.SetCurrentValue(TextBlock.TextProperty, oldText);
            statusWord.SetCurrentValue(TextBlock.TextProperty, oldStatus);
        }
    }

    private static void VerifyConnectionControl(MainWindow window)
    {
        var shell = (FrameworkElement)window.FindName("AppShell");

        // By name, not by command: the top bar's button carries the same command, and a
        // visual-tree walk reaches it first - it is 38 DIP tall, so a size check would
        // have been measuring the wrong control.
        var button = (Button)FindPageElement(window, "ConnectButton");

        Require(button.ActualWidth > 100 && button.ActualHeight > 100, "The connection button is not the hero control.");

        var anchor = button.TranslatePoint(new Point(), shell);
        Require(anchor.Y > 0, "The connection button is not laid out.");
    }

    /// <summary>
    /// The greeting covers the content area, leaves the window alone, and hands it back.
    /// </summary>
    /// <remarks>
    /// The window's size and position are read before and after. "Covers the content
    /// area" and "takes over the screen" look the same in a description and nothing alike
    /// to somebody who had the window where they wanted it, so the difference is measured
    /// rather than asserted.
    /// </remarks>
    private static void VerifyWelcome(MainWindow window, string scenario)
    {
        var viewModel = (MainViewModel)window.DataContext;
        var shell = (FrameworkElement)window.FindName("AppShell");
        var overlay = (FrameworkElement)window.FindName("WelcomeOverlay");

        var width = window.Width;
        var height = window.Height;
        var left = window.Left;
        var top = window.Top;
        var state = window.WindowState;

        try
        {
            viewModel.BeginWelcome(WelcomeKind.Tour);
            window.UpdateLayout();

            Require(overlay.IsVisible, "The greeting did not appear.");
            Require(!shell.IsVisible, "The app stayed reachable behind the greeting.");
            Require(window.WindowState == state, "The greeting changed the window state.");
            Require(Math.Abs(window.Width - width) < 0.5 && Math.Abs(window.Height - height) < 0.5,
                "The greeting resized the window.");
            Require(double.IsNaN(left) || Math.Abs(window.Left - left) < 0.5, "The greeting moved the window.");
            Require(double.IsNaN(top) || Math.Abs(window.Top - top) < 0.5, "The greeting moved the window.");

            // It fills the content area rather than floating in the middle of it. Measured
            // against the window's own content element: Window.ActualWidth is the outer
            // width, resize frame included, and the client area is several DIP narrower.
            var client = (FrameworkElement)window.Content;
            Require(Math.Abs(overlay.ActualWidth - client.ActualWidth) < 2,
                $"The greeting is {overlay.ActualWidth:0} wide in a {client.ActualWidth:0} content area.");
            Require(overlay.ActualHeight <= client.ActualHeight + 1, "The greeting overflowed the content area.");

            // Usable from the first frame. The entrance animates opacity and a few DIP of
            // travel, and neither may stand between somebody and the way out: the actions
            // are laid out, enabled, and hit-testable before any of it has finished.
            foreach (var name in new[] { "Tanıtımı atla", "Karşılamayı açılışta göster" })
            {
                var action = Descendants<FrameworkElement>(overlay).FirstOrDefault(element =>
                    AutomationProperties.GetName(element) == name);

                Require(action is not null, $"The greeting has no '{name}' control.");
                Require(action!.IsVisible, $"'{name}' is not visible on the first frame.");
                Require(action.IsEnabled, $"'{name}' is not usable on the first frame.");
                Require(action.ActualWidth > 0 && action.ActualHeight > 0,
                    $"'{name}' has not been laid out on the first frame.");
                Require(action.IsHitTestVisible, $"'{name}' cannot be clicked on the first frame.");
            }

            // The light behind the greeting never intercepts a click.
            var glow = (FrameworkElement)window.FindName("WelcomeGlow");
            Require(!glow.IsHitTestVisible, "The greeting's background light is hit-testable.");

            SaveFrame(window, $"welcome-{scenario}");

            // Every card is reachable and none of them overflows the content area.
            for (var card = 0; card < viewModel.WelcomeCards.Count; card++)
            {
                window.UpdateLayout();
                Require(overlay.ActualHeight <= client.ActualHeight + 1, "A greeting card overflowed the window.");
                viewModel.WelcomeNextCommand.Execute(null);
            }

            Require(!overlay.IsVisible, "The greeting did not end on the last card.");
            Require(shell.IsVisible, "The app did not come back after the greeting.");
        }
        finally
        {
            viewModel.BeginWelcome(WelcomeKind.None);
            window.UpdateLayout();

            // Collapsed, which is what stops every storyboard inside it. A greeting that
            // was dismissed, or a window that went to the notification area, must not leave
            // anything animating behind it.
            Require(!overlay.IsVisible, "The greeting is still visible after being dismissed.");
        }
    }

    private static FrameworkElement FindPageElement(MainWindow window, string name)
        => Descendants<FrameworkElement>(window).Single(element => element.Name == name);

    /// <summary>
    /// Renders the capsule badges on their own, at the text lengths and text scales that
    /// a fixed radius could never have covered.
    /// </summary>
    /// <remarks>
    /// The ends are what went wrong, and in a screenshot of the whole window a badge is
    /// forty pixels across. This puts one of each on a page at a readable size so the
    /// difference between a capsule and an oval is visible without measuring it.
    /// </remarks>
    private static void SaveBadgeGallery(string theme)
    {
        var rows = new StackPanel { Margin = new Thickness(24) };

        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            foreach (var text in new[] { "i", "BETA", "Önerilen", "Çok daha uzun bir rozet metni" })
            {
                var badge = new Border
                {
                    Background = (Brush)Application.Current.Resources["AppAccentSoftBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["AppAccentBrush"],
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10 * scale, 2 * scale, 10 * scale, 2 * scale),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 10),
                    Child = new TextBlock
                    {
                        Text = text,
                        FontSize = 11.5 * scale,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)Application.Current.Resources["AppAccentBrush"],
                    },
                };

                PillShape.SetIsPill(badge, true);
                rows.Children.Add(badge);
            }

            // The welcome dots, at rest and current, on the same page.
            var dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 18),
            };

            foreach (var width in new[] { 8d, 8d, 22d })
            {
                var dot = new Border
                {
                    Width = width * scale,
                    Height = 8 * scale,
                    Margin = new Thickness(4 * scale, 0, 4 * scale, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = (Brush)Application.Current.Resources[
                        width > 8 ? "AppAccentBrush" : "AppDividerBrush"],
                };

                PillShape.SetIsPill(dot, true);
                dots.Children.Add(dot);
            }

            rows.Children.Add(dots);
        }

        var page = new Border
        {
            Background = (Brush)Application.Current.Resources["AppBackgroundBrush"],
            Child = rows,
        };

        // Measured and arranged off-window: nothing here is ever shown to a user, it only
        // has to be laid out well enough to render.
        page.Measure(new Size(520, double.PositiveInfinity));
        page.Arrange(new Rect(new Point(0, 0), page.DesiredSize));
        page.UpdateLayout();

        SaveVisual(page, page.DesiredSize, $"badges-{theme}");
    }

    private static void SaveFrame(MainWindow window, string scenario)
    {
        SaveVisual(window, new Size(window.ActualWidth, window.ActualHeight), $"latency-{scenario}");
    }

    private static void SaveVisual(Visual visual, Size size, string name)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ui-selftest"));
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width),
            (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, $"{name}.png"));
        encoder.Save(output);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
