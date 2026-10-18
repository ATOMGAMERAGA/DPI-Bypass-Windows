using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.SocialAccess;

/// <summary>Never lets the existing elevated engine give its token to a browser.</summary>
public static class DesktopUrlLauncher
{
    public static bool TryLaunchUserApplication(IEnumerable<string> arguments)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var token = UserShellToken();
            if (token is null || !DuplicateTokenEx(token, 0xB, nint.Zero, 2, 1, out var primary)) return false;
            using (primary)
            {
                var executable = Environment.ProcessPath!;
                // Only window-startup flags cross from an old elevated logon task.
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--minimized", "--autostart", "--ui-selftest", "--show" };
                var command = new StringBuilder("\"" + executable + "\" " + string.Join(' ', arguments.Where(allowed.Contains)));
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                if (!CreateProcessWithTokenW(primary, 1, executable, command, 0, nint.Zero,
                        AppContext.BaseDirectory, ref startup, out var process)) return false;
                CloseHandle(process.Thread); CloseHandle(process.Process); return true;
            }
        }
        catch { return false; }
    }
    public static bool Available()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!Elevation.IsElevated) return true;
        using var token = UserShellToken();
        return token is not null;
    }
    public static void Open(SocialTarget target)
    {
        if (!SocialTarget.TryCreate(target.PlatformId, target.Url.AbsoluteUri, out var checkedTarget)) throw new ArgumentException("INVALID_TARGET");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Elevation.IsElevated)
        {
            Process.Start(new ProcessStartInfo(checkedTarget!.Url.AbsoluteUri) { UseShellExecute = true }); return;
        }
        using var token = UserShellToken() ?? throw new InvalidOperationException("UNELEVATED_USER_SHELL_UNAVAILABLE");
        if (!DuplicateTokenEx(token, 0xB, nint.Zero, 2, 1, out var primary)) throw new InvalidOperationException("USER_TOKEN_UNAVAILABLE");
        using (primary)
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var command = new StringBuilder($"\"{explorer}\" \"{checkedTarget!.Url.AbsoluteUri}\"");
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            if (!CreateProcessWithTokenW(primary, 1, explorer, command, 0, nint.Zero,
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), ref startup, out var process))
                throw new InvalidOperationException("USER_BROWSER_LAUNCH_FAILED");
            CloseHandle(process.Thread); CloseHandle(process.Process);
        }
    }
    private static SafeAccessTokenHandle? UserShellToken()
    {
        var window = GetShellWindow();
        if (window == nint.Zero) return null;
        GetWindowThreadProcessId(window, out var pid);
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 0xB, out var token)) return null;
        try
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            if (identity.User?.Value != Elevation.CurrentUserSid || !GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) || elevated != 0)
            { token.Dispose(); return null; }
            return token;
        }
        catch { token.Dispose(); return null; }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int size, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int impersonation, int type, out SafeAccessTokenHandle primary);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint flags, string app, StringBuilder command,
        uint creationFlags, nint environment, string directory, ref StartupInfo startup, out ProcessInformation process);
}
