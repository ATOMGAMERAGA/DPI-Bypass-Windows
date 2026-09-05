using System.Collections.Concurrent;
using System.Text;

namespace DpiBypass.Core.Logging;

/// <summary>
/// Writes log entries to a daily file, on its own thread, in batches.
/// </summary>
/// <remarks>
/// <para>
/// The engine logs from the packet workers, the tuner, the DNS proxy and the IPC loop,
/// and it logs in bursts. Every one of those entries used to be a synchronous
/// <c>File.AppendAllText</c> under a shared lock - opening, writing and closing a file
/// per line, with every other thread that wanted to log waiting behind it. On a machine
/// whose antivirus inspects the log directory that back-pressure reached the packet path.
/// </para>
/// <para>
/// Producers now enqueue and return. One background task takes the burst behind each
/// wake-up and writes it as a single append. The queue is bounded, so a disk that has
/// stopped answering costs a fixed amount of memory and a count of what was lost rather
/// than growing until the process dies.
/// </para>
/// </remarks>
public sealed class LogFileWriter : IDisposable
{
    /// <summary>
    /// How many entries may wait to be written before the oldest are dropped.
    /// </summary>
    /// <remarks>
    /// Sized so that dropping means the disk has genuinely stopped answering, not that a
    /// burst outran the writer for a moment: at roughly 120 bytes an entry this is about
    /// six megabytes at its worst, against a packet path that logs a few lines a second
    /// and a strategy sweep that logs a few hundred in total. A tighter bound saved
    /// nothing worth having and turned an ordinary burst into lost evidence.
    /// </remarks>
    public const int DefaultQueueCapacity = 50_000;

    /// <summary>How many entries one append covers.</summary>
    public const int DefaultBatchSize = 256;

    /// <summary>
    /// How full the queue may get before the writer stops gathering and writes.
    /// </summary>
    /// <remarks>
    /// Without this the writer slept for the whole flush interval before its first drain,
    /// so a burst was measured against the queue's capacity rather than against the disk:
    /// twenty thousand entries in fifteen milliseconds filled a ten thousand entry queue
    /// and half of them were dropped by a writer that was not busy at all, only waiting.
    /// The gather now ends as soon as there is clearly enough to write.
    /// </remarks>
    private const int DrainThresholdBatches = 4;

    /// <summary>
    /// How large one day's file may grow before it continues in a numbered part.
    /// </summary>
    /// <remarks>
    /// With the retention window below, this is what bounds the directory rather than
    /// only the file: a per file limit with an unbounded file count adds up to an
    /// unbounded directory, and so does a bounded count of unbounded files.
    /// </remarks>
    public const long DefaultMaxFileBytes = 4L * 1024 * 1024;

    /// <summary>How many parts one day may roll into before it stops rolling.</summary>
    public const int MaxPartsPerDay = 4;

    private readonly string _directory;
    private readonly int _queueCapacity;
    private readonly int _batchSize;
    private readonly long _maxFileBytes;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _retention;

    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _fileGate = new();

    /// <summary>
    /// Held for the whole of a drain, so only one runs at a time.
    /// </summary>
    /// <remarks>
    /// Two drains - the background loop and a caller's Flush - each build their own batch
    /// from the same queue, so without this a Flush could return having written its own
    /// share while entries the background drain had already dequeued were still in its
    /// StringBuilder. From the caller's side that is an entry it logged, was told had been
    /// flushed, and which is not in the file.
    /// </remarks>
    private readonly Lock _drainGate = new();
    private readonly Task _writer;

    private string? _filePath;
    private DateOnly _fileDate;
    private int _filePart;
    private long _fileBytes;
    private int _pendingCount;

    /// <summary>
    /// Whether the writer has already been told there is work.
    /// </summary>
    /// <remarks>
    /// One wake-up per drain cycle rather than one per entry. Releasing a semaphore takes
    /// its internal lock, so signalling on every entry put eight logging threads in a
    /// queue behind each other for a writer that was going to take the whole burst in one
    /// pass anyway - the contention the batching exists to avoid, moved from the file to
    /// the semaphore.
    /// </remarks>
    private int _signalled;
    private long _dropped;
    private bool _disposed;

