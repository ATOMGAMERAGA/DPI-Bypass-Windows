using System.Windows;
using DpiBypass.Core.Logging;
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

    /// <summary>
    /// Merges the palette for the current Windows setting, replacing the previous one.
    /// </summary>
    /// <remarks>
    /// A palette that will not load leaves the window looking like the Fluent theme
    /// alone, which is plain but perfectly readable - so this reports rather than
    /// throws. It is called from a dispatcher callback raised by a Windows
    /// personalisation change as well as from start-up, and neither is a place worth
    /// losing the application over a colour.
    /// </remarks>
    public void Apply()
    {
        try
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
        catch (Exception ex)
        {
            AppLog.Error("Renk paleti yüklenemedi", ex);
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

    /// <summary>
    /// Reacts to a Windows personalisation change. Runs on the SystemEvents thread.
    /// </summary>
    /// <remarks>
    /// Everything here is inside a handler because of where it runs. SystemEvents
    /// raises its notifications on a thread of its own and does not catch what a
    /// handler throws, so an exception escaping this method is unhandled on a thread
    /// nobody owns - which ends the process rather than the notification. And there
    /// is a real way to throw: once shutdown has begun the dispatcher rejects new
    /// work, so a theme change arriving while the app is closing would take it down
    /// on the way out.
    /// </remarks>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        try
        {
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color))
            {
                return;
            }

            var dark = IsSystemDark();
            var themeSwitched = dark != _isDark;
            _isDark = dark;

            // Queued rather than waited on: this runs on the SystemEvents thread, which
            // every other listener in the process shares, and a busy UI thread would hold
            // all of them up.
            _application.Dispatcher.BeginInvoke(() =>
            {
                if (themeSwitched)
                {
                    Apply();
                    ThemeChanged?.Invoke(dark);
                }

                PersonalisationChanged?.Invoke();
            });
        }
        catch (Exception)
        {
            // The palette staying as it is costs the user a colour. Letting this
            // escape costs them the application.
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
