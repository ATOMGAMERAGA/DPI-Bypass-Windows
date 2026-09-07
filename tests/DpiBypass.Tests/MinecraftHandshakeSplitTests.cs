using System.Buffers.Binary;
using System.Text;
using DpiBypass.Core.Net;
using DpiBypass.Core.Vodafone;
using Xunit;

namespace DpiBypass.Tests;

public sealed class MinecraftHandshakeSplitTests
{
    [Theory]
    [InlineData("eu.mcpvp.club", 776, 25565)]
    [InlineData("mcpvp.com", 776, 25565)]
    [InlineData("play.example.test", 47, 30123)]
    [InlineData("play.example.test\0FML\0", 767, 25565)]
    public void SplitsLoginWithoutChangingStreamOrRouting(string host, int protocol, int port)
    {
        // A Login Start may share the TCP packet with the handshake.
        var payload = Handshake(host, protocol).Concat(new byte[] { 3, 0, 1, 65 }).ToArray();
        var packet = PacketFactory.BuildIPv4Tcp(payload, destinationPort: (ushort)port,
            sequence: uint.MaxValue - 1, ttl: 65, tcpOptionBytes: 12);
        packet[40] = 1; // Preserve TCP options verbatim.
        var original = packet.ToArray();

        Assert.True(MinecraftHandshakeSplit.TryCreateSegments(packet, 32, out var first, out var second));
        var a = TcpIpPacket.Parse(first);
        var b = TcpIpPacket.Parse(second);
        Assert.True(a.IsValid && b.IsValid);
        Assert.Equal(4, a.PayloadLength);
        Assert.Equal(2u, b.SequenceNumber); // TCP sequence wraparound.
        Assert.Equal(payload, a.Payload(first).ToArray().Concat(b.Payload(second).ToArray()).ToArray());
        Assert.Equal(original, packet);
        foreach (var part in new[] { first, second })
        {
            var parsed = TcpIpPacket.Parse(part);
            Assert.Equal((ushort)port, parsed.DestinationPort);
            Assert.Equal((ushort)51000, parsed.SourcePort);
            Assert.Equal(65, parsed.TimeToLive);
            Assert.Equal(original[12..20], part[12..20]);
            Assert.Equal(original[40..52], part[40..52]);
            Assert.Equal(part.Length, parsed.TotalLength);
        }

        // A reinjected prefix/remainder must not be split again.
        Assert.False(MinecraftHandshakeSplit.TryCreateSegments(first, 32, out _, out _));
        Assert.False(MinecraftHandshakeSplit.TryCreateSegments(second, 32, out _, out _));
        // Retransmission of the original is handled identically without flow state.
        Assert.True(MinecraftHandshakeSplit.TryCreateSegments(packet, 32, out var retry, out _));
        Assert.Equal(first, retry);
    }

    [Fact]
    public void PreservesIpv6ExtensionsAndIpv4Options()
    {
        var payload = Handshake(new string('a', 130), 776); // Multi-byte frame/host lengths.
        var ipv6 = PacketFactory.BuildIPv6Tcp(payload, destinationOptions: true);
        Assert.True(MinecraftHandshakeSplit.TryCreateSegments(ipv6, 32, out var first, out var second));
        foreach (var part in new[] { first, second })
        {
            var parsed = TcpIpPacket.Parse(part);
            Assert.Equal(48, parsed.TcpHeaderOffset);
            Assert.Equal(ipv6[8..48], part[8..48]);
            Assert.Equal(part.Length - 40, BinaryPrimitives.ReadUInt16BigEndian(part.AsSpan(4)));
        }

        var plain = PacketFactory.BuildIPv4Tcp(payload);
        var options = plain[..20].Concat(new byte[] { 1, 1, 1, 1 }).Concat(plain[20..]).ToArray();
        options[0] = 0x46;
        TcpIpPacket.SetTotalLength(options, false, options.Length);
        Assert.True(MinecraftHandshakeSplit.TryCreateSegments(options, 32, out first, out second));
        Assert.Equal(options[20..24], first[20..24]);
        Assert.Equal(options[20..24], second[20..24]);
        Assert.Equal(24, TcpIpPacket.Parse(second).TcpHeaderOffset);
    }

    [Fact]
    public void LeavesStatusUnrelatedAndIncompletePayloadsAlone()
    {
        var login = Handshake("eu.mcpvp.club", 776);
        var status = login.ToArray();
        status[^1] = 1;
        var invalidHost = login.ToArray();
        invalidHost[5] = 0xFF;
        var extraFrameByte = login.Concat(new byte[] { 0 }).ToArray();
        extraFrameByte[0]++;
        foreach (var payload in new[]
        {
            status, invalidHost, extraFrameByte, PacketFactory.HttpRequest("example.test"),
            new byte[] { 0x16, 3, 1, 0, 100 }, new byte[] { 255, 255, 255, 255, 127 },
        })
        {
            Assert.False(MinecraftHandshakeSplit.IsLoginHandshake(payload));
        }

        for (var length = 0; length < login.Length; length++)
        {
            Assert.False(MinecraftHandshakeSplit.IsLoginHandshake(login.AsSpan(0, length)));
        }
    }

    [Theory]
    [InlineData(8, 0x18, false)] // Low-TTL desync decoy.
    [InlineData(65, 0x1A, false)] // SYN.
    [InlineData(65, 0x19, false)] // FIN.
    [InlineData(65, 0x1C, false)] // RST.
    [InlineData(65, 0x38, false)] // Urgent data.
    [InlineData(65, 0x18, true)] // IP fragment.
    public void DoesNotAlterDecoysControlPacketsOrFragments(byte ttl, byte flags, bool fragmented)
    {
        var packet = PacketFactory.BuildIPv4Tcp(Handshake("mcpvp.com", 776), ttl: ttl);
        packet[33] = flags;
        if (fragmented) packet[6] = 0x20;
        Assert.False(MinecraftHandshakeSplit.TryCreateSegments(packet, 32, out _, out _));
    }

    private static byte[] Handshake(string host, int protocol)
    {
        var name = Encoding.UTF8.GetBytes(host);
        var frame = new List<byte> { 0 };
        AddVarInt(frame, protocol);
        AddVarInt(frame, name.Length);
        frame.AddRange(name);
        frame.AddRange([0x63, 0xDD, 2]);
        var wire = new List<byte>();
        AddVarInt(wire, frame.Count);
        wire.AddRange(frame);
        return wire.ToArray();
    }

    private static void AddVarInt(List<byte> bytes, int value)
    {
        do
        {
            var part = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? part : (byte)(part | 0x80));
        } while (value != 0);
    }
}
