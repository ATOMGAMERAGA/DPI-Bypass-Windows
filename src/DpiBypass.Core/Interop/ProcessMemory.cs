using System.Runtime.InteropServices;

namespace DpiBypass.Core.Interop;

/// <summary>
/// Hands pages the process is no longer touching back to Windows.
/// </summary>
/// <remarks>
/// <para>
/// A .NET process keeps its working set after a peak. Building the window, parsing the
/// XAML, jitting the startup path and running a latency benchmark each commit tens of
/// megabytes that are dead the moment they are finished with, and nothing asks for them
/// back: the collector releases the managed heap but the pages stay resident, which is
/// exactly the number the user sees in Task Manager for an app sitting in the
/// notification area doing nothing.
/// </para>
/// <para>
/// <c>SetProcessWorkingSetSize(-1, -1)</c> is the documented way to say so. It is a
/// hint, not a free: pages that are still live are faulted straight back in, so the
/// cost of being wrong is a handful of soft faults the next time the window is opened,
/// and nothing at all is discarded. Called only when the window has been away for a
/// few seconds, never on the packet path.
/// </para>
/// </remarks>
public static class ProcessMemory
{
    /// <summary>
    /// Asks Windows to trim this process's working set. Returns false when it could not.
    /// </summary>
    public static bool TrimWorkingSet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // -1 in both bounds is the documented "trim as much as you can" value; any
            // other pair would set a permanent quota, which is not what this is for.
            return SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch (Exception)
        {
            // A memory hint failing is not worth a single line of anybody's log.
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(nint process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);
}
