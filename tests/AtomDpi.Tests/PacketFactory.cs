using System.Buffers.Binary;
using System.Text;

namespace AtomDpi.Tests;

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

    public static byte[] BuildIPv6Tcp(byte[] payload, ushort destinationPort = 443, uint sequence = 7)
    {
        var total = 40 + 20 + payload.Length;
        var packet = new byte[total];

        packet[0] = 0x60; // IPv6
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(20 + payload.Length));
        packet[6] = 6; // TCP
        packet[7] = 64; // hop limit

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(40), 40000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(42), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(44), sequence);
        packet[52] = 5 << 4;
        packet[53] = 0x18;

        payload.CopyTo(packet, 60);
        return packet;
    }

    public static byte[] HttpRequest(string host, string extraHeaders = "") => Encoding.ASCII.GetBytes(
        $"GET /api/v9/gateway HTTP/1.1\r\nHost: {host}\r\nUser-Agent: test\r\n{extraHeaders}Accept: */*\r\n\r\n");
}
