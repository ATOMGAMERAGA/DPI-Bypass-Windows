using System.Diagnostics;
using System.Security.Principal;

namespace DpiBypass.Core.Interop;

/// <summary>Administrator checks and the relaunch-as-admin path.</summary>
public static class Elevation
{
    /// <summary>
    /// The packet driver cannot be opened without administrator rights, so this is a
    /// hard requirement rather than a nice-to-have.
    /// </summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static string CurrentUserSid
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.User?.Value ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    public static string CurrentUserName
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.Name;
            }
            catch (Exception)
            {
                return Environment.UserName;
            }
        }
    }

    /// <summary>Restarts this executable with the UAC prompt. Returns false if the user declined.</summary>
    public static bool TryRelaunchElevated(string? arguments = null)
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(startInfo);
            return true;
        }
        catch (Exception)
        {
            // 1223 == the user clicked No on the consent dialog.
            return false;
        }
    }
}
