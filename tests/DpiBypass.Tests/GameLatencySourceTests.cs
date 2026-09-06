using System.Net;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

public sealed class GameLatencySourceTests
{
    [Theory]
    [InlineData("Network RTT: 27 ms", 27d)]
    [InlineData(" 104 ", 104d)]
    [InlineData("no value", null)]
    [InlineData("9999", null)]
    public void ValorantOcrParserAcceptsOnlyPlausibleNumericRtt(string text, double? expected)
        => Assert.Equal(expected, ValorantHudLatencySource.ParseRtt(text));

    [Fact]
    public async Task GameDisplayNeedsAtLeastEightyPercentValidFramesAndNeverInventsLoss()
    {
        var source = new StubGameSource(new GameLatencySeries
        {
            Instrument = "VALORANT Network RTT (ekran)",
            FramesRead = 5,
            Samples = [20, 21, 22, 23],
        });
        var probe = new GameAwareLatencyProbe(new StubProbe(), source, new EmptyLoadSampler());

        var measurement = await probe.MeasureAsync(
            Fake.Network("valorant"),
            new LatencyProbeRequest { ProbeCount = 5, WarmupCount = 0 }.For(ValorantEndpoint()));

        Assert.Equal(LatencySampleSource.GameDisplay, measurement.Source);
        Assert.Equal(0.8, measurement.ValidSampleShare);
        Assert.Equal(4, measurement.RemoteReplies);
        Assert.Equal(0, measurement.RemoteAttempts);
        Assert.Null(measurement.PacketLossPercent);
        Assert.True(measurement.HasRemoteConnectivity);

        source.Series = source.Series with { Samples = [20, 21, 22] };
        measurement = await probe.MeasureAsync(
            Fake.Network("valorant-low-share"),
            new LatencyProbeRequest { ProbeCount = 5, WarmupCount = 0 }.For(ValorantEndpoint()));

        Assert.False(measurement.HasRemoteConnectivity);
        Assert.Equal(0.6, measurement.ValidSampleShare);
        Assert.Null(measurement.PacketLossPercent);
    }

    [Fact]
    public void AnOpenMinecraftJavaConnectionUsesItsExistingTcpEStatsTuple()
    {
        var local = new IPEndPoint(IPAddress.Parse("192.0.2.4"), 50123);
        var remote = new IPEndPoint(IPAddress.Parse("203.0.113.18"), 25565);

        var candidate = Assert.Single(GameEndpointDiscovery.Rank(
            "javaw",
            new HashSet<uint> { 42 },
            [],
            [new ProcessTcpConnection(local, remote)],
            DateTimeOffset.UtcNow));

        Assert.Equal(LatencyProtocol.TcpEStats, candidate.Endpoint.Protocol);
        Assert.Equal(local, candidate.Endpoint.LocalEndpoint);
        Assert.True(candidate.Endpoint.MeasuresApplicationRoundTrip);
    }

    private static LatencyEndpoint ValorantEndpoint() => new()
    {
        Address = IPAddress.Parse("203.0.113.10"),
        Port = 7000,
        Protocol = LatencyProtocol.Icmp,
        ApplicationProtocol = LatencyProtocol.Udp,
        Kind = LatencyTargetKind.Application,
        Label = "VALORANT-Win64-Shipping → 203.0.113.10:7000",
        RouteReferenceOnly = true,
    };

    private sealed class StubGameSource(GameLatencySeries series) : IGameLatencySource
    {
        public GameLatencySeries Series { get; set; } = series;

        public bool CanMeasure(LatencyEndpoint endpoint) => true;

        public Task<GameLatencySeries> MeasureAsync(
            LatencyEndpoint endpoint,
            LatencyProbeRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Series);
    }

    private sealed class StubProbe : ILatencyProbe
    {
        public Task<LatencyMeasurement> MeasureAsync(
            NetworkFingerprint network,
            LatencyProbeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Fake.Measurement(99));

        public Task<LatencyConnectivity> CheckConnectivityAsync(
            NetworkFingerprint network,
            string remoteEndpoint,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LatencyConnectivity(true, true));
    }

    private sealed class EmptyLoadSampler : INetworkLoadSampler
    {
        public NetworkCounters? Read(NetworkFingerprint network) => null;
    }
}
