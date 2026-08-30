using System.Diagnostics;

namespace DpiBypass.Core.Network;

public enum LoadDirection
{
    Upload = 0,
    Download = 1,
}

/// <summary>
/// What this link has been seen to carry, so "loaded" can mean something on it.
/// </summary>
/// <remarks>
/// A fixed threshold cannot classify load: 256 kbit/s saturates a 512 kbit/s uplink and
/// is background noise on a 500 Mbit/s one. What matters is the share of the link in
/// use, so the classification is relative to the largest rate this link has actually
/// been observed to sustain - or to a figure the user typed in, which they know better
/// than any measurement we are entitled to take without asking.
/// </remarks>
public sealed record LinkCapacityEstimate
{
    public static readonly LinkCapacityEstimate Unknown = new();

    /// <summary>Share of capacity at or above which the link counts as loaded.</summary>
    public const double LoadedShare = 0.25;

    /// <summary>Highest sustained uplink rate seen on this network, in kbit/s.</summary>
    public double? UplinkKbps { get; init; }

    public double? DownlinkKbps { get; init; }

    /// <summary>True when the figures came from the user rather than from observation.</summary>
    public bool UserSupplied { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public bool HasUplink => UplinkKbps is > 0;

    /// <summary>
    /// The rate at which sending counts as loading the link.
    /// </summary>
    /// <remarks>
    /// The absolute floor stays, because a machine whose counters have only ever seen a
    /// trickle must not decide that a trickle is saturation.
    /// </remarks>
    public double LoadedUplinkThresholdKbps => UplinkKbps is { } uplink and > 0
        ? Math.Max(NetworkLoadSample.LoadedKbps, uplink * LoadedShare)
        : NetworkLoadSample.LoadedKbps;

    public double LoadedDownlinkThresholdKbps => DownlinkKbps is { } downlink and > 0
        ? Math.Max(NetworkLoadSample.LoadedKbps, downlink * LoadedShare)
        : NetworkLoadSample.LoadedKbps;

    /// <summary>Raises the estimate when a window carried more than anything before it.</summary>
    public LinkCapacityEstimate Observing(NetworkLoadSample sample, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (UserSupplied || !sample.IsLoaded)
        {
            return this;
        }

        return this with
        {
            UplinkKbps = Math.Max(UplinkKbps ?? 0, sample.UplinkKbps),
            DownlinkKbps = Math.Max(DownlinkKbps ?? 0, sample.DownlinkKbps),
            ObservedAt = at,
        };
    }

    public string Describe() => (UplinkKbps, DownlinkKbps) switch
    {
        (null, null) => "hat kapasitesi bilinmiyor",
        ({ } up, null) => $"gönderim ~{up / 1000:F1} Mbit/s",
        (null, { } down) => $"indirme ~{down / 1000:F1} Mbit/s",
        ({ } up, { } down) => $"gönderim ~{up / 1000:F1} · indirme ~{down / 1000:F1} Mbit/s",
    };
}

public sealed record LoadExperimentRequest
{
    public required LatencyEndpoint Endpoint { get; init; }

    public required LoadDirection Direction { get; init; }

    public LinkCapacityEstimate Capacity { get; init; } = LinkCapacityEstimate.Unknown;

    public LatencyProbeRequest Probe { get; init; } = LatencyProbeRequest.Benchmark;

