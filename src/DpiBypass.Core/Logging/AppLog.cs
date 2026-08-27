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
/// <remarks>
/// <para>
/// Writing to the file is done by one background thread and never by the caller.
/// The obvious implementation - open, append, flush, close, once per line, under a
/// process wide lock - is what the app used to do, and it is far more expensive on
/// Windows than it looks: every one of those opens is a file the real time scanner
/// inspects, so a line costs milliseconds rather than microseconds. The engine logs
/// in bursts (opening the driver, measuring a strategy, rewriting DNS), and those
/// bursts came out of the UI thread's budget during start-up and out of the packet
/// workers' budget while running. Handing the lines to a writer that keeps the file
/// open takes the cost off every caller and leaves the ordering unchanged.
/// </para>
/// <para>
/// The queue is bounded. A logger that runs away - a worker faulting in a loop -
/// must cost lines, never memory, so the oldest pending line is dropped rather than
/// letting the queue grow without limit.
/// </para>
/// </remarks>
public static class AppLog
{
    private const int BufferCapacity = 500;

    /// <summary>
    /// Lines allowed to be waiting for the writer. Roughly a second of a very loud
    /// burst; beyond this the app is logging faster than any disk will take it.
    /// </summary>
    internal const int PendingCapacity = 4096;

    private static readonly ConcurrentQueue<LogEntry> Buffer = new();
    private static readonly ConcurrentQueue<LogEntry> Pending = new();

    /// <summary>Signals the writer that <see cref="Pending"/> has something in it.</summary>
    private static readonly SemaphoreSlim PendingSignal = new(0);

    private static readonly Lock WriterGate = new();

    /// <summary>Held while the pending queue is being written out.</summary>
    private static readonly Lock DrainGate = new();

    private static Thread? _writer;
    private static string? _logDirectory;
    private static StreamWriter? _file;
    private static DateOnly _fileDate;
    private static volatile bool _stopping;
    private static bool _exitHooked;
    private static long _dropped;

    public static event Action<LogEntry>? Written;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static IReadOnlyList<LogEntry> Snapshot() => [.. Buffer];

    /// <summary>Lines discarded because the writer could not keep up.</summary>
    public static long DroppedLines => Interlocked.Read(ref _dropped);

    /// <summary>Lines handed to the writer that are not on disk yet.</summary>
    public static int PendingLines => Pending.Count;

    /// <param name="logDirectory">
    /// Where to write, or null for the product's own folder. Only the tests pass one.
    /// </param>
    public static void Initialise(string? logDirectory = null)
    {
        try
        {
            if (logDirectory is null)
            {
                AppPaths.EnsureCreated();
                _logDirectory = AppPaths.LogDirectory;
            }
            else
            {
                Directory.CreateDirectory(logDirectory);
                _logDirectory = logDirectory;
            }

            // Housekeeping belongs to the writer thread, which does it when it opens
            // the day's file. Enumerating the folder here would put a directory scan in
            // front of the first line of the log, on the thread building the window.
            StartWriter();
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

        Enqueue(entry);
    }

    /// <summary>
    /// Hands a line to the background writer. Never blocks and never throws.
    /// </summary>
    private static void Enqueue(LogEntry entry)
    {
        if (_logDirectory is null)
        {
            return;
        }

        Pending.Enqueue(entry);

        // Trim from the front: the newest lines are the ones that explain what is
        // happening now, and a burst this size means the disk is the problem.
        while (Pending.Count > PendingCapacity && Pending.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropped);
        }

        if (_stopping)
        {
            // The writer has already stopped, which on the way out is exactly when the
            // most useful lines are written: putting the DNS back, closing the driver,
            // why the app is going. Paying for them on this thread is slow and correct;
            // dropping them leaves a log that stops mid-sentence.
            DrainPending();

            // And closed again, because there is no longer anybody to close it later.
            CloseFile();
            return;
        }

        try
        {
            PendingSignal.Release();
        }
        catch (Exception)
        {
            // Disposed; the branch above is what catches the tail from here on.
        }
    }

    private static void StartWriter()
    {
        lock (WriterGate)
        {
            if (_writer is not null)
            {
                return;
            }

            _stopping = false;

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "DpiBypass.Log",
                // Below normal on purpose: a log line must never be scheduled ahead of
                // a packet worker or the UI thread.
                Priority = ThreadPriority.BelowNormal,
            };

            _writer.Start();

            if (!_exitHooked)
            {
                _exitHooked = true;

                // A crash still gets the tail of the log, which is the part that
                // explains it.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            }
        }
    }

    private static void WriterLoop()
    {
        while (true)
        {
            try
            {
                // Woken per line, but each wake drains everything queued, so a burst
                // costs one open file and one flush rather than one of each per line.
                // The timeout is what guarantees shutdown makes progress: the signal
                // has one permit per line, and a drain that swallowed several lines at
                // once leaves the thread parked with none left to take.
                PendingSignal.Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                return;
            }

            DrainPending();

            // The signal carries one permit per line, and the drain above took every
            // line at once. Collapsing the surplus keeps a burst of ten thousand lines
            // from costing ten thousand wake-ups that find nothing to do.
            while (Pending.IsEmpty && PendingSignal.CurrentCount > 0 && PendingSignal.Wait(0))
            {
                // Discarding a permit whose line has already been written.
            }

            if (_stopping && Pending.IsEmpty)
            {
                return;
            }
        }
    }

