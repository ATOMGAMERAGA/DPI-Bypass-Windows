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
    private static string? _logDirectory;
    private static string? _filePath;
    private static DateOnly _fileDate;

    public static event Action<LogEntry>? Written;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static IReadOnlyList<LogEntry> Snapshot() => [.. Buffer];

    public static void Initialise()
    {
        try
        {
            AppPaths.EnsureCreated();

            lock (FileGate)
            {
                _logDirectory = AppPaths.LogDirectory;
                ResolveFile(_logDirectory, DateTime.Now);
            }
        }
        catch (Exception)
        {
            _logDirectory = null; // memory-only logging is still better than crashing
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

        try
        {
            Written?.Invoke(entry);
        }
        catch (Exception)
        {
            // This runs on whichever thread logged - a WinDivert worker, the TTL fix,
            // the IPC accept loop - and the UI subscriber marshals through a Dispatcher
            // that throws once shutdown has begun. An unhandled exception there would
            // take the process down, so a log call must never be able to hurt its caller.
        }

        AppendToFile(entry);
    }

    private static void AppendToFile(LogEntry entry)
    {
        var directory = _logDirectory;
        if (directory is null)
        {
            return;
        }

        try
        {
            lock (FileGate)
            {
                File.AppendAllText(
                    ResolveFile(directory, entry.Timestamp.LocalDateTime),
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

    /// <summary>
    /// The file for the day an entry belongs to. Caller must hold <see cref="FileGate"/>.
    /// </summary>
    /// <remarks>
    /// Resolved per write rather than once at startup: an instance the logon task
    /// started can stay up for weeks, and everything it logged would otherwise pile
    /// into the file named after the day it happened to boot on.
    /// </remarks>
    private static string ResolveFile(string directory, DateTime stamp)
    {
        var day = DateOnly.FromDateTime(stamp);
        var path = _filePath;

        if (path is null || day != _fileDate)
        {
            path = Path.Combine(directory, $"dpibypass-{day:yyyy-MM-dd}.log");
            _filePath = path;
            _fileDate = day;
            PruneOldFiles();
        }

        return path;
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
