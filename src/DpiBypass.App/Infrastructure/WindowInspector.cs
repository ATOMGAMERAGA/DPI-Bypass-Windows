using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DpiBypass.Core.Startup;

namespace DpiBypass.App.Infrastructure;

/// <summary>
/// What the window manager and the compositor say about a window, as opposed to what
/// WPF says about it.
/// </summary>
/// <param name="Handle">The HWND, or zero when the window has no source yet.</param>
/// <param name="Visible"><c>IsWindowVisible</c>.</param>
/// <param name="Minimised"><c>IsIconic</c>.</param>
/// <param name="Cloak">What DWM is hiding, if anything.</param>
/// <param name="Bounds">The window rectangle in device pixels.</param>
/// <param name="WorkAreas">Every monitor work area currently attached, in device pixels.</param>
/// <param name="OnScreen">Whether enough of <paramref name="Bounds"/> lands on one of them.</param>
internal readonly record struct NativeWindowState(
    nint Handle,
    bool Visible,
    bool Minimised,
    WindowCloak Cloak,
    WindowRect Bounds,
    IReadOnlyList<WindowRect> WorkAreas,
    bool OnScreen)
{
    public bool HasHandle => Handle != nint.Zero;

    /// <summary>The single line that goes in the log when a window is not reachable.</summary>
    public override string ToString()
    {
        var monitors = WorkAreas.Count == 0
            ? "ekran bilgisi yok"
            : string.Join(" | ", WorkAreas.Select(a => a.ToString()));

        return $"HWND=0x{Handle:X} · IsWindowVisible={Visible} · simge durumunda={Minimised} · "
            + $"DWM gizleme={Cloak} · pencere={Bounds} · ekranda={OnScreen} · çalışma alanları: {monitors}";
    }
}

/// <summary>
/// Reads the real state of a window from Windows.
/// </summary>
/// <remarks>
/// Everything here answers a question WPF cannot. <c>Window.IsVisible</c> is true from
/// the moment a handle exists and stays true whether or not anything is ever drawn,
/// whether or not DWM is cloaking the window, and whether or not the coordinates it
/// holds belong to a monitor that is still attached. Those three are the ways this app
/// has ended up alive and unreachable, so they are asked about directly.
/// </remarks>
internal static class WindowInspector
{
    /// <summary>DWMWA_CLOAKED. Windows 8 and later; older builds simply refuse it.</summary>
    private const int DwmwaCloaked = 14;

    private const int DwmCloakedApp = 0x0000_0001;
    private const int DwmCloakedShell = 0x0000_0002;
    private const int DwmCloakedInherited = 0x0000_0004;

    public static NativeWindowState Inspect(Window window)
    {
        nint handle;
        try
        {
            handle = new WindowInteropHelper(window).Handle;
        }
        catch (Exception)
        {
            handle = nint.Zero;
        }

        return Inspect(handle);
    }

    public static NativeWindowState Inspect(nint handle)
    {
        if (handle == nint.Zero)
        {
            return new NativeWindowState(nint.Zero, false, false, WindowCloak.None, WindowRect.Empty, [], false);
        }

        var visible = Call(() => IsWindowVisible(handle));
        var minimised = Call(() => IsIconic(handle));
        var cloak = ReadCloak(handle);
        var bounds = ReadBounds(handle);
        var areas = WorkAreas();

        // A minimised window is parked far off the desktop by Windows itself, so its
        // rectangle says nothing about whether the user could reach it once restored.
        var onScreen = minimised || WindowPlacement.IsReachable(bounds, areas);

        return new NativeWindowState(handle, visible, minimised, cloak, bounds, areas, onScreen);
    }

    /// <summary>Every attached monitor's work area, in device pixels.</summary>
    /// <remarks>
    /// Device pixels throughout, and deliberately so: the window rectangle comes from
    /// <c>GetWindowRect</c> in the same units, and converting either into WPF's
    /// device-independent pixels would need a per-monitor scale factor that is exactly
    /// the thing being questioned when a display configuration has just changed.
    /// </remarks>
    public static IReadOnlyList<WindowRect> WorkAreas()
    {
        var areas = new List<WindowRect>();

        try
        {
            EnumDisplayMonitors(nint.Zero, nint.Zero, (monitor, _, _, _) =>
            {
                try
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        areas.Add(WindowRect.FromEdges(
                            info.Work.Left,
                            info.Work.Top,
                            info.Work.Right,
                            info.Work.Bottom));
                    }
                }
                catch (Exception)
                {
                    // One monitor we cannot read is not a reason to lose the others.
                }

                return true;
            }, nint.Zero);
        }
        catch (Exception)
        {
            // No enumeration means no judgement: WindowPlacement treats an empty list
            // as "cannot tell" and leaves the window where it is.
            return [];
        }

        return areas;
    }

    /// <summary>Moves a window back onto an attached monitor. Device pixels throughout.</summary>
    /// <returns>The rectangle it was moved to, or null when nothing needed doing.</returns>
    public static WindowRect? MoveOnScreen(nint handle)
    {
        if (handle == nint.Zero)
        {
            return null;
        }

        var areas = WorkAreas();
        var bounds = ReadBounds(handle);
        var target = WindowPlacement.MoveOnScreen(bounds, areas);

        if (target == bounds)
        {
            return null;
        }

        return SetWindowPos(
            handle,
            nint.Zero,
            (int)Math.Round(target.Left),
            (int)Math.Round(target.Top),
            (int)Math.Round(target.Width),
            (int)Math.Round(target.Height),
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder)
            ? target
            : null;
    }

    private static WindowCloak ReadCloak(nint handle)
    {
        try
        {
            // The return value is an HRESULT: anything but zero means the attribute was
            // not read, which on Windows 7 is simply "this build has no such idea".
            if (DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) != 0)
            {
                return WindowCloak.None;
            }

            var state = WindowCloak.None;
            if ((cloaked & DwmCloakedApp) != 0)
            {
                state |= WindowCloak.App;
            }

            if ((cloaked & DwmCloakedShell) != 0)
            {
                state |= WindowCloak.Shell;
            }

            if ((cloaked & DwmCloakedInherited) != 0)
            {
                state |= WindowCloak.Inherited;
            }

            return state;
        }
        catch (Exception)
        {
            // dwmapi missing or refusing the call. Not knowing is not the same as
            // cloaked, and treating it as cloaked would put a healthy window into
            // recovery on every older machine.
            return WindowCloak.None;
        }
    }

    private static WindowRect ReadBounds(nint handle)
    {
        try
        {
            return GetWindowRect(handle, out var rect)
                ? WindowRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom)
                : WindowRect.Empty;
        }
        catch (Exception)
        {
            return WindowRect.Empty;
        }
    }

    private static bool Call(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint deviceContext, nint clip, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
