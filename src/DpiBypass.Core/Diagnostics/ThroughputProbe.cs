using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace DpiBypass.Core.Diagnostics;

/// <summary>Measures how much a link actually carries, at the cost of the user's data.</summary>
/// <remarks>
/// An interface because the cost is the whole point: a sweep that runs whenever a network
/// changes must not be able to spend somebody's mobile allowance, so the probe is absent
/// unless the user asked for it and the selection policy is built to say "hız testi
/// yapılmadı" rather than to guess.
/// </remarks>
public interface IThroughputProbe
{
    /// <summary>
    /// Transfers roughly <paramref name="bytes"/> and reports what the link sustained.
    /// </summary>
    /// <returns>
    /// The result, or null when the transfer could not be completed. Null is "not
    /// measured" and must never be rendered as a zero.
    /// </returns>
    Task<ThroughputResult?> MeasureAsync(long bytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// Downloads a known number of bytes and times it, then measures what the transfer did to
/// the round trip.
/// </summary>
/// <remarks>
/// <para>
/// Two numbers, because one of them on its own is misleading. Sustained throughput says
/// what the link carries; the increase in round trip time while it is carrying it says
/// what that costs everything else on the connection - which is most of what people mean
/// when they say a download "lags out" their game. Reporting only the first is how a
/// change that fills the uplink buffer gets recorded as an improvement.
/// </para>
/// <para>
/// This borrows the shape of <see href="https://www.rfc-editor.org/rfc/rfc6349">RFC 6349</see>
/// - separate the round trip, the throughput and the delay under load rather than
/// collapsing them into one "speed" - and is emphatically not a conforming implementation
/// of it. There is no baseline RTT and bottleneck bandwidth derivation, no TCP window
/// sizing, no transfer time ratio, and one direction rather than both. It is a short
/// application check that borrows the method's separation of concerns, and nothing here
/// should be described as an RFC 6349 test.
/// </para>
/// <para>
/// The endpoint is Cloudflare's documented measurement endpoint, which returns exactly
/// the number of bytes asked for. That matters: a transfer whose size is decided by the
/// server cannot be budgeted, and a budget is what keeps this from being something that
/// happens to somebody rather than something they chose.
/// </para>
/// </remarks>
public sealed class CloudflareThroughputProbe : IThroughputProbe, IDisposable
{
    private const string Endpoint = "https://speed.cloudflare.com/__down?bytes=";

    /// <summary>The most this will ever move in one call, whatever it is asked for.</summary>
    public const long MaximumBytes = 16 * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly IConnectivityProbe? _latencyProbe;
    private readonly string _latencyHost;
    private readonly Action<string>? _log;

    /// <param name="latencyProbe">
    /// Used to sample the round trip while the transfer is running. Optional: without it
    /// the loaded-latency figure is null, which is "not measured" and is rendered as such.
    /// </param>
    public CloudflareThroughputProbe(
        IConnectivityProbe? latencyProbe = null,
        string latencyHost = ConnectivityTester.PrimaryHost,
        Action<string>? log = null,
        HttpMessageHandler? handler = null)
    {
        _latencyProbe = latencyProbe;
        _latencyHost = latencyHost;
        _log = log;

        _client = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,

                // A pooled connection would carry the previous candidate's established
                // session into this one's measurement. Every transfer opens its own.
                PooledConnectionLifetime = TimeSpan.Zero,
            })
            : new HttpClient(handler);

        _client.Timeout = TimeSpan.FromSeconds(60);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("DpiBypass/1.0");

        // The point is to measure the link, not the cache.
        _client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
        };
    }

    public async Task<ThroughputResult?> MeasureAsync(long bytes, CancellationToken cancellationToken = default)
    {
        var budget = Math.Clamp(bytes, 256 * 1024, MaximumBytes);

        double? idleMs = null;
        if (_latencyProbe is not null)
        {
            var before = await _latencyProbe.ProbeAsync(_latencyHost, false, cancellationToken).ConfigureAwait(false);
            idleMs = before.Success ? (before.Connect ?? before.Elapsed).TotalMilliseconds : null;
        }

        var stopwatch = Stopwatch.StartNew();
        long received = 0;
        Task<ProbeResult>? loaded = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke($"throughput: endpoint answered {(int)response.StatusCode}; no measurement taken.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];

            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                received += read;

                // Sampled a quarter of the way in, once, so the link is genuinely busy and
                // the probe is not itself most of the load.
                if (loaded is null && _latencyProbe is not null && received > budget / 4)
                {
                    loaded = _latencyProbe.ProbeAsync(_latencyHost, false, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"throughput: transfer failed after {received:N0} bytes ({ex.Message}); no measurement taken.");
            return null;
        }

        stopwatch.Stop();

        // A transfer that ended early did not measure a sustained rate, it measured a
        // failure. Reporting the bytes that did arrive divided by the time they took would
        // be a number with no meaning attached to it.
        if (received < budget / 2 || stopwatch.Elapsed < TimeSpan.FromMilliseconds(200))
        {
            _log?.Invoke($"throughput: only {received:N0} of {budget:N0} bytes arrived; no measurement taken.");
            return null;
        }

        double? increase = null;
        if (loaded is not null)
        {
            try
            {
                var under = await loaded.ConfigureAwait(false);
                if (under.Success && idleMs is { } idle)
                {
                    increase = Math.Max(0, (under.Connect ?? under.Elapsed).TotalMilliseconds - idle);
                }
            }
            catch (Exception)
            {
                // The throughput figure stands on its own; the queueing delay is extra.
            }
        }

        var mbps = received * 8d / stopwatch.Elapsed.TotalSeconds / 1_000_000d;

        return new ThroughputResult(
            DownlinkMbps: mbps,
            UplinkMbps: null,
            LoadedLatencyIncreaseMs: increase,
            Bytes: received,
            Duration: stopwatch.Elapsed);
    }

    public void Dispose() => _client.Dispose();
}
