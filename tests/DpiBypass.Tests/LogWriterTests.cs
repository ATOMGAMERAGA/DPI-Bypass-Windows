using System.Diagnostics;
using DpiBypass.Core.Logging;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// What the file writer promises: producers never wait on the disk, the queue is
/// bounded, nothing worth keeping is silently lost, and the day's file stays openable.
/// </summary>
public sealed class LogWriterTests
{
    private static LogEntry Entry(string message, LogLevel level = LogLevel.Info)
        => new(DateTimeOffset.Now, level, message);

    [Fact]
    public void EveryQueuedEntryReachesTheDaysFile()
    {
        using var directory = new TempDirectory();
        using var writer = new LogFileWriter(directory.Path, flushInterval: TimeSpan.Zero);

        for (var i = 0; i < 500; i++)
        {
            writer.Enqueue(Entry($"satır {i}"));
        }

        writer.Flush();

        var text = ReadAll(directory.Path);
        Assert.Contains("satır 0", text);
        Assert.Contains("satır 499", text);
        Assert.Equal(500, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(0, writer.Dropped);
    }

    /// <summary>
    /// The whole point: logging costs the calling thread an enqueue, whatever the disk
    /// is doing.
    /// </summary>
    /// <remarks>
    /// Measured against a wall clock rather than against the old implementation, because
    /// the old one is gone. Ten thousand entries through a synchronous open-write-close
    /// per line was seconds; the bar here is deliberately loose - it is checking that the
    /// producer is not waiting on the writer at all, not micro-benchmarking an enqueue.
    /// </remarks>
    [Fact]
    public void LoggingDoesNotBlockTheThreadThatLogged()
    {
        using var directory = new TempDirectory();
        using var writer = new LogFileWriter(directory.Path, flushInterval: TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            writer.Enqueue(Entry($"yoğun kayıt {i}"));
        }

        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"enqueueing 10.000 entries took {stopwatch.ElapsedMilliseconds} ms, which means the producer waited on the disk");
    }

    /// <summary>
    /// A writer that cannot keep up costs a bounded amount of memory and an honest count.
    /// </summary>
    [Fact]
    public void TheQueueIsBoundedAndSaysWhatItDropped()
    {
        using var directory = new TempDirectory();

        // A long flush interval keeps the writer parked so the queue really does fill.
        using var writer = new LogFileWriter(
            directory.Path,
            flushInterval: TimeSpan.FromMinutes(5),
            queueCapacity: 100);

        for (var i = 0; i < 1_000; i++)
        {
            writer.Enqueue(Entry($"taşma {i}"));
        }

        Assert.True(writer.Pending <= 100, $"queue held {writer.Pending} entries against a cap of 100");
        Assert.Equal(900, writer.Dropped);

        writer.Flush();

        // What survived is the most recent, which is what somebody reading a flooded log
        // is looking for.
        var text = ReadAll(directory.Path);
        Assert.Contains("taşma 999", text);
        Assert.DoesNotContain("taşma 0 ", text);
    }

    /// <summary>One noisy day continues into numbered parts rather than one huge file.</summary>
    [Fact]
    public void ADayThatOutgrowsTheSizeLimitRollsIntoNumberedParts()
    {
        using var directory = new TempDirectory();
        using var writer = new LogFileWriter(
            directory.Path,
            flushInterval: TimeSpan.Zero,
            batchSize: 1,
            maxFileBytes: 2048);

        for (var i = 0; i < 200; i++)
        {
            writer.Enqueue(Entry(new string('x', 200)));
        }

        writer.Flush();

        var files = Directory.GetFiles(directory.Path, "dpibypass-*.log").Select(Path.GetFileName).Order().ToArray();

        Assert.True(files.Length > 1, "the day never rolled");
        Assert.True(files.Length <= LogFileWriter.MaxPartsPerDay, $"rolled into {files.Length} parts");
        Assert.Contains(files, f => f!.Contains(".1.log", StringComparison.Ordinal));
    }

    /// <summary>Files older than the retention window are removed when the writer starts.</summary>
    [Fact]
    public void FilesOlderThanTheRetentionWindowAreRemoved()
    {
        using var directory = new TempDirectory();
        var stale = Path.Combine(directory.Path, LogFileWriter.FileName(DateOnly.FromDateTime(DateTime.Now.AddDays(-40)), 0));
        var recent = Path.Combine(directory.Path, LogFileWriter.FileName(DateOnly.FromDateTime(DateTime.Now.AddDays(-1)), 0));

        File.WriteAllText(stale, "eski\n");
        File.WriteAllText(recent, "yeni\n");
        File.SetLastWriteTime(stale, DateTime.Now.AddDays(-40));
        File.SetLastWriteTime(recent, DateTime.Now.AddDays(-1));

        using var writer = new LogFileWriter(directory.Path, flushInterval: TimeSpan.Zero);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
    }

    /// <summary>Disposing flushes: the closing lines are the ones a shutdown is read for.</summary>
    [Fact]
    public void DisposingWritesWhateverWasStillQueued()
    {
        using var directory = new TempDirectory();
        var writer = new LogFileWriter(directory.Path, flushInterval: TimeSpan.FromMinutes(5));

        writer.Enqueue(Entry("kapatma nedeni", LogLevel.Error));
        writer.Dispose();

        Assert.Contains("kapatma nedeni", ReadAll(directory.Path));
    }

    /// <summary>Entries written from many threads all arrive, none of them torn.</summary>
    [Fact]
    public async Task ConcurrentProducersAllReachTheFileIntact()
    {
        using var directory = new TempDirectory();
        using var writer = new LogFileWriter(directory.Path, flushInterval: TimeSpan.FromMilliseconds(10));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                writer.Enqueue(Entry($"t{thread}-{i}"));
            }
        })));

        writer.Flush();
        var lines = ReadAll(directory.Path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(1600, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", line));
    }

    private static string ReadAll(string directory)
        => string.Concat(Directory.GetFiles(directory, "dpibypass-*.log").Order().Select(File.ReadAllText));
}

/// <summary>
/// One log subscriber going wrong must not take the others with it.
/// </summary>
public sealed class AppLogSubscriberTests
{
    [Fact]
    public void AThrowingSubscriberDoesNotSilenceTheOnesRegisteredAfterIt()
    {
        var reached = new List<string>();

        void Angry(LogEntry _) => throw new InvalidOperationException("dispatcher is shutting down");
        void Calm(LogEntry entry) => reached.Add(entry.Message);

        AppLog.Written += Angry;
        AppLog.Written += Calm;

        try
        {
            AppLog.Info("herkese ulaşmalı");
        }
        finally
        {
            AppLog.Written -= Angry;
            AppLog.Written -= Calm;
        }

        Assert.Contains("herkese ulaşmalı", reached);
    }

    [Fact]
    public void ASubscriberThatThrowsNeverReachesTheCaller()
    {
        void Angry(LogEntry _) => throw new InvalidOperationException("boom");

        AppLog.Written += Angry;
        try
        {
            AppLog.Warning("kayıt çağrısı hiçbir zaman fırlatmamalı");
        }
        finally
        {
            AppLog.Written -= Angry;
        }
    }
}
