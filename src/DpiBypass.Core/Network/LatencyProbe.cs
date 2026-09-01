using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Network;

public sealed record LatencyConnectivity(bool GatewayReachable, bool RemoteReachable)
{
    public bool IsUsable => GatewayReachable || RemoteReachable;
}

/// <summary>How one measurement should be taken.</summary>
/// <remarks>
/// Both halves of an A/B pair must use the same request, or the comparison is between
/// two different experiments rather than between two adapter settings. In the language
/// of RFC 2681 this is the Type-P: protocol, port, packet size and treatment all belong
/// to the metric, not to the measurement.
/// </remarks>
public sealed record LatencyProbeRequest
{
    /// <summary>The one target to probe. Null lets the probe pick the best of its list.</summary>
    public string? RemoteEndpoint { get; init; }

    /// <summary>
    /// The pinned endpoint, when one has been resolved.
    /// </summary>
    /// <remarks>
    /// Preferred over <see cref="RemoteEndpoint"/> because it carries the protocol and
    /// port as well as the address, and because it was resolved once at the start of the
    /// experiment rather than looked up again per measurement.
    /// </remarks>
    public LatencyEndpoint? Endpoint { get; init; }

    public int ProbeCount { get; init; } = 40;

    /// <summary>
    /// Probes sent and thrown away before the ones that count.
    /// </summary>
    /// <remarks>
    /// The first packets after a driver setting is written, or after any pause, are not
    /// representative: an ARP entry may have expired, a radio may be ramping, a queue may
    /// still be draining. Including them measures the transition rather than the state.
    /// </remarks>
    public int WarmupCount { get; init; } = 3;

    public int GatewayProbeCount { get; init; } = 8;

    /// <summary>Gap between consecutive probes in the same series.</summary>
    public TimeSpan Pacing { get; init; } = TimeSpan.FromMilliseconds(45);

    /// <summary>
    /// How long a reply may take before the probe counts as lost rather than slow.
    /// </summary>
    /// <remarks>
    /// RFC 2681 requires this to be reported: "the threshold (or methodology to
    /// distinguish) between a large finite delay and loss MUST be reported". It appears
    /// in the measurement report for exactly that reason.
    /// </remarks>
    public int TimeoutMilliseconds { get; init; } = 900;

    /// <summary>The short pass that picks a target and shows the user a first number.</summary>
    public static readonly LatencyProbeRequest Survey = new()
    {
        ProbeCount = 9,
        WarmupCount = 1,
        GatewayProbeCount = 3,
        Pacing = TimeSpan.FromMilliseconds(40),
    };

    /// <summary>
    /// The pass a verdict is allowed to rest on.
    /// </summary>
    /// <remarks>
    /// Forty replies is the smallest batch where the p95 is an order statistic rather
    /// than "the second worst sample", and where one lost probe moves the loss figure by
    /// 2.5 percent instead of 4. It is still far too few for a p99, which is why a
    /// tail-only claim needs the deep pass.
    /// </remarks>
    public static readonly LatencyProbeRequest Benchmark = new();

    /// <summary>
    /// The pass a p99 claim is allowed to rest on.
    /// </summary>
    /// <remarks>
    /// A p99 estimated from fewer than a hundred replies is the maximum sample wearing a
    /// percentile's name. This is the only request that produces enough of them, and the
    /// evaluator will not use p99 as a decision metric without it.
    /// </remarks>
    public static readonly LatencyProbeRequest Deep = new()
    {
        ProbeCount = 120,
        WarmupCount = 5,
        GatewayProbeCount = 12,
    };

    public LatencyProbeRequest For(string? endpoint) => this with { RemoteEndpoint = endpoint, Endpoint = null };

    public LatencyProbeRequest For(LatencyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return this with { RemoteEndpoint = endpoint.Address.ToString(), Endpoint = endpoint };
    }

    /// <summary>Grows the sample when the result sat too close to the decision threshold.</summary>
    public LatencyProbeRequest Widened(int probeCount) => this with
    {
        ProbeCount = Math.Max(ProbeCount, probeCount),
        GatewayProbeCount = Math.Max(GatewayProbeCount, Math.Min(24, probeCount / 8)),
    };
}

