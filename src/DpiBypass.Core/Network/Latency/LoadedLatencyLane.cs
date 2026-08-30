using DpiBypass.Core.Logging;

namespace DpiBypass.Core.Network;

public sealed record LoadedLaneRequest
{
    public LatencyTargetSpec Target { get; init; } = LatencyTargetSpec.Reference;

    /// <summary>Whether to try a send-rate limit if queueing is found.</summary>
    public bool RunTrafficGuard { get; init; }

    /// <summary>
    /// The executable whose bulk sending may be paced. Required for the guard.
    /// </summary>
    /// <remarks>
    /// Never inferred. The app cannot tell which of a user's uploads they would rather
    /// slow down, and guessing wrong means throttling the thing they care about.
    /// </remarks>
    public string? BulkApplication { get; init; }

    public LinkCapacityEstimate Capacity { get; init; } = LinkCapacityEstimate.Unknown;

    public LatencyProbeRequest Probe { get; init; } = LatencyProbeRequest.Benchmark;

    /// <summary>Skip the download half when the user only cares about their uplink.</summary>
    public bool MeasureDownload { get; init; } = true;
}

/// <summary>
/// The loaded-latency lane: what happens to the round trip while the link is busy.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from the idle optimizer because it answers a different question with
/// different tools. The idle lane asks whether an adapter property moves the round trip
/// when nothing else is happening, and usually the honest answer is no. This lane asks
/// what happens to that same round trip while the machine is sending or receiving at
/// full rate - which on a typical home connection is where the tens or hundreds of
/// milliseconds actually are.
/// </para>
/// <para>
/// It runs only when the user asks for it, and only measures while traffic the user
/// started is running. Nothing here generates load.
/// </para>
/// </remarks>
public sealed class LoadedLatencyLane
{
    private readonly ILatencyProbe _probe;
    private readonly ILatencyTargetResolver _targets;
    private readonly ILoadExperiment _load;
    private readonly IQosController _qos;
    private readonly ILatencySnapshotStore _snapshots;
    private readonly Func<NetworkFingerprint> _capture;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;

