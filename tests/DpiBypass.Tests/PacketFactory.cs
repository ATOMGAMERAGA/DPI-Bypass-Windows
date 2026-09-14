using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace DpiBypass.Tests;

/// <summary>Builds synthetic IPv4/IPv6 + TCP packets so the parser can be exercised without a driver.</summary>
internal static class PacketFactory
{
    public static byte[] BuildIPv4Tcp(
        byte[] payload,
        ushort sourcePort = 51000,
        ushort destinationPort = 443,
        uint sequence = 0x11223344,
        byte ttl = 128,
        int tcpOptionBytes = 0)
    {
        var tcpHeaderLength = 20 + tcpOptionBytes;
        var total = 20 + tcpHeaderLength + payload.Length;
        var packet = new byte[total];

        packet[0] = 0x45; // IPv4, 5 word header
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)total);
        packet[8] = ttl;
        packet[9] = 6; // TCP
        new byte[] { 192, 168, 1, 50 }.CopyTo(packet, 12);
        new byte[] { 162, 159, 128, 233 }.CopyTo(packet, 16);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24), sequence);
        packet[32] = (byte)((tcpHeaderLength / 4) << 4);
        packet[33] = 0x18; // PSH | ACK
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(34), 64240);

        payload.CopyTo(packet, 20 + tcpHeaderLength);
        return packet;
    }

    public static byte[] BuildIPv6Tcp(
        byte[] payload,
        ushort destinationPort = 443,
        uint sequence = 7,
        bool destinationOptions = false)
    {
        var extensionLength = destinationOptions ? 8 : 0;
        var tcpOffset = 40 + extensionLength;
        var total = tcpOffset + 20 + payload.Length;
        var packet = new byte[total];

        packet[0] = 0x60; // IPv6
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(extensionLength + 20 + payload.Length));
        packet[6] = destinationOptions ? (byte)60 : (byte)6;
        packet[7] = 64; // hop limit

        if (destinationOptions)
        {
            packet[40] = 6; // next header: TCP
            packet[41] = 0; // (0 + 1) * 8 bytes
        }

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(tcpOffset), 40000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(tcpOffset + 2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(tcpOffset + 4), sequence);
        packet[tcpOffset + 12] = 5 << 4;
        packet[tcpOffset + 13] = 0x18;

        payload.CopyTo(packet, tcpOffset + 20);
        return packet;
    }

    /// <summary>An IPv6 packet over an arbitrary next header, addressed where the test wants it.</summary>
    /// <remarks>
    /// The hotspot rule decides what to drop from the destination address and the
    /// destination port, so both have to be things a test can set. Used for the
    /// link-scoped plumbing (neighbour discovery, DHCPv6) as well as for DNS.
    /// </remarks>
    public static byte[] BuildIPv6(
        byte nextHeader,
        string destination,
        ushort sourcePort = 49000,
        ushort destinationPort = 443,
        byte hopLimit = 64,
        int payloadBytes = 8)
    {
        var packet = new byte[40 + payloadBytes];

        packet[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)payloadBytes);
        packet[6] = nextHeader;
        packet[7] = hopLimit;

        IPAddress.Parse("fd00::1").GetAddressBytes().CopyTo(packet, 8);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 24);

        if (nextHeader is 6 or 17 && payloadBytes >= 4)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(40), sourcePort);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(42), destinationPort);
        }

        return packet;
    }

    public static byte[] HttpRequest(string host, string extraHeaders = "") => Encoding.ASCII.GetBytes(
        $"GET /api/v9/gateway HTTP/1.1\r\nHost: {host}\r\nUser-Agent: test\r\n{extraHeaders}Accept: */*\r\n\r\n");
}
