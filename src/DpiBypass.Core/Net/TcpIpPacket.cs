using System.Buffers.Binary;
using System.Net;

namespace DpiBypass.Core.Net;

[Flags]
public enum TcpFlags : byte
{
    None = 0,
    Fin = 0x01,
    Syn = 0x02,
    Rst = 0x04,
    Psh = 0x08,
    Ack = 0x10,
    Urg = 0x20,
    Ece = 0x40,
    Cwr = 0x80,
}

/// <summary>
/// A parsed view over a raw IPv4/IPv6 + TCP packet. Nothing is copied: the struct
/// only records offsets so the caller can slice the original buffer.
/// </summary>
public readonly struct TcpIpPacket
{
    public const byte ProtocolTcp = 6;
    public const byte ProtocolUdp = 17;

    public bool IsValid { get; private init; }
    public bool IsIPv6 { get; private init; }
    public int IpHeaderLength { get; private init; }
    public int TcpHeaderOffset { get; private init; }
    public int TcpHeaderLength { get; private init; }
    public int PayloadOffset { get; private init; }
    public int PayloadLength { get; private init; }
    public ushort SourcePort { get; private init; }
    public ushort DestinationPort { get; private init; }
    public uint SequenceNumber { get; private init; }
    public TcpFlags Flags { get; private init; }
    public byte TimeToLive { get; private init; }

    public int TotalLength => PayloadOffset + PayloadLength;

    /// <summary>Offset of the TTL (v4) or hop limit (v6) byte.</summary>
    public int TimeToLiveOffset => IsIPv6 ? 7 : 8;

    public static TcpIpPacket Parse(ReadOnlySpan<byte> packet)
    {
        if (!TryLocateTransport(packet, ProtocolTcp, out var isIPv6, out var tcpOffset, out var usable))
        {
            return default;
        }

        if (usable < tcpOffset + 20)
        {
            return default;
        }

        var tcpHeaderLength = (packet[tcpOffset + 12] >> 4) * 4;
        if (tcpHeaderLength < 20 || tcpOffset + tcpHeaderLength > usable)
        {
            return default;
        }

        var payloadOffset = tcpOffset + tcpHeaderLength;

        return new TcpIpPacket
        {
            IsValid = true,
            IsIPv6 = isIPv6,
            IpHeaderLength = tcpOffset,
            TcpHeaderOffset = tcpOffset,
            TcpHeaderLength = tcpHeaderLength,
            PayloadOffset = payloadOffset,
            PayloadLength = usable - payloadOffset,
            SourcePort = BinaryPrimitives.ReadUInt16BigEndian(packet[tcpOffset..]),
            DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(packet[(tcpOffset + 2)..]),
            SequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(packet[(tcpOffset + 4)..]),
            Flags = (TcpFlags)packet[tcpOffset + 13],
            TimeToLive = packet[isIPv6 ? 7 : 8],
        };
    }

    internal static bool TryLocateTransport(
        ReadOnlySpan<byte> packet,
        byte expectedProtocol,
        out bool isIPv6,
        out int transportOffset,
        out int usableLength)
    {
        isIPv6 = false;
        transportOffset = 0;
        usableLength = 0;

        if (packet.Length < 20)
        {
            return false;
        }

        var version = packet[0] >> 4;
        if (version == 4)
        {
            var headerLength = (packet[0] & 0x0F) * 4;
            var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
            if (headerLength < 20 || declaredLength < headerLength || declaredLength > packet.Length)
            {
                return false;
            }

            // Reassembly is deliberately out of scope. Parsing a later fragment as
            // a TCP/UDP header could rewrite or drop unrelated payload bytes.
            var fragment = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
            if ((fragment & 0x3FFF) != 0 || packet[9] != expectedProtocol)
            {
                return false;
            }

            transportOffset = headerLength;
            usableLength = declaredLength;
            return true;
        }

        if (version != 6 || packet.Length < 40)
        {
            return false;
        }

        isIPv6 = true;
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        var declaredTotal = 40 + payloadLength;
        if (payloadLength == 0 || declaredTotal > packet.Length)
        {
            // IPv6 jumbograms require the Jumbo Payload option, which this parser
            // intentionally does not implement.
            return false;
        }

        var nextHeader = packet[6];
        var offset = 40;
        for (var extensionCount = 0; nextHeader != expectedProtocol; extensionCount++)
        {
            if (extensionCount >= 16 || offset + 2 > declaredTotal)
            {
                return false;
            }

            int extensionLength;
            switch (nextHeader)
            {
                case 0:   // Hop-by-Hop Options
                case 43:  // Routing
                case 60:  // Destination Options
                case 135: // Mobility
                    extensionLength = (packet[offset + 1] + 1) * 8;
                    break;
                case 51: // Authentication Header
                    extensionLength = (packet[offset + 1] + 2) * 4;
                    break;
                case 44: // Fragment
                case 50: // ESP
                case 59: // No Next Header
                default:
                    return false;
            }

            if (extensionLength < 8 || offset + extensionLength > declaredTotal)
            {
                return false;
            }

            nextHeader = packet[offset];
            offset += extensionLength;
        }

        transportOffset = offset;
        usableLength = declaredTotal;
        return true;
    }

    public ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> packet) => packet.Slice(PayloadOffset, PayloadLength);

    public IPAddress DestinationAddress(ReadOnlySpan<byte> packet)
        => IsIPv6 ? new IPAddress(packet.Slice(24, 16)) : new IPAddress(packet.Slice(16, 4));

    public IPAddress SourceAddress(ReadOnlySpan<byte> packet)
        => IsIPv6 ? new IPAddress(packet.Slice(8, 16)) : new IPAddress(packet.Slice(12, 4));

    /// <summary>Rewrites the IP length field after the payload has been resized.</summary>
    public static void SetTotalLength(Span<byte> packet, bool isIPv6, int totalLength)
    {
        if (isIPv6)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet[4..], (ushort)(totalLength - 40));
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet[2..], (ushort)totalLength);
        }
    }

    public static void SetSequenceNumber(Span<byte> packet, int tcpOffset, uint sequence)
        => BinaryPrimitives.WriteUInt32BigEndian(packet[(tcpOffset + 4)..], sequence);

    public static uint GetSequenceNumber(ReadOnlySpan<byte> packet, int tcpOffset)
        => BinaryPrimitives.ReadUInt32BigEndian(packet[(tcpOffset + 4)..]);

    public static void SetFlags(Span<byte> packet, int tcpOffset, TcpFlags flags)
        => packet[tcpOffset + 13] = (byte)flags;

    public static void SetUrgentPointer(Span<byte> packet, int tcpOffset, ushort pointer)
        => BinaryPrimitives.WriteUInt16BigEndian(packet[(tcpOffset + 18)..], pointer);

    public static void SetChecksum(Span<byte> packet, int tcpOffset, ushort checksum)
        => BinaryPrimitives.WriteUInt16BigEndian(packet[(tcpOffset + 16)..], checksum);

    public static void SetTimeToLive(Span<byte> packet, bool isIPv6, byte value) => packet[isIPv6 ? 7 : 8] = value;

    /// <summary>Randomises the IPv4 identification field so split segments do not collide.</summary>
    public static void SetIdentification(Span<byte> packet, bool isIPv6, ushort id)
    {
        if (!isIPv6)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet[4..], id);
        }
    }
}
