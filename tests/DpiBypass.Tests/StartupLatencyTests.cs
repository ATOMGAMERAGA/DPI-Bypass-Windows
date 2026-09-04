using DpiBypass.Core.Dns;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The wait between pressing "Korumayı başlat" and protection being on.
/// </summary>
/// <remarks>
/// The user-visible defect was a status pill that read "Başlatılıyor…" for the better
/// part of a minute on an ordinary laptop. None of that minute was spent doing anything
/// to the network: every adapter's DNS change was its own <c>powershell.exe</c>, and so
/// was the cache flush, so a machine with Wi-Fi, Ethernet and a couple of virtual
/// adapters paid for seven or eight cold PowerShell starts one after another. These
/// tests pin the batching that removed them.
/// </remarks>
public sealed class StartupLatencyTests
{
    [Fact]
    public void EveryAdapterChangeGoesIntoOneInvocation()
    {
        var script = DnsConfigurator.BuildWriteScript(
            [
                new DnsConfigurator.DnsWrite(5, ["127.0.0.1"]),
                new DnsConfigurator.DnsWrite(6, ["::1"]),
                new DnsConfigurator.DnsWrite(11, null),
            ],
            flushCache: true);

        Assert.Contains("-InterfaceIndex 5 -ServerAddresses ('127.0.0.1')", script, StringComparison.Ordinal);
        Assert.Contains("-InterfaceIndex 6 -ServerAddresses ('::1')", script, StringComparison.Ordinal);
        Assert.Contains("-InterfaceIndex 11 -ResetServerAddresses", script, StringComparison.Ordinal);

        // The flush rides along rather than starting a process of its own.
        Assert.Contains("Clear-DnsClientCache", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// One adapter refusing must not be reported as the whole machine refusing.
    /// </summary>
    /// <remarks>
    /// Each operation reports itself by ordinal, so the caller can still tell which
    /// adapter failed - which is what decides whether a snapshot row is kept for a
    /// later repair or dropped as done.
    /// </remarks>
    [Fact]
    public void EachChangeReportsItsOwnOutcome()
    {
        var script = DnsConfigurator.BuildWriteScript(
            [
                new DnsConfigurator.DnsWrite(5, ["127.0.0.1"]),
                new DnsConfigurator.DnsWrite(6, ["::1"]),
            ],
            flushCache: false);

        Assert.Contains("'dns 0 1'", script, StringComparison.Ordinal);
        Assert.Contains("'dns 0 0'", script, StringComparison.Ordinal);
        Assert.Contains("'dns 1 1'", script, StringComparison.Ordinal);
        Assert.Contains("'dns 1 0'", script, StringComparison.Ordinal);

        // A refusal is caught per operation; the rest of the batch still runs.
        Assert.Contains("catch", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear-DnsClientCache", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Addresses come off disk, so they are parsed before they reach a command line.
    /// </summary>
    /// <remarks>
    /// The snapshot is an ordinary JSON file in the state directory. A corrupted or
    /// hand-edited one has always been able to cost a restore; it must never be able to
    /// become PowerShell in a process running as administrator.
    /// </remarks>
    [Fact]
    public void OnlyAddressesThatParseReachTheCommandLine()
    {
        var script = DnsConfigurator.BuildWriteScript(
            [new DnsConfigurator.DnsWrite(5, ["1.1.1.1", "'; Stop-Computer #", "9.9.9.9"])],
            flushCache: false);

        Assert.Contains("('1.1.1.1','9.9.9.9')", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Computer", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// A batch with nothing in it still flushes, and asks for no PowerShell otherwise.
    /// </summary>
    [Fact]
    public async Task AnEmptyBatchIsNotAProcessLaunch()
    {
        var outcome = await DnsConfigurator.ApplyWritesAsync([], flushCache: false, CancellationToken.None);

        Assert.Empty(outcome);
    }
}
