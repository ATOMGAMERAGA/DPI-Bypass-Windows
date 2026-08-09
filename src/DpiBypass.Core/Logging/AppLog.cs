using System.Collections.Concurrent;
using System.Text;

namespace DpiBypass.Core.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

public readonly record struct LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)
{
    public override string ToString() => $"{Timestamp.ToLocalTime():HH:mm:ss} [{Level.ToString().ToUpperInvariant()[0]}] {Message}";
}

/// <summary>
/// One log sink for the whole app: a bounded in-memory buffer the UI binds to, plus
/// a daily file so a user can send us something useful after the fact.
/// </summary>
public static class AppLog
{
    private const int BufferCapacity = 500;

    private static readonly ConcurrentQueue<LogEntry> Buffer = new();
    private static readonly Lock FileGate = new();
    private static string? _filePath;

    public static event Action<LogEntry>? Written;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static IReadOnlyList<LogEntry> Snapshot() => [.. Buffer];

    public static void Initialise()
    {
        try
        {
            AppPaths.EnsureCreated();
            _filePath = Path.Combine(AppPaths.LogDirectory, $"dpibypass-{DateTime.Now:yyyy-MM-dd}.log");
            PruneOldFiles();
        }
        catch (Exception)
        {
            _filePath = null; // memory-only logging is still better than crashing
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warning(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception exception) => Write(LogLevel.Error, $"{message}: {exception.Message}");

    public static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var entry = new LogEntry(DateTimeOffset.Now, level, message);

        Buffer.Enqueue(entry);
        while (Buffer.Count > BufferCapacity && Buffer.TryDequeue(out _))
        {
            // Bounded on purpose - the UI shows a live tail, not the whole history.
        }

        Written?.Invoke(entry);
        AppendToFile(entry);
    }

    private static void AppendToFile(LogEntry entry)
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            lock (FileGate)
            {
                File.AppendAllText(
                    _filePath,
                    $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{entry.Level}] {entry.Message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Disk problems must never take the engine down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "dpibypass-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // Housekeeping only.
        }
    }

    /// <summary>Convenience adapter for the components that take an <c>Action&lt;string&gt;</c> logger.</summary>
    public static Action<string> InfoSink { get; } = Info;
}
