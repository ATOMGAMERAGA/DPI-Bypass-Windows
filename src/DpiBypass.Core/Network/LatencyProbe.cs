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
/// two different experiments rather than between two adapter settings.
/// </remarks>
public sealed record LatencyProbeRequest
{
    /// <summary>The one target to probe. Null lets the probe pick the best of its list.</summary>
    public string? RemoteEndpoint { get; init; }

    public int ProbeCount { get; init; } = 24;

    public int GatewayProbeCount { get; init; } = 8;

    /// <summary>Gap between consecutive probes in the same series.</summary>
    public TimeSpan Pacing { get; init; } = TimeSpan.FromMilliseconds(45);

    public int TimeoutMilliseconds { get; init; } = 900;

    /// <summary>The short pass that picks a target and shows the user a first number.</summary>
    public static readonly LatencyProbeRequest Survey = new()
    {
        ProbeCount = 9,
        GatewayProbeCount = 3,
        Pacing = TimeSpan.FromMilliseconds(40),
    };

    /// <summary>
    /// The pass a verdict is allowed to rest on. Twenty-four probes is the smallest
    /// batch where a p95 is more than "the second worst sample" and one lost probe is
    /// about four percent rather than a tenth of the result.
    /// </summary>
    public static readonly LatencyProbeRequest Benchmark = new();

    public LatencyProbeRequest For(string? endpoint) => this with { RemoteEndpoint = endpoint };
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

/// <summary>Measures gateway and public-IP latency without involving DNS.</summary>
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
    private static readonly IPAddress[] RemoteEndpoints =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8"),
        IPAddress.Parse("9.9.9.9"),
    ];

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

        var endpoints = ResolveEndpoints(request.RemoteEndpoint);
        var perEndpoint = Math.Max(1, request.ProbeCount / endpoints.Length);

        var gatewayTask = gateway is null
            ? Task.FromResult<IReadOnlyList<double>>([])
            : PingSeriesAsync(gateway, request.GatewayProbeCount, GatewayPacing(request), request.TimeoutMilliseconds, cancellationToken);

        var samples = new Dictionary<IPAddress, IReadOnlyList<double>>();
        IReadOnlyList<double> gatewaySamples;

        try
        {
            foreach (var endpoint in endpoints)
            {
                samples[endpoint] = await PingSeriesAsync(
                    endpoint,
                    perEndpoint,
                    request.Pacing,
                    request.TimeoutMilliseconds,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Awaited on every path, including cancellation: a series left running is an
            // unobserved task still sending echo requests after the caller gave up.
            gatewaySamples = await Observed(gatewayTask).ConfigureAwait(false);
        }

        var selected = endpoints
            .OrderByDescending(endpoint => samples[endpoint].Count)
            .ThenBy(endpoint => MedianOrInfinity(samples[endpoint]))
            .First();
        var selectedSamples = samples[selected];
        var protocol = "ICMP";
        var attempts = perEndpoint;

        // Some networks drop ICMP while ordinary HTTPS works. TCP connect latency is a
        // real fallback measurement; it is labelled and never mixed with ICMP.
        if (selectedSamples.Count == 0)
        {
            var tcp = await MeasureTcpFallbackAsync(endpoints, request, cancellationToken).ConfigureAwait(false);
            selected = tcp.Endpoint;
            selectedSamples = tcp.Samples;
            attempts = tcp.Attempts;
            protocol = "TCP/443";
        }

        return LatencyMeasurement.Create(
            selected.ToString(),
            protocol,
            selectedSamples,
            attempts,
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
            : RemoteEndpoints[0];
        var gateway = IPAddress.TryParse(network.GatewayAddress, out var parsedGateway) ? parsedGateway : null;

        var remotePing = TryPingAsync(remote, 900, cancellationToken);
        var gatewayPing = gateway is null
            ? Task.FromResult<double?>(null)
            : TryPingAsync(gateway, 900, cancellationToken);

        await Task.WhenAll(remotePing, gatewayPing).ConfigureAwait(false);

        var remoteReachable = remotePing.Result is not null;
        if (!remoteReachable)
        {
            remoteReachable = await TryTcpConnectAsync(remote, cancellationToken).ConfigureAwait(false) is not null;
        }

        return new LatencyConnectivity(gatewayPing.Result is not null, remoteReachable);
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

    private static IPAddress[] ResolveEndpoints(string? requested)
    {
        if (IPAddress.TryParse(requested, out var address)
            && address.AddressFamily == AddressFamily.InterNetwork)
        {
            return [address];
        }

        return RemoteEndpoints;
    }

    private static async Task<IReadOnlyList<double>> PingSeriesAsync(
        IPAddress address,
        int count,
        TimeSpan pacing,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var samples = new List<double>(count);

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryPingAsync(address, timeoutMilliseconds, cancellationToken).ConfigureAwait(false) is { } rtt)
            {
                samples.Add(rtt);
            }

            if (index + 1 < count && pacing > TimeSpan.Zero)
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
                if (await TryTcpConnectAsync(endpoint, cancellationToken).ConfigureAwait(false) is { } elapsed)
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

    private static async Task<double?> TryTcpConnectAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            var started = Stopwatch.GetTimestamp();
            await client.ConnectAsync(address, 443, cancellationToken).ConfigureAwait(false);
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
