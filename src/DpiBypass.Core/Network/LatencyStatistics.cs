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
    /// Robust spread around the median, scaled to be comparable with standard deviation
    /// for normally distributed observations.
    /// </summary>
    /// <remarks>
    /// Candidate benchmarks only have a handful of paired cycles. Squaring one outlying
    /// cycle lets it dominate an ordinary standard deviation; median absolute deviation
    /// instead asks how far a typical cycle sits from the typical result.
    /// </remarks>
    public static double MedianAbsoluteDeviation(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < 2)
        {
            return 0;
        }

        var median = Median(values);
        var absoluteDeviations = values.Select(value => Math.Abs(value - median));

        return Median(absoluteDeviations) * 1.4826;
    }

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

    /// <summary>
    /// A confidence interval for the mean paired difference, by resampling the pairs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handful of A/B cycles is far too few to assume any particular distribution, and
    /// the thing being estimated - "how much better is the candidate, typically" - is a
    /// mean of paired differences whose spread is exactly what the resampling measures.
    /// If the lower bound of that interval sits at or below zero, the cycles have not
    /// ruled out "no difference", whatever the point estimate looks like.
    /// </para>
    /// <para>
    /// Seeded on purpose. A verdict that changes when the same numbers are evaluated
    /// twice is not a verdict, and a test cannot pin one down.
    /// </para>
    /// </remarks>
    public static (double Lower, double Upper) PairedMeanInterval(
        IReadOnlyList<double> differences,
        double confidenceLevel = 0.90,
        int iterations = 2000,
        int seed = 0x5EED)
    {
        ArgumentNullException.ThrowIfNull(differences);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(confidenceLevel, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(confidenceLevel, 1);

        if (differences.Count == 0)
        {
            return (0, 0);
        }

        if (differences.Count == 1)
        {
            // One pair cannot bound anything. Reporting the value as its own interval
            // would claim a precision that does not exist, so the interval is opened to
            // include zero and the caller's lower-bound test fails, as it should.
            return (Math.Min(0, differences[0]), Math.Max(0, differences[0]));
        }

        var random = new Random(seed);
        var means = new double[Math.Max(200, iterations)];

        for (var iteration = 0; iteration < means.Length; iteration++)
        {
            var total = 0d;
            for (var draw = 0; draw < differences.Count; draw++)
            {
                total += differences[random.Next(differences.Count)];
            }

            means[iteration] = total / differences.Count;
        }

        Array.Sort(means);
        var tail = (1 - confidenceLevel) / 2;

        return (PercentileOfSorted(means, tail), PercentileOfSorted(means, 1 - tail));
    }

    /// <summary>
    /// The paired sign-flip permutation p-value for "the candidate changed nothing".
    /// </summary>
    /// <remarks>
    /// Under the null hypothesis the sign of each paired difference is arbitrary, so the
    /// distribution of the mean under every possible relabelling is the reference. Exact
    /// while the number of pairs is small enough to enumerate, sampled - deterministically
    /// - beyond that. Reported rather than used as the sole gate: with the few cycles a
    /// user will sit through, the smallest attainable p-value is often still large.
    /// </remarks>
    public static double PairedSignFlipPValue(
        IReadOnlyList<double> differences,
        int iterations = 4096,
        int seed = 0x5EED)
    {
        ArgumentNullException.ThrowIfNull(differences);

        if (differences.Count == 0)
        {
            return 1;
        }

        var observed = Math.Abs(Mean(differences));
        var atLeastAsExtreme = 0;
        var total = 0;

        if (differences.Count <= 14)
        {
            var combinations = 1 << differences.Count;
            for (var mask = 0; mask < combinations; mask++)
            {
                var sum = 0d;
                for (var index = 0; index < differences.Count; index++)
                {
                    sum += (mask & (1 << index)) == 0 ? differences[index] : -differences[index];
                }

                total++;
                if (Math.Abs(sum / differences.Count) >= observed - 1e-12)
                {
                    atLeastAsExtreme++;
                }
            }
        }
        else
        {
            var random = new Random(seed);
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var sum = 0d;
                foreach (var difference in differences)
                {
                    sum += random.Next(2) == 0 ? difference : -difference;
                }

                total++;
                if (Math.Abs(sum / differences.Count) >= observed - 1e-12)
                {
                    atLeastAsExtreme++;
                }
            }
        }

        return total == 0 ? 1 : (double)atLeastAsExtreme / total;
    }
}
