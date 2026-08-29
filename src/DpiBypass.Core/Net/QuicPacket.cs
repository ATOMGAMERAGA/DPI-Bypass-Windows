using System.Buffers.Binary;

namespace DpiBypass.Core.Net;

internal static class QuicPacket
{
    private const uint Version1 = 0x00000001;
    private const uint Version2 = 0x6B3343CF;

    public static bool IsInitial(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 5 || (datagram[0] & 0xC0) != 0xC0)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(datagram[1..]);
        var packetType = (datagram[0] >> 4) & 0x03;
        return (version == Version1 && packetType == 0)
            || (version == Version2 && packetType == 1);
    }
}
