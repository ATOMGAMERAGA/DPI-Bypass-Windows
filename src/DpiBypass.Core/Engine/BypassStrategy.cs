namespace DpiBypass.Core.Engine;

/// <summary>How the first data segment of a connection is cut up.</summary>
public enum SplitMode
{
    /// <summary>Leave the segment alone.</summary>
    None = 0,

    /// <summary>Send the first half, then the second half.</summary>
    Split = 1,

    /// <summary>Send the second half first. Reassemblers that ignore sequence order choke on it.</summary>
    Disorder = 2,
}

/// <summary>Where the cut lands.</summary>
public enum SplitAnchor
{
    /// <summary>A fixed byte offset from the start of the payload.</summary>
    Absolute = 0,

    /// <summary>The middle of the SNI hostname (or the Host header value for plaintext HTTP).</summary>
    HostMiddle = 1,

    /// <summary>Immediately after the hostname.</summary>
    HostEnd = 2,

    /// <summary>Immediately before the hostname.</summary>
    HostStart = 3,
}

/// <summary>How a decoy packet is made unacceptable to the real server.</summary>
public enum FakeMode
{
    None = 0,

    /// <summary>Low TTL, so it dies in the operator network after the inspector has seen it.</summary>
    ExpiredTtl = 1,

    /// <summary>Sequence number far outside the window, so the server drops it silently.</summary>
    BadSequence = 2,

    /// <summary>Deliberately wrong TCP checksum.</summary>
    BadChecksum = 3,
}

/// <summary>
/// Plaintext HTTP header mangling. Ignored for TLS.
/// </summary>
/// <remarks>
/// Every trick here rewrites bytes in place. None of them may change the length of
/// the request: the engine sees outbound packets only, so the local TCP stack keeps
/// the byte count it already committed to, and a longer or shorter payload would make
/// the peer acknowledge data that stack never sent.
/// </remarks>
[Flags]
public enum HttpTricks
{
    None = 0,

    /// <summary>"Host:" becomes "hOSt:".</summary>
    HostCase = 1,

    /// <summary>
    /// The space after "Host:" becomes a tab. HTTP allows either as optional
    /// whitespace, so the server reads the same request, while a filter matching the
    /// literal "Host: " misses the header.
    /// </summary>
    HostTab = 2,
}

/// <summary>
/// One complete recipe for getting a first data packet past an inspector.
/// Strategies are data, not code, so the auto-tuner can walk the whole list.
/// </summary>
public sealed record BypassStrategy
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public SplitMode Split { get; init; } = SplitMode.None;

    public SplitAnchor Anchor { get; init; } = SplitAnchor.Absolute;

    /// <summary>Offset for <see cref="SplitAnchor.Absolute"/>, or a nudge applied to a host anchored cut.</summary>
    public int SplitPosition { get; init; } = 2;

    /// <summary>Optional second cut, producing three segments. Zero disables it.</summary>
    public int SecondSplitPosition { get; init; }

    public FakeMode Fake { get; init; } = FakeMode.None;

    /// <summary>TTL used by <see cref="FakeMode.ExpiredTtl"/>.</summary>
    public byte FakeTtl { get; init; } = 4;

    /// <summary>How many decoys to emit before the real data.</summary>
    public int FakeCount { get; init; } = 1;

    /// <summary>Send a single urgent byte ahead of the payload.</summary>
    public bool OutOfBand { get; init; }

    public HttpTricks Http { get; init; } = HttpTricks.None;

    /// <summary>Nothing to do - used as the control arm when measuring whether a network blocks at all.</summary>
    public bool IsPassthrough =>
        Split == SplitMode.None
        && Fake == FakeMode.None
        && !OutOfBand
        && Http == HttpTricks.None;

    public override string ToString() => Id;
}
