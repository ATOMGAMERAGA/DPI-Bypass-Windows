using System.Text;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Net;
using Xunit;

namespace DpiBypass.Tests;

public class TlsClientHelloTests
{
    [Theory]
    [InlineData("discord.com")]
    [InlineData("gateway.discord.gg")]
    [InlineData("cdn.discordapp.com")]
    [InlineData("a.co")]
    public void ReportsTheServerNameAndItsExactOffset(string host)
    {
        var hello = FakePayloadFactory.CreateTlsClientHello(host);

        Assert.True(TlsClientHello.IsClientHello(hello));
        Assert.True(TlsClientHello.TryParse(hello, out var info));
        Assert.Equal(host, info.ServerName);

        // The offset has to point at the hostname itself, because the desync planner
        // splits the packet there.
        var atOffset = Encoding.ASCII.GetString(hello, info.ServerNameOffset, info.ServerNameLength);
        Assert.Equal(host, atOffset);
    }

    [Fact]
    public void RecordLengthMatchesTheTlsHeader()
    {
        var hello = FakePayloadFactory.CreateTlsClientHello("discord.com");
        Assert.True(TlsClientHello.TryParse(hello, out var info));
        Assert.Equal(hello.Length, info.RecordLength);
    }

    [Fact]
    public void PaddingRequestProducesAtLeastTheRequestedLength()
    {
        var hello = FakePayloadFactory.CreateTlsClientHello("discord.com", minimumLength: 700);
        Assert.True(hello.Length >= 700);
        Assert.True(TlsClientHello.TryParse(hello, out var info));
        Assert.Equal("discord.com", info.ServerName);
    }

