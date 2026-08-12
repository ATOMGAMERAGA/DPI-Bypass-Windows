using DpiBypass.Core.Net;

namespace DpiBypass.Core.Engine;

/// <summary>
/// One packet the engine should put on the wire, described relative to the
/// (possibly rewritten) payload of the packet we intercepted.
/// </summary>
public sealed record DesyncSegment
{
    /// <summary>Offset into <see cref="DesyncPlan.Payload"/>, or -1 when <see cref="FakePayload"/> is used.</summary>
    public int Offset { get; init; } = -1;

    public int Length { get; init; }

    public byte[]? FakePayload { get; init; }

    /// <summary>Added to the original sequence number so the receiver reassembles correctly.</summary>
    public uint SequenceOffset { get; init; }

    /// <summary>Extra skew applied on top of <see cref="SequenceOffset"/> to push a decoy out of the window.</summary>
    public int SequenceSkew { get; init; }

    /// <summary>Overrides the IP TTL / hop limit when set.</summary>
    public byte? TimeToLive { get; init; }

    /// <summary>Emit with a deliberately broken TCP checksum.</summary>
    public bool CorruptChecksum { get; init; }

    /// <summary>Emit with URG set - the out-of-band trick.</summary>
    public bool Urgent { get; init; }

    public bool IsDecoy => FakePayload is not null;
}

/// <summary>The full rewrite of one intercepted packet.</summary>
/// <param name="PayloadRewritten">
/// True when <paramref name="Payload"/> differs from the bytes that arrived. Without
/// this the engine cannot tell a single full-length segment carrying a rewritten
/// payload apart from one carrying the original, and would forward the untouched
/// packet - silently dropping every header trick that does not also cut the segment.
/// </param>
public sealed record DesyncPlan(
    byte[] Payload,
    IReadOnlyList<DesyncSegment> Segments,
    bool PayloadRewritten = false)
{
    /// <summary>True when the engine can simply forward the original packet untouched.</summary>
    public bool IsNoOp => !PayloadRewritten
        && Segments.Count == 1
        && Segments[0].Offset == 0
        && Segments[0].Length == Payload.Length
        && Segments[0].FakePayload is null
        && Segments[0].TimeToLive is null
        && !Segments[0].CorruptChecksum
        && !Segments[0].Urgent
        && Segments[0].SequenceSkew == 0;

    public static DesyncPlan Passthrough(byte[] payload) =>
        new(payload, [new DesyncSegment { Offset = 0, Length = payload.Length }]);
}

/// <summary>Turns a <see cref="BypassStrategy"/> plus one observed packet into a concrete send plan.</summary>
/// <remarks>
/// One rule governs everything here: the plan may reorder, cut and duplicate the
/// payload, but it may never change how many bytes of it there are. The engine is
/// handed outbound packets only, so the local TCP stack has already committed to the
/// byte count it gave us and keeps its own SND.NXT. Send more bytes than that and the
/// peer acknowledges data the stack never sent, which Windows answers by discarding
/// the segment - the handshake then stalls until it times out. That is why the
/// record-layer re-framing this planner used to do had to go: it is valid TLS, but it
/// adds five bytes per record, and a stateless rewriter cannot fix up the sequence
/// space in both directions to pay for them.
/// </remarks>
public static class DesyncPlanner
{
    /// <summary>Nothing below this many bytes is worth cutting up.</summary>
    private const int MinimumSplittableLength = 8;

