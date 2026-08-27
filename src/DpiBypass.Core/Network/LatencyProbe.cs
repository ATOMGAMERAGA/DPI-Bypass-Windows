using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DpiBypass.Core.Network;

public sealed record LatencyConnectivity(bool GatewayReachable, bool RemoteReachable)
{
    public bool IsUsable => GatewayReachable || RemoteReachable;
}

public interface ILatencyProbe
{
    Task<LatencyMeasurement> MeasureAsync(
        NetworkFingerprint network,
        string? remoteEndpoint = null,
        CancellationToken cancellationToken = default);

    Task<LatencyConnectivity> CheckConnectivityAsync(
        NetworkFingerprint network,
        string remoteEndpoint,
        CancellationToken cancellationToken = default);
}

/// <summary>Measures gateway and public-IP latency without involving DNS.</summary>
public sealed class LatencyProbe : ILatencyProbe
{
    private static readonly IPAddress[] RemoteEndpoints =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8"),
        IPAddress.Parse("9.9.9.9"),
    ];

    private static readonly TimeSpan BetweenBatches = TimeSpan.FromMilliseconds(120);
    private const int TimeoutMilliseconds = 900;
    private const int BatchCount = 3;

    public async Task<LatencyMeasurement> MeasureAsync(
        NetworkFingerprint network,
        string? remoteEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        var endpoints = ResolveEndpoints(remoteEndpoint);
        var samplesPerEndpointPerBatch = endpoints.Length == 1 ? 4 : 2;
        var remote = endpoints.ToDictionary(address => address, _ => new List<double>());
        var gatewaySamples = new List<double>();
        var gateway = IPAddress.TryParse(network.GatewayAddress, out var parsedGateway) ? parsedGateway : null;

        for (var batch = 0; batch < BatchCount; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteTasks = endpoints.ToDictionary(
                endpoint => endpoint,
                endpoint => Enumerable.Range(0, samplesPerEndpointPerBatch)
                    .Select(_ => TryPingAsync(endpoint, cancellationToken)).ToArray());
            var gatewayTasks = gateway is null
                ? []
                : Enumerable.Range(0, 2).Select(_ => TryPingAsync(gateway, cancellationToken)).ToArray();

            await Task.WhenAll(remoteTasks.Values.SelectMany(tasks => tasks).Concat(gatewayTasks)).ConfigureAwait(false);

            foreach (var (endpoint, tasks) in remoteTasks)
            {
                remote[endpoint].AddRange(tasks.Select(task => task.Result).OfType<double>());
            }

            gatewaySamples.AddRange(gatewayTasks.Select(task => task.Result).OfType<double>());

            if (batch + 1 < BatchCount)
            {
                await Task.Delay(BetweenBatches, cancellationToken).ConfigureAwait(false);
            }
        }

        var selected = endpoints
            .OrderByDescending(endpoint => remote[endpoint].Count)
            .ThenBy(endpoint => MedianOrInfinity(remote[endpoint]))
            .First();
        var selectedSamples = remote[selected];
        var protocol = "ICMP";
        var attempts = BatchCount * samplesPerEndpointPerBatch;

        // Some networks drop ICMP while ordinary HTTPS works. TCP connect latency is
        // a real fallback measurement; it is labelled and never mixed with ICMP.
        if (selectedSamples.Count == 0)
        {
            var tcp = await MeasureTcpFallbackAsync(endpoints, cancellationToken).ConfigureAwait(false);
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
            gateway is null ? 0 : BatchCount * 2);
    }

    public async Task<LatencyConnectivity> CheckConnectivityAsync(
        NetworkFingerprint network,
        string remoteEndpoint,
        CancellationToken cancellationToken = default)
    {
        var remote = IPAddress.TryParse(remoteEndpoint, out var parsedRemote)
            ? parsedRemote
            : RemoteEndpoints[0];
        var gateway = IPAddress.TryParse(network.GatewayAddress, out var parsedGateway) ? parsedGateway : null;

        var remotePing = TryPingAsync(remote, cancellationToken);
        var gatewayPing = gateway is null
            ? Task.FromResult<double?>(null)
            : TryPingAsync(gateway, cancellationToken);

        await Task.WhenAll(remotePing, gatewayPing).ConfigureAwait(false);

        var remoteReachable = remotePing.Result is not null;
        if (!remoteReachable)
        {
            remoteReachable = await TryTcpConnectAsync(remote, cancellationToken).ConfigureAwait(false) is not null;
        }

        return new LatencyConnectivity(gatewayPing.Result is not null, remoteReachable);
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

    private static async Task<(IPAddress Endpoint, List<double> Samples, int Attempts)> MeasureTcpFallbackAsync(
        IReadOnlyList<IPAddress> endpoints,
        CancellationToken cancellationToken)
    {
        const int attemptsPerEndpoint = 3;
        var samples = endpoints.ToDictionary(endpoint => endpoint, _ => new List<double>());

        for (var batch = 0; batch < attemptsPerEndpoint; batch++)
        {
            var tasks = endpoints.ToDictionary(endpoint => endpoint, endpoint => TryTcpConnectAsync(endpoint, cancellationToken));
            await Task.WhenAll(tasks.Values).ConfigureAwait(false);

            foreach (var (endpoint, task) in tasks)
            {
                if (task.Result is { } elapsed)
                {
                    samples[endpoint].Add(elapsed);
                }
            }

            if (batch + 1 < attemptsPerEndpoint)
            {
                await Task.Delay(BetweenBatches, cancellationToken).ConfigureAwait(false);
            }
        }

        var selected = endpoints
            .OrderByDescending(endpoint => samples[endpoint].Count)
            .ThenBy(endpoint => MedianOrInfinity(samples[endpoint]))
            .First();

        return (selected, samples[selected], attemptsPerEndpoint);
    }

    private static async Task<double?> TryPingAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var started = Stopwatch.GetTimestamp();
            var reply = await ping.SendPingAsync(address, TimeoutMilliseconds).WaitAsync(cancellationToken).ConfigureAwait(false);

            if (reply.Status != IPStatus.Success)
            {
                return null;
            }

            // Ping rounds sub-millisecond replies down to zero. Stopwatch preserves
            // the useful precision for gateway measurements while the reply still
            // proves the ICMP transaction succeeded.
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
    {
        if (values.Count == 0)
        {
            return double.PositiveInfinity;
        }

        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }
}
