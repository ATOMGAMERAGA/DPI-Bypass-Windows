using System.Net;
using DpiBypass.Core.Dns;
using DpiBypass.Core.MobileHotspot;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The failure this whole area exists to prevent: full IPv4 connectivity, no name resolution.
/// </summary>
/// <remarks>
/// A phone advertises IPv6 resolvers in its router advertisements and Windows asks those
/// before the IPv4 servers beside them. So anything that makes an adapter's IPv6 unusable -
/// the hotspot rule dropping it, or the app writing public IPv6 resolvers onto an adapter
/// whose IPv6 does not carry traffic - takes name resolution down with it while ICMP,
/// routing and every IPv4 path keep working perfectly. It reads on the card as
/// "İnternet erişimi: Çalışıyor · Ad çözümleme: Ad çözülemiyor", which is exactly true and
/// exactly useless unless the app can say why.
/// </remarks>
public sealed class HotspotDnsBlackHoleTests
{
    // --- the app must not install resolvers it is itself dropping --------------

    /// <summary>
    /// Loopback is not on the shared adapter, so it is reachable whatever the rule does.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLoopbackProxyStaysTheIpv6ResolverWhicheverWayTheRuleIsSet(bool ipv6Blocked)
    {
        var servers = DnsConfigurator.ChooseIpv6Servers(
            DnsMode.EncryptedLoopback, loopbackHasIPv6: true, ipv6Blocked);

        Assert.Equal(["::1"], servers);
    }

    /// <summary>
    /// The regression itself: no public IPv6 resolver while outbound IPv6 is being dropped.
    /// </summary>
    /// <remarks>
    /// Writing these pointed Windows at addresses this same process was black-holing. The
    /// IPv4 servers on the same adapter are the ones that answer, so nothing is written
    /// for the family at all - which also leaves the user's own IPv6 servers untouched for
    /// the restore to find.
    /// </remarks>
    [Theory]
    [InlineData(DnsMode.EncryptedLoopback)]
    [InlineData(DnsMode.PublicResolvers)]
    public void NoIpv6ResolverIsInstalledWhileOutboundIpv6IsDropped(DnsMode mode)
    {
        Assert.Null(DnsConfigurator.ChooseIpv6Servers(mode, loopbackHasIPv6: false, ipv6Blocked: true));
    }

    /// <summary>With IPv6 passing, the public resolvers go on as they always did.</summary>
    [Theory]
    [InlineData(DnsMode.EncryptedLoopback)]
    [InlineData(DnsMode.PublicResolvers)]
    public void ThePublicResolversAreStillInstalledWhenIpv6Works(DnsMode mode)
    {
        var servers = DnsConfigurator.ChooseIpv6Servers(mode, loopbackHasIPv6: false, ipv6Blocked: false);

        Assert.Equal(DnsConfigurator.PublicV6, servers);
    }

    // --- and the card has to be able to name the fault -------------------------

    /// <summary>
    /// "DNS is broken" and "this machine is asking the wrong server" are different faults.
    /// </summary>
    [Fact]
    public void AResolverConfigurationFaultIsNotReportedAsABrokenNetwork()
    {
        var findings = MobileHotspotDiagnostics.Findings(
            Working() with { DnsWorks = false, SystemResolverFaulty = true });

        Assert.Contains(findings, finding => finding.Contains("DNS ayarlarında", StringComparison.Ordinal));
    }

