using DpiBypass.Core.Vodafone;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Which outbound IPv6 packets Vodafone Sınırsız Modu may drop.
/// </summary>
/// <remarks>
/// This file exists because of a real failure. The mode used to drop every outbound IPv6
/// packet on the shared adapter, and a phone hands the laptop IPv6 resolvers in its router
/// advertisements. Windows asks those before the IPv4 servers beside them, so every query
/// went into a black hole: ICMP to 1.1.1.1 fine, 28 ms round trip, 0% loss - and not one
/// name resolved. The card said "internet çalışıyor · ad çözülemiyor" and was right.
/// </remarks>
public class HotspotIpv6PolicyTests
{
    private const byte Tcp = 6;
    private const byte Udp = 17;
    private const byte Icmpv6 = 58;

    /// <summary>The traffic the mode exists to hide still goes nowhere.</summary>
    [Theory]
    [InlineData(Tcp, 443)]
    [InlineData(Tcp, 80)]
    [InlineData(Udp, 443)]
    [InlineData(Udp, 3478)]
    public void OrdinaryTrafficToTheInternetIsStillDropped(byte protocol, ushort port)
    {
        var packet = PacketFactory.BuildIPv6(protocol, "2606:4700:4700::1111", destinationPort: port);

        Assert.Equal(Ipv6Disposition.Drop, HotspotIpv6Policy.Decide(packet));
    }

    /// <summary>
    /// Name resolution is forwarded even when it is addressed off-link.
    /// </summary>
    /// <remarks>
    /// The one exemption that can reach the operator, and it is deliberate: a handful of
    /// DNS queries is a far smaller thing to be seen than a connection that resolves
    /// nothing. Both ports, because a machine configured for DNS over TLS asks on 853 and
    /// would otherwise be back in the same black hole.
    /// </remarks>
    [Theory]
    [InlineData(Udp, 53)]
    [InlineData(Tcp, 53)]
    [InlineData(Tcp, 853)]
    [InlineData(Udp, 853)]
    public void NameResolutionIsForwarded(byte protocol, ushort port)
    {
        var packet = PacketFactory.BuildIPv6(protocol, "2001:4860:4860::8888", destinationPort: port);

        Assert.Equal(Ipv6Disposition.Forward, HotspotIpv6Policy.Decide(packet));
    }

    /// <summary>
    /// Anything addressed to the link itself is forwarded, whatever it carries.
    /// </summary>
    /// <remarks>
    /// Multicast and link-local unicast both stop at the phone by definition, so nothing
    /// here can show an operator a second source. Neighbour discovery matters most: without
    /// it Windows never completes the neighbour entry an IPv6 packet needs before it is
    /// sent, and every other exemption on this list becomes unreachable. These are
    /// forwarded untouched rather than rewritten - see
    /// <see cref="TheHopLimitOfNeighbourDiscoveryIsLeftAtTwoFiftyFive"/>.
    /// </remarks>
    [Theory]
    [InlineData(Icmpv6, "ff02::1:ff00:1")]  // neighbour solicitation
    [InlineData(Icmpv6, "ff02::2")]         // router solicitation
    [InlineData(Icmpv6, "fe80::1")]         // neighbour advertisement to the gateway
    [InlineData(Udp, "ff02::1:2")]          // DHCPv6
    [InlineData(Tcp, "fe80::1")]
    public void TheLinkItselfKeepsWorking(byte protocol, string destination)
    {
        var packet = PacketFactory.BuildIPv6(protocol, destination);

        Assert.Equal(Ipv6Disposition.ForwardOnLink, HotspotIpv6Policy.Decide(packet));
    }

    /// <summary>ICMPv6 that would leave the link is dropped like anything else.</summary>
    [Fact]
    public void Icmpv6AddressedOffLinkIsDropped()
    {
        var packet = PacketFactory.BuildIPv6(Icmpv6, "2606:4700:4700::1111");

        Assert.Equal(Ipv6Disposition.Drop, HotspotIpv6Policy.Decide(packet));
    }

    /// <summary>
    /// The exemption is on where a packet is going, not where it came from.
    /// </summary>
    /// <remarks>
    /// A source port of 53 is an answer, not a question, and a machine is not a resolver
    /// for the internet. Matching it would exempt any traffic that happened to be assigned
    /// that ephemeral port.
    /// </remarks>
    [Fact]
    public void AnswersFromPortFiftyThreeAreNotAnExemption()
    {
        var packet = PacketFactory.BuildIPv6(Udp, "2606:4700:4700::1111", sourcePort: 53, destinationPort: 443);

        Assert.Equal(Ipv6Disposition.Drop, HotspotIpv6Policy.Decide(packet));
    }

