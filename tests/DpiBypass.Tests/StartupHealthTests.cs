using System.Net;
using System.Text;
using DpiBypass.Core;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Logging;
using DpiBypass.Core.Net;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The kernel filter decides how much of the machine's traffic has to come through
/// this process at all, so a mismatch between it and the user mode parsers is either
/// a bypass that silently stops working or a filter that diverts far more than it
/// needs to.
/// </summary>
public class KernelFilterTests
{
    private static string NarrowFilter => BypassEngine.TcpFilterLadder[0];

    [Fact]
    public void TheNarrowFilterIsPreferredAndTheBroadOnesRemainAsFallbacks()
    {
        var ladder = BypassEngine.TcpFilterLadder;

        Assert.Equal(3, ladder.Count);

        // The first entry matches on payload content; the fallbacks match every
        // packet that carries payload, which is what the driver gets if it will not
        // take payload indexing.
        Assert.Contains("tcp.Payload[", ladder[0], StringComparison.Ordinal);
        Assert.DoesNotContain("tcp.Payload[", ladder[1], StringComparison.Ordinal);
        Assert.DoesNotContain("tcp.Payload[", ladder[2], StringComparison.Ordinal);
        Assert.All(ladder, filter => Assert.StartsWith("outbound and", filter, StringComparison.Ordinal));
    }

    [Fact]
    public void TheNarrowFilterMatchesTheSameTlsRecordTheParserAccepts()
    {
        // A TLS handshake record whose first handshake byte says ClientHello. These
        // are the two bytes TlsClientHello.IsClientHello looks at, so the filter has
        // to name both or the kernel would drop handshakes the parser would have
        // rewritten.
        Assert.Contains("tcp.Payload[0] == 0x16", NarrowFilter, StringComparison.Ordinal);
        Assert.Contains("tcp.Payload[5] == 0x01", NarrowFilter, StringComparison.Ordinal);

        var hello = new byte[6];
        hello[0] = 0x16;
        hello[5] = 0x01;
        Assert.True(TlsClientHello.IsClientHello(hello));

        // Application data - every byte of an upload - carries 0x17 and must never
        // reach user mode.
        var applicationData = new byte[6];
        applicationData[0] = 0x17;
        Assert.False(TlsClientHello.IsClientHello(applicationData));
    }

    [Fact]
    public void TheNarrowFilterAdmitsEveryHttpMethodTheParserKnows()
    {
        foreach (var method in HttpRequestHead.Methods)
        {
            var first = (byte)method[0];
            Assert.Contains(
                $"tcp.Payload[0] == 0x{first:X2}",
                NarrowFilter,
                StringComparison.Ordinal);

            // And the parser really does accept a request that starts with it, so the
            // two ends of this agree about more than the first letter.
            Assert.True(HttpRequestHead.IsRequest(Encoding.ASCII.GetBytes($"{method}/ HTTP/1.1\r\n")));
        }
    }

