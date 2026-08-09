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

/// <summary>Where a ClientHello is cut into several TLS records.</summary>
public enum TlsRecordSplit
{
    /// <summary>Leave the record layer alone.</summary>
    None = 0,

    /// <summary>Cut in the middle of the SNI hostname.</summary>
    HostMiddle = 1,

    /// <summary>Cut immediately before the SNI hostname.</summary>
    HostStart = 2,

    /// <summary>Cut at a fixed offset into the handshake body.</summary>
    Absolute = 3,
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

/// <summary>Plaintext HTTP header mangling. Ignored for TLS.</summary>
[Flags]
public enum HttpTricks
{
    None = 0,

    /// <summary>"Host:" becomes "hOSt:".</summary>
    HostCase = 1,

    /// <summary>An extra space between the colon and the value.</summary>
    ExtraSpace = 2,

    /// <summary>A trailing dot on the hostname, which DNS accepts and naive filters do not.</summary>
    DottedHost = 4,
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

    /// <summary>
    /// Split the ClientHello across several TLS records. Valid TLS, costs no
    /// latency, and ignored for plaintext HTTP.
    /// </summary>
    public TlsRecordSplit TlsRecords { get; init; } = TlsRecordSplit.None;

    /// <summary>Handshake-body offset used by <see cref="TlsRecordSplit.Absolute"/>.</summary>
    public int TlsRecordPosition { get; init; } = 1;

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
        && Http == HttpTricks.None
        && TlsRecords == TlsRecordSplit.None;

    public override string ToString() => Id;
}
