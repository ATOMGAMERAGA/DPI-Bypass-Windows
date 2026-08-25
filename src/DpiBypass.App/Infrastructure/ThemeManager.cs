using System.Windows;
using Microsoft.Win32;

namespace DpiBypass.App.Infrastructure;

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
    private ResourceDictionary? _palette;
    private bool _isDark;

    public ThemeManager(Application application)
    {
        _application = application;
        _isDark = IsSystemDark();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsDark => _isDark;

    public event Action<bool>? ThemeChanged;

    /// <summary>
    /// Raised for any personalisation change, including the ones that leave the
    /// light/dark choice alone.
    /// </summary>
    /// <remarks>
    /// Turning "transparency effects" off does not change the theme, but it does stop
    /// Windows drawing the material behind the window - and a window still handing
    /// its client area to a compositor that has stopped painting is invisible. So
    /// this fires whether or not the palette needs swapping.
    /// </remarks>
    public event Action? PersonalisationChanged;

    public void Apply()
    {
        var source = new Uri(_isDark ? "Theme/Dark.xaml" : "Theme/Light.xaml", UriKind.Relative);
        var replacement = new ResourceDictionary { Source = source };
        var merged = _application.Resources.MergedDictionaries;

        // Swap by reference, never by position. Setting ThemeMode makes WPF insert its
        // own Fluent dictionaries into this same collection, and a positional swap
        // would overwrite one of those instead of the palette.
        var index = _palette is null ? -1 : merged.IndexOf(_palette);
        if (index >= 0)
        {
            merged[index] = replacement;
        }
        else
        {
            merged.Add(replacement);
        }

        _palette = replacement;
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
        var themeSwitched = dark != _isDark;
        _isDark = dark;

        _application.Dispatcher.Invoke(() =>
        {
            if (themeSwitched)
            {
                Apply();
                ThemeChanged?.Invoke(dark);
            }

            PersonalisationChanged?.Invoke();
        });
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