    private static void DrainPending()
    {
        // Normally the writer thread is the only caller. After shutdown any thread
        // that logs drains its own line, so this has to tolerate company.
        lock (DrainGate)
        {
            DrainPendingCore();
        }
    }

    private static void DrainPendingCore()
    {
        var directory = _logDirectory;
        if (directory is null)
        {
            while (Pending.TryDequeue(out _))
            {
                // Nowhere to put them.
            }

            return;
        }

        var wrote = false;

        while (Pending.TryDequeue(out var entry))
        {
            try
            {
                var file = ResolveFile(directory, entry.Timestamp.LocalDateTime);
                if (file is null)
                {
                    continue;
                }

                file.Write(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
                file.Write(" [");
                file.Write(entry.Level.ToString());
                file.Write("] ");
                file.WriteLine(entry.Message);
                wrote = true;
            }
            catch (Exception)
            {
                // A disk that will not take the line is not worth a second attempt,
                // and must never be able to end the writer thread.
                CloseFile();
            }
        }

        if (!wrote)
        {
            return;
        }

        try
        {
            _file?.Flush();
        }
        catch (Exception)
        {
            CloseFile();
        }
    }

    /// <summary>
    /// The open writer for the day an entry belongs to. Writer thread only.
    /// </summary>
    /// <remarks>
    /// Resolved per line rather than once at startup: an instance the logon task
    /// started can stay up for weeks, and everything it logged would otherwise pile
    /// into the file named after the day it happened to boot on. The handle is kept
    /// open across lines, so the check is a date comparison rather than a file open.
    /// </remarks>
    private static StreamWriter? ResolveFile(string directory, DateTime stamp)
    {
        var day = DateOnly.FromDateTime(stamp);

        if (_file is not null && day == _fileDate)
        {
            return _file;
        }

        CloseFile();

        var path = Path.Combine(directory, $"dpibypass-{day:yyyy-MM-dd}.log");

        try
        {
            // Shared read/write so a second copy of the app, or the user opening the
            // file in Notepad, does not stop this one logging.
            var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                FileOptions.SequentialScan);

            _file = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };

            _fileDate = day;

            // A process the logon task started can run for weeks, so the day it rolls
            // over is the only moment it would ever notice the folder filling up.
            PruneOldFiles();

            return _file;
        }
        catch (Exception)
        {
            _file = null;
            return null;
        }
    }

    private static void CloseFile()
    {
        // Exchanged rather than assigned: shutdown drains from the caller's thread
        // when the writer took too long to stop, and disposing the same handle twice
        // from two threads is the one way this could throw on the way out.
        var file = Interlocked.Exchange(ref _file, null);

        try
        {
            file?.Flush();
            file?.Dispose();
        }
        catch (Exception)
        {
            // Going away regardless.
        }
    }

    /// <summary>
    /// Writes out whatever is still queued and closes the file. Safe to call twice.
    /// </summary>
    public static void Shutdown()
    {
        Thread? writer;

        lock (WriterGate)
        {
            _stopping = true;
            writer = _writer;
            _writer = null;
        }

        try
        {
            PendingSignal.Release();
        }
        catch (Exception)
        {
            // Already disposed.
        }

        try
        {
            // Short: the tail of the log is worth a moment, never a hung exit.
            writer?.Join(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Nothing left to do on the way down.
        }

        // The writer may have given up before the queue was empty.
        DrainPending();
        CloseFile();
    }

    private static void PruneOldFiles()
    {
        try
        {
            var directory = _logDirectory;
            if (directory is null)
            {
                return;
            }

            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(directory, "dpibypass-*.log"))
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

    /// <summary>
    /// Puts the logger back to the state it was in before <see cref="Initialise"/>.
    /// </summary>
    /// <remarks>
    /// Only the tests need this. The logger is process-wide static state by design -
    /// every component in the app logs through it without being handed one - and a
    /// test that starts a writer has to be able to stop owning the folder it wrote to.
    /// </remarks>
    internal static void ResetForTesting()
    {
        Shutdown();

        _logDirectory = null;
        _fileDate = default;
        Interlocked.Exchange(ref _dropped, 0);

        while (Pending.TryDequeue(out _))
        {
            // Anything still queued belonged to the test that just finished.
        }

        while (PendingSignal.CurrentCount > 0 && PendingSignal.Wait(0))
        {
            // Drain the permits too, or the next writer wakes up to an empty queue
            // once per line the previous one never got to.
        }
    }

    /// <summary>Convenience adapter for the components that take an <c>Action&lt;string&gt;</c> logger.</summary>
    public static Action<string> InfoSink { get; } = Info;
}
