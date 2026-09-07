using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Vodafone;
using Xunit;

namespace DpiBypass.Tests;

public sealed class HotspotCompatibilityTests
{
    [Theory]
    [InlineData(443, true)]
    [InlineData(25565, false)]
    public void TtlChangePreservesTransportChecksumAndOffloadState(int port, bool checksumReady)
    {
        var packet = PacketFactory.BuildIPv4Tcp([1, 2, 3], destinationPort: (ushort)port);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(36), 0xBAD1);
        var transport = packet[20..];
        var address = new WinDivertAddress { TCPChecksum = checksumReady, UDPChecksum = checksumReady };

        Assert.NotNull(HotspotTtlFix.Rewrite(packet, 8, TtlFixSettings.Default));
        Assert.True(HotspotTtlFix.RecalculateIpChecksum(packet, ref address));
        Assert.Equal(transport, packet[20..]);
        Assert.Equal(checksumReady, address.TCPChecksum);
        Assert.Equal(checksumReady, address.UDPChecksum);
        Assert.True(address.IPChecksum);

        uint sum = 0;
        for (var i = 0; i < 20; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(i));
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        Assert.Equal(0xFFFFu, sum);
    }

    [Fact]
    public async Task HotspotIpv6PolicyOverridesCacheAndCanBeDisabled()
    {
        var blocked = false;
        using var upstream = new EchoDns();
        using var resolver = new DohResolver(transport: upstream);
        await using var proxy = new DnsProxyServer(resolver) { SuppressIPv6Answers = () => blocked };
        var query = DnsMessage.BuildQuery(123, "play.example.test", DnsRecordType.Aaaa);

        Assert.Single(DnsMessage.ReadAddresses((await proxy.ResolveAsync(query, default))!));
        blocked = true;
        var response = (await proxy.ResolveAsync(query, default))!;
        Assert.True(DnsMessage.IsResponseForQuery(query, response));
        Assert.Equal(0, DnsMessage.GetResponseCode(response));
        Assert.Empty(DnsMessage.ReadAddresses(response));
        Assert.Equal(1, upstream.Requests);
        blocked = false;
        Assert.Single(DnsMessage.ReadAddresses((await proxy.ResolveAsync(query, default))!));
    }

    [Theory]
    [InlineData("play.example.test", DnsRecordType.A)]
    [InlineData("_minecraft._tcp.example.test", 33)]
    public async Task HotspotStillForwardsIpv4AndMinecraftSrv(string name, ushort type)
    {
        using var upstream = new EchoDns();
        using var resolver = new DohResolver(transport: upstream);
        await using var proxy = new DnsProxyServer(resolver) { SuppressIPv6Answers = () => true };
        var query = DnsMessage.BuildQuery(123, name, type);
        var response = await proxy.ResolveAsync(query, default);
        Assert.NotNull(response);
        Assert.True(DnsMessage.IsResponseForQuery(query, response));
        Assert.Equal(1, upstream.Requests);
    }

    private sealed class EchoDns : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var reply = await request.Content!.ReadAsByteArrayAsync(token);
            Assert.True(DnsMessage.TryReadQuestion(reply, out var question));
            reply[2] |= 0x80;
            reply[3] = 0x80;
            if (question.Type == DnsRecordType.Aaaa)
            {
                var offset = reply.Length;
                Array.Resize(ref reply, offset + 28);
                reply[7] = 1;
                reply[offset] = 0xC0;
                reply[offset + 1] = 12;
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(offset + 2), DnsRecordType.Aaaa);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(offset + 4), 1);
                BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(offset + 6), 120);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(offset + 10), 16);
                IPAddress.Parse("2001:db8::1").GetAddressBytes().CopyTo(reply, offset + 12);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(reply) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            return response;
        }
    }
}