    /// <summary>How long to wait for the user's own traffic before giving up.</summary>
    public TimeSpan LoadWaitTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How long a measured idle window must stay idle to count.</summary>
    public TimeSpan IdleSettle { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed record LoadExperimentResult
{
    public required LoadDirection Direction { get; init; }

    /// <summary>The idle window this experiment's loaded window is compared against.</summary>
    public LatencyMeasurement? Idle { get; init; }

    public LatencyMeasurement? Loaded { get; init; }

    /// <summary>What the link actually carried during the loaded window.</summary>
    public NetworkLoadSample ObservedLoad { get; init; } = NetworkLoadSample.Unknown;

    public LinkCapacityEstimate Capacity { get; init; } = LinkCapacityEstimate.Unknown;

    public string? Failure { get; init; }

    public bool Succeeded => Idle is not null && Loaded is not null && Failure is null;

    /// <summary>Added median delay under load, when both halves exist and are comparable.</summary>
    public double? QueueingMs => Succeeded
        ? LatencyPathAnalysis.Describe(
            Idle!,
            Direction == LoadDirection.Upload ? Loaded : null,
            Direction == LoadDirection.Download ? Loaded : null).QueueingMs
        : null;

    public static LoadExperimentResult Failed(LoadDirection direction, string reason)
        => new() { Direction = direction, Failure = reason };
}

/// <summary>Measures latency while the link is genuinely busy in one direction.</summary>
public interface ILoadExperiment
{
    /// <summary>What the user has to do for this experiment to be able to run.</summary>
    string Instruction(LoadDirection direction);

    Task<LoadExperimentResult> RunAsync(
        NetworkFingerprint network,
        LoadExperimentRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Measures the loaded window from traffic the user creates, and sends nothing itself.
/// </summary>
/// <remarks>
/// <para>
/// Filling a home uplink means pushing tens of megabytes at somebody else's server, and
/// no consent the user gives covers the server on the other end. So this generates no
/// load at all: it asks the user to start the download or upload they were going to
/// start anyway, watches the adapter's own counters until the link really is busy, and
/// measures the round trip while it is. Nothing leaves this machine that the user did
/// not start.
/// </para>
/// <para>
/// The cost of that choice is honest too: if no load appears within the window, the
/// answer is "not measured", never an estimate.
/// </para>
/// </remarks>
public sealed class ObservedLoadExperiment : ILoadExperiment
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    private readonly ILatencyProbe _probe;
    private readonly INetworkLoadSampler _load;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _log;

    public ObservedLoadExperiment(
        ILatencyProbe probe,
        INetworkLoadSampler? loadSampler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _load = loadSampler ?? new NetworkLoadSampler();
        _delay = delay ?? Task.Delay;
        _log = log;
    }

    public string Instruction(LoadDirection direction) => direction == LoadDirection.Upload
        ? "Şimdi büyük bir dosya göndermeye başlayın (bulut yedeklemesi, video yükleme, oyun yayını). "
          + "Uygulama hiçbir veri göndermez; yalnız sizin trafiğiniz sürerken gecikmeyi ölçer."
        : "Şimdi büyük bir indirme başlatın (oyun güncellemesi, büyük dosya). "
          + "Uygulama hiçbir veri indirmez; yalnız sizin trafiğiniz sürerken gecikmeyi ölçer.";

    public async Task<LoadExperimentResult> RunAsync(
        NetworkFingerprint network,
        LoadExperimentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(request);

        var probe = request.Probe.For(request.Endpoint);

        var idle = await _probe.MeasureAsync(network, probe, cancellationToken).ConfigureAwait(false);
        if (!idle.HasRemoteConnectivity)
        {
            return LoadExperimentResult.Failed(request.Direction, "Boştaki gecikme ölçülemedi; hedef yanıt vermedi.");
        }

        if (idle.Load.State != LatencyLoadState.Idle)
        {
            return LoadExperimentResult.Failed(
                request.Direction,
                "Ölçüm başlarken hat zaten meşguldü; boştaki değer alınamadığı için karşılaştırma yapılamaz.");
        }

        var capacity = request.Capacity.Observing(idle.Load, idle.MeasuredAt);
        var waited = await WaitForLoadAsync(network, request, capacity, cancellationToken).ConfigureAwait(false);

        if (!waited)
        {
            return new LoadExperimentResult
            {
                Direction = request.Direction,
                Idle = idle,
                Capacity = capacity,
                Failure = "Beklenen süre içinde yeterli trafik görülmedi; yük altındaki gecikme ölçülmedi.",
            };
        }

        var loaded = await _probe.MeasureAsync(network, probe, cancellationToken).ConfigureAwait(false);
        capacity = capacity.Observing(loaded.Load, loaded.MeasuredAt);

        if (!loaded.HasRemoteConnectivity)
        {
            return new LoadExperimentResult
            {
                Direction = request.Direction,
                Idle = idle,
                Capacity = capacity,
                Failure = "Yük altındayken hedef yanıt vermedi.",
            };
        }

        // The measurement carries its own load classification across exactly the window
        // it covers. If the transfer stopped halfway through it, the window is not a
        // loaded one however busy the link was when the wait ended.
        if (!IsLoadedInDirection(loaded.Load, request.Direction, capacity))
        {
            return new LoadExperimentResult
            {
                Direction = request.Direction,
                Idle = idle,
                Capacity = capacity,
                Failure = "Trafik ölçüm penceresi boyunca sürmedi; sonuç yük altındaki gecikme sayılmaz.",
            };
        }

        _log?.Invoke(
            $"latency.load.measured: {request.Direction} · boşta {idle.MedianRttMs:F1} ms → "
            + $"yük altında {loaded.MedianRttMs:F1} ms");

        return new LoadExperimentResult
        {
            Direction = request.Direction,
            Idle = idle,
            Loaded = loaded,
            ObservedLoad = loaded.Load,
            Capacity = capacity,
        };
    }

    private async Task<bool> WaitForLoadAsync(
        NetworkFingerprint network,
        LoadExperimentRequest request,
        LinkCapacityEstimate capacity,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var previous = _load.Read(network);

        while (deadline.Elapsed < request.LoadWaitTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _delay(PollInterval, cancellationToken).ConfigureAwait(false);

            var current = _load.Read(network);
            var sample = NetworkLoadSample.Between(previous, current);
            previous = current;

            if (IsLoadedInDirection(sample, request.Direction, capacity))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a window was busy in the direction this experiment is about.</summary>
    private static bool IsLoadedInDirection(
        NetworkLoadSample sample,
        LoadDirection direction,
        LinkCapacityEstimate capacity)
    {
        if (sample.State == LatencyLoadState.Unknown)
        {
            return false;
        }

        return direction == LoadDirection.Upload
            ? sample.UplinkKbps >= capacity.LoadedUplinkThresholdKbps
            : sample.DownlinkKbps >= capacity.LoadedDownlinkThresholdKbps;
    }
}