public interface ILatencyProbe
{
    Task<LatencyMeasurement> MeasureAsync(
        NetworkFingerprint network,
        LatencyProbeRequest request,
        CancellationToken cancellationToken = default);

    Task<LatencyConnectivity> CheckConnectivityAsync(
        NetworkFingerprint network,
        string remoteEndpoint,
        CancellationToken cancellationToken = default);
}

/// <summary>Measures gateway and remote latency without involving DNS.</summary>
/// <remarks>
/// <para>
/// Probes in a series are sent one at a time with a fixed gap, the way <c>ping</c> does
/// it. Firing a batch concurrently is faster but measures the machine's own send queue
/// as much as the network, and an A/B comparison built on that mostly compares how
/// contended the two batches were.
/// </para>
/// <para>
/// The gateway series runs alongside the remote one rather than after it, so both halves
/// describe the same slice of time; that is at most two echo requests in flight, which
/// is small enough not to be the thing being measured.
/// </para>
/// </remarks>
public sealed class LatencyProbe : ILatencyProbe
{
    /// <summary>
    /// TCP handshakes are expensive for the far end, so a series of them is capped well
    /// below the ICMP count and paced more slowly, whatever the request asked for.
    /// </summary>
    public const int MaximumTcpProbes = 24;

    /// <summary>The same ceiling for application-level exchanges over one connection.</summary>
    public const int MaximumApplicationProbes = 40;

    /// <summary>How many gateway probes may overshoot the request before the series stops.</summary>
    private const int MaximumGatewayOvershoot = 4;

    /// <summary>Deadline for the yes/no reachability checks between experiment arms.</summary>
    private const int ConnectivityTimeoutMs = 900;

    private static readonly TimeSpan MinimumTcpPacing = TimeSpan.FromMilliseconds(120);

    private static IReadOnlyList<IPAddress> ReferenceEndpoints => LatencyTargetResolver.ReferenceAddresses;

    private readonly INetworkLoadSampler _load;

    public LatencyProbe(INetworkLoadSampler? loadSampler = null)
        => _load = loadSampler ?? new NetworkLoadSampler();

    public async Task<LatencyMeasurement> MeasureAsync(
        NetworkFingerprint network,
        LatencyProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(request);

        var gateway = IPAddress.TryParse(network.GatewayAddress, out var parsedGateway) ? parsedGateway : null;
        var loadStart = _load.Read(network);

        // The gateway series runs until the remote series says it is finished rather than
        // for a precomputed number of probes. An earlier build derived the gateway pacing
        // from the ICMP probe count, so on a TCP series - which is slower per probe and
        // paced differently - the gateway samples covered only the first part of the
        // window and were then subtracted from a remote median measured across all of it.
        using var remoteFinished = new CancellationTokenSource();
        var gatewayTask = gateway is null
            ? Task.FromResult<GatewaySeries>(GatewaySeries.Empty)
            : PingUntilAsync(
                gateway,
                request,
                remoteFinished.Token,
                cancellationToken);

        SeriesResult series;
        GatewaySeries gatewaySamples;

        try
        {
            series = await MeasureRemoteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Awaited on every path, including cancellation: a series left running is an
            // unobserved task still sending echo requests after the caller gave up.
            await remoteFinished.CancelAsync().ConfigureAwait(false);
            gatewaySamples = await Observed(gatewayTask).ConfigureAwait(false);
        }

        return LatencyMeasurement.Create(
            series.Endpoint,
            series.Protocol,
            series.Samples,
            series.Attempts,
            gatewaySamples.Samples,
            gatewaySamples.Attempts,
            NetworkLoadSample.Between(loadStart, _load.Read(network)),
            clockResolutionMs: series.ClockResolutionMs);
    }

