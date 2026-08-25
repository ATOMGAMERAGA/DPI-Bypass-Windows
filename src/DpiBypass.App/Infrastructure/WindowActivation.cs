using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DpiBypass.App.Infrastructure;

/// <summary>
/// Gets a window in front of the user and reports whether it actually got there.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Window.Activate"/> on its own is not enough here. Windows only lets
/// the process that owns the foreground window hand it over, so when a second
/// launch asks the running copy to show itself, the running copy - a background
/// process that has been sitting in the notification area - is refused. WPF reports
/// nothing: the window is shown, stays behind whatever the user was looking at, and
/// the taskbar button blinks. From the user's chair the shortcut did nothing.
/// </para>
/// <para>
/// The way through is for the process that <i>does</i> hold the foreground - the one
/// the user just launched - to hand its right over with
/// <c>AllowSetForegroundWindow</c> before it asks. The rest here is the belt and
/// braces around that: restore a minimised window, push it to the top of the
/// z-order even when focus is refused, and then confirm with the window manager
/// rather than trusting that any of it worked.
/// </para>
/// </remarks>
public static class WindowActivation
{
    /// <summary>Lets any process take the foreground from this one.</summary>
    /// <remarks>
    /// Called by a launch that is about to hand over to the running instance and
    /// exit, so the permission it gives away costs it nothing.
    /// </remarks>
    public const int AllowAnyProcess = -1;

    private const int SwRestore = 9;
    private const int SwShow = 5;

    public static void AllowForegroundHandover()
    {
        try
        {
            AllowSetForegroundWindow(AllowAnyProcess);
        }
        catch (Exception)
        {
            // Older or locked down systems: the handover still shows the window, it
            // just may not take focus.
        }
    }

    /// <summary>
    /// Shows, restores and raises the window. Returns true when the window manager
    /// agrees it is on screen.
    /// </summary>
    public static bool BringToFront(Window window)
    {
        try
        {
            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            // A window that was hidden while the taskbar button was suppressed has no
            // way back through the taskbar either. Assigned only when it differs:
            // writing this property on a live window makes WPF rebuild the handle.
            if (!window.ShowInTaskbar)
            {
                window.ShowInTaskbar = true;
            }

            var handle = new WindowInteropHelper(window).Handle;
            if (handle != nint.Zero)
            {
                // SW_RESTORE on a window that is merely hidden would also un-maximise
                // it, so it is used for the one case that needs it.
                ShowWindow(handle, IsIconic(handle) ? SwRestore : SwShow);
                SetForegroundWindow(handle);
            }

            window.Activate();

            // Raises the window above the others even when focus was refused. The
            // second assignment puts it back into the normal band, still on top of it.
            window.Topmost = true;
            window.Topmost = false;

            window.Focus();

            return handle == nint.Zero ? window.IsVisible : IsWindowVisible(handle);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
}