    /// <summary>
    /// A packet the policy cannot read is forwarded, not dropped.
    /// </summary>
    /// <remarks>
    /// Only packets the kernel filter already matched reach here, so anything unreadable
    /// is truncated or malformed. Dropping what we failed to parse is how a rule
    /// black-holes traffic nobody decided to black-hole.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(39)]
    public void APacketTooShortToClassifyIsForwarded(int length)
    {
        Assert.NotEqual(Ipv6Disposition.Drop, HotspotIpv6Policy.Decide(new byte[length]));
    }

    [Fact]
    public void AnIPv4PacketIsNotThisPolicysBusiness()
    {
        var packet = PacketFactory.BuildIPv4Tcp([1, 2, 3], destinationPort: 443);

        Assert.NotEqual(Ipv6Disposition.Drop, HotspotIpv6Policy.Decide(packet));
    }

    // --- what the rule then does with the packets it keeps ---------------------

    /// <summary>
    /// A DNS query that leaves the phone is rewritten like any other packet.
    /// </summary>
    /// <remarks>
    /// The exemption must not become a tell. A query going out at the routed-once hop
    /// limit while every other packet on the adapter carries 65 would be exactly the
    /// signature the mode exists to remove.
    /// </remarks>
    [Fact]
    public void AForwardedDnsQueryStillLeavesAtTheRewrittenHopLimit()
    {
        using var fix = new HotspotTtlFix();
        var packet = PacketFactory.BuildIPv6(Udp, "2001:4860:4860::8888", destinationPort: 53, hopLimit: 64);

        Assert.True(fix.TryFix(packet, out var change));
        Assert.Equal((7, (byte)64), change);
        Assert.Equal(TtlFixSettings.DefaultTimeToLive, packet[7]);
        Assert.Equal(1, fix.ForwardedIPv6Packets);
        Assert.Equal(0, fix.DroppedIPv6Packets);
    }

    /// <summary>
    /// Neighbour discovery keeps the hop limit that makes it valid.
    /// </summary>
    /// <remarks>
    /// RFC 4861 has the receiver discard a neighbour solicitation, advertisement or router
    /// solicitation whose hop limit is not 255 - that value being the proof no router
    /// forwarded it. Rewriting these to 65 would have the phone drop every one, leaving
    /// the adapter unable to resolve its own next hop and undoing the DNS exemption along
    /// with it. Nothing upstream ever sees these, so there is nothing to disguise.
    /// </remarks>
    [Fact]
    public void TheHopLimitOfNeighbourDiscoveryIsLeftAtTwoFiftyFive()
    {
        using var fix = new HotspotTtlFix();
        var packet = PacketFactory.BuildIPv6(Icmpv6, "ff02::1:ff00:1", hopLimit: 255);

        Assert.True(fix.TryFix(packet, out var change));
        Assert.Null(change);
        Assert.Equal(255, packet[7]);
        Assert.Equal(1, fix.ForwardedIPv6Packets);
    }

    /// <summary>Ordinary IPv6 is dropped, and counted as dropped.</summary>
    [Fact]
    public void OrdinaryIPv6IsDroppedAndCounted()
    {
        using var fix = new HotspotTtlFix();
        var packet = PacketFactory.BuildIPv6(Tcp, "2606:4700:4700::1111", destinationPort: 443);

        Assert.False(fix.TryFix(packet, out var change));
        Assert.Null(change);
        Assert.Equal(1, fix.DroppedIPv6Packets);
        Assert.Equal(0, fix.ForwardedIPv6Packets);
    }

    /// <summary>The IPv6 rule never reaches an IPv4 packet.</summary>
    [Fact]
    public void AnIPv4PacketIsRewrittenAndNeverDropped()
    {
        using var fix = new HotspotTtlFix();
        var packet = PacketFactory.BuildIPv4Tcp([1, 2, 3], destinationPort: 443, ttl: 128);

        Assert.True(fix.TryFix(packet, out var change));
        Assert.Equal((8, (byte)128), change);
        Assert.Equal(0, fix.DroppedIPv6Packets);
        Assert.Equal(0, fix.ForwardedIPv6Packets);
    }

    /// <summary>Both halves of the link-scoped test, against the RFC boundaries.</summary>
    [Theory]
    [InlineData("fe80::1", true)]
    [InlineData("febf:ffff::1", true)]
    [InlineData("ff01::1", true)]   // interface-local multicast
    [InlineData("ff02::1", true)]   // link-local multicast
    [InlineData("ff05::1:3", false)]  // site-scoped multicast: a router may forward it
    [InlineData("ff0e::1", false)]  // global-scope multicast
    [InlineData("fec0::1", false)]  // site-local: deprecated, and routed
    [InlineData("fd00::1", false)]  // unique local: routed within the site
    [InlineData("2606:4700:4700::1111", false)]
    [InlineData("::1", false)]
    public void LinkScopeIsDecidedByThePrefix(string address, bool linkScoped)
    {
        var destination = System.Net.IPAddress.Parse(address).GetAddressBytes();

        Assert.Equal(linkScoped, HotspotIpv6Policy.IsLinkScoped(destination));
    }
}