    public static DesyncPlan Plan(BypassStrategy strategy, ReadOnlySpan<byte> payload, bool isTls, string? hostName)
    {
        var working = payload.ToArray();
        var hostOffset = -1;
        var hostLength = 0;
        var rewritten = false;

        if (isTls)
        {
            if (TlsClientHello.TryParse(working, out var hello) && hello.HasServerName)
            {
                hostOffset = hello.ServerNameOffset;
                hostLength = hello.ServerNameLength;
            }
        }
        else if (HttpRequestHead.TryParse(working, out var head) && head.HasHost)
        {
            rewritten = ApplyHttpTricks(strategy.Http, working, head);
            hostOffset = head.HostValueOffset;
            hostLength = head.HostValueLength;
        }

        if (strategy.IsPassthrough)
        {
            return DesyncPlan.Passthrough(working);
        }

        var segments = new List<DesyncSegment>(6);

        if (strategy.OutOfBand && working.Length > 1)
        {
            segments.Add(new DesyncSegment
            {
                FakePayload = [0x00],
                SequenceOffset = 0,
                SequenceSkew = -1,
                Urgent = true,
            });
        }

        if (strategy.Fake != FakeMode.None)
        {
            // Unsigned: string hashing is randomised per process and may hand back
            // int.MinValue, which Math.Abs cannot represent.
            var hash = (uint)(hostName?.GetHashCode() ?? 0);
            var decoyHost = FakePayloadFactory.DecoyHosts[hash % (uint)FakePayloadFactory.DecoyHosts.Length];
            var decoy = isTls
                ? FakePayloadFactory.CreateTlsClientHello(decoyHost, working.Length)
                : FakePayloadFactory.CreateHttpRequest(decoyHost, working.Length);

            for (var i = 0; i < Math.Max(1, strategy.FakeCount); i++)
            {
                segments.Add(new DesyncSegment
                {
                    FakePayload = decoy,
                    SequenceOffset = 0,
                    SequenceSkew = strategy.Fake == FakeMode.BadSequence ? -0x40000 : 0,
                    TimeToLive = strategy.Fake == FakeMode.ExpiredTtl ? strategy.FakeTtl : null,
                    CorruptChecksum = strategy.Fake == FakeMode.BadChecksum,
                });
            }
        }

        var cuts = ResolveCuts(strategy, working.Length, hostOffset, hostLength);
        var realSegments = new List<DesyncSegment>(cuts.Count + 1);
        var start = 0;
        foreach (var cut in cuts)
        {
            realSegments.Add(new DesyncSegment { Offset = start, Length = cut - start, SequenceOffset = (uint)start });
            start = cut;
        }

        realSegments.Add(new DesyncSegment { Offset = start, Length = working.Length - start, SequenceOffset = (uint)start });

        if (strategy.Split == SplitMode.Disorder)
        {
            realSegments.Reverse();
        }

        segments.AddRange(realSegments);
        return new DesyncPlan(working, segments, rewritten);
    }

    /// <summary>Works out the absolute cut offsets, deduplicated and in ascending order.</summary>
    private static List<int> ResolveCuts(BypassStrategy strategy, int payloadLength, int hostOffset, int hostLength)
    {
        var cuts = new List<int>(2);
        if (strategy.Split == SplitMode.None || payloadLength < MinimumSplittableLength)
        {
            return cuts;
        }

        var primary = strategy.Anchor switch
        {
            SplitAnchor.HostMiddle when hostOffset >= 0 => hostOffset + (hostLength / 2) + strategy.SplitPosition,
            SplitAnchor.HostStart when hostOffset >= 0 => hostOffset + strategy.SplitPosition,
            SplitAnchor.HostEnd when hostOffset >= 0 => hostOffset + hostLength + strategy.SplitPosition,
            // No hostname in this packet: fall back to a small fixed offset, which
            // still breaks up the record header the inspector keys on.
            _ => strategy.SplitPosition,
        };

        AddCut(cuts, primary, payloadLength);

        if (strategy.SecondSplitPosition > 0)
        {
            AddCut(cuts, strategy.SecondSplitPosition, payloadLength);
        }

        cuts.Sort();
        return cuts;
    }

    private static void AddCut(List<int> cuts, int position, int payloadLength)
    {
        if (position <= 0 || position >= payloadLength)
        {
            return;
        }

        if (!cuts.Contains(position))
        {
            cuts.Add(position);
        }
    }

    /// <summary>
    /// Rewrites the Host header in place. Returns true when anything changed.
    /// </summary>
    /// <remarks>
    /// Every trick is a byte-for-byte substitution, so the header offsets the caller
    /// already holds stay valid and the segment keeps the length the local TCP stack
    /// is expecting. Tricks that inserted bytes - a second space, a trailing root dot -
    /// are not available here for that reason: on a plaintext connection they moved
    /// the peer's acknowledgement past what the stack believed it had sent, which cost
    /// the whole request rather than just the trick.
    /// </remarks>
    private static bool ApplyHttpTricks(HttpTricks tricks, byte[] payload, HttpRequestHeadInfo head)
    {
        var changed = false;

        if (tricks.HasFlag(HttpTricks.HostCase))
        {
            // "Host:" -> "hOSt:"
            payload[head.HostHeaderNameOffset] = (byte)'h';
            payload[head.HostHeaderNameOffset + 1] = (byte)'O';
            payload[head.HostHeaderNameOffset + 2] = (byte)'S';
            payload[head.HostHeaderNameOffset + 3] = (byte)'t';
            changed = true;
        }

        if (tricks.HasFlag(HttpTricks.HostTab))
        {
            // Only a real space may become a tab: with the value hard against the
            // colon there is nothing to substitute, and the byte before it is the
            // colon itself.
            var separator = head.HostValueOffset - 1;
            if (separator > 0 && payload[separator] == (byte)' ')
            {
                payload[separator] = (byte)'\t';
                changed = true;
            }
        }

        return changed;
    }
}
