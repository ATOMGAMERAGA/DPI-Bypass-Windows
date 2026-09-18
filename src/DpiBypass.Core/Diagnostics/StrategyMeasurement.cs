using DpiBypass.Core.Network;

namespace DpiBypass.Core.Diagnostics;

/// <summary>How a sample was taken, so nothing downstream can present one as another.</summary>
public enum ProbeMethod
{
    /// <summary>A TCP connect, which is the closest thing here to a round trip time.</summary>
    TcpConnect = 0,

    /// <summary>A full TLS handshake with the hostname in SNI. Several round trips plus crypto.</summary>
    TlsHandshake = 1,

    /// <summary>An HTTP request over an established connection.</summary>
    HttpRequest = 2,
}

/// <summary>One host this sweep has to reach, and whether reaching it is optional.</summary>
/// <param name="Host">The hostname, which is also what goes in SNI.</param>
/// <param name="Required">
/// A candidate that cannot reach a required host is not a candidate, however fast it is on
/// the others. "One target answered" is not "the network is open".
/// </param>
/// <param name="Purpose">What a user would recognise this host as, for the rationale text.</param>
public sealed record ProbeTarget(string Host, bool Required, string Purpose);

/// <summary>The hosts a sweep measures against.</summary>
/// <remarks>
/// Small, controlled and representative rather than one host or a long list. One host
/// cannot tell "this recipe works" from "this host happens to be reachable", and a long
/// list turns every sweep into minutes of traffic. Two required hosts covering the two
/// things Discord actually needs - the web/API endpoint and the gateway the voice and
/// event channel runs over - plus the CDN as a third, optional, signal.
/// </remarks>
public static class StrategyTargets
{
    public static IReadOnlyList<ProbeTarget> Default { get; } =
    [
        new("discord.com", Required: true, "Discord web ve API"),
        new("gateway.discord.gg", Required: true, "Discord ses ve olay kanalı"),
        new("cdn.discordapp.com", Required: false, "Discord içerik dağıtımı"),
    ];

    public static int RequiredCount(IReadOnlyList<ProbeTarget> targets)
        => targets.Count(target => target.Required);
}

/// <summary>One probe, with its parts kept apart.</summary>
/// <param name="Host">Which target.</param>
/// <param name="Method">How it was measured.</param>
/// <param name="Success">Whether the handshake completed and the certificate validated.</param>
/// <param name="Outcome">What happened, for the failure breakdown.</param>
/// <param name="Dns">Name resolution, or null when it was served from cache or not measured.</param>
/// <param name="Connect">
/// TCP connect. One round trip plus the server's accept, and the nearest thing this
/// instrument has to an RTT - which is still not a game's ping and is never labelled one.
/// </param>
/// <param name="Handshake">
/// The TLS handshake after the connect: several more round trips and the certificate work.
/// Reported separately precisely so it cannot be shown as a round trip time.
/// </param>
public readonly record struct ProbeSample(
    string Host,
    ProbeMethod Method,
    bool Success,
    ProbeOutcome Outcome,
    TimeSpan? Dns,
    TimeSpan? Connect,
    TimeSpan? Handshake);

/// <summary>What a load test found, when the user asked for one.</summary>
/// <param name="DownlinkMbps">Sustained download over the measured window.</param>
/// <param name="UplinkMbps">Sustained upload, or null when only download was measured.</param>
/// <param name="LoadedLatencyIncreaseMs">
/// How much the round trip grew while the link was busy: the queueing delay, which is what
/// most of what people call lag actually is.
/// </param>
/// <param name="Bytes">How much data it moved, so the cost is reportable rather than hidden.</param>
/// <param name="Duration">How long it ran for.</param>
public sealed record ThroughputResult(
    double DownlinkMbps,
    double? UplinkMbps,
    double? LoadedLatencyIncreaseMs,
    long Bytes,
    TimeSpan Duration);

