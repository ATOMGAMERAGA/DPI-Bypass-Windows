namespace DpiBypass.Core.Network;

/// <summary>
/// The statistics the latency work is judged by, kept apart from the measuring and
/// from the deciding so each can be tested on its own.
/// </summary>
/// <remarks>
/// Everything here is order statistics rather than an average: a single 300 ms outlier
/// moves a mean far enough to invent - or hide - a result, and the whole point of this
/// subsystem is that a reported improvement is one that actually happened.
/// </remarks>
public static class LatencyStatistics
{
    /// <summary>Linear interpolation between the two ranks the percentile falls between.</summary>
    /// <param name="ordered">Samples already sorted ascending.</param>
    public static double PercentileOfSorted(IReadOnlyList<double> ordered, double percentile)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentOutOfRangeException.ThrowIfNegative(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 1);

        if (ordered.Count == 0)
        {
            return 0;
        }

        if (ordered.Count == 1)
        {
            return ordered[0];
        }

        var position = (ordered.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        return lower == upper
            ? ordered[lower]
            : ordered[lower] + ((ordered[upper] - ordered[lower]) * (position - lower));
    }

    public static double Percentile(IEnumerable<double> samples, double percentile)
        => PercentileOfSorted([.. samples.Order()], percentile);

    public static double Median(IEnumerable<double> samples) => Percentile(samples, 0.50);

    /// <summary>
    /// Mean absolute difference between consecutive samples: the delay variation a
    /// real-time stream actually has to absorb.
    /// </summary>
    /// <remarks>
    /// Deliberately not the standard deviation. A link that alternates 20/60/20/60 ms
    /// and one that runs at 20 ms then steps to 60 ms have a similar spread but behave
    /// completely differently for anything that buffers, and the successive difference
    /// is what separates them. Samples must be in the order they were collected.
    /// </remarks>
    public static double DelayVariation(IReadOnlyList<double> samplesInOrder)
    {
        ArgumentNullException.ThrowIfNull(samplesInOrder);

        if (samplesInOrder.Count < 2)
        {
            return 0;
        }

        var total = 0d;
        for (var index = 1; index < samplesInOrder.Count; index++)
        {
            total += Math.Abs(samplesInOrder[index] - samplesInOrder[index - 1]);
        }

        return total / (samplesInOrder.Count - 1);
    }

    public static double Mean(IEnumerable<double> values)
    {
        var count = 0;
        var total = 0d;

        foreach (var value in values)
        {
            total += value;
            count++;
        }

        return count == 0 ? 0 : total / count;
    }

    /// <summary>Sample standard deviation; zero for fewer than two values.</summary>
    public static double StandardDeviation(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < 2)
        {
            return 0;
        }

        var mean = Mean(values);
        var sum = 0d;

        foreach (var value in values)
        {
            var difference = value - mean;
            sum += difference * difference;
        }

        return Math.Sqrt(sum / (values.Count - 1));
    }

    /// <summary>
    /// Loss as a percentage of what was sent. No probes sent means nothing was
    /// measured, which is reported as total loss rather than as a perfect link.
    /// </summary>
    public static double PacketLossPercent(int sent, int received)
    {
        if (sent <= 0)
        {
            return 100;
        }

        return Math.Clamp((sent - Math.Clamp(received, 0, sent)) * 100d / sent, 0, 100);
    }

    /// <summary>How much one lost probe is worth, in percentage points.</summary>
    public static double OneProbeWorth(int sent) => sent <= 0 ? 100 : 100d / sent;
}
