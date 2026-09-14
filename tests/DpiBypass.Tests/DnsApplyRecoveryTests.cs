using System.Text.Json;
using DpiBypass.Core.Dns;
using Xunit;

namespace DpiBypass.Tests;

public sealed class DnsApplyRecoveryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task IncompleteWritesFailAndKeepTheOriginalsForRecovery(bool v4Accepted, bool v6Accepted)
    {
        using var directory = new TempDirectory();
        var adapter = new AdapterDnsSnapshot("wifi", "Wi-Fi", 17, 17, ["192.168.43.1"], ["fe80::1%17"]);
        var configurator = new DnsConfigurator(directory.File("state"),
            _ => Task.FromResult<IReadOnlyList<AdapterDnsSnapshot>>([adapter]),
            (_, _, _) => Task.FromResult(new[] { v4Accepted, v6Accepted }));

        Assert.False(await configurator.ApplyAsync(DnsMode.EncryptedLoopback, false, true));
        Assert.True(configurator.HasPendingRestore);
        var saved = JsonSerializer.Deserialize<AdapterDnsSnapshot[]>(
            File.ReadAllText(directory.File("state/dns-snapshot.json")))!;
        Assert.Equal(adapter.OriginalV4, saved[0].OriginalV4);
        Assert.Equal(adapter.OriginalV6, saved[0].OriginalV6);
    }

    [Fact]
    public async Task ReapplyingOnTheSameAdapterClearsNewIpv6DnsAndKeepsTheFirstOriginals()
    {
        using var directory = new TempDirectory();
        var adapter = new AdapterDnsSnapshot("wifi", "Wi-Fi", 17, 17, ["192.168.43.1"], ["fe80::1%17"]);
        var batches = new List<DnsConfigurator.DnsWrite[]>();
        var configurator = new DnsConfigurator(directory.File("state"),
            _ => Task.FromResult<IReadOnlyList<AdapterDnsSnapshot>>([adapter]),
            (writes, _, _) =>
            {
                batches.Add(writes.ToArray());
                return Task.FromResult(writes.Select(_ => true).ToArray());
            });
        Assert.True(await configurator.ApplyAsync(DnsMode.EncryptedLoopback, false, true));
        adapter = adapter with { OriginalV4 = ["127.0.0.1"], OriginalV6 = ["2606:4700:4700::1111"] };
        Assert.True(await configurator.ApplyAsync(DnsMode.EncryptedLoopback, false, true));

        Assert.Equal(2, batches.Count);
        Assert.All(batches, batch => Assert.Empty(batch[1].Servers!));
        var saved = JsonSerializer.Deserialize<AdapterDnsSnapshot[]>(
            File.ReadAllText(directory.File("state/dns-snapshot.json")))!;
        Assert.Equal(["192.168.43.1"], saved[0].OriginalV4);
        Assert.Equal(["fe80::1%17"], saved[0].OriginalV6);
    }

    [Fact]
    public async Task AnAdapterWithoutIpv6DoesNotRequireAnIpv6Write()
    {
        using var directory = new TempDirectory();
        var adapter = new AdapterDnsSnapshot("wifi", "Wi-Fi", 17, 0, ["192.168.43.1"], []);
        var configurator = new DnsConfigurator(directory.File("state"),
            _ => Task.FromResult<IReadOnlyList<AdapterDnsSnapshot>>([adapter]),
            (writes, _, _) =>
            {
                Assert.Equal(["127.0.0.1"], Assert.Single(writes).Servers!);
                return Task.FromResult(new[] { true });
            });
        Assert.True(await configurator.ApplyAsync(DnsMode.EncryptedLoopback, false, true));
    }
}
