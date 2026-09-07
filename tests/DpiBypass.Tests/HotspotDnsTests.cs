using System.Buffers.Binary;
using DpiBypass.Core.Dns;
using Xunit;

namespace DpiBypass.Tests;

public sealed class HotspotDnsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlockedIPv6ReturnsNoDataWithoutAnUpstreamLookup(bool recursionDesired)
    {
        using var resolver = new DohResolver();
        await using var proxy = new DnsProxyServer(resolver) { SuppressIPv6Answers = () => true };
        var query = DnsMessage.BuildQuery(123, "play.example.test", DnsRecordType.Aaaa, recursionDesired);

        // A cancelled upstream token proves this response needs no network request.
        var response = await proxy.ResolveAsync(query, new CancellationToken(canceled: true));

        Assert.NotNull(response);
        Assert.True(DnsMessage.IsResponseForQuery(query, response));
        Assert.Equal(0, DnsMessage.GetResponseCode(response));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)));
        Assert.Equal(query[2] & 1, response[2] & 1);
        Assert.Empty(DnsMessage.ReadAddresses(response));
    }

    [Fact]
    public async Task DisablingTheRuleImmediatelyAllowsIPv6ResolutionAgain()
    {
        var blocking = true;
        using var resolver = new DohResolver();
        await using var proxy = new DnsProxyServer(resolver) { SuppressIPv6Answers = () => blocking };
        var query = DnsMessage.BuildQuery(123, "play.example.test", DnsRecordType.Aaaa);
        var cancelled = new CancellationToken(canceled: true);

        Assert.NotNull(await proxy.ResolveAsync(query, cancelled));
        blocking = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proxy.ResolveAsync(query, cancelled));
    }

    [Theory]
    [InlineData("play.example.test", DnsRecordType.A)]
    [InlineData("_minecraft._tcp.example.test", DnsRecordType.Srv)]
    public async Task HotspotModeStillForwardsAddressAndMinecraftServiceQueries(string name, ushort type)
    {
        using var resolver = new DohResolver();
        await using var proxy = new DnsProxyServer(resolver) { SuppressIPv6Answers = () => true };
        var query = DnsMessage.BuildQuery(123, name, type);

        // Only upstream work observes this cancellation. A synthetic answer here
        // would hide the IPv4 address or Minecraft's advertised port/target.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            proxy.ResolveAsync(query, new CancellationToken(canceled: true)));
    }
}