    public LoadedLatencyLane(
        ILatencyProbe? probe = null,
        ILatencyTargetResolver? targets = null,
        ILoadExperiment? load = null,
        IQosController? qos = null,
        ILatencySnapshotStore? snapshots = null,
        Func<NetworkFingerprint>? capture = null,
        Func<DateTimeOffset>? now = null,
        Action<string>? log = null)
    {
        _log = log ?? AppLog.InfoSink;
        _probe = probe ?? new LatencyProbe();
        _targets = targets ?? new LatencyTargetResolver(log: _log);
        _load = load ?? new ObservedLoadExperiment(_probe, log: _log);
        _qos = qos ?? new WindowsQosController(_log);
        _snapshots = snapshots ?? new LatencySnapshotStore();
        _capture = capture ?? NetworkFingerprint.Capture;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>What the user must do for the experiment to have anything to measure.</summary>
    public string Instruction(LoadDirection direction) => _load.Instruction(direction);

    public async Task<LatencyOptimizationResult> RunAsync(
        LoadedLaneRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var network = _capture();
        if (!network.IsOnline)
        {
            return new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Offline,
                StatusLine = "Aktif internet bağlantısı bulunamadı.",
                NetworkKey = network.Key,
            };
        }

        var resolution = await _targets.ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Offline,
                StatusLine = resolution.Failure ?? "Ölçüm hedefi çözümlenemedi.",
                NetworkKey = network.Key,
                TargetLabel = request.Target.Describe(),
            };
        }

        var endpoint = resolution.Endpoints[0];
        var probe = request.Probe.For(endpoint);
        var idle = await _probe.MeasureAsync(network, probe, cancellationToken).ConfigureAwait(false);

        if (!idle.HasRemoteConnectivity)
        {
            return new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Offline,
                StatusLine = "Hedef yanıt vermedi; yük altındaki gecikme ölçülemedi.",
                NetworkKey = network.Key,
                TargetLabel = endpoint.Label,
                TargetProtocol = endpoint.ProtocolLabel,
            };
        }

        _log?.Invoke($"latency.loaded.started: {endpoint.Label} · {endpoint.ProtocolLabel}");

        var upload = await _load
            .RunAsync(network, LoadRequest(request, endpoint, LoadDirection.Upload), cancellationToken)
            .ConfigureAwait(false);

        var download = request.MeasureDownload
            ? await _load
                .RunAsync(network, LoadRequest(request, endpoint, LoadDirection.Download), cancellationToken)
                .ConfigureAwait(false)
            : null;

        var capacity = download?.Capacity ?? upload.Capacity;
        var path = LatencyPathAnalysis.Describe(
            upload.Idle ?? idle,
            upload.Succeeded ? upload.Loaded : null,
            download is { Succeeded: true } ? download.Loaded : null);

        var guard = TrafficGuardState.Off;
        if (request.RunTrafficGuard)
        {
            guard = await RunGuardAsync(request, network, endpoint, capacity, cancellationToken).ConfigureAwait(false);
        }

        _log?.Invoke(
            $"latency.loaded.completed: gönderim kuyruğu {Describe(upload.QueueingMs)} · "
            + $"indirme kuyruğu {Describe(download?.QueueingMs)}");

        var notices = new List<string>();
        if (resolution.Notice is not null)
        {
            notices.Add(resolution.Notice);
        }

        if (upload.Failure is not null)
        {
            notices.Add($"Gönderim ölçümü: {upload.Failure}");
        }

        if (download?.Failure is not null)
        {
            notices.Add($"İndirme ölçümü: {download.Failure}");
        }

        return new LatencyOptimizationResult
        {
            Status = guard.IsActive
                ? LatencyOptimizationStatus.TrafficGuardActive
                : upload.Succeeded || download is { Succeeded: true }
                    ? LatencyOptimizationStatus.MonitoringOnly
                    : LatencyOptimizationStatus.NeedsDeepTest,
            StatusLine = LatencyReport.Loaded(network, upload.Idle ?? idle, upload, download, path, endpoint, guard),
            AdapterName = network.AdapterName ?? network.DisplayName,
            NetworkKey = network.Key,
            Before = upload.Idle ?? idle,
            After = upload.Loaded ?? download?.Loaded,
            UploadLoaded = upload.Loaded,
            DownloadLoaded = download?.Loaded,
            Path = path,
            TrafficGuard = guard,
            TargetLabel = endpoint.Label,
            TargetProtocol = endpoint.ProtocolLabel,
            RouteReferenceOnly = endpoint.RouteReferenceOnly,
            Notices = notices,
        };
    }

    /// <summary>Removes every QoS policy this application owns, wherever it came from.</summary>
    public async Task<int> ClearOwnedPoliciesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _qos.RemoveAllOwnedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"latency.guard: ilkeler temizlenemedi ({ex.Message}).");
            return 0;
        }
    }

    private async Task<TrafficGuardState> RunGuardAsync(
        LoadedLaneRequest request,
        NetworkFingerprint network,
        LatencyEndpoint endpoint,
        LinkCapacityEstimate capacity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BulkApplication))
        {
            return new TrafficGuardState
            {
                Status = TrafficGuardStatus.NotMeasured,
                Summary = "Sınırlanacak uygulama seçilmedi; Traffic Guard çalıştırılmadı.",
            };
        }

        var guard = new TrafficGuard(_qos, _load, _log);
        var outcome = await guard.RunAsync(
            new TrafficGuardRequest
            {
                Network = network,
                Endpoint = endpoint,
                ProfileId = network.Key,
                BulkApplication = request.BulkApplication,
                Capacity = capacity,
                Probe = request.Probe,
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome.Resource is not null)
        {
            await RecordResourceAsync(network, outcome.Resource, cancellationToken).ConfigureAwait(false);
        }

        return outcome.State;
    }

    /// <summary>
    /// Writes the policy into the same transaction file the adapter settings use.
    /// </summary>
    /// <remarks>
    /// Recovery reads one file. A policy recorded anywhere else would survive a crash
    /// with nothing to remove it, which is precisely the failure the file exists to
    /// prevent.
    /// </remarks>
    private async Task RecordResourceAsync(
        NetworkFingerprint network,
        LatencyResourceSnapshot resource,
        CancellationToken cancellationToken)
    {
        var snapshot = await _snapshots.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? new LatencyOptimizationSnapshot
            {
                AdapterId = network.AdapterId ?? string.Empty,
                AdapterName = network.AdapterName ?? network.DisplayName,
                NetworkKey = network.Key,
                CreatedAt = _now(),
                State = LatencyTransactionState.SnapshotCreated,
            };

        snapshot.Resources.RemoveAll(entry =>
            entry.Kind == resource.Kind
            && string.Equals(entry.TargetId, resource.TargetId, StringComparison.Ordinal));
        snapshot.Resources.Add(resource);

        await _snapshots.SaveAsync(
            snapshot with { State = LatencyTransactionState.Committed, PendingProperty = null },
            cancellationToken).ConfigureAwait(false);
    }

    private static LoadExperimentRequest LoadRequest(
        LoadedLaneRequest request,
        LatencyEndpoint endpoint,
        LoadDirection direction) => new()
        {
            Endpoint = endpoint,
            Direction = direction,
            Capacity = request.Capacity,
            Probe = request.Probe,
        };

    private static string Describe(double? queueing) => queueing is { } value ? $"{value:F0} ms" : "ölçülmedi";
}
