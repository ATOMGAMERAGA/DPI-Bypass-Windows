using System.Buffers.Binary;
using System.Text;
using DpiBypass.Core.Net;

namespace DpiBypass.Core.Vodafone;

/// <summary>Segments the Java handshake that joins a server on the active hotspot adapter.</summary>
/// <remarks>
/// Both intents that lead into the login phase are covered: the Login (2) the client sends
/// when the player picks a server, and the Transfer (3) it sends on the fresh connection a
/// 1.20.5+ server asks for when it hands the player to another server. The two packets
/// differ in a single byte, so an inspector that blocks one blocks the other - and because
/// a transfer opens a brand new connection, the client has nothing to report and sits on
/// "Transferring to new server" for as long as the login answer never comes. Status pings
/// (1) are deliberately left whole: they are not a join, and the server list sends them
/// constantly.
/// </remarks>
internal static class MinecraftHandshakeSplit
{
    private const int SplitOffset = 4;

    /// <summary>Next state 2: the player is joining the server they picked.</summary>
    private const int LoginIntent = 2;

    /// <summary>Next state 3: the same login on a connection the server asked for (1.20.5+).</summary>
    private const int TransferIntent = 3;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <param name="transfer">
    /// True when the handshake is a transfer rather than a first login. Only worth a log
    /// line, but that line is how someone whose server moves them between lobbies can see
    /// the step is being handled at all.
    /// </param>
    internal static bool TryCreateSegments(
        ReadOnlySpan<byte> packet, int ttlGuard, out byte[] first, out byte[] second, out bool transfer)
    {
        first = second = [];
        transfer = false;
        var parsed = TcpIpPacket.Parse(packet);
        if (!parsed.IsValid || parsed.TimeToLive < ttlGuard
            || (parsed.Flags & TcpFlags.Ack) == 0
            || (parsed.Flags & (TcpFlags.Syn | TcpFlags.Fin | TcpFlags.Rst | TcpFlags.Urg)) != 0
            || !IsJoinHandshake(parsed.Payload(packet), out var intent))
        {
            return false;
        }

        // No port/hostname list: SRV records and custom servers use other ports.
        // Keep all bytes, including a Login Start coalesced into the same packet.
        transfer = intent == TransferIntent;
        first = CreateSegment(packet, parsed, 0, SplitOffset);
        second = CreateSegment(packet, parsed, SplitOffset, parsed.PayloadLength - SplitOffset);
        return true;
    }

    /// <param name="intent">The handshake's next state, valid only when this returns true.</param>
    internal static bool IsJoinHandshake(ReadOnlySpan<byte> payload, out int intent)
    {
        intent = 0;
        var offset = 0;
        if (!TryReadVarInt(payload, ref offset, out var frameLength)
            || frameLength < 7 || frameLength > 1030 || frameLength > payload.Length - offset)
        {
            return false;
        }

        // Parse only the first length-delimited frame. Incomplete/already segmented
        // handshakes and unrelated traffic are forwarded without buffering.
        var frame = payload.Slice(offset, frameLength);
        offset = 0;
        if (!TryReadVarInt(frame, ref offset, out var packetId) || packetId != 0
            || !TryReadVarInt(frame, ref offset, out var protocol) || protocol == 0
            || !TryReadVarInt(frame, ref offset, out var hostLength)
            || hostLength is < 1 or > 1020 || hostLength > frame.Length - offset - 3)
        {
            return false;
        }

        try
        {
            if (StrictUtf8.GetCharCount(frame.Slice(offset, hostLength)) > 255)
            {
                return false;
            }
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        offset += hostLength;
        if (BinaryPrimitives.ReadUInt16BigEndian(frame[offset..]) == 0)
        {
            return false;
        }

        offset += 2;
        if (!TryReadVarInt(frame, ref offset, out var nextState)
            || nextState is not (LoginIntent or TransferIntent)
            || offset != frame.Length)
        {
            return false;
        }

        intent = nextState;
        return true;
    }

    private static bool TryReadVarInt(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        value = 0;
        for (var shift = 0; shift < 35 && offset < data.Length; shift += 7)
        {
            var current = data[offset++];
            if (shift == 28 && current > 7)
            {
                return false;
            }

            value |= (current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return shift == 0 || current != 0;
            }
        }

        return false;
    }

    private static byte[] CreateSegment(ReadOnlySpan<byte> packet, TcpIpPacket parsed, int offset, int length)
    {
        var segment = new byte[parsed.PayloadOffset + length];
        packet[..parsed.PayloadOffset].CopyTo(segment);
        parsed.Payload(packet).Slice(offset, length).CopyTo(segment.AsSpan(parsed.PayloadOffset));
        TcpIpPacket.SetTotalLength(segment, parsed.IsIPv6, segment.Length);
        TcpIpPacket.SetSequenceNumber(segment, parsed.TcpHeaderOffset, unchecked(parsed.SequenceNumber + (uint)offset));
        if (offset != 0)
        {
            TcpIpPacket.SetFlags(segment, parsed.TcpHeaderOffset, parsed.Flags & ~TcpFlags.Cwr);
            if (!parsed.IsIPv6)
            {
                var identification = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
                TcpIpPacket.SetIdentification(segment, false, unchecked((ushort)(identification + 1)));
            }
        }

        // The caller recalculates both IP and TCP checksums before either send.
        TcpIpPacket.SetChecksum(segment, parsed.TcpHeaderOffset, 0);
        return segment;
    }
}
