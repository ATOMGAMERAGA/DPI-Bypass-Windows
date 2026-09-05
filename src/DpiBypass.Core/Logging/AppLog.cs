using System.Collections.Concurrent;

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
/// <remarks>
/// Logging costs the calling thread an enqueue and nothing else. The file half is a
/// <see cref="LogFileWriter"/> on its own thread; it used to be a synchronous append
/// under a shared lock on every entry, which put whatever the disk was doing directly in
/// front of the packet workers.
/// </remarks>
public static class AppLog
{
    private const int BufferCapacity = 500;

    private static readonly ConcurrentQueue<LogEntry> Buffer = new();
    private static readonly Lock WriterGate = new();

    private static LogFileWriter? _file;

    public static event Action<LogEntry>? Written;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Entries the file writer could not keep up with, for the diagnostics report.</summary>
    public static long DroppedFileEntries => _file?.Dropped ?? 0;

    /// <summary>The log file being written to now, or null when logging is memory only.</summary>
    public static string? CurrentFile => _file?.CurrentFile;

    public static IReadOnlyList<LogEntry> Snapshot() => [.. Buffer];

    public static void Initialise()
    {
        try
        {
            AppPaths.EnsureCreated();

            LogFileWriter writer;
            lock (WriterGate)
            {
                if (_file is not null)
                {
                    return;
                }

                writer = new LogFileWriter(AppPaths.LogDirectory);
                _file = writer;
            }

            // Everything logged before the directory was known is in memory only, and on
            // a launch that fails those lines are the whole explanation. They go to the
            // file as soon as there is a file to put them in.
            foreach (var early in Buffer)
            {
                writer.Enqueue(early);
            }
        }
        catch (Exception)
        {
            _file = null; // memory-only logging is still better than crashing
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warning(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    // Exception.Message throws away the inner exception, which is commonly the only
    // useful part of WPF and reflection failures (for example, the missing resource
    // wrapped by a XamlParseException). Keep the full exception chain and stack in the
    // persistent log so a startup failure can be diagnosed from the file the dialog
    // points at instead of reproducing it on the developer's machine first.
    public static void Error(string message, Exception exception) => Write(LogLevel.Error, $"{message}:{Environment.NewLine}{exception}");

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

        Publish(entry);
        _file?.Enqueue(entry);
    }

    /// <summary>
    /// Hands the entry to every subscriber, keeping one bad subscriber to itself.
    /// </summary>
    /// <remarks>
    /// One try/catch around the whole multicast invocation meant the first subscriber to
    /// throw silenced every subscriber registered after it - the UI's dispatcher throwing
    /// once during shutdown could take a diagnostics sink down with it. Each subscriber is
    /// now called on its own.
    /// </remarks>
    private static void Publish(LogEntry entry)
    {
        var subscribers = Written;
        if (subscribers is null)
        {
            return;
        }

        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<LogEntry>)subscriber)(entry);
            }
            catch (Exception)
            {
                // This runs on whichever thread logged - a WinDivert worker, the TTL fix,
                // the IPC accept loop - and the UI subscriber marshals through a Dispatcher
                // that throws once shutdown has begun. An unhandled exception there would
                // take the process down, so a log call must never be able to hurt its
                // caller. Reporting it would be a log call from inside a log call.
            }
        }
    }

    /// <summary>
    /// Writes everything queued and stops the file writer.
    /// </summary>
    /// <remarks>
    /// Called on the way out so the last lines - the ones describing why the app is
    /// closing - reach the disk. It cannot cover a process that is killed outright:
    /// entries written in the moments before a hard termination are lost, and the log has
    /// no way to promise otherwise.
    /// </remarks>
    public static void Shutdown()
    {
        LogFileWriter? writer;
        lock (WriterGate)
        {
            writer = _file;
            _file = null;
        }

        writer?.Dispose();
    }

    /// <summary>Convenience adapter for the components that take an <c>Action&lt;string&gt;</c> logger.</summary>
    public static Action<string> InfoSink { get; } = Info;
}
