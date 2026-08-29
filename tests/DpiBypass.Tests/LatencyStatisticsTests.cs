using DpiBypass.Core.Network;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>The arithmetic every latency verdict is built on.</summary>
public sealed class LatencyStatisticsTests
{
    [Fact]
    public void MedianOfAnEvenCountInterpolatesBetweenTheMiddlePair()
        => Assert.Equal(13, LatencyStatistics.Median([10, 12, 14, 16]));

    [Fact]
    public void MedianOfAnOddCountIsTheMiddleSample()
        => Assert.Equal(12, LatencyStatistics.Median([10, 12, 16]));

    [Fact]
    public void PercentilesInterpolateBetweenRanks()
    {
        double[] samples = [10, 12, 14, 16];

        Assert.Equal(15.7, LatencyStatistics.Percentile(samples, 0.95), precision: 1);
        Assert.Equal(15.94, LatencyStatistics.Percentile(samples, 0.99), precision: 2);
    }

    [Fact]
    public void PercentilesOfATwentyFourProbeBatchSeparateTheTailFromTheMaximum()
    {
        // Twenty-three well behaved probes and one 200 ms outlier: a p95 that simply
        // reported the maximum would call this link unusable.
        var samples = Enumerable.Repeat(20d, 23).Append(200d).ToArray();

        Assert.Equal(20, LatencyStatistics.Median(samples));
        Assert.Equal(20, LatencyStatistics.Percentile(samples, 0.95), precision: 6);
        Assert.True(LatencyStatistics.Percentile(samples, 0.99) > 20);
        Assert.True(LatencyStatistics.Percentile(samples, 0.99) < 200);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void PercentilesOfASingleSampleAreThatSample(double percentile)
        => Assert.Equal(7, LatencyStatistics.Percentile([7], percentile));

    [Fact]
    public void PercentilesOfNothingAreZeroRatherThanAnException()
        => Assert.Equal(0, LatencyStatistics.Percentile([], 0.95));

    [Fact]
    public void PercentileRejectsAFractionOutsideTheUnitInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LatencyStatistics.Percentile([1, 2], 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => LatencyStatistics.Percentile([1, 2], -0.1));
    }

    [Fact]
    public void DelayVariationIsTheMeanStepBetweenConsecutiveProbes()
        => Assert.Equal(2, LatencyStatistics.DelayVariation([10, 12, 14, 16]));

    /// <summary>
    /// Two series with the same spread but very different behaviour: one alternates every
    /// probe, the other steps once. Only the successive difference tells them apart, and
    /// the alternating one is what a voice or game stream actually has to buffer for.
    /// </summary>
    [Fact]
    public void DelayVariationSeparatesAnAlternatingLinkFromAOneStepChange()
    {
        double[] alternating = [20, 60, 20, 60, 20, 60];
        double[] stepped = [20, 20, 20, 60, 60, 60];

        Assert.Equal(
            LatencyStatistics.StandardDeviation(alternating),
            LatencyStatistics.StandardDeviation(stepped),
            precision: 6);

        Assert.Equal(40, LatencyStatistics.DelayVariation(alternating));
        Assert.Equal(8, LatencyStatistics.DelayVariation(stepped));
    }

    [Fact]
    public void DelayVariationOfFewerThanTwoProbesIsZero()
    {
        Assert.Equal(0, LatencyStatistics.DelayVariation([]));
        Assert.Equal(0, LatencyStatistics.DelayVariation([12]));
    }

    [Theory]
    [InlineData(24, 24, 0)]
    [InlineData(24, 23, 4.1666)]
    [InlineData(24, 0, 100)]
    [InlineData(0, 0, 100)]
    public void LossIsAShareOfWhatWasSent(int sent, int received, double expected)
        => Assert.Equal(expected, LatencyStatistics.PacketLossPercent(sent, received), precision: 3);

    /// <summary>More replies than probes is a bug somewhere; it must not read as negative loss.</summary>
    [Fact]
    public void MoreRepliesThanProbesIsClampedRatherThanNegative()
        => Assert.Equal(0, LatencyStatistics.PacketLossPercent(10, 12));

    [Fact]
    public void OneLostProbeIsWorthAKnownShareOfTheBatch()
    {
        Assert.Equal(4.1666, LatencyStatistics.OneProbeWorth(24), precision: 3);
        Assert.Equal(100, LatencyStatistics.OneProbeWorth(0));
    }

    [Fact]
    public void StandardDeviationOfFewerThanTwoValuesIsZero()
    {
        Assert.Equal(0, LatencyStatistics.StandardDeviation([]));
        Assert.Equal(0, LatencyStatistics.StandardDeviation([4]));
    }

    [Fact]
    public void MeasurementDerivesEveryHeadlineNumberFromTheSamples()
    {
        var measurement = LatencyMeasurement.Create(
            "1.1.1.1",
            "ICMP",
            [10, 12, 14, 16],
            remoteAttempts: 5,
            gatewaySamples: [1, 2, 3],
            gatewayAttempts: 3);

        Assert.Equal(10, measurement.MinimumRttMs);
        Assert.Equal(13, measurement.MedianRttMs);
        Assert.Equal(15.7, measurement.P95RttMs, precision: 1);
        Assert.Equal(15.94, measurement.P99RttMs, precision: 2);
        Assert.Equal(2, measurement.JitterMs);
        Assert.Equal(20, measurement.PacketLossPercent);
        Assert.Equal(2, measurement.GatewayMedianRttMs);
        Assert.Equal(LatencyLoadState.Unknown, measurement.Load.State);
    }

    [Fact]
    public void AMeasurementWithNoRepliesIsTotalLossRatherThanZeroMilliseconds()
    {
        var measurement = LatencyMeasurement.Create("1.1.1.1", "ICMP", [], 24, [], 8);

        Assert.False(measurement.HasRemoteConnectivity);
        Assert.Equal(100, measurement.PacketLossPercent);
        Assert.Equal(0, measurement.MedianRttMs);
    }
}
