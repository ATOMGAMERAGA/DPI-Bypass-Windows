using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace AtomDpi.Core.Engine;

/// <summary>
/// Resolves a process id to its executable path, cached.
/// </summary>
/// <remarks>
/// <c>QueryFullProcessImageName</c> with limited-information access works against
/// processes that would refuse a full <c>MainModule</c> read, which matters because
/// Discord and the browsers all run sandboxed child processes.
/// </remarks>
public static class ProcessLookup
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly ConcurrentDictionary<uint, Entry> Cache = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public static string? GetImagePath(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        if (Cache.TryGetValue(processId, out var cached) && cached.Expires > DateTime.UtcNow)
        {
            return cached.Path;
        }

        var path = Query(processId);
        Cache[processId] = new Entry(path, DateTime.UtcNow.Add(Lifetime));

        if (Cache.Count > 2048)
        {
            Prune();
        }

        return path;
    }

    public static string? GetImageName(uint processId)
    {
        var path = GetImagePath(processId);
        return path is null ? null : Net.WindowsPath.FileName(path);
    }

    private static string? Query(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, entry) in Cache)
        {
            if (entry.Expires <= now)
            {
                Cache.TryRemove(key, out _);
            }
        }
    }

    public static void Clear() => Cache.Clear();

    private readonly record struct Entry(string? Path, DateTime Expires);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder exeName, ref int size);
}
