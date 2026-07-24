using AtomDpi.Core.Net;

namespace AtomDpi.Core.Engine;

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
public sealed record DesyncPlan(byte[] Payload, IReadOnlyList<DesyncSegment> Segments)
{
    /// <summary>True when the engine can simply forward the original packet untouched.</summary>
    public bool IsNoOp => Segments.Count == 1
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
public static class DesyncPlanner
{
    /// <summary>Nothing below this many bytes is worth cutting up.</summary>
    private const int MinimumSplittableLength = 8;

    public static DesyncPlan Plan(BypassStrategy strategy, ReadOnlySpan<byte> payload, bool isTls, string? hostName)
    {
        var working = payload.ToArray();
        var hostOffset = -1;
        var hostLength = 0;

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
            working = ApplyHttpTricks(strategy.Http, working, ref head);
            hostOffset = head.HostValueOffset;
            hostLength = head.HostValueLength;
        }

        if (strategy.IsPassthrough && working.Length == payload.Length)
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
            var decoyHost = FakePayloadFactory.DecoyHosts[Math.Abs(hostName?.GetHashCode() ?? 0) % FakePayloadFactory.DecoyHosts.Length];
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
        return new DesyncPlan(working, segments);
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

    /// <summary>Rewrites the Host header in place (or grows the buffer when a trick needs more room).</summary>
    private static byte[] ApplyHttpTricks(HttpTricks tricks, byte[] payload, ref HttpRequestHeadInfo head)
    {
        if (tricks == HttpTricks.None)
        {
            return payload;
        }

        if (tricks.HasFlag(HttpTricks.HostCase))
        {
            // "Host:" -> "hOSt:" - same length, so offsets stay valid.
            payload[head.HostHeaderNameOffset] = (byte)'h';
            payload[head.HostHeaderNameOffset + 1] = (byte)'O';
            payload[head.HostHeaderNameOffset + 2] = (byte)'S';
            payload[head.HostHeaderNameOffset + 3] = (byte)'t';
        }

        var insertions = new List<(int Offset, byte Value)>(2);
        if (tricks.HasFlag(HttpTricks.ExtraSpace))
        {
            insertions.Add((head.HostValueOffset, (byte)' '));
        }

        if (tricks.HasFlag(HttpTricks.DottedHost))
        {
            insertions.Add((head.HostValueOffset + head.HostValueLength, (byte)'.'));
        }

        if (insertions.Count == 0)
        {
            return payload;
        }

        var result = new byte[payload.Length + insertions.Count];
        var source = 0;
        var target = 0;
        var hostValueOffset = head.HostValueOffset;
        var hostValueLength = head.HostValueLength;

        foreach (var (offset, value) in insertions.OrderBy(i => i.Offset))
        {
            var take = offset - source;
            Array.Copy(payload, source, result, target, take);
            source += take;
            target += take;
            result[target++] = value;

            if (offset <= head.HostValueOffset)
            {
                hostValueOffset++;
            }
            else
            {
                hostValueLength++;
            }
        }

        Array.Copy(payload, source, result, target, payload.Length - source);
        head = head with { HostValueOffset = hostValueOffset, HostValueLength = hostValueLength };
        return result;
    }
}
