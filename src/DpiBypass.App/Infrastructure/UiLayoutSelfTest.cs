using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DpiBypass.App.ViewModels;
using DpiBypass.Core.Logging;
using DpiBypass.Core.Onboarding;
using TabControl = System.Windows.Controls.TabControl;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

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
                }
            }
            AppLog.Info(
                "Arayüz yerleşimi doğrulandı: 6 sekme, 3 pencere boyutu, 2 palet; "
                + "ilerleme alanı sabit, karşılama ve bağlantı denetimi çizildi.");
        }
        finally
        {
            palettes.Remove(palette);
            window.Width = originalWidth;
            window.Height = originalHeight;
            tabs.SelectedIndex = originalTab;
        }
    }

    private static void VerifyLatencyProgress(MainWindow window, string scenario)
    {
        var panel = (FrameworkElement)FindPageElement(window, "LatencyProgressPanel");
        var slot = (FrameworkElement)FindPageElement(window, "LatencyProgressSlot");
        var cards = (FrameworkElement)FindPageElement(window, "LatencyResultCards");
        var button = (Button)FindPageElement(window, "LatencyPrimaryButton");
        var title = (TextBlock)FindPageElement(window, "LatencyProgressLabel");
        var section = (FrameworkElement)FindPageElement(window, "LatencySection");
        var idleHint = (FrameworkElement)FindPageElement(window, "LatencyIdleHint");
        var oldHintVisibility = idleHint.Visibility;
        var oldVisibility = panel.Visibility;
        var oldText = title.Text;
        var oldContent = button.Content;
        var oldEnabled = button.IsEnabled;
        try
        {
            panel.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
            button.SetCurrentValue(ContentControl.ContentProperty, "Bağlantımı analiz et");
            window.UpdateLayout();
            var before = cards.TranslatePoint(new Point(), section);
            var buttonSize = button.RenderSize;
            var slotSize = slot.RenderSize;
            Require(slotSize.Height is > 0 and <= 80, "Progress slot must remain compact.");

            // Long updates and disabled captions must not grow the action or push the results down.
            idleHint.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            panel.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            title.SetCurrentValue(TextBlock.TextProperty,
                "Daha iyi bağlantı yolu aranıyor; ağ kartı seçenekleri ve bağlantı kalitesi ölçülüyor…");
            button.SetCurrentValue(ContentControl.ContentProperty, "Uygun ayarları dene");
            button.SetCurrentValue(UIElement.IsEnabledProperty, false);
            window.UpdateLayout();
            var after = cards.TranslatePoint(new Point(), section);
            Require(Math.Abs(before.Y - after.Y) < 1, "Starting measurement moved the results.");
            Require(button.RenderSize == buttonSize, "The busy action changed size.");
            Require(slot.RenderSize == slotSize, "The progress panel changed size.");

            // Open details explicitly and ensure its template/content can be materialized too.
            var details = Descendants<Expander>(section).First();
            details.SetCurrentValue(Expander.IsExpandedProperty, true);
            window.UpdateLayout();
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
            button.SetCurrentValue(ContentControl.ContentProperty, oldContent);
            button.SetCurrentValue(UIElement.IsEnabledProperty, oldEnabled);
        }
    }

    /// <summary>
    /// The connection control draws every state, and its ring never displaces the text.
    /// </summary>
    /// <remarks>
    /// The ring and the label are stacked, so a stage whose headline wraps to two lines
    /// would move the button up under the ring if either were sized by its content. Each
    /// stage is driven through the view model and the control's position is compared
    /// against the first one.
    /// </remarks>
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
        }
    }

    private static FrameworkElement FindPageElement(MainWindow window, string name)
        => Descendants<FrameworkElement>(window).Single(element => element.Name == name);

    private static void SaveFrame(MainWindow window, string scenario)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ui-selftest"));
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, $"latency-{scenario}.png"));
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
