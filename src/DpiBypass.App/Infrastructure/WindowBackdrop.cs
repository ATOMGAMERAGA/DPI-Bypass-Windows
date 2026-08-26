using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace DpiBypass.App.Infrastructure;

/// <summary>
/// Applies the Windows 11 Mica material behind the window - but only where Windows
/// is going to draw it.
/// </summary>
/// <remarks>
/// <para>
/// Mica is what makes an app look like it belongs on Windows 11 rather than like a
/// port, and the way it is switched on is by handing the client area over to the
/// compositor: the WPF render target stops painting a background and DWM paints the
/// material behind it. When DWM does paint, that is beautiful. When it does not,
/// the window is a hole - the title bar is there, the controls are there, and the
/// body shows whatever is behind the window. To someone who just double-clicked a
/// shortcut that is indistinguishable from the app never opening, and it is the
/// worst possible failure for a program whose whole job is to be reachable from the
/// notification area.
/// </para>
/// <para>
/// So the material is now only requested when every precondition for it being drawn
/// holds, and the transparency is applied strictly after the attribute has been
/// accepted, never before. Anything unexpected rolls the window back to an opaque
/// background, which looks slightly less modern and always works.
/// </para>
/// </remarks>
public static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int BackdropMainWindow = 2;
    private const int BackdropNone = 1;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int SmRemoteSession = 0x1000;

    /// <summary>
    /// DWMWA_SYSTEMBACKDROP_TYPE arrived in 22H2. On 21H2 the call is rejected, and
    /// on anything older dwmapi has no idea what is being asked of it.
    /// </summary>
    private static readonly Version BackdropMinimum = new(10, 0, 22621);

    /// <summary>Why the backdrop was or was not used, for the log.</summary>
    public static string Availability { get; private set; } = "denenmedi";

    public static bool IsSupported => DescribeUnavailability() is null;

    /// <summary>
    /// Why the material would not be drawn right now, or null when it would be.
    /// </summary>
    public static string? DescribeUnavailability()
        => Environment.OSVersion.Version < BackdropMinimum
            ? $"Windows {Environment.OSVersion.Version} · Mica desteklenmiyor"
            : DescribeBlocker();

    /// <summary>Returns true when Mica was applied and the caller should stop painting a background.</summary>
    public static bool TryApply(Window window, bool darkMode)
    {
        // The title bar follows the app theme whether or not the material is used.
        UpdateTitleBarTheme(window, darkMode);

        var blocker = DescribeUnavailability();
        if (blocker is not null)
        {
            Availability = blocker;
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            Availability = "pencere tanıtıcısı yok";
            return false;
        }

        HwndSource? source;
        try
        {
            source = HwndSource.FromHwnd(handle);
        }
        catch (Exception)
        {
            source = null;
        }

        if (source?.CompositionTarget is null)
        {
            // Without the render target there is no way to make the client area
            // transparent, and a Mica window that keeps painting over the material
            // just looks like a normal window - so leave it as one.
            Availability = "işleme hedefi yok";
            return false;
        }

        try
        {
            var backdrop = BackdropMainWindow;
            var result = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            if (result != 0)
            {
                Availability = $"DWM isteği reddetti (0x{result:X8})";
                return false;
            }

            // Only now, with the material guaranteed to be drawn, is it safe to stop
            // painting the client area. Doing this first is what turns a failed
            // backdrop into an invisible window.
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;

            Availability = "Mica";
            return true;
        }
        catch (DllNotFoundException)
        {
            Availability = "dwmapi.dll yok";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            Availability = "dwmapi bu çağrıyı tanımıyor";
            return false;
        }
        catch (Exception ex)
        {
            Availability = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Puts an opaque window back. Used when the material stops being drawn while the
    /// app is running - the user turning transparency off, or a session change.
    /// </summary>
    public static void Remove(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;

        try
        {
            if (handle != nint.Zero)
            {
                var none = BackdropNone;
                DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref none, sizeof(int));

                var source = HwndSource.FromHwnd(handle);
                if (source?.CompositionTarget is not null)
                {
                    source.CompositionTarget.BackgroundColor = Colors.Black;
                }
            }
        }
        catch (Exception)
        {
            // The background reference below is what actually makes it readable.
        }

        window.SetResourceReference(Window.BackgroundProperty, "AppWindowBackgroundBrush");
        Availability = "kapalı";
    }

    /// <summary>Keeps the title bar's light/dark rendering in step with the app theme.</summary>
    public static void UpdateTitleBarTheme(Window window, bool darkMode)
    {
        if (Environment.OSVersion.Version < new Version(10, 0, 18985))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        try
        {
            var dark = darkMode ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }

    /// <summary>
    /// The states in which Windows accepts the backdrop request but does not
    /// necessarily paint anything behind the window. Null means it is safe.
    /// </summary>
    private static string? DescribeBlocker()
    {
        try
        {
            // The return value is an HRESULT, not the answer: zero means the call
            // worked and the flag it wrote is what to read.
            if (DwmIsCompositionEnabled(out var composited) != 0 || !composited)
            {
                return "masaüstü birleştirme kapalı";
            }
        }
        catch (Exception)
        {
            return "dwmapi sorgulanamadı";
        }

        try
        {
            // Over Remote Desktop the material is not composited at the far end.
            if (GetSystemMetrics(SmRemoteSession) != 0)
            {
                return "uzak masaüstü oturumu";
            }
        }
        catch (Exception)
        {
            // Not fatal; carry on with the remaining checks.
        }

        try
        {
            if (SystemParameters.HighContrast)
            {
                return "yüksek karşıtlık teması";
            }
        }
        catch (Exception)
        {
            // Ditto.
        }

        if (!TransparencyEffectsEnabled())
        {
            // With transparency off the material degrades to a flat fill that WPF is
            // not told about, so the safe thing is to paint the window ourselves.
            return "saydamlık efektleri kapalı";
        }

        try
        {
            // Tier 0 is WPF rendering in software - a virtual machine, a remoted
            // session, a driver that failed to initialise. The material is a
            // compositor effect, so on a window whose client area is being painted by
            // the CPU there is nothing behind the transparency but the desktop.
            if ((RenderCapability.Tier >> 16) == 0)
            {
                return "donanım hızlandırma kapalı";
            }
        }
        catch (Exception)
        {
            // Cannot tell; the checks above already cover the common refusals.
        }

        return null;
    }

    private static bool TransparencyEffectsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
