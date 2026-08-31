using System.Runtime.InteropServices;

namespace DpiBypass.Core.Interop;

/// <summary>
/// Whether this process is driving a local console or a remote desktop session.
/// </summary>
/// <remarks>
/// The latency work restarts network adapters when the user agrees to it. Doing that
/// over a remote session would take away the session that asked for it, so the answer to
/// this question is a hard gate rather than a warning. <c>SM_REMOTESESSION</c> is the
/// documented way to ask: "the calling process is associated with a Terminal Services
/// client session".
/// </remarks>
public static partial class SessionKind
{
    private const int SmRemoteSession = 0x1000;

    /// <summary>True when this is a Terminal Services / Remote Desktop client session.</summary>
    public static bool IsRemoteSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return GetSystemMetrics(SmRemoteSession) != 0;
        }
        catch (DllNotFoundException)
        {
            // Nothing to gain from guessing: a machine that cannot answer is treated as
            // remote, because the cost of being wrong that way is a candidate not
            // measured, and the cost of being wrong the other way is a lost session.
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return true;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static partial int GetSystemMetrics(int index);
}
