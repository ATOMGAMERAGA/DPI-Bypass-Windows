namespace DpiBypass.Core.Engine;

/// <summary>Live counters for the status page. All updates are interlocked.</summary>
public sealed class EngineStatistics
{
    private long _inspected;
    private long _rewritten;
    private long _segments;
    private long _decoys;
    private long _passedThrough;
    private long _quicBlocked;
    private long _errors;

    public long Inspected => Interlocked.Read(ref _inspected);

    public long Rewritten => Interlocked.Read(ref _rewritten);

    public long SegmentsSent => Interlocked.Read(ref _segments);

    public long DecoysSent => Interlocked.Read(ref _decoys);

    public long PassedThrough => Interlocked.Read(ref _passedThrough);

    public long QuicHandshakesBlocked => Interlocked.Read(ref _quicBlocked);

    public long Errors => Interlocked.Read(ref _errors);

    public string? LastHost { get; internal set; }

    public DateTimeOffset? StartedAt { get; internal set; }

    public TimeSpan Uptime => StartedAt is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - StartedAt.Value;

    internal void AddInspected() => Interlocked.Increment(ref _inspected);

    internal void AddRewritten() => Interlocked.Increment(ref _rewritten);

    internal void AddSegments(int count) => Interlocked.Add(ref _segments, count);

    internal void AddDecoys(int count) => Interlocked.Add(ref _decoys, count);

    internal void AddPassedThrough() => Interlocked.Increment(ref _passedThrough);

    internal void AddQuicBlocked() => Interlocked.Increment(ref _quicBlocked);

    internal void AddError() => Interlocked.Increment(ref _errors);

    public void Reset()
    {
        Interlocked.Exchange(ref _inspected, 0);
        Interlocked.Exchange(ref _rewritten, 0);
        Interlocked.Exchange(ref _segments, 0);
        Interlocked.Exchange(ref _decoys, 0);
        Interlocked.Exchange(ref _passedThrough, 0);
        Interlocked.Exchange(ref _quicBlocked, 0);
        Interlocked.Exchange(ref _errors, 0);
        LastHost = null;
    }
}
