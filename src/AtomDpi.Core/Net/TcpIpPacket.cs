using System.Buffers.Binary;
using System.Net;

namespace AtomDpi.Core.Net;

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
        if (packet.Length < 20)
        {
            return default;
        }

        var version = packet[0] >> 4;
        int ipHeaderLength;
        bool isIPv6;
        byte protocol;
        byte ttl;
        int declaredTotal;

        if (version == 4)
        {
            isIPv6 = false;
            ipHeaderLength = (packet[0] & 0x0F) * 4;
            if (ipHeaderLength < 20 || packet.Length < ipHeaderLength)
            {
                return default;
            }

            declaredTotal = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
            ttl = packet[8];
            protocol = packet[9];
        }
        else if (version == 6)
        {
            isIPv6 = true;
            ipHeaderLength = 40;
            if (packet.Length < 40)
            {
                return default;
            }

            declaredTotal = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) + 40;
            protocol = packet[6];
            ttl = packet[7];
        }
        else
        {
            return default;
        }

        if (protocol != ProtocolTcp)
        {
            return default;
        }

        // Trust the shorter of "what the header claims" and "what we actually got".
        var usable = declaredTotal > 0 && declaredTotal <= packet.Length ? declaredTotal : packet.Length;
        if (usable < ipHeaderLength + 20)
        {
            return default;
        }

        var tcpOffset = ipHeaderLength;
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
            IpHeaderLength = ipHeaderLength,
            TcpHeaderOffset = tcpOffset,
            TcpHeaderLength = tcpHeaderLength,
            PayloadOffset = payloadOffset,
            PayloadLength = usable - payloadOffset,
            SourcePort = BinaryPrimitives.ReadUInt16BigEndian(packet[tcpOffset..]),
            DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(packet[(tcpOffset + 2)..]),
            SequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(packet[(tcpOffset + 4)..]),
            Flags = (TcpFlags)packet[tcpOffset + 13],
            TimeToLive = ttl,
        };
    }

    public ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> packet) => packet.Slice(PayloadOffset, PayloadLength);

    public IPAddress DestinationAddress(ReadOnlySpan<byte> packet)
        => IsIPv6 ? new IPAddress(packet.Slice(24, 16)) : new IPAddress(packet.Slice(16, 4));

    public IPAddress SourceAddress(ReadOnlySpan<byte> packet)
        => IsIPv6 ? new IPAddress(packet.Slice(8, 16)) : new IPAddress(packet.Slice(0, 4));

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
