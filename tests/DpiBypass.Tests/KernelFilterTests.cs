using DpiBypass.Core.Engine;
using Xunit;

namespace DpiBypass.Tests;

public class KernelFilterTests
{
    private static string NarrowFilter => BypassEngine.TcpFilterLadder[0];

    [Fact]
    public void HandshakeFilterIsPreferredBeforeBroadFallbacks()
    {
        var ladder = BypassEngine.TcpFilterLadder;

        Assert.Equal(3, ladder.Count);
        Assert.Contains("tcp.Payload[", ladder[0], StringComparison.Ordinal);
        Assert.DoesNotContain("tcp.Payload[", ladder[1], StringComparison.Ordinal);
        Assert.DoesNotContain("tcp.Payload[", ladder[2], StringComparison.Ordinal);
        Assert.All(ladder, filter => Assert.StartsWith("outbound and", filter, StringComparison.Ordinal));
    }

    [Fact]
    public void TlsFilterRequiresAClientHelloRecord()
    {
        Assert.Contains("tcp.PayloadLength >= 6", NarrowFilter, StringComparison.Ordinal);
        Assert.Contains("tcp.Payload[0] == 0x16", NarrowFilter, StringComparison.Ordinal);
        Assert.Contains("tcp.Payload[5] == 0x01", NarrowFilter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("47")]
    [InlineData("50")]
    [InlineData("48")]
    [InlineData("44")]
    [InlineData("4F")]
    [InlineData("54")]
    [InlineData("43")]
    public void HttpFilterIncludesEverySupportedMethodInitial(string firstByte)
        => Assert.Contains($"tcp.Payload[0] == 0x{firstByte}", NarrowFilter, StringComparison.Ordinal);

    [Fact]
    public void FilterOnlyDivertsPortsUnderstoodByTheEngine()
    {
        Assert.Contains("tcp.DstPort == 443", NarrowFilter, StringComparison.Ordinal);
        Assert.Contains("tcp.DstPort == 80", NarrowFilter, StringComparison.Ordinal);
    }
}