    /// <summary>Dispatches to the instrument the pinned endpoint actually calls for.</summary>
    private async Task<SeriesResult> MeasureRemoteAsync(
        LatencyProbeRequest request,
        CancellationToken cancellationToken)
        => request.Endpoint switch
        {
            { Protocol: LatencyProtocol.MinecraftStatus } minecraft
                => await MeasureMinecraftAsync(minecraft, request, cancellationToken).ConfigureAwait(false),
            { Protocol: LatencyProtocol.TcpEStats, LocalEndpoint: not null } estats
                => await MeasureEStatsAsync(estats, request, cancellationToken).ConfigureAwait(false),
            { Protocol: LatencyProtocol.Tcp, Port: > 0 } tcp
                => await MeasureTcpAsync(tcp, request, cancellationToken).ConfigureAwait(false),
            _ => await MeasureIcmpAsync(request, cancellationToken).ConfigureAwait(false),
        };

    public async Task<LatencyConnectivity> CheckConnectivityAsync(
        NetworkFingerprint network,
        string remoteEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);

        var remote = IPAddress.TryParse(remoteEndpoint, out var parsedRemote)
            ? parsedRemote
            : ReferenceEndpoints[0];
        var gateway = IPAddress.TryParse(network.GatewayAddress, out var parsedGateway) ? parsedGateway : null;

        var remotePing = TryPingAsync(remote, ConnectivityTimeoutMs, cancellationToken);
        var gatewayPing = gateway is null
            ? Task.FromResult<double?>(null)
            : TryPingAsync(gateway, ConnectivityTimeoutMs, cancellationToken);

        await Task.WhenAll(remotePing, gatewayPing).ConfigureAwait(false);

        var remoteReachable = remotePing.Result is not null;
        if (!remoteReachable)
        {
            remoteReachable = await TryTcpConnectAsync(remote, 443, ConnectivityTimeoutMs, cancellationToken)
                .ConfigureAwait(false) is not null;
        }