    /// <summary>The specific shape, named, with the remedy that matches it.</summary>
    [Fact]
    public void AnIpv6ResolverOnALinkWithoutIpv6IsNamedAsTheCause()
    {
        var result = Working() with
        {
            DnsWorks = false,
            SystemResolverFaulty = true,
            HasIpv6 = true,
            Ipv6Works = false,
            HasIpv6Dns = true,
        };

        Assert.Contains(
            MobileHotspotDiagnostics.Findings(result),
            finding => finding.Contains("IPv6 DNS sunucusu", StringComparison.Ordinal));

        var remediation = MobileHotspotDiagnostics.Remediation(result)!;
        Assert.Contains("IPv6 DNS sunucularını", remediation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A machine that simply has no internet is still told to fix that first.
    /// </summary>
    /// <remarks>
    /// The resolver advice is worse than useless on a dead link: it sends someone editing
    /// adapter settings to explain a cable.
    /// </remarks>
    [Fact]
    public void TheResolverAdviceDoesNotDisplaceADeadLink()
    {
        var result = Working() with
        {
            Ipv4Works = false,
            Ipv6Works = false,
            DnsWorks = false,
            HasIpv6Dns = true,
        };

        Assert.DoesNotContain("IPv6 DNS", MobileHotspotDiagnostics.Remediation(result)!, StringComparison.Ordinal);
    }

    /// <summary>Without an IPv6 resolver in play the old advice is still the right advice.</summary>
    [Fact]
    public void AnOrdinaryDnsFailureKeepsTheOriginalAdvice()
    {
        var result = Working() with { DnsWorks = false, HasIpv6Dns = false };

        Assert.Contains("sistem varsayılanına", MobileHotspotDiagnostics.Remediation(result)!, StringComparison.Ordinal);
    }

    /// <summary>The report says which of the two DNS faults it is looking at.</summary>
    [Fact]
    public void TheReportDistinguishesTheTwoDnsFaults()
    {
        var broken = (Working() with { DnsWorks = false }).ToReport();
        var misconfigured = (Working() with
        {
            DnsWorks = false,
            SystemResolverFaulty = true,
            HasIpv6Dns = true,
        }).ToReport();

        Assert.Contains("ad çözülemiyor", broken, StringComparison.Ordinal);
        Assert.Contains("ağ çözüyor", misconfigured, StringComparison.Ordinal);
    }

    // --- which resolvers the direct probe asks ---------------------------------

    /// <summary>
    /// The network's own servers first: that is what "can this network resolve" means.
    /// </summary>
    [Fact]
    public void TheAdaptersOwnIpv4ResolversAreAskedFirst()
    {
        var servers = MobileHotspotDiagnostics.DirectResolvers(
            [IPAddress.Parse("192.168.43.1"), IPAddress.Parse("8.8.8.8")]);

        Assert.Equal([IPAddress.Parse("192.168.43.1"), IPAddress.Parse("8.8.8.8")], servers);
    }

    /// <summary>
    /// Our own loopback proxy is skipped: asking it answers a different question.
    /// </summary>
    /// <remarks>
    /// The probe runs precisely when the system path failed, and on this machine the
    /// system path may well <i>be</i> that proxy. A public resolver finishes the list so
    /// the check still produces an answer.
    /// </remarks>
    [Fact]
    public void OurOwnLoopbackProxyIsNotTreatedAsTheNetwork()
    {
        var servers = MobileHotspotDiagnostics.DirectResolvers([IPAddress.Loopback]);

        Assert.Equal([IPAddress.Parse("1.1.1.1")], servers);
    }

    /// <summary>
    /// IPv6 servers are never asked: the link's IPv6 is the suspect, not the witness.
    /// </summary>
    [Fact]
    public void TheDirectProbeNeverAsksOverIpv6()
    {
        var servers = MobileHotspotDiagnostics.DirectResolvers(
            [IPAddress.Parse("2606:4700:4700::1111"), IPAddress.Parse("192.168.43.1")]);

        Assert.Equal(
            [IPAddress.Parse("192.168.43.1"), IPAddress.Parse("1.1.1.1")],
            servers);
    }

    /// <summary>The probe is bounded: a black hole must not become a hung card.</summary>
    [Fact]
    public void TheDirectProbeStopsAfterASmallNumberOfServers()
    {
        var servers = MobileHotspotDiagnostics.DirectResolvers(
        [
            IPAddress.Parse("192.168.43.1"),
            IPAddress.Parse("192.168.43.2"),
            IPAddress.Parse("192.168.43.3"),
            IPAddress.Parse("192.168.43.4"),
        ]);

        Assert.Equal(2, servers.Count);
    }

    // --- and the wiring that keeps the two in step ----------------------------

    /// <summary>
    /// The resolver choice is redone whenever the rule's IPv6 state changes.
    /// </summary>
    /// <remarks>
    /// Pinned against the source because the alternative needs a WinDivert driver, an
    /// elevated process and a phone. The failure it guards is silent and total: the rule
    /// goes up on a network change, the IPv6 resolvers written at start are now being
    /// dropped, and nothing resolves until the user restarts the application.
    /// </remarks>
    [Fact]
    public void TheServiceRewritesItsResolversWhenTheRuleChangesTheIpv6State()
    {
        var service = File.ReadAllText(RepoFiles.Find("src", "DpiBypass.Core", "ProtectionService.cs"));

        // One definition of "IPv6 is being dropped", used by the proxy's AAAA suppression,
        // by the resolver choice and by the card.
        Assert.Contains("internal bool VodafoneDropsIPv6", service, StringComparison.Ordinal);
        Assert.Contains("SuppressIPv6Answers = () => VodafoneDropsIPv6", service, StringComparison.Ordinal);

        // The flag reaches the configurator, and the rule going up or down re-runs it.
        Assert.Contains("ApplyAsync(mode, loopbackHasIPv6, ipv6Blocked", service, StringComparison.Ordinal);
        Assert.Contains("ReapplyDnsForIpv6Async", service, StringComparison.Ordinal);
        Assert.Contains("private void RefreshVodafoneDns()", service, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule installs itself before the resolvers are chosen, on every path.
    /// </summary>
    /// <remarks>
    /// <c>ApplyVodafoneMode</c> calls <c>RefreshVodafoneDns</c> after both the apply and
    /// the clear, which is what makes the resolver choice a consequence of the rule rather
    /// than a snapshot of whatever was true when protection started.
    /// </remarks>
    [Fact]
    public void BothSidesOfTheRuleRefreshTheResolvers()
    {
        var service = File.ReadAllText(RepoFiles.Find("src", "DpiBypass.Core", "ProtectionService.cs"));
        var apply = service.IndexOf("private void ApplyVodafoneMode()", StringComparison.Ordinal);

        Assert.True(apply >= 0, "ApplyVodafoneMode is where the rule goes up and down");

        var body = service[apply..service.IndexOf("EnableVodafoneModeHere", apply, StringComparison.Ordinal)];
        var refreshes = body.Split("RefreshVodafoneDns();").Length - 1;

        Assert.True(refreshes >= 2, $"expected the clear and the apply paths to refresh; found {refreshes}");
    }

    private static HotspotDiagnosticResult Working() => new()
    {
        NetworkName = "Galaxy S24 Ultra 4383",
        AdapterName = "Wi-Fi",
        HasIpv4 = true,
        HasIpv6 = false,
        Ipv4Works = true,
        Ipv6Works = false,
        DnsWorks = true,
        MedianRttMs = 28,
        P95RttMs = 30,
        PacketLossPercent = 0,
        AddressKind = HotspotAddressKind.Private,
        VpnAdapterActive = false,
    };
}