    [Fact]
    public void TheNarrowFilterKeepsTheLengthGuardsTheParsersNeed()
    {
        // Payload[5] is only readable when there are six bytes, and IsRequest refuses
        // anything shorter than five. Without these the driver would be indexing past
        // the end of short packets.
        Assert.Contains("tcp.PayloadLength >= 6", NarrowFilter, StringComparison.Ordinal);
        Assert.Contains("tcp.PayloadLength >= 5", NarrowFilter, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheTwoPortsTheEngineUnderstandsAreDiverted()
    {
        Assert.Contains("tcp.DstPort == 443", NarrowFilter, StringComparison.Ordinal);
        Assert.Contains("tcp.DstPort == 80", NarrowFilter, StringComparison.Ordinal);
    }
}

public class DnsPlanTests
{
    [Theory]
    [InlineData("APPLIED=3", 3)]
    [InlineData("noise\r\nAPPLIED=2\r\nmore noise", 2)]
    [InlineData("APPLIED=0", 0)]
    public void TheAppliedCountIsReadOutOfWhateverElseTheScriptPrinted(string output, int expected)
        => Assert.Equal(expected, DnsConfigurator.ReadApplied(output));

    [Theory]
    [InlineData("")]
    [InlineData("Set-DnsClientServerAddress : access denied")]
    [InlineData("APPLIED=")]
    [InlineData("APPLIED=lots")]
    public void UnreadableOutputCountsAsNothingApplied(string output)
        => Assert.Equal(0, DnsConfigurator.ReadApplied(output));
}

public class PlainDnsFallbackTests
{
    [Fact]
    public void PublicResolversComeBeforeWhateverTheMachineWasUsing()
    {
        var client = new PlainDnsClient();
        client.UseAsLastResort(["192.168.1.1"]);

        var servers = client.Servers;

        Assert.Equal(IPAddress.Parse("1.1.1.1"), servers[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), servers[^1]);
    }

    [Fact]
    public void OurOwnLoopbackIsNeverUsedAsAnUpstream()
    {
        var client = new PlainDnsClient();
        client.UseAsLastResort(["127.0.0.1", "::1", "192.168.1.1"]);

        // Pointing the fallback at the proxy would be the proxy asking itself, which
        // is the dead loop the whole fallback exists to avoid.
        Assert.DoesNotContain(IPAddress.Loopback, client.Servers);
        Assert.DoesNotContain(IPAddress.IPv6Loopback, client.Servers);
        Assert.Contains(IPAddress.Parse("192.168.1.1"), client.Servers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("8.8.8.8")]
    public void RubbishAndDuplicatesAreDropped(string server)
    {
        var client = new PlainDnsClient();
        var before = client.Servers.Count;

        client.UseAsLastResort([server]);

        Assert.Equal(before, client.Servers.Count);
    }
}

public class DohChainTests
{
    [Fact]
    public void AFreshResolverKeepsTheConfiguredProviderOrder()
    {
        using var resolver = new DohResolver();

        Assert.Equal(DohResolver.DefaultChain, resolver.OrderedChain());
    }

    [Fact]
    public void TheChainLeadsWithCloudflareAndCarriesASecondAddressForEachProvider()
    {
        var chain = DohResolver.DefaultChain;

        Assert.Equal(DohResolver.Cloudflare, chain[0]);

        // Two addresses per provider, so losing one address is not losing a provider.
        Assert.Equal(3, chain.Select(endpoint => endpoint.Provider).Distinct().Count());
        Assert.Equal(6, chain.Count);
        Assert.Equal(chain.Count, chain.Select(endpoint => endpoint.Url).Distinct().Count());
    }

    [Fact]
    public void EveryEndpointIsAddressedByIpLiteralSoThereIsNoSniToFilterOn()
    {
        foreach (var endpoint in DohResolver.DefaultChain)
        {
            var host = new Uri(endpoint.Url).Host.Trim('[', ']');

            Assert.True(IPAddress.TryParse(host, out var parsed), $"{endpoint.Url} is not an IP literal");
            Assert.Equal(endpoint.PlainAddress, parsed);
        }
    }
}

public class AppLogWriterTests
{
    [Fact]
    public void LinesReachTheFileEvenThoughTheCallerNeverTouchesIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dpibypass-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            // The writer is a background thread, so a line is on disk only once the
            // log has been flushed - which is what Shutdown is for and why the app
            // calls it on every exit path.
            AppLog.Initialise(directory);
            AppLog.Info("kernel filter narrowed");
            AppLog.Warning("dns fell back to plain udp");
            AppLog.Shutdown();

            var written = Directory.EnumerateFiles(directory, "dpibypass-*.log")
                .Select(File.ReadAllText)
                .ToList();

            Assert.Single(written);
            Assert.Contains("kernel filter narrowed", written[0], StringComparison.Ordinal);
            Assert.Contains("dns fell back to plain udp", written[0], StringComparison.Ordinal);
        }
        finally
        {
            AppLog.ResetForTesting();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // The temp folder is not the point of the test.
            }
        }
    }

    [Fact]
    public void ABurstIsBoundedRatherThanQueuedWithoutLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dpibypass-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            AppLog.Initialise(directory);

            // A worker faulting in a loop must cost lines, never memory.
            for (var i = 0; i < 20_000; i++)
            {
                AppLog.Info($"line {i}");
            }

            Assert.True(AppLog.PendingLines <= AppLog.PendingCapacity);
            AppLog.Shutdown();
        }
        finally
        {
            AppLog.ResetForTesting();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Ditto.
            }
        }
    }
}
