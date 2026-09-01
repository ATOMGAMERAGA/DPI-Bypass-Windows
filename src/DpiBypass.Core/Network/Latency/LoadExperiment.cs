using System.Diagnostics;

namespace DpiBypass.Core.Network;

public enum LoadDirection
{
    Upload = 0,
    Download = 1,
}

/// <summary>One stage of the deep test: measure the round trip while the link is busy.</summary>
public sealed record LoadExperimentRequest
{
    public required LatencyEndpoint Endpoint { get; init; }

    public required LoadDirection Direction { get; init; }

    public LinkCapacityEstimate Capacity { get; init; } = LinkCapacityEstimate.Unknown;

    public LatencyProbeRequest Probe { get; init; } = LatencyProbeRequest.Benchmark;

    /// <summary>How long to wait for the user's own traffic before giving up.</summary>
    public TimeSpan LoadWaitTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the link must stay quiet before an idle baseline is taken.</summary>
    public TimeSpan QuietWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Whether the loaded window has to reach the measured ceiling to count.
    /// </summary>
    /// <remarks>
    /// On for anything that will be turned into a bufferbloat conclusion. Off only for a
    /// pure ramp, where the point is to find the ceiling rather than to sit at it.
    /// </remarks>
    public bool RequireSaturation { get; init; } = true;

    /// <summary>Which stage the card should be showing while this runs.</summary>
    public LoadedLaneStage MeasuringStage { get; init; } = LoadedLaneStage.MeasuringUploadBaseline;

    public LoadedLaneStage WaitingStage { get; init; } = LoadedLaneStage.AwaitingUploadStart;

    /// <summary>What the user is asked to do while the wait stage is showing.</summary>
    public string? Instruction { get; init; }

    /// <summary>Whether to take a fresh idle baseline, or reuse one already measured.</summary>
    public LatencyMeasurement? Baseline { get; init; }
}

public sealed record LoadExperimentResult
{
    public required LoadDirection Direction { get; init; }

    /// <summary>The idle window this experiment's loaded window is compared against.</summary>
    public LatencyMeasurement? Idle { get; init; }

    public LatencyMeasurement? Loaded { get; init; }

    /// <summary>What the link actually carried during the loaded window.</summary>
    public NetworkLoadSample ObservedLoad { get; init; } = NetworkLoadSample.Unknown;

    /// <summary>What that amounted to relative to the link's own ceiling.</summary>
    public LinkLoadClassification Classification { get; init; } = LinkLoadClassification.Unknown;

    public LinkCapacityEstimate Capacity { get; init; } = LinkCapacityEstimate.Unknown;

    /// <summary>Bytes the link carried while this stage ran, in both directions.</summary>
    public long DataUsedBytes { get; init; }

    public string? Failure { get; init; }

    public bool Succeeded => Idle is not null && Loaded is not null && Failure is null;

    /// <summary>
    /// Whether this window is allowed to support a statement about queueing.
    /// </summary>
    /// <remarks>
    /// Only a saturated window can. Below the ceiling the sender is not outrunning the
    /// drain rate, so any extra delay measured is not a queue this machine created and
    /// pacing the sender would not remove it.
    /// </remarks>
    public bool ProvesQueueing => Succeeded && Classification == LinkLoadClassification.Saturated;

    /// <summary>Added median delay under load, only when the link was genuinely full.</summary>
    public double? QueueingMs => ProvesQueueing
        ? LatencyPathAnalysis.Describe(
            Idle!,
            Direction == LoadDirection.Upload ? Loaded : null,
            Direction == LoadDirection.Download ? Loaded : null).QueueingMs
        : null;

    /// <summary>The rate the loaded window actually carried, in the tested direction.</summary>
    public double ThroughputKbps => Direction == LoadDirection.Upload
        ? ObservedLoad.UplinkKbps
        : ObservedLoad.DownlinkKbps;

    public static LoadExperimentResult Failed(LoadDirection direction, string reason)
        => new() { Direction = direction, Failure = reason };
}

/// <summary>Measures latency while the link is genuinely busy in one direction.</summary>
public interface ILoadExperiment
{
    /// <summary>What the user has to do for this experiment to be able to run.</summary>
    string Instruction(LoadDirection direction);

    /// <summary>What the user has to do to end a stage that needs a quiet link next.</summary>
    string StopInstruction(LoadDirection direction);

