using System.Buffers.Binary;

namespace DpiBypass.Core.Net;

/// <summary>
/// Rewrites a ClientHello so it arrives spread over several TLS records.
/// </summary>
/// <remarks>
/// <para>
/// This is the cheapest technique in the toolbox and usually the most effective on
/// fixed lines. A handshake message is allowed to span any number of records, so
/// cutting one in half is completely valid TLS: the server's record layer
/// reassembles it and the handshake proceeds exactly as before. An inspector that
/// only parses the first record - which is most of them, because reassembling the
/// record layer costs state per flow - never sees a whole SNI extension.
/// </para>
/// <para>
/// Unlike the TTL and checksum tricks there is nothing to lose here. No packet is
/// discarded by anyone, so no retransmission is waited for and no latency is added;
/// the client simply sends five extra header bytes per additional record.
/// </para>
/// </remarks>
public static class TlsRecordFragmenter
{
    private const int RecordHeaderLength = 5;
    private const byte ContentTypeHandshake = 0x16;

    /// <summary>Records smaller than this are not worth producing.</summary>
    private const int MinimumRecordBody = 1;

    /// <summary>
    /// Splits the single record in <paramref name="payload"/> at
    /// <paramref name="bodyOffset"/> bytes into the handshake body.
    /// </summary>
    /// <returns>The rewritten payload, or null when the input is not a splittable record.</returns>
    public static byte[]? Split(ReadOnlySpan<byte> payload, int bodyOffset)
        => Split(payload, [bodyOffset]);

    /// <summary>Splits at several points at once, producing <c>cuts.Count + 1</c> records.</summary>
    public static byte[]? Split(ReadOnlySpan<byte> payload, IReadOnlyList<int> bodyOffsets)
    {
        if (payload.Length <= RecordHeaderLength || payload[0] != ContentTypeHandshake)
        {
            return null;
        }

        var declared = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);

        // Only rewrite a record we have in full. A ClientHello split across TCP
        // segments by the client itself is left alone rather than corrupted.
        if (declared == 0 || RecordHeaderLength + declared != payload.Length)
        {
            return null;
        }

        var body = payload[RecordHeaderLength..];
        var cuts = Normalise(bodyOffsets, body.Length);
        if (cuts.Count == 0)
        {
            return null;
        }

        var major = payload[1];
        var minor = payload[2];

        var result = new byte[payload.Length + (cuts.Count * RecordHeaderLength)];
        var written = 0;
        var start = 0;

        foreach (var cut in cuts.Append(body.Length))
        {
            var length = cut - start;
            result[written] = ContentTypeHandshake;
            result[written + 1] = major;
            result[written + 2] = minor;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(written + 3), (ushort)length);
            body.Slice(start, length).CopyTo(result.AsSpan(written + RecordHeaderLength));

            written += RecordHeaderLength + length;
            start = cut;
        }

        return result;
    }

    /// <summary>
    /// Converts a payload offset (what the ClientHello parser reports) into an offset
    /// into the handshake body (what this class cuts on).
    /// </summary>
    public static int ToBodyOffset(int payloadOffset) => payloadOffset - RecordHeaderLength;

    private static List<int> Normalise(IReadOnlyList<int> offsets, int bodyLength)
    {
        var cuts = new List<int>(offsets.Count);

        foreach (var offset in offsets)
        {
            if (offset < MinimumRecordBody || offset > bodyLength - MinimumRecordBody || cuts.Contains(offset))
            {
                continue;
            }

            cuts.Add(offset);
        }

        cuts.Sort();
        return cuts;
    }
}
