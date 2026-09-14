using DpiBypass.Core.Net;

namespace DpiBypass.Core.Vodafone;

/// <summary>What the hotspot rule does with one outbound IPv6 packet.</summary>
internal enum Ipv6Disposition
{
    /// <summary>Drop it: on the wire it would show the operator a second source.</summary>
    Drop = 0,

    /// <summary>
    /// Forward it, with its hop limit rewritten like any other packet that leaves the phone.
    /// </summary>
    Forward = 1,

    /// <summary>
    /// Forward it exactly as it arrived.
    /// </summary>
    /// <remarks>
    /// Link-scoped traffic never reaches the operator, so there is nothing to disguise -
    /// and rewriting it would do real damage. RFC 4861 has receivers discard a neighbour
    /// solicitation, advertisement or router solicitation whose hop limit is not still
    /// 255, that being the proof it was never forwarded. Rewriting those to 65 would make
    /// the phone drop every one of them and leave the adapter unable to resolve its own
    /// next hop, which is the plumbing this exemption exists to protect.
    /// </remarks>
    ForwardOnLink = 2,
}

/// <summary>
/// Which outbound IPv6 packets <see cref="HotspotTtlFix"/> may drop, and which it must not.
/// </summary>
/// <remarks>
/// <para>
/// Dropping every outbound IPv6 packet looked like the safe reading of "the operator must
/// see one source", and it is how this started. It is not safe. A phone hands the laptop
/// IPv6 resolvers in its router advertisements, Windows prefers those resolvers over the
/// IPv4 ones on the same adapter, and a query sent into a black hole is never answered and
/// never refused - so the machine keeps full IPv4 connectivity and cannot resolve a single
/// name. That is the "internet works, DNS does not" failure this class exists to prevent.
/// </para>
/// <para>
/// The distinction that actually serves the goal is not "IPv6 or not" but <b>does this
/// packet leave the phone</b>. Only a packet that reaches the operator's network can show
/// them a second source address, so two kinds are forwarded:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Link-scoped traffic</b> - anything addressed to <c>fe80::/10</c> or to a multicast
/// group. Neighbour discovery, router solicitation, MLD and DHCPv6 all live here. They
/// stop at the phone by definition, so they leak nothing at all, and without neighbour
/// discovery Windows cannot even build the entry an IPv6 packet needs before it is sent -
/// which would make every other exemption pointless.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Name resolution</b> - UDP or TCP to port 53 or 853. This is the one exemption that
/// can reach the operator, and it is deliberate: a handful of DNS queries is a far smaller
/// thing to be seen than a connection that resolves no names at all. The app avoids even
/// that where it can, by not installing IPv6 resolvers of its own while this rule is up.
/// </description>
/// </item>
/// </list>
/// <para>
/// Everything else - the ordinary traffic that would genuinely give the tethering away -
/// is still dropped. A DNS query that leaves the phone has its hop limit rewritten like any
/// other packet, so the exemption does not become a tell of its own; link-scoped traffic is
/// forwarded untouched, because nobody upstream sees it and neighbour discovery is defined
/// to be discarded unless its hop limit arrives at 255.
/// </para>
/// </remarks>
internal static class HotspotIpv6Policy
{
    /// <summary>Plain DNS.</summary>
    internal const ushort DnsPort = 53;

    /// <summary>DNS over TLS, which Windows and Android both use when it is configured.</summary>
    internal const ushort DnsOverTlsPort = 853;

    private const int HeaderLength = 40;
    private const int DestinationOffset = 24;

    /// <summary>Multicast scope 1: never leaves the machine.</summary>
    private const int InterfaceLocalScope = 1;

    /// <summary>Multicast scope 2: never leaves the link. Neighbour discovery lives here.</summary>
    private const int LinkLocalScope = 2;

    /// <summary>Whether an outbound IPv6 packet may be dropped by the hotspot rule.</summary>
    internal static Ipv6Disposition Decide(ReadOnlySpan<byte> packet)
    {
        // Not an IPv6 packet we can read. Forwarding is the conservative answer: the
        // caller only asks about packets the kernel filter already matched as IPv6, so
        // anything unreadable here is a malformed or truncated capture, and dropping
        // something we could not parse is how a rule black-holes traffic it never meant
        // to touch.
        if (packet.Length < HeaderLength || (packet[0] >> 4) != 6)
        {
            return Ipv6Disposition.ForwardOnLink;
        }

        if (IsLinkScoped(packet.Slice(DestinationOffset, 16)))
        {
            return Ipv6Disposition.ForwardOnLink;
        }

        return IsNameResolution(packet) ? Ipv6Disposition.Forward : Ipv6Disposition.Drop;
    }

    /// <summary>
    /// Whether the destination is one the phone answers itself rather than forwards.
    /// </summary>
    /// <remarks>
    /// Link-local unicast (<c>fe80::/10</c>) is defined to stop at the first router.
    /// Multicast only stops there at the two scopes that say so - interface-local and
    /// link-local - which is where neighbour discovery, MLD, DHCPv6 and LLMNR all live; a
    /// wider scope can be forwarded by a multicast router, so it is treated as leaving and
    /// dropped with everything else. Nothing a Windows host sends in anger falls in the
    /// gap, and reading the scope rather than the <c>ff</c> is the difference between a
    /// rule that is exactly right and one that is nearly right.
    /// </remarks>
    internal static bool IsLinkScoped(ReadOnlySpan<byte> destination)
    {
        if (destination.Length < 16)
        {
            return false;
        }

        if (destination[0] == 0xFF)
        {
            var scope = destination[1] & 0x0F;
            return scope is InterfaceLocalScope or LinkLocalScope;
        }

        return destination[0] == 0xFE && (destination[1] & 0xC0) == 0x80;
    }

    /// <summary>Whether the packet is a DNS or DNS-over-TLS query leaving this machine.</summary>
    internal static bool IsNameResolution(ReadOnlySpan<byte> packet)
        => IsQueryTo(packet, TcpIpPacket.ProtocolUdp) || IsQueryTo(packet, TcpIpPacket.ProtocolTcp);

    private static bool IsQueryTo(ReadOnlySpan<byte> packet, byte protocol)
    {
        if (!TcpIpPacket.TryLocateTransport(packet, protocol, out var isIPv6, out var transport, out var usable)
            || !isIPv6
            || transport + 4 > usable)
        {
            return false;
        }

        var port = (ushort)((packet[transport + 2] << 8) | packet[transport + 3]);
        return port is DnsPort or DnsOverTlsPort;
    }
}