        return new LatencyConnectivity(gatewayPing.Result is not null, remoteReachable);
    }

    /// <summary>
    /// One remote series, and how finely the instrument that produced it could see.
    /// </summary>
    /// <remarks>
    /// <see cref="IcmpResolutionMs"/> for echo requests because <see cref="Ping"/> reports
    /// whole milliseconds; everything else is timed with <see cref="Stopwatch"/> and can
    /// resolve far below any threshold the evaluator uses.
    /// </remarks>
    private sealed record SeriesResult(
        string Endpoint,
        string Protocol,
        IReadOnlyList<double> Samples,
        int Attempts,
        double ClockResolutionMs = IcmpResolutionMs);

    /// <summary>The gateway half of a measurement, with the attempts it really made.</summary>
    private sealed record GatewaySeries(IReadOnlyList<double> Samples, int Attempts)
    {
        public static readonly GatewaySeries Empty = new([], 0);
    }

    /// <summary>Ping reports round trips as whole milliseconds, so that is the resolution.</summary>
    public const double IcmpResolutionMs = 1.0;

    /// <summary>What a stopwatch-timed instrument can resolve, in milliseconds.</summary>
    public static double StopwatchResolutionMs => 1000d / Stopwatch.Frequency;

    private async Task<SeriesResult> MeasureIcmpAsync(
        LatencyProbeRequest request,
        CancellationToken cancellationToken)
    {
        var endpoints = ResolveEndpoints(request);
        var perEndpoint = Math.Max(1, request.ProbeCount / endpoints.Count);
        var samples = new Dictionary<IPAddress, IReadOnlyList<double>>();

        foreach (var endpoint in endpoints)
        {
            samples[endpoint] = await PingSeriesAsync(
                endpoint,
                perEndpoint,
                request.Pacing,
                request.TimeoutMilliseconds,
                cancellationToken,
                request.WarmupCount).ConfigureAwait(false);
        }

        var selected = endpoints
            .OrderByDescending(endpoint => samples[endpoint].Count)
            .ThenBy(endpoint => MedianOrInfinity(samples[endpoint]))
            .First();

        var selectedSamples = samples[selected];
        if (selectedSamples.Count > 0)
        {
            return new SeriesResult(selected.ToString(), "ICMP", selectedSamples, perEndpoint);
        }

        // Some networks drop ICMP while ordinary HTTPS works. TCP connect latency is a
        // real fallback measurement; it is labelled and never mixed with ICMP.
        var fallback = await MeasureTcpFallbackAsync(endpoints, request, cancellationToken).ConfigureAwait(false);
        return new SeriesResult(
            fallback.Endpoint.ToString(),
            "TCP/443 (ICMP yanıtsız; el sıkışma süresi)",
            fallback.Samples,
            fallback.Attempts,
            StopwatchResolutionMs);
    }

    private static async Task<SeriesResult> MeasureTcpAsync(
        LatencyEndpoint endpoint,
        LatencyProbeRequest request,
        CancellationToken cancellationToken)
    {
        var port = endpoint.Port ?? 443;
        var attempts = Math.Clamp(request.ProbeCount, 3, MaximumTcpProbes);
        var pacing = request.Pacing > MinimumTcpPacing ? request.Pacing : MinimumTcpPacing;
        var warmup = Math.Clamp(request.WarmupCount, 0, 2);
        var samples = new List<double>(attempts);

        for (var index = 0; index < attempts + warmup; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var elapsed = await TryTcpConnectAsync(
                endpoint.Address, port, request.TimeoutMilliseconds, cancellationToken).ConfigureAwait(false);

            if (index >= warmup && elapsed is { } value)
            {
                samples.Add(value);
            }

            if (index + 1 < attempts + warmup)
            {
                await Task.Delay(pacing, cancellationToken).ConfigureAwait(false);
            }
        }

        return new SeriesResult(
            endpoint.Address.ToString(),
            $"TCP/{port} (el sıkışma süresi)",
            samples,
            attempts,
            StopwatchResolutionMs);
    }

    /// <summary>
    /// Samples what TCP has already measured on a connection the application owns.
    /// </summary>
    /// <remarks>
    /// No packets are sent. Each sample is a read of the stack's smoothed estimate, which
    /// makes this the only instrument here that reports a running game's own round trip
    /// without adding a single connection to the server it is talking to. A stack that
    /// will not enable collection produces no samples at all rather than a fallback
    /// wearing the same label.
    /// </remarks>
    private static async Task<SeriesResult> MeasureEStatsAsync(
        LatencyEndpoint endpoint,
        LatencyProbeRequest request,
        CancellationToken cancellationToken)
    {
        var local = endpoint.LocalEndpoint!;
        var remote = new IPEndPoint(endpoint.Address, endpoint.Port ?? 0);
        var label = $"TCP/{remote.Port} (EStats)";

        if (!TcpEStats.TryEnable(local, remote))
        {
            // Not supported here, or not permitted. The handshake series is a real
            // measurement of a different thing, so it is used and said to be different -
            // never reported under the label of the connection's own round trip.
            return await MeasureTcpAsync(endpoint, request, cancellationToken).ConfigureAwait(false);
        }

        var attempts = Math.Max(1, request.ProbeCount);
        var samples = new List<double>(attempts);
        var pacing = request.Pacing > TimeSpan.Zero ? request.Pacing : TimeSpan.FromMilliseconds(45);
        var previous = double.NaN;

        for (var index = 0; index < attempts + request.WarmupCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sample = TcpEStats.TryRead(local, remote);
            if (sample is null)
            {
                // The connection went away mid-series. What was collected still describes
                // it; nothing is invented to fill the rest.
                break;
            }

            // The smoothed estimate only moves when the stack takes a new measurement, so
            // repeating an unchanged value would report the same round trip several times
            // and shrink the apparent variation to nothing.
            if (index >= request.WarmupCount && !AreClose(sample.SmoothedRttMs, previous))
            {
                samples.Add(sample.SmoothedRttMs);
            }

            previous = sample.SmoothedRttMs;

            if (index + 1 < attempts + request.WarmupCount)
            {
                await Task.Delay(pacing, cancellationToken).ConfigureAwait(false);
            }
        }

        if (samples.Count == 0)
        {
            // Collection was enabled but the connection produced nothing usable - it ended,
            // or the stack has not measured it yet. Fall back rather than report an empty
            // series as an unreachable target, and label what the fallback actually is.
            return await MeasureTcpAsync(endpoint, request, cancellationToken).ConfigureAwait(false);
        }

        return new SeriesResult(endpoint.Address.ToString(), label, samples, attempts, IcmpResolutionMs);

        static bool AreClose(double first, double second)
            => double.IsFinite(second) && Math.Abs(first - second) < 0.0001;
    }

    /// <summary>Times the Minecraft Java status Ping/Pong exchange: a real application RTT.</summary>
    private static async Task<SeriesResult> MeasureMinecraftAsync(
        LatencyEndpoint endpoint,
        LatencyProbeRequest request,
        CancellationToken cancellationToken)
    {
        var port = endpoint.Port ?? MinecraftStatusProbe.DefaultPort;
        var host = string.IsNullOrWhiteSpace(endpoint.Host) ? endpoint.Address.ToString() : endpoint.Host;
        var attempts = Math.Clamp(request.ProbeCount, 3, MaximumApplicationProbes);

        var series = await MinecraftStatusProbe.MeasureAsync(
            endpoint.Address,
            port,
            host,
            attempts,
            Math.Clamp(request.WarmupCount, 0, 3),
            request.Pacing > MinimumTcpPacing ? request.Pacing : MinimumTcpPacing,
            request.TimeoutMilliseconds,
            cancellationToken).ConfigureAwait(false);

        return new SeriesResult(
            endpoint.Address.ToString(),
            $"Minecraft/{port}",
            series.Samples,
            series.Attempts,
            StopwatchResolutionMs);
    }

    /// <summary>Waits for a series to finish, treating cancellation as "no samples".</summary>
    private static async Task<GatewaySeries> Observed(Task<GatewaySeries> series)
    {
        try
        {
            return await series.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GatewaySeries.Empty;
        }
    }

    /// <summary>
    /// Probes the gateway for exactly as long as the remote series runs.
    /// </summary>
    /// <remarks>
    /// Both halves have to describe the same slice of time or their difference is not the
    /// local part of the path. Rather than guessing how long the remote series will take,
    /// this simply keeps going until it is told the remote series is over, pacing itself
    /// from the request's estimate and capped so a very long series cannot turn into a
    /// flood of echo requests at the router.
    /// </remarks>
    private static async Task<GatewaySeries> PingUntilAsync(
        IPAddress address,
        LatencyProbeRequest request,
        CancellationToken stop,
        CancellationToken cancellationToken)
    {
        var target = Math.Max(1, request.GatewayProbeCount);
        var cap = target * MaximumGatewayOvershoot;
        var pacing = GatewayPacing(request);
        var samples = new List<double>(target);
        var attempts = 0;

        while (attempts < cap && !stop.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();

            attempts++;
            if (await TryPingAsync(address, request.TimeoutMilliseconds, cancellationToken).ConfigureAwait(false)
                is { } rtt)
            {
                samples.Add(rtt);
            }

            if (pacing <= TimeSpan.Zero)
            {
                continue;
            }

            try
            {
                await Task.Delay(pacing, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return new GatewaySeries(samples, attempts);
    }

    /// <summary>The gap between gateway probes, from the request's own estimate.</summary>
    private static TimeSpan GatewayPacing(LatencyProbeRequest request)
    {
        if (request.GatewayProbeCount <= 1)
        {
            return request.Pacing;
        }

        var window = request.Pacing * Math.Max(1, request.ProbeCount);
        return window / request.GatewayProbeCount;
    }

    private static IReadOnlyList<IPAddress> ResolveEndpoints(LatencyProbeRequest request)
    {
        if (request.Endpoint is { } endpoint)
        {
            return [endpoint.Address];
        }

        if (IPAddress.TryParse(request.RemoteEndpoint, out var address)
            && address.AddressFamily == AddressFamily.InterNetwork)
        {
            return [address];
        }

        return ReferenceEndpoints;
    }

    private static async Task<IReadOnlyList<double>> PingSeriesAsync(
        IPAddress address,
        int count,
        TimeSpan pacing,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        int warmupCount = 0)
    {
        var samples = new List<double>(count);
        var total = count + Math.Max(0, warmupCount);

        for (var index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rtt = await TryPingAsync(address, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            if (index >= warmupCount && rtt is { } value)
            {
                samples.Add(value);
            }

            if (index + 1 < total && pacing > TimeSpan.Zero)
            {
                await Task.Delay(pacing, cancellationToken).ConfigureAwait(false);
            }
        }

        return samples;
    }

    private static async Task<(IPAddress Endpoint, IReadOnlyList<double> Samples, int Attempts)> MeasureTcpFallbackAsync(
        IReadOnlyList<IPAddress> endpoints,
        LatencyProbeRequest request,
        CancellationToken cancellationToken)
    {
        // A TCP handshake is far more expensive than an echo request, for the far end as
        // much as for us, so the fallback is deliberately a fraction of the ICMP count.
        var attempts = Math.Clamp(request.ProbeCount / 4, 3, 8);
        var samples = endpoints.ToDictionary(endpoint => endpoint, _ => new List<double>());

        for (var round = 0; round < attempts; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var endpoint in endpoints)
            {
                if (await TryTcpConnectAsync(endpoint, 443, request.TimeoutMilliseconds, cancellationToken)
                    .ConfigureAwait(false) is { } elapsed)
                {
                    samples[endpoint].Add(elapsed);
                }
            }

            if (round + 1 < attempts)
            {
                await Task.Delay(request.Pacing, cancellationToken).ConfigureAwait(false);
            }
        }

        var selected = endpoints
            .OrderByDescending(endpoint => samples[endpoint].Count)
            .ThenBy(endpoint => MedianOrInfinity(samples[endpoint]))
            .First();

        return (selected, samples[selected], attempts);
    }

    private static async Task<double?> TryPingAsync(
        IPAddress address,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var started = Stopwatch.GetTimestamp();
            var reply = await ping.SendPingAsync(address, timeoutMilliseconds).WaitAsync(cancellationToken).ConfigureAwait(false);

            if (reply.Status != IPStatus.Success)
            {
                return null;
            }

            // Ping rounds sub-millisecond replies down to zero. Stopwatch preserves the
            // useful precision for gateway measurements while the reply still proves the
            // ICMP transaction succeeded.
            return reply.RoundtripTime > 0
                ? reply.RoundtripTime
                : Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// One connect attempt with a real deadline of its own.
    /// </summary>
    /// <remarks>
    /// <see cref="LatencyProbeRequest.TimeoutMilliseconds"/> used to apply to echo
    /// requests only: a TCP attempt inherited nothing but the caller's token, so a
    /// black-holed SYN sat in the operating system's own retransmit schedule for around
    /// twenty seconds. That single attempt is longer than the entire series is meant to
    /// take, and RFC 2681 requires the threshold that separates a large finite delay from
    /// a loss to be part of the metric - which it cannot be if it is never applied.
    /// </remarks>
    /// <summary>The connect path, exposed so its deadline can be held to in a test.</summary>
    internal static Task<double?> TryTcpConnectForTestAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
        => TryTcpConnectAsync(address, port, timeoutMilliseconds, cancellationToken);

    private static async Task<double?> TryTcpConnectAsync(
        IPAddress address,
        int port,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMilliseconds)));

        try
        {
            using var client = new TcpClient(address.AddressFamily);
            var started = Stopwatch.GetTimestamp();
            await client.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false);
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Timed out or refused: a probe that did not come back inside the threshold is
            // a lost probe, which is what the loss figure is for.
            return null;
        }
    }

    private static double MedianOrInfinity(IReadOnlyList<double> values)
        => values.Count == 0 ? double.PositiveInfinity : LatencyStatistics.Median(values);
}
