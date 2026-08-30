using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Working out which part of the path the delay is in, so the app can say what a NIC
/// setting could possibly move and what it could not.
/// </summary>
public sealed class LatencyPathTests
{
    [Fact]
    public void AFastFirstHopAndASlowFarEndIsTheInternetPathNotTheAdapter()
    {
        var path = LatencyPathAnalysis.Describe(Fake.Measurement(72, gateway: 1.1));

        Assert.Equal(LatencyBottleneck.WanRoute, path.Bottleneck);
        Assert.False(path.LocallyImprovable);
        Assert.Equal(1.1, path.LocalLinkMs);
        Assert.Equal(70.9, path.RemotePathMs!.Value, precision: 1);
    }

    [Fact]
    public void ASlowFirstHopIsTheLocalLink()
    {
        var path = LatencyPathAnalysis.Describe(Fake.Measurement(30, gateway: 18));

        Assert.Equal(LatencyBottleneck.LocalLink, path.Bottleneck);
        Assert.True(path.LocallyImprovable);
    }

    [Fact]
    public void AFirstHopCarryingMostOfTheTotalIsTheLocalLinkEvenWhenItIsSmall()
    {
        var path = LatencyPathAnalysis.Describe(Fake.Measurement(10, gateway: 6));

        Assert.Equal(LatencyBottleneck.LocalLink, path.Bottleneck);
    }

    [Fact]
    public void AnOrdinaryHomeLinkIsAttributedToTheAccessLink()
    {
        var path = LatencyPathAnalysis.Describe(Fake.Measurement(18, gateway: 4));

        Assert.Equal(LatencyBottleneck.AccessLink, path.Bottleneck);
        Assert.True(path.LocallyImprovable);
    }