    public LogFileWriter(
        string directory,
        TimeSpan? flushInterval = null,
        int queueCapacity = DefaultQueueCapacity,
        int batchSize = DefaultBatchSize,
        long maxFileBytes = DefaultMaxFileBytes,
        TimeSpan? retention = null)
    {
        _directory = directory;
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(250);
        _queueCapacity = Math.Max(1, queueCapacity);
        _batchSize = Math.Max(1, batchSize);
        _maxFileBytes = Math.Max(1024, maxFileBytes);
        _retention = retention ?? TimeSpan.FromDays(14);

        Directory.CreateDirectory(_directory);
        PruneOldFiles();

        _writer = Task.Factory.StartNew(
            () => WriteLoopAsync(_stopping.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>Entries dropped because the queue was full or the disk refused them.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Entries queued but not yet written.</summary>
    public int Pending => Volatile.Read(ref _pendingCount);

    /// <summary>Queues an entry. Never blocks, never throws.</summary>
    public void Enqueue(LogEntry entry)
    {
        if (_disposed)
        {
            return;
        }

        _pending.Enqueue(entry);

        if (Interlocked.Increment(ref _pendingCount) > _queueCapacity && _pending.TryDequeue(out _))
        {
            // The oldest goes rather than the newest: a writer that has fallen this far
            // behind is being read for what happened most recently.
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _dropped);
        }

        Signal();
    }

    /// <summary>Wakes the writer, unless it has already been woken and not yet answered.</summary>
    private void Signal()
    {
        if (Interlocked.Exchange(ref _signalled, 1) != 0)
        {
            return;
        }

        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Writes everything queued right now and returns once it is on disk.
    /// </summary>
    /// <remarks>
    /// For the shutdown path and for the tests. It writes on the calling thread rather
    /// than waiting for the background one, so it cannot be left waiting on a writer that
    /// has already been told to stop.
    /// </remarks>
    public void Flush() => Drain();

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            // Cleared before the gather, so an entry arriving during this cycle raises a
            // fresh wake-up rather than being left in the queue with nobody coming back.
            Volatile.Write(ref _signalled, 0);

            // One entry woke us; wait a moment for the rest of its burst so a start-up
            // sweep's few hundred lines cost a handful of appends rather than hundreds -
            // but never at the cost of the queue overflowing while the writer waits.
            await GatherAsync(cancellationToken).ConfigureAwait(false);

            Drain();

            if (!_pending.IsEmpty)
            {
                Signal();
            }
        }

        Drain();
    }

    /// <summary>
    /// Waits briefly for the rest of a burst, cut short once there is plenty to write.
    /// </summary>
    /// <remarks>
    /// Batching is what turns a start-up sweep's few hundred lines into a handful of
    /// appends, and it only needs a moment. Sitting out the whole interval regardless is
    /// what let a fast producer overflow the queue against a disk that was keeping up.
    /// </remarks>
    private async Task GatherAsync(CancellationToken cancellationToken)
    {
        if (_flushInterval <= TimeSpan.Zero)
        {
            return;
        }

        var threshold = _batchSize * DrainThresholdBatches;
        // Short slices on purpose: the gather has to notice a queue filling within a
        // millisecond or two, and this only runs while there is already work waiting.
        var slice = TimeSpan.FromMilliseconds(Math.Clamp(_flushInterval.TotalMilliseconds / 8, 1, 5));
        var waited = TimeSpan.Zero;

        try
        {
            while (waited < _flushInterval && Volatile.Read(ref _pendingCount) < threshold)
            {
                await Task.Delay(slice, cancellationToken).ConfigureAwait(false);
                waited += slice;
            }
        }
        catch (OperationCanceledException)
        {
            // Still drain: a writer that discards its last batch on cancellation loses
            // exactly the lines a shutdown or a crash needs.
        }
    }

    private void Drain()
    {
        lock (_drainGate)
        {
            DrainCore();
        }
    }

    private void DrainCore()
    {
        var batch = new StringBuilder();
        var count = 0;
        var day = default(DateTime);

        while (_pending.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _pendingCount);
            var stamp = entry.Timestamp.LocalDateTime;

            // A batch belongs to one file, so a burst spanning midnight is split rather
            // than filed under whichever day happened to start it.
            if (count > 0 && stamp.Date != day.Date)
            {
                Append(day, batch, count);
                batch.Clear();
                count = 0;
            }

            if (count == 0)
            {
                day = stamp;
            }

            batch.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [")
                .Append(entry.Level)
                .Append("] ")
                .Append(entry.Message)
                .Append(Environment.NewLine);

            if (++count >= _batchSize)
            {
                Append(day, batch, count);
                batch.Clear();
                count = 0;
            }
        }

        if (count > 0)
        {
            Append(day, batch, count);
        }
    }

    private void Append(DateTime stamp, StringBuilder batch, int entries)
    {
        var text = batch.ToString();

        try
        {
            lock (_fileGate)
            {
                File.AppendAllText(ResolveFile(stamp, Encoding.UTF8.GetByteCount(text)), text, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // A disk problem must never take the engine down, and it must never be
            // reported through the log: that would be a write from inside the writer,
            // which on a failing disk is a loop with no way out. The count is what the
            // diagnostics report shows instead.
            Interlocked.Add(ref _dropped, entries);
        }
        catch (UnauthorizedAccessException)
        {
            Interlocked.Add(ref _dropped, entries);
        }
    }

    /// <summary>
    /// The file an entry belongs in. Caller must hold <see cref="_fileGate"/>.
    /// </summary>
    /// <remarks>
    /// Resolved per write rather than once at startup: an instance the logon task started
    /// can stay up for weeks, and everything it logged would otherwise pile into the file
    /// named after the day it happened to boot on.
    /// </remarks>
    private string ResolveFile(DateTime stamp, int incomingBytes)
    {
        var day = DateOnly.FromDateTime(stamp);

        if (_filePath is null || day != _fileDate)
        {
            _fileDate = day;
            _filePart = 0;
            _filePath = Path.Combine(_directory, FileName(day, 0));
            _fileBytes = LengthOf(_filePath);
            PruneOldFiles();
        }
        else if (_fileBytes + incomingBytes > _maxFileBytes && _filePart < MaxPartsPerDay - 1)
        {
            _filePart++;
            _filePath = Path.Combine(_directory, FileName(day, _filePart));
            _fileBytes = LengthOf(_filePath);
        }

        _fileBytes += incomingBytes;
        return _filePath;
    }

    internal static string FileName(DateOnly day, int part)
        => part == 0 ? $"dpibypass-{day:yyyy-MM-dd}.log" : $"dpibypass-{day:yyyy-MM-dd}.{part}.log";

    private static long LengthOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now - _retention;
            foreach (var file in Directory.EnumerateFiles(_directory, "dpibypass-*.log"))
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _stopping.Cancel();
            _writer.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // The final drain below covers whatever the writer did not reach.
        }

        Drain();
        _stopping.Dispose();
        _signal.Dispose();
    }
}
