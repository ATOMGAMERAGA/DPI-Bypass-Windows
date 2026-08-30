using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

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

        var gatewayTask = gateway is null
            ? Task.FromResult<IReadOnlyList<double>>([])
            : PingSeriesAsync(gateway, request.GatewayProbeCount, GatewayPacing(request), request.TimeoutMilliseconds, cancellationToken);

        SeriesResult series;
        IReadOnlyList<double> gatewaySamples;

        try
        {
            series = request.Endpoint is { Protocol: LatencyProtocol.Tcp, Port: > 0 } tcp
                ? await MeasureTcpAsync(tcp, request, cancellationToken).ConfigureAwait(false)
                : await MeasureIcmpAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Awaited on every path, including cancellation: a series left running is an
            // unobserved task still sending echo requests after the caller gave up.
            gatewaySamples = await Observed(gatewayTask).ConfigureAwait(false);
        }

        return LatencyMeasurement.Create(
            series.Endpoint,
            series.Protocol,
            series.Samples,
            series.Attempts,
            gatewaySamples,
            gateway is null ? 0 : request.GatewayProbeCount,
            NetworkLoadSample.Between(loadStart, _load.Read(network)));
    }

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

        var remotePing = TryPingAsync(remote, 900, cancellationToken);
        var gatewayPing = gateway is null
            ? Task.FromResult<double?>(null)
            : TryPingAsync(gateway, 900, cancellationToken);

        await Task.WhenAll(remotePing, gatewayPing).ConfigureAwait(false);

        var remoteReachable = remotePing.Result is not null;
        if (!remoteReachable)
        {
            remoteReachable = await TryTcpConnectAsync(remote, 443, cancellationToken).ConfigureAwait(false) is not null;
        }

        return new LatencyConnectivity(gatewayPing.Result is not null, remoteReachable);
    }

    private sealed record SeriesResult(string Endpoint, string Protocol, IReadOnlyList<double> Samples, int Attempts);

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
        return new SeriesResult(fallback.Endpoint.ToString(), "TCP/443", fallback.Samples, fallback.Attempts);
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

            var elapsed = await TryTcpConnectAsync(endpoint.Address, port, cancellationToken).ConfigureAwait(false);
            if (index >= warmup && elapsed is { } value)
            {
                samples.Add(value);
            }

            if (index + 1 < attempts + warmup)
            {
                await Task.Delay(pacing, cancellationToken).ConfigureAwait(false);
            }
        }

        return new SeriesResult(endpoint.Address.ToString(), $"TCP/{port}", samples, attempts);
    }

    /// <summary>Waits for a series to finish, treating cancellation as "no samples".</summary>
    private static async Task<IReadOnlyList<double>> Observed(Task<IReadOnlyList<double>> series)
    {
        try
        {
            return await series.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>Spread the gateway probes across the whole remote series.</summary>
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
                if (await TryTcpConnectAsync(endpoint, 443, cancellationToken).ConfigureAwait(false) is { } elapsed)
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

    private static async Task<double?> TryTcpConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            var started = Stopwatch.GetTimestamp();
            await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
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

    private static double MedianOrInfinity(IReadOnlyList<double> values)
        => values.Count == 0 ? double.PositiveInfinity : LatencyStatistics.Median(values);
}