/// <summary>
/// Everything one candidate profile measured, with each quantity in its own field.
/// </summary>
/// <remarks>
/// The fields are separate because conflating them is the way this kind of screen lies.
/// A TLS handshake time is not a round trip time; a round trip time is not a game's ping;
/// a failed HTTP request is not a lost packet; and a download that happened to be fast
/// once is not a line speed. Anything that was not measured is null, and null is rendered
/// as "Ölçülmedi" rather than as a zero.
/// </remarks>
public sealed record StrategyMeasurement
{
    public required string StrategyId { get; init; }

    public required string StrategyName { get; init; }

    /// <summary>Every probe, in the order it was taken.</summary>
    public IReadOnlyList<ProbeSample> Samples { get; init; } = [];

    /// <summary>The load test's result, or null when none was run.</summary>
    public ThroughputResult? Throughput { get; init; }

    /// <summary>When the last sample was taken.</summary>
    public DateTimeOffset MeasuredAt { get; init; } = DateTimeOffset.UtcNow;

    public int Attempts => Samples.Count;

    public int Successes => Samples.Count(sample => sample.Success);

    /// <summary>
    /// The share of probes that did not complete, 0-1.
    /// </summary>
    /// <remarks>
    /// A failure rate, not a packet loss rate. A refused TLS handshake is a filter acting
    /// on the connection, not a packet dropped on the wire, and calling it loss would
    /// invent a network problem that is not there.
    /// </remarks>
    public double FailureRate => Attempts == 0 ? 1d : 1d - ((double)Successes / Attempts);

    /// <summary>Which required hosts this candidate actually reached.</summary>
    public IReadOnlyCollection<string> HostsReached => Samples
        .Where(sample => sample.Success)
        .Select(sample => sample.Host)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>The median TCP connect time over the successful probes, in ms.</summary>
    /// <remarks>
    /// Median rather than mean: one retransmit doubles a mean and barely moves a median,
    /// and the question being asked is what a typical connection costs.
    /// </remarks>
    public double? MedianConnectMs => Median(sample => sample.Connect);

    /// <summary>The median TLS handshake time over the successful probes, in ms.</summary>
    public double? MedianHandshakeMs => Median(sample => sample.Handshake);

    /// <summary>The median name resolution time, when it was measured.</summary>
    public double? MedianDnsMs => Median(sample => sample.Dns);

    /// <summary>
    /// Variation between consecutive connect times, in ms.
    /// </summary>
    /// <remarks>
    /// Null below three successful samples. Two points describe a line, not a
    /// distribution, and a jitter figure from them is a number with no information in it.
    /// </remarks>
    public double? JitterMs
    {
        get
        {
            var series = Samples
                .Where(sample => sample.Success && sample.Connect is not null)
                .Select(sample => sample.Connect!.Value.TotalMilliseconds)
                .ToArray();

            return series.Length >= 3 ? LatencyStatistics.DelayVariation(series) : null;
        }
    }

    /// <summary>The spread of the connect times, for reporting how settled the answer is.</summary>
    public double? ConnectSpreadMs
    {
        get
        {
            var series = Samples
                .Where(sample => sample.Success && sample.Connect is not null)
                .Select(sample => sample.Connect!.Value.TotalMilliseconds)
                .ToArray();

            return series.Length >= 3 ? LatencyStatistics.MedianAbsoluteDeviation(series) : null;
        }
    }

    /// <summary>Whether every required host answered at least once.</summary>
    public bool ReachedEveryRequiredTarget(IReadOnlyList<ProbeTarget> targets)
    {
        var reached = HostsReached;

        return targets
            .Where(target => target.Required)
            .All(target => reached.Contains(target.Host, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The required hosts this candidate never reached, for the rationale.</summary>
    public IReadOnlyList<string> MissingRequiredTargets(IReadOnlyList<ProbeTarget> targets)
    {
        var reached = HostsReached;

        return targets
            .Where(target => target.Required && !reached.Contains(target.Host, StringComparer.OrdinalIgnoreCase))
            .Select(target => target.Host)
            .ToArray();
    }

    private double? Median(Func<ProbeSample, TimeSpan?> select)
    {
        var values = Samples
            .Where(sample => sample.Success)
            .Select(select)
            .Where(value => value is not null)
            .Select(value => value!.Value.TotalMilliseconds)
            .ToArray();

        return values.Length == 0 ? null : LatencyStatistics.Median(values);
    }
}
