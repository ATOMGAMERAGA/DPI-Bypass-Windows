using System.Net;
using System.Net.NetworkInformation;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Interop;
using Xunit;

namespace DpiBypass.Tests;

public sealed class DnsEnumerationTests
{
    [Fact]
    public async Task CompleteNativeSnapshotNeedsNoPowerShellAndPreservesServerOrder()
    {
        var adapter = new Adapter("Wi-Fi", 7, 9,
            ["1.1.1.1", "127.0.0.1", "fe80::1%9", "::1", "9.9.9.9", "2606:4700:4700::1111"]);
        var snapshots = await new DnsConfigurator("unused").EnumerateAsync([adapter], UnexpectedProbe);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("Wi-Fi-id", snapshot.Id);
        Assert.Equal(7, snapshot.InterfaceIndexV4);
        Assert.Equal(9, snapshot.InterfaceIndexV6);
        Assert.Equal(["1.1.1.1", "9.9.9.9"], snapshot.OriginalV4);
        Assert.Equal(["fe80::1%9", "2606:4700:4700::1111"], snapshot.OriginalV6);
    }

    [Fact]
    public async Task DisabledIpv6DoesNotForceAShellProbe()
    {
        var snapshots = await new DnsConfigurator("unused").EnumerateAsync(
            [new Adapter("Ethernet", 3, 0, ["8.8.8.8"])], UnexpectedProbe);
        Assert.Equal(0, Assert.Single(snapshots).InterfaceIndexV6);
    }

    [Fact]
    public async Task NoActiveAdaptersNeedsNoPowerShell()
    {
        var snapshots = await new DnsConfigurator("unused").EnumerateAsync(
            [new Adapter("Down", 2, 2, []) { Status = OperationalStatus.Down },
             new Adapter("Loopback", 1, 1, []) { Type = NetworkInterfaceType.Loopback },
             new Adapter("Tunnel", 4, 4, []) { Type = NetworkInterfaceType.Tunnel }], UnexpectedProbe);
        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task NativeReadFailureUsesOneFallbackAndRetainsOtherAdapters()
    {
        var calls = 0;
        var snapshots = await new DnsConfigurator("unused").EnumerateAsync(
            [new Adapter("Good", 2, 2, ["9.9.9.9"]),
             new Adapter("Fallback", 7, 7, []) { FailRead = true },
             new Adapter("Fallback2", 8, 8, []) { FailRead = true }],
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new ProcessResult(0, """
                    [{"Index":7,"Alias":"Fallback","Family":"v4","Servers":["8.8.8.8"]},
                     {"Index":7,"Alias":"Fallback","Family":"v6","Servers":["2001:4860:4860::8888"]},
                     {"Index":8,"Alias":"Fallback2","Family":"v4","Servers":["1.1.1.1"]}]
                    """, ""));
            });

        Assert.Equal(1, calls);
        Assert.Equal(3, snapshots.Count);
        Assert.Equal(["9.9.9.9"], snapshots[0].OriginalV4);
        Assert.Equal(["8.8.8.8"], snapshots[1].OriginalV4);
        Assert.Equal(["2001:4860:4860::8888"], snapshots[1].OriginalV6);
        Assert.Equal(["1.1.1.1"], snapshots[2].OriginalV4);
    }

    [Fact]
    public async Task CancellationDoesNotStartAProbe()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DnsConfigurator("unused")
            .EnumerateAsync([new Adapter("Wi-Fi", 7, 7, [])], UnexpectedProbe, new CancellationToken(true)));
    }

    private static Task<ProcessResult> UnexpectedProbe(string script, CancellationToken token)
        => throw new Xunit.Sdk.XunitException("A complete native read must not launch PowerShell.");

    private sealed class Adapter(string name, int v4, int v6, string[] servers) : NetworkInterface
    {
        public bool FailRead { get; init; }
        public OperationalStatus Status { get; init; } = OperationalStatus.Up;
        public NetworkInterfaceType Type { get; init; } = NetworkInterfaceType.Ethernet;
        public override string Id => name + "-id";
        public override string Name => name;
        public override string Description => name;
        public override bool IsReceiveOnly => false;
        public override bool SupportsMulticast => true;
        public override long Speed => 1_000_000_000;
        public override OperationalStatus OperationalStatus => Status;
        public override NetworkInterfaceType NetworkInterfaceType => Type;
        public override bool Supports(NetworkInterfaceComponent component)
            => component == NetworkInterfaceComponent.IPv4 ? v4 > 0 : v6 > 0;
        public override IPInterfaceProperties GetIPProperties()
            => FailRead ? throw new NetworkInformationException() : new Properties(v4, v6, servers);
        public override IPv4InterfaceStatistics GetIPv4Statistics() => throw new NotSupportedException();
        public override PhysicalAddress GetPhysicalAddress() => PhysicalAddress.None;
    }

    private sealed class Properties(int v4, int v6, string[] servers) : IPInterfaceProperties
    {
        public override IPAddressCollection DnsAddresses => new Addresses(servers);
        public override IPv4InterfaceProperties GetIPv4Properties() => new V4(v4);
        public override IPv6InterfaceProperties GetIPv6Properties() => new V6(v6);
        public override bool IsDnsEnabled => true;
        public override bool IsDynamicDnsEnabled => true;
        public override string DnsSuffix => "";
        public override IPAddressInformationCollection AnycastAddresses => throw new NotSupportedException();
        public override IPAddressCollection DhcpServerAddresses => throw new NotSupportedException();
        public override GatewayIPAddressInformationCollection GatewayAddresses => throw new NotSupportedException();
        public override MulticastIPAddressInformationCollection MulticastAddresses => throw new NotSupportedException();
        public override UnicastIPAddressInformationCollection UnicastAddresses => throw new NotSupportedException();
        public override IPAddressCollection WinsServersAddresses => throw new NotSupportedException();
    }

    private sealed class Addresses(string[] addresses) : IPAddressCollection
    {
        public override IEnumerator<IPAddress> GetEnumerator() => addresses.Select(IPAddress.Parse).GetEnumerator();
    }

    private sealed class V4(int index) : IPv4InterfaceProperties
    {
        public override int Index => index;
        public override int Mtu => 1500;
        public override bool IsAutomaticPrivateAddressingActive => false;
        public override bool IsAutomaticPrivateAddressingEnabled => false;
        public override bool IsDhcpEnabled => true;
        public override bool IsForwardingEnabled => false;
        public override bool UsesWins => false;
    }

    private sealed class V6(int index) : IPv6InterfaceProperties
    {
        public override int Index => index;
        public override int Mtu => 1500;
    }
}