    /// <summary>
    /// Delay that only shows up while this machine is sending or receiving is queueing,
    /// and no adapter property fixes that - which the summary has to say rather than
    /// letting the optimizer chase it.
    /// </summary>
    [Fact]
    public void LatencyThatOnlyAppearsUnderLoadIsReportedAsQueueing()
    {
        var idle = Fake.Measurement(24, gateway: 2, load: LatencyLoadState.Idle);
        var loaded = Fake.Measurement(140, gateway: 2, load: LatencyLoadState.UplinkLoaded);

        var path = LatencyPathAnalysis.Describe(idle, loaded);

        Assert.Equal(LatencyBottleneck.LocalQueueing, path.Bottleneck);
        Assert.Equal(116, path.QueueingMs);
        Assert.False(path.LocallyImprovable);
        Assert.Contains("kuyruklanma", path.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ASmallDifferenceUnderLoadIsNotCalledBufferbloat()
    {
        var idle = Fake.Measurement(24, gateway: 2, load: LatencyLoadState.Idle);
        var loaded = Fake.Measurement(30, gateway: 2, load: LatencyLoadState.DownlinkLoaded);

        Assert.NotEqual(LatencyBottleneck.LocalQueueing, LatencyPathAnalysis.Describe(idle, loaded).Bottleneck);
    }

    /// <summary>Two windows that were equally busy say nothing about queueing.</summary>
    [Fact]
    public void QueueingIsOnlyClaimedFromAnIdleWindowAgainstALoadedOne()
    {
        var first = Fake.Measurement(24, load: LatencyLoadState.UplinkLoaded);
        var second = Fake.Measurement(140, load: LatencyLoadState.UplinkLoaded);

        Assert.Null(LatencyPathAnalysis.Describe(first, second).QueueingMs);
    }

    [Fact]
    public void AGatewayThatDoesNotAnswerLeavesThePathUnsplit()
    {
        var measurement = LatencyMeasurement.Create("1.1.1.1", "ICMP", [20, 21, 22], 3, [], 8);
        var path = LatencyPathAnalysis.Describe(measurement);

        Assert.Equal(LatencyBottleneck.Unknown, path.Bottleneck);
        Assert.Null(path.LocalLinkMs);
        Assert.False(path.LocallyImprovable);
        Assert.Contains("varsayılmaz", path.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRemoteRepliesMeansNothingIsClaimedAtAll()
    {
        var measurement = LatencyMeasurement.Create("1.1.1.1", "ICMP", [], 24, [1, 1, 1], 8);
        var path = LatencyPathAnalysis.Describe(measurement);

        Assert.Equal(LatencyBottleneck.Unknown, path.Bottleneck);
        Assert.False(path.LocallyImprovable);
    }
}

/// <summary>Classifying how busy the link was, from the adapter's own byte counters.</summary>
public sealed class NetworkLoadTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AQuietLinkIsIdle()
    {
        var sample = Sample(sentBytes: 2_000, receivedBytes: 8_000, seconds: 2);

        Assert.Equal(LatencyLoadState.Idle, sample.State);
        Assert.False(sample.IsLoaded);
    }

    [Fact]
    public void AnUploadIsSeenOnTheUplinkOnly()
    {
        var sample = Sample(sentBytes: 4_000_000, receivedBytes: 8_000, seconds: 2);

        Assert.Equal(LatencyLoadState.UplinkLoaded, sample.State);
        Assert.True(sample.UplinkKbps > NetworkLoadSample.LoadedKbps);
    }

    [Fact]
    public void ADownloadIsSeenOnTheDownlinkOnly()
        => Assert.Equal(LatencyLoadState.DownlinkLoaded, Sample(8_000, 9_000_000, 2).State);

    [Fact]
    public void TrafficInBothDirectionsIsReportedAsBoth()
        => Assert.Equal(LatencyLoadState.BidirectionalLoaded, Sample(4_000_000, 9_000_000, 2).State);

    [Fact]
    public void AWindowWithNoReadingsIsUnknownRatherThanIdle()
    {
        Assert.Equal(LatencyLoadState.Unknown, NetworkLoadSample.Between(null, null).State);
        Assert.Equal(
            LatencyLoadState.Unknown,
            NetworkLoadSample.Between(new NetworkCounters(0, 0, Start), null).State);
    }

    /// <summary>A driver that resets its counters must not read as gigabits of traffic.</summary>
    [Fact]
    public void CountersGoingBackwardsAreUnknownRatherThanEnormous()
    {
        var sample = NetworkLoadSample.Between(
            new NetworkCounters(9_000_000, 9_000_000, Start),
            new NetworkCounters(10, 10, Start + TimeSpan.FromSeconds(2)));

        Assert.Equal(LatencyLoadState.Unknown, sample.State);
    }

    [Fact]
    public void AWindowTooShortToDivideByIsUnknown()
    {
        var sample = NetworkLoadSample.Between(
            new NetworkCounters(0, 0, Start),
            new NetworkCounters(1_000_000, 0, Start + TimeSpan.FromMilliseconds(10)));

        Assert.Equal(LatencyLoadState.Unknown, sample.State);
    }

    [Fact]
    public void IdleWindowsAreComparableButUnknownIsNotEvidence()
    {
        var idle = Fake.Load(LatencyLoadState.Idle);
        var busy = Fake.Load(LatencyLoadState.DownlinkLoaded);

        Assert.True(idle.ComparableWith(idle));
        Assert.False(idle.ComparableWith(busy));

        Assert.False(NetworkLoadSample.Unknown.ComparableWith(busy));
        Assert.False(busy.ComparableWith(NetworkLoadSample.Unknown));
        Assert.False(NetworkLoadSample.Unknown.ComparableWith(NetworkLoadSample.Unknown));
    }

    [Theory]
    [InlineData(LatencyLoadState.UplinkLoaded, 5_000, 40, 5_500, 60, true)]
    [InlineData(LatencyLoadState.UplinkLoaded, 300, 40, 50_000, 60, false)]
    [InlineData(LatencyLoadState.DownlinkLoaded, 40, 8_000, 60, 9_500, true)]
    [InlineData(LatencyLoadState.DownlinkLoaded, 40, 300, 60, 80_000, false)]
    public void SameDirectionLoadAlsoRequiresSimilarMagnitude(
        LatencyLoadState state,
        double firstUp,
        double firstDown,
        double secondUp,
        double secondDown,
        bool expected)
    {
        var first = Load(state, firstUp, firstDown);
        var second = Load(state, secondUp, secondDown);

        Assert.Equal(expected, first.ComparableWith(second));
        Assert.Equal(expected, second.ComparableWith(first));
    }

    [Fact]
    public void BidirectionalLoadRequiresBothDirectionsToHaveSimilarMagnitude()
    {
        var baseline = Load(LatencyLoadState.BidirectionalLoaded, 5_000, 20_000);

        Assert.True(baseline.ComparableWith(Load(LatencyLoadState.BidirectionalLoaded, 5_500, 18_000)));
        Assert.False(baseline.ComparableWith(Load(LatencyLoadState.BidirectionalLoaded, 5_500, 80_000)));
        Assert.False(baseline.ComparableWith(Load(LatencyLoadState.BidirectionalLoaded, 30_000, 18_000)));
    }

    private static NetworkLoadSample Sample(long sentBytes, long receivedBytes, double seconds)
        => NetworkLoadSample.Between(
            new NetworkCounters(1_000, 2_000, Start),
            new NetworkCounters(1_000 + sentBytes, 2_000 + receivedBytes, Start + TimeSpan.FromSeconds(seconds)));

    private static NetworkLoadSample Load(LatencyLoadState state, double uplink, double downlink) => new()
    {
        State = state,
        UplinkKbps = uplink,
        DownlinkKbps = downlink,
    };
}
