using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DpiBypass.Core.Logging;
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
        var palettes = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary();
        palettes.Add(palette);
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            {
                palette.Source = new Uri($"Theme/{theme}.xaml", UriKind.Relative);
                foreach (var width in new[] { 820d, 1080d })
                {
                    window.Width = width;
                    window.Height = width == 820 ? 620 : 780;
                    for (var i = 0; i < tabs.Items.Count; i++)
                    {
                        tabs.SelectedIndex = i;
                        window.UpdateLayout();
                        foreach (var button in Descendants<Button>(window).Where(b => b.IsVisible))
                        {
                            Require(button.ActualHeight is > 0 and <= 64,
                                $"Unexpected button height: {button.Content} ({button.ActualHeight})");
                        }
                    }

                    tabs.SelectedIndex = 4; // Settings contains the latency action.
                    window.UpdateLayout();
                    VerifyLatencyProgress(window, $"{theme}-{width:0}");
                }
            }
            AppLog.Info("Arayüz yerleşimi doğrulandı: 6 sekme, 2 pencere boyutu, 2 palet; ilerleme alanı sabit.");
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
        var panel = (FrameworkElement)window.FindName("LatencyProgressPanel");
        var slot = (FrameworkElement)window.FindName("LatencyProgressSlot");
        var cards = (FrameworkElement)window.FindName("LatencyResultCards");
        var button = (Button)window.FindName("LatencyPrimaryButton");
        var title = (TextBlock)window.FindName("LatencyProgressLabel");
        var section = (FrameworkElement)window.FindName("LatencySection");
        var idleHint = (FrameworkElement)window.FindName("LatencyIdleHint");
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
