using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DpiBypass.App.Infrastructure;

/// <summary>
/// Applies the Windows 11 Mica material behind the window.
/// </summary>
/// <remarks>
/// Mica is what makes an app look like it belongs on Windows 11 rather than like a
/// port. It needs the DWM attributes below plus a transparent client area, so the
/// whole thing is applied together and rolled back if any part is unavailable -
/// on Windows 10 the window simply keeps the solid Fluent background.
/// </remarks>
public static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int BackdropMica = 2;

    private static readonly Version MicaMinimum = new(10, 0, 22000);

    public static bool IsSupported => Environment.OSVersion.Version >= MicaMinimum;

    /// <summary>Returns true when Mica was applied and the caller should stop painting a background.</summary>
    public static bool TryApply(Window window, bool darkMode)
    {
        if (!IsSupported)
        {
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return false;
        }

        try
        {
            var dark = darkMode ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            var backdrop = BackdropMica;
            if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) != 0)
            {
                return false;
            }

            // The material is drawn by the compositor underneath the client area, so
            // the client area has to stop being opaque or it hides the effect.
            var source = HwndSource.FromHwnd(handle);
            if (source is not null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            window.Background = Brushes.Transparent;

            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Keeps the title bar's light/dark rendering in step with the app theme.</summary>
    public static void UpdateTitleBarTheme(Window window, bool darkMode)
    {
        if (!IsSupported)
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