    [Fact]
    public void RejectsNonHandshakeTraffic()
    {
        Assert.False(TlsClientHello.IsClientHello(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n")));
        Assert.False(TlsClientHello.IsClientHello([0x17, 0x03, 0x03, 0x00, 0x10, 0x01]));
    }

    [Fact]
    public void SurvivesATruncatedHello()
    {
        var hello = FakePayloadFactory.CreateTlsClientHello("discord.com");
        var truncated = hello[..30];

        // A ClientHello split across packets must not throw; we just do not learn the name.
        Assert.True(TlsClientHello.TryParse(truncated, out var info));
        Assert.False(info.HasServerName);
    }

    [Fact]
    public void DoesNotThrowOnRandomBytesThatStartLikeAHandshake()
    {
        var random = new byte[200];
        Random.Shared.NextBytes(random);
        random[0] = 0x16;
        random[5] = 0x01;

        TlsClientHello.TryParse(random, out _);
    }
}

public class HttpRequestHeadTests
{
    [Fact]
    public void FindsTheHostHeaderAndItsValueOffset()
    {
        var request = PacketFactory.HttpRequest("discord.com");

        Assert.True(HttpRequestHead.IsRequest(request));
        Assert.True(HttpRequestHead.TryParse(request, out var head));
        Assert.Equal("discord.com", head.Host);
        Assert.Equal("discord.com", Encoding.ASCII.GetString(request, head.HostValueOffset, head.HostValueLength));
        Assert.Equal("Host:", Encoding.ASCII.GetString(request, head.HostHeaderNameOffset, 5));
    }

    [Fact]
    public void MatchesTheHeaderNameCaseInsensitively()
    {
        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nhOSt:   discord.com  \r\n\r\n");

        Assert.True(HttpRequestHead.TryParse(request, out var head));
        Assert.Equal("discord.com", head.Host);
    }

    [Fact]
    public void IgnoresRequestsWithoutAHostHeader()
    {
        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nAccept: */*\r\n\r\n");
        Assert.False(HttpRequestHead.TryParse(request, out _));
    }

    [Fact]
    public void RejectsTrafficThatIsNotARequest()
    {
        Assert.False(HttpRequestHead.IsRequest(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n")));
        Assert.False(HttpRequestHead.IsRequest([0x16, 0x03, 0x01, 0x00, 0x05, 0x01]));
    }
}

public class TcpIpPacketTests
{
    [Fact]
    public void ParsesIPv4WithOffsetsThatLocateThePayload()
    {
        var payload = Encoding.ASCII.GetBytes("hello dpi");
        var packet = PacketFactory.BuildIPv4Tcp(payload, sourcePort: 1234, destinationPort: 443, sequence: 42, ttl: 64);

        var parsed = TcpIpPacket.Parse(packet);

        Assert.True(parsed.IsValid);
        Assert.False(parsed.IsIPv6);
        Assert.Equal(20, parsed.IpHeaderLength);
        Assert.Equal(20, parsed.TcpHeaderOffset);
        Assert.Equal(20, parsed.TcpHeaderLength);
        Assert.Equal(40, parsed.PayloadOffset);
        Assert.Equal(payload.Length, parsed.PayloadLength);
        Assert.Equal(1234, parsed.SourcePort);
        Assert.Equal(443, parsed.DestinationPort);
        Assert.Equal(42u, parsed.SequenceNumber);
        Assert.Equal(64, parsed.TimeToLive);
        Assert.Equal(payload, parsed.Payload(packet).ToArray());
        Assert.Equal(TcpFlags.Psh | TcpFlags.Ack, parsed.Flags);
    }

    [Fact]
    public void HonoursTcpOptionsWhenLocatingThePayload()
    {
        var payload = Encoding.ASCII.GetBytes("with options");
        var packet = PacketFactory.BuildIPv4Tcp(payload, tcpOptionBytes: 12);

        var parsed = TcpIpPacket.Parse(packet);

        Assert.Equal(32, parsed.TcpHeaderLength);
        Assert.Equal(52, parsed.PayloadOffset);
        Assert.Equal(payload, parsed.Payload(packet).ToArray());
    }

    [Fact]
    public void ParsesIPv6()
    {
        var payload = Encoding.ASCII.GetBytes("v6 payload");
        var packet = PacketFactory.BuildIPv6Tcp(payload, sequence: 9);

        var parsed = TcpIpPacket.Parse(packet);

        Assert.True(parsed.IsValid);
        Assert.True(parsed.IsIPv6);
        Assert.Equal(60, parsed.PayloadOffset);
        Assert.Equal(9u, parsed.SequenceNumber);
        Assert.Equal(payload, parsed.Payload(packet).ToArray());
        Assert.Equal(7, parsed.TimeToLiveOffset);
    }

    [Fact]
    public void RewritesLengthAndSequenceForASplitSegment()
    {
        var payload = Encoding.ASCII.GetBytes("0123456789");
        var packet = PacketFactory.BuildIPv4Tcp(payload, sequence: 1000);
        var parsed = TcpIpPacket.Parse(packet);

        // Emulate what the engine does when it emits the second half.
        var trimmed = packet[..(parsed.PayloadOffset + 4)];
        TcpIpPacket.SetTotalLength(trimmed, isIPv6: false, trimmed.Length);
        TcpIpPacket.SetSequenceNumber(trimmed, parsed.TcpHeaderOffset, 1006);

        var reparsed = TcpIpPacket.Parse(trimmed);
        Assert.Equal(4, reparsed.PayloadLength);
        Assert.Equal(1006u, reparsed.SequenceNumber);
        Assert.Equal(1006u, TcpIpPacket.GetSequenceNumber(trimmed, parsed.TcpHeaderOffset));
    }

    [Fact]
    public void RewritesTtlForBothAddressFamilies()
    {
        var v4 = PacketFactory.BuildIPv4Tcp([1, 2, 3], ttl: 128);
        TcpIpPacket.SetTimeToLive(v4, isIPv6: false, 4);
        Assert.Equal(4, TcpIpPacket.Parse(v4).TimeToLive);

        var v6 = PacketFactory.BuildIPv6Tcp([1, 2, 3]);
        TcpIpPacket.SetTimeToLive(v6, isIPv6: true, 3);
        Assert.Equal(3, TcpIpPacket.Parse(v6).TimeToLive);
    }

    [Theory]
    [InlineData(new byte[] { 0x45 })]
    [InlineData(new byte[] { 0x35, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void RejectsShortOrUnknownPackets(byte[] bytes) => Assert.False(TcpIpPacket.Parse(bytes).IsValid);

    [Fact]
    public void RejectsNonTcpProtocols()
    {
        var packet = PacketFactory.BuildIPv4Tcp([1, 2, 3]);
        packet[9] = 17; // UDP
        Assert.False(TcpIpPacket.Parse(packet).IsValid);
    }

    [Fact]
    public void IgnoresTrailingBytesBeyondTheDeclaredLength()
    {
        var payload = Encoding.ASCII.GetBytes("exact");
        var packet = PacketFactory.BuildIPv4Tcp(payload);
        var padded = new byte[packet.Length + 16];
        packet.CopyTo(padded, 0);

        // Ethernet padding must not be mistaken for extra payload.
        var parsed = TcpIpPacket.Parse(padded);
        Assert.Equal(payload.Length, parsed.PayloadLength);
    }
}
