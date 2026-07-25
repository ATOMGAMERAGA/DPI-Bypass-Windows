using System.Windows;
using Microsoft.Win32;

namespace AtomDpi.App.Infrastructure;

/// <summary>
/// Keeps the app's palette in step with the Windows light/dark setting, live.
/// </summary>
/// <remarks>
/// The Fluent theme handles the built-in control chrome; the app's own card, text
/// and accent brushes live in <c>Theme/Light.xaml</c> and <c>Theme/Dark.xaml</c> and
/// are swapped in at index 0 of the merged dictionaries. Owning the palette means
/// the window looks identical on Windows 10 and 11 rather than depending on which
/// internal resource keys a given build happens to ship.
/// </remarks>
public sealed class ThemeManager : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Application _application;
    private bool _isDark;

    public ThemeManager(Application application)
    {
        _application = application;
        _isDark = IsSystemDark();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsDark => _isDark;

    public event Action<bool>? ThemeChanged;

    public void Apply()
    {
        var source = new Uri(_isDark ? "Theme/Dark.xaml" : "Theme/Light.xaml", UriKind.Relative);
        var dictionary = new ResourceDictionary { Source = source };

        var merged = _application.Resources.MergedDictionaries;
        if (merged.Count == 0)
        {
            merged.Add(dictionary);
        }
        else
        {
            merged[0] = dictionary;
        }
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // The value is "apps use LIGHT theme", so zero means dark.
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color))
        {
            return;
        }

        var dark = IsSystemDark();
        if (dark == _isDark)
        {
            return;
        }

        _isDark = dark;

        _application.Dispatcher.Invoke(() =>
        {
            Apply();
            ThemeChanged?.Invoke(dark);
        });
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
