namespace DpiBypass.Core.Network;

/// <summary>One series read from a game's own latency display.</summary>
public sealed record GameLatencySeries
{
    public IReadOnlyList<double> Samples { get; init; } = [];

    public int FramesRead { get; init; }

    public string Instrument { get; init; } = string.Empty;

    public string? Failure { get; init; }

    public double ValidSampleShare => FramesRead > 0 ? Samples.Count / (double)FramesRead : 0;
}

public interface IGameLatencySource
{
    bool CanMeasure(LatencyEndpoint endpoint);

    Task<GameLatencySeries> MeasureAsync(
        LatencyEndpoint endpoint,
        LatencyProbeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Uses a game's own RTT source when available and the ordinary probe otherwise.</summary>
public sealed class GameAwareLatencyProbe : ILatencyProbe
{
    private const string RouteReferenceNotice = "UDP oturumunun kendi gidiş-dönüş süresi dışarıdan ölçülemez; "
        + "aynı adrese ICMP ile yalnızca rota referansı ölçülür.";
    private readonly ILatencyProbe _fallback;
    private readonly IGameLatencySource _game;
    private readonly INetworkLoadSampler _load;

    public GameAwareLatencyProbe(
        ILatencyProbe fallback,
        IGameLatencySource game,
        INetworkLoadSampler? load = null)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _load = load ?? new NetworkLoadSampler();
    }

    public bool MeasuresApplicationRoundTrip(LatencyEndpoint endpoint) => _game.CanMeasure(endpoint);

    public LatencyEndpoint PrepareEndpoint(LatencyEndpoint endpoint)
        => !_game.CanMeasure(endpoint)
            ? endpoint
            : endpoint with
            {
                RouteReferenceOnly = false,
                ProtocolLabelOverride = "VALORANT Network RTT (ekran)",
            };

    public LatencyTargetResolution PrepareResolution(LatencyTargetResolution resolution)
    {
        var endpoints = resolution.Endpoints.Select(PrepareEndpoint).ToArray();
        if (!endpoints.Any(endpoint => endpoint.ProtocolLabelOverride is not null))
        {
            return resolution;
        }

        var candidates = resolution.Candidates
            .Select(candidate => candidate with { Endpoint = PrepareEndpoint(candidate.Endpoint) })
            .ToArray();
        var notice = (resolution.Notice ?? string.Empty)
            .Replace(RouteReferenceNotice, string.Empty, StringComparison.Ordinal)
            .Trim();
        var gameNotice = "VALORANT'ın oyun içi Network RTT göstergesi okunuyor; "
            + "bu değer uygulamanın kendi RTT'sidir.";

        return resolution with
        {
            Endpoints = endpoints,
            Candidates = candidates,
            Notice = string.IsNullOrWhiteSpace(notice) ? gameNotice : $"{notice} {gameNotice}",
        };
    }

    public async Task<LatencyMeasurement> MeasureAsync(
        NetworkFingerprint network,
        LatencyProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Endpoint is not { } endpoint || !_game.CanMeasure(endpoint))
        {
            return await _fallback.MeasureAsync(network, request, cancellationToken).ConfigureAwait(false);
        }

        var before = _load.Read(network);
        var series = await _game.MeasureAsync(endpoint, request, cancellationToken).ConfigureAwait(false);
        var after = _load.Read(network);

        return LatencyMeasurement.Create(
            endpoint.Address.ToString(),
            series.Instrument,
            series.ValidSampleShare >= 0.80 ? series.Samples : [],
            series.FramesRead,
            [],
            0,
            NetworkLoadSample.Between(before, after),
            clockResolutionMs: 1,
            source: LatencySampleSource.GameDisplay,
            validSampleShare: series.ValidSampleShare);
    }

    public Task<LatencyConnectivity> CheckConnectivityAsync(
        NetworkFingerprint network,
        string remoteEndpoint,
        CancellationToken cancellationToken = default)
        => _fallback.CheckConnectivityAsync(network, remoteEndpoint, cancellationToken);
}