    Task<LoadExperimentResult> RunAsync(
        NetworkFingerprint network,
        LoadExperimentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Waits until the link has been quiet long enough to take a baseline on.</summary>
    Task<bool> WaitForQuietLinkAsync(
        NetworkFingerprint network,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Measures the loaded window from traffic the user creates, and sends nothing itself.
/// </summary>
/// <remarks>
/// <para>
/// Filling a home uplink means pushing tens of megabytes at somebody else's server, and
/// no consent the user gives covers the server on the other end. So this generates no
/// load at all: it asks the user to start the transfer they were going to start anyway,
/// watches the adapter's own counters until the link really is at its ceiling, and
/// measures the round trip while it is. Nothing leaves this machine that the user did not
/// start, which is also why there is no automatic load provider to disable on a metered
/// connection.
/// </para>
/// <para>
/// The wait is also where capacity is learned. Windows taken while the transfer ramps up
/// feed <see cref="LinkCapacityRamp"/>, and the experiment only proceeds once that ramp
/// has flattened - so "loaded" means "at the measured ceiling" rather than "faster than
/// some fixed number".
/// </para>
/// </remarks>
public sealed class ObservedLoadExperiment : ILoadExperiment
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    private readonly ILatencyProbe _probe;
    private readonly INetworkLoadSampler _load;
    private readonly ILatencyStageReporter _stages;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _log;

    public ObservedLoadExperiment(
        ILatencyProbe probe,
        INetworkLoadSampler? loadSampler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null,
        ILatencyStageReporter? stages = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _load = loadSampler ?? new NetworkLoadSampler();
        _delay = delay ?? Task.Delay;
        _log = log;
        _stages = stages ?? NullStageReporter.Instance;
    }

    public string Instruction(LoadDirection direction) => direction == LoadDirection.Upload
        ? "Şimdi büyük bir dosya göndermeye başlayın (bulut yedeklemesi, video yükleme, oyun yayını). "
          + "Uygulama hiçbir veri göndermez; yalnız sizin trafiğiniz sürerken gecikmeyi ölçer."
        : "Şimdi büyük bir indirme başlatın (oyun güncellemesi, büyük dosya). "
          + "Uygulama hiçbir veri indirmez; yalnız sizin trafiğiniz sürerken gecikmeyi ölçer.";

    public string StopInstruction(LoadDirection direction) => direction == LoadDirection.Upload
        ? "Şimdi gönderimi durdurun ve hattın boşalmasını bekleyin."
        : "Şimdi indirmeyi durdurun ve hattın boşalmasını bekleyin.";

    public async Task<bool> WaitForQuietLinkAsync(
        NetworkFingerprint network,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);

        var elapsed = Stopwatch.StartNew();
        var previous = _load.Read(network);
        var quietWindows = 0;

        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _delay(PollInterval, cancellationToken).ConfigureAwait(false);

            var current = _load.Read(network);
            var sample = NetworkLoadSample.Between(previous, current);
            previous = current;

            _stages.Report(new LoadedLaneProgress
            {
                Stage = LoadedLaneStage.WaitingForQuietLink,
                Title = LoadedLaneProgress.TitleFor(LoadedLaneStage.WaitingForQuietLink),
                Instruction = "Ölçüm başlamadan önce hattın boşta olması gerekiyor.",
                InstantKbps = sample.State == LatencyLoadState.Unknown
                    ? null
                    : Math.Max(sample.UplinkKbps, sample.DownlinkKbps),
                Remaining = timeout - elapsed.Elapsed,
            });

            // Two consecutive quiet windows, not one: a transfer between segments can
            // look idle for a moment without having stopped.
            quietWindows = sample.State == LatencyLoadState.Idle ? quietWindows + 1 : 0;
            if (quietWindows >= 2)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<LoadExperimentResult> RunAsync(
        NetworkFingerprint network,
        LoadExperimentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(request);

        var startCounters = _load.Read(network);
        var probe = request.Probe.For(request.Endpoint);

        var idle = request.Baseline;
        if (idle is null)
        {
            _stages.Report(Progress(request, LoadedLaneStage.IdleBaseline, string.Empty, startCounters, startCounters, request.Capacity));
            idle = await _probe.MeasureAsync(network, probe, cancellationToken).ConfigureAwait(false);
        }

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

        var (reached, capacity) = await WaitForLoadAsync(network, request, startCounters, cancellationToken)
            .ConfigureAwait(false);

        if (!reached)
        {
            return new LoadExperimentResult
            {
                Direction = request.Direction,
                Idle = idle,
                Capacity = capacity,
                DataUsedBytes = DataUsed(startCounters, _load.Read(network)),
                Failure = request.RequireSaturation && capacity.ConfidenceFor(request.Direction) == LinkCapacityConfidence.Weak
                    ? "Aktarım hızı beklenen sürede plato yapmadı; hattın dolduğu kanıtlanamadığı için "
                        + "yük altındaki gecikme ölçülmedi."
                    : "Beklenen süre içinde yeterli trafik görülmedi; yük altındaki gecikme ölçülmedi.",
            };
        }

        _stages.Report(Progress(
            request,
            request.MeasuringStage,
            string.Empty,
            startCounters,
            _load.Read(network),
            capacity));

        var loaded = await _probe.MeasureAsync(network, probe, cancellationToken).ConfigureAwait(false);
        var classification = capacity.Classify(loaded.Load, request.Direction);
        var dataUsed = DataUsed(startCounters, _load.Read(network));

        if (!loaded.HasRemoteConnectivity)
        {
            return new LoadExperimentResult
            {
                Direction = request.Direction,
                Idle = idle,
                Capacity = capacity,
                DataUsedBytes = dataUsed,
                Failure = "Yük altındayken hedef yanıt vermedi.",
            };
        }

        // The measurement carries its own load classification across exactly the window
        // it covers. If the transfer stopped or slowed halfway through it, the window is
        // not a saturated one however busy the link was when the wait ended.
        if (request.RequireSaturation && classification != LinkLoadClassification.Saturated)
        {
            return new LoadExperimentResult
            {
                Direction = request.Direction,
                Idle = idle,
                Capacity = capacity,
                ObservedLoad = loaded.Load,
                Classification = classification,
                DataUsedBytes = dataUsed,
                Failure = classification == LinkLoadClassification.Unknown
                    ? "Ölçüm penceresinde bağdaştırıcı sayaçları okunamadı; sonuç yük altındaki gecikme sayılmaz."
                    : $"Aktarım ölçüm penceresi boyunca hattı doldurmadı ({Describe(classification)}); "
                        + "kuyruklanma bu veriden çıkarılamaz.",
            };
        }

        _log?.Invoke(
            $"latency.load.measured: {request.Direction} · boşta {idle.MedianRttMs:F1} ms → "
            + $"yük altında {loaded.MedianRttMs:F1} ms · {classification}");

        return new LoadExperimentResult
        {
            Direction = request.Direction,
            Idle = idle,
            Loaded = loaded,
            ObservedLoad = loaded.Load,
            Classification = classification,
            Capacity = capacity,
            DataUsedBytes = dataUsed,
        };
    }

    /// <summary>
    /// Waits for the user's transfer to reach the link's ceiling, learning it on the way.
    /// </summary>
    /// <returns>Whether the link got there, and what is now known about its capacity.</returns>
    private async Task<(bool Reached, LinkCapacityEstimate Capacity)> WaitForLoadAsync(
        NetworkFingerprint network,
        LoadExperimentRequest request,
        NetworkCounters? startCounters,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var previous = _load.Read(network);
        var ramp = new LinkCapacityRamp();
        var capacity = request.Capacity;
        var instruction = request.Instruction ?? Instruction(request.Direction);

        while (elapsed.Elapsed < request.LoadWaitTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _delay(PollInterval, cancellationToken).ConfigureAwait(false);

            var current = _load.Read(network);
            var sample = NetworkLoadSample.Between(previous, current);
            previous = current;

            ramp.Add(sample, request.Direction);
            var estimate = ramp.Evaluate();
            if (estimate.Confidence != LinkCapacityConfidence.None)
            {
                capacity = capacity.With(request.Direction, estimate, DateTimeOffset.UtcNow);
            }

            var classification = capacity.Classify(sample, request.Direction);

            _stages.Report(new LoadedLaneProgress
            {
                Stage = request.WaitingStage,
                Title = LoadedLaneProgress.TitleFor(request.WaitingStage),
                Instruction = instruction,
                Direction = request.Direction,
                Target = request.Endpoint.Label,
                InstantKbps = sample.State == LatencyLoadState.Unknown
                    ? null
                    : request.Direction == LoadDirection.Upload ? sample.UplinkKbps : sample.DownlinkKbps,
                CapacityShare = capacity.ShareOfCapacity(sample, request.Direction),
                Load = classification,
                Remaining = request.LoadWaitTimeout - elapsed.Elapsed,
                DataUsedBytes = DataUsed(startCounters, current),
            });

            if (!request.RequireSaturation)
            {
                if (classification is LinkLoadClassification.HighUtilisation or LinkLoadClassification.Saturated
                    || (estimate.IsMeasured && sample.State != LatencyLoadState.Unknown))
                {
                    return (true, capacity);
                }

                continue;
            }

            if (classification == LinkLoadClassification.Saturated)
            {
                return (true, capacity);
            }
        }

        return (false, capacity);
    }

    private LoadedLaneProgress Progress(
        LoadExperimentRequest request,
        LoadedLaneStage stage,
        string instruction,
        NetworkCounters? from,
        NetworkCounters? to,
        LinkCapacityEstimate capacity) => new()
        {
            Stage = stage,
            Title = LoadedLaneProgress.TitleFor(stage),
            Instruction = instruction,
            Direction = request.Direction,
            Target = request.Endpoint.Label,
            CapacityShare = null,
            DataUsedBytes = DataUsed(from, to),
            Load = capacity.IsConfident(request.Direction)
                ? LinkLoadClassification.Traffic
                : LinkLoadClassification.Unknown,
        };

    /// <summary>Bytes the adapter carried between two counter reads, in both directions.</summary>
    private static long DataUsed(NetworkCounters? from, NetworkCounters? to)
    {
        if (from is not { } start || to is not { } end)
        {
            return 0;
        }

        var sent = end.BytesSent - start.BytesSent;
        var received = end.BytesReceived - start.BytesReceived;
        return sent < 0 || received < 0 ? 0 : sent + received;
    }

    private static string Describe(LinkLoadClassification classification) => classification switch
    {
        LinkLoadClassification.Quiet => "hat boştaydı",
        LinkLoadClassification.Traffic => "trafik vardı ancak kapasitenin çok altındaydı",
        LinkLoadClassification.HighUtilisation => "kapasiteye yaklaştı fakat doygunluğa ulaşmadı",
        LinkLoadClassification.Saturated => "hat doydu",
        _ => "durum belirlenemedi",
    };
}
