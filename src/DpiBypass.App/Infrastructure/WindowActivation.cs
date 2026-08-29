using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DpiBypass.Core.Logging;
using DpiBypass.Core.Startup;

namespace DpiBypass.App.Infrastructure;

/// <summary>What a raise attempt managed to do. Not a claim that anything is on screen.</summary>
/// <param name="Completed">Every step ran without throwing.</param>
/// <param name="Foreground">Windows let this process take the foreground.</param>
/// <param name="Failure">The first step that did not work, with its Win32 error.</param>
public readonly record struct RaiseOutcome(bool Completed, bool Foreground, string? Failure);

/// <summary>
/// Gets a window shown, restored and raised. Whether the result is something the user
/// can see is a different question, asked elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Window.Activate"/> on its own is not enough here. Windows only lets the
/// process that owns the foreground window hand it over, so when a second launch asks
/// the running copy to show itself, the running copy - a background process that has
/// been sitting in the notification area - is refused. WPF reports nothing: the window
/// is shown, stays behind whatever the user was looking at, and the taskbar button
/// blinks. From the user's chair the shortcut did nothing.
/// </para>
/// <para>
/// The way through is for the process that <i>does</i> hold the foreground - the one
/// the user just launched - to hand its right over with <c>AllowSetForegroundWindow</c>
/// before it asks. The rest here is the belt and braces around that: restore a
/// minimised window, and push it to the top of the z-order when focus is refused.
/// </para>
/// <para>
/// What this deliberately no longer does is decide whether it worked. It used to
/// finish by asking <c>IsWindowVisible</c> and reporting that as success, and an HWND
/// that has never drawn a frame answers yes - which is how a copy with an invisible
/// window came to tell every later launch that the user was looking at the app.
/// Raised, activated, visible, rendered and reachable are five different things;
/// <see cref="WindowInspector"/> and <see cref="WindowHealthEvaluator"/> judge the last
/// three, and this one only reports what it did.
/// </para>
/// </remarks>
public static class WindowActivation
{
    /// <summary>Lets any process take the foreground from this one.</summary>
    /// <remarks>
    /// Called by a launch that is about to hand over to the running instance and exit,
    /// so the permission it gives away costs it nothing.
    /// </remarks>
    public const int AllowAnyProcess = -1;

    private const int SwRestore = 9;
    private const int SwShow = 5;

    public static void AllowForegroundHandover()
    {
        try
        {
            if (!AllowSetForegroundWindow(AllowAnyProcess))
            {
                AppLog.Debug($"Ön plan devri verilemedi ({LastError()}).");
            }
        }
        catch (Exception ex)
        {
            // Older or locked down systems: the handover still shows the window, it
            // just may not take focus.
            AppLog.Debug($"Ön plan devri çağrısı başarısız: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows, unminimises and raises the window, reporting what each step managed.
    /// </summary>
    /// <remarks>
    /// Being refused the foreground is normal - Windows protects whatever the user is
    /// typing into - and is reported rather than treated as a failure. An exception is
    /// not normal, so it is logged with the step that threw instead of disappearing
    /// into a <c>return false</c> nobody can diagnose.
    /// </remarks>
    public static RaiseOutcome Raise(Window window)
    {
        var step = "başlangıç";

        try
        {
            step = "Show";
            if (!window.IsVisible)
            {
                window.Show();
            }

            step = "WindowState";
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            // A window that was hidden while the taskbar button was suppressed has no
            // way back through the taskbar either. Assigned only when it differs:
            // writing this property on a live window makes WPF rebuild the handle.
            step = "ShowInTaskbar";
            if (!window.ShowInTaskbar)
            {
                window.ShowInTaskbar = true;
            }

            step = "tanıtıcı";
            var handle = new WindowInteropHelper(window).Handle;
            var foreground = false;

            if (handle != nint.Zero)
            {
                // SW_RESTORE on a window that is merely hidden would also un-maximise
                // it, so it is used for the one case that needs it.
                step = "ShowWindow";
                ShowWindow(handle, IsIconic(handle) ? SwRestore : SwShow);

                step = "SetForegroundWindow";
                foreground = SetForegroundWindow(handle);

                if (!foreground)
                {
                    AppLog.Debug($"Ön plana alınamadı ({LastError()}); z-sırası ile yükseltiliyor.");

                    // Raises the window above the others even when focus was refused.
                    // The second assignment puts it back into the normal band, still on
                    // top of it. Only used when the foreground request was turned down -
                    // doing it every time is a hack that steals z-order for no reason.
                    step = "Topmost";
                    window.Topmost = true;
                    window.Topmost = false;
                }
            }

            step = "Activate";
            window.Activate();

            step = "Focus";
            window.Focus();

            return new RaiseOutcome(Completed: true, foreground, Failure: null);
        }
        catch (Exception ex)
        {
            var failure = $"{step}: {Describe(ex)}";
            AppLog.Error("Pencere öne getirilemedi", ex);
            AppLog.Warning($"Pencereyi öne getirme adımı başarısız · {failure}");
            return new RaiseOutcome(Completed: false, Foreground: false, failure);
        }
    }

    /// <summary>
    /// Puts a window whose coordinates belong to a monitor that is no longer there back
    /// onto one that is. Does nothing to a window that is already reachable.
    /// </summary>
    public static bool EnsureOnScreen(Window window)
    {
        try
        {
            // A maximised or minimised window has no rectangle worth moving; restoring
            // it first is what gives the reposition something to act on.
            if (window.WindowState != WindowState.Normal)
            {
                window.WindowState = WindowState.Normal;
            }

            var handle = new WindowInteropHelper(window).Handle;
            var moved = WindowInspector.MoveOnScreen(handle);

            if (moved is null)
            {
                return false;
            }

            AppLog.Warning($"Pencere etkin ekranların dışındaydı; {moved} konumuna taşındı.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Pencere ekrana taşınamadı", ex);
            return false;
        }
    }

    private static string Describe(Exception exception) => exception is Win32Exception win32
        ? $"{exception.Message} (Win32 0x{win32.NativeErrorCode:X8})"
        : exception.Message;

    private static string LastError()
    {
        var code = Marshal.GetLastWin32Error();
        return code == 0 ? "Win32 hatası bildirilmedi" : $"Win32 0x{code:X8}: {new Win32Exception(code).Message}";
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
}
