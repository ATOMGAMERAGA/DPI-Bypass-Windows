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

    /// <summary>Which trade-off the cap search should optimise for.</summary>
    public TrafficGuardMode GuardMode { get; init; } = TrafficGuardMode.Balanced;

    public LinkCapacityEstimate Capacity { get; init; } = LinkCapacityEstimate.Unknown;

    public LatencyProbeRequest Probe { get; init; } = LatencyProbeRequest.Benchmark;

    /// <summary>Skip the download half when the user only cares about their uplink.</summary>
    public bool MeasureDownload { get; init; } = true;

    /// <summary>
    /// How many caps the guard's search may apply, when a caller needs to bound it.
    /// </summary>
    /// <remarks>
    /// Production leaves this at the guard's own default. A test scripts a fixed number of
    /// measurement results and needs the search to ask for exactly that many.
    /// </remarks>
    internal int? MaximumTrialsForTest { get; init; }
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
/// It runs as an explicit sequence of named stages, every one of which is published to
/// the card as it starts. That is not cosmetic: the run needs the user to start a
/// transfer, stop it, and start a fresh one after the policy exists, and a build that
/// asked for the first and then silently waited for the others could not be completed by
/// anybody who was not reading the source. Nothing here generates load.
/// </para>
/// </remarks>
public sealed class LoadedLatencyLane
{
    private readonly ILatencyProbe _probe;
    private readonly ILatencyTargetResolver _targets;
    private readonly ILoadExperiment _load;
    private readonly IQosController _qos;
    private readonly ILatencySnapshotStore _snapshots;
    private readonly IProcessFlowObserver? _flows;
    private readonly IBulkApplicationResolver _applications;
    private readonly ILatencyStageReporter _stages;
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
        Action<string>? log = null,
        IProcessFlowObserver? flows = null,
        IBulkApplicationResolver? applications = null,
        ILatencyStageReporter? stages = null)
    {
        _log = log ?? AppLog.InfoSink;
        _stages = stages ?? NullStageReporter.Instance;
        _probe = probe ?? new LatencyProbe();
        _flows = flows;
        _targets = targets ?? new LatencyTargetResolver(log: _log, flows: _flows);
        _load = load ?? new ObservedLoadExperiment(_probe, log: _log, stages: _stages);
        _qos = qos ?? new WindowsQosController(_log);
        _snapshots = snapshots ?? new LatencySnapshotStore();
        _applications = applications ?? new WindowsBulkApplicationResolver(_log);
        _capture = capture ?? NetworkFingerprint.Capture;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>What the user must do for the experiment to have anything to measure.</summary>
    public string Instruction(LoadDirection direction) => _load.Instruction(direction);

    /// <summary>What the user must do to end a stage that needs a quiet link next.</summary>
    public string StopInstruction(LoadDirection direction) => _load.StopInstruction(direction);

    public async Task<LatencyOptimizationResult> RunAsync(
        LoadedLaneRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await RunStagesAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a state the user chose, not a failure, and it still has to
            // leave the machine as it was found: every policy this run created is ours,
            // and the sweep removes ours and only ours.
            var removed = await ClearOwnedPoliciesAsync(CancellationToken.None).ConfigureAwait(false);
            Report(LoadedLaneStage.Cancelled, string.Empty, removed > 0
                ? $"İptal edildi; oluşturulan {removed} QoS ilkesi kaldırıldı."
                : "İptal edildi; kaldırılacak bir ilke yoktu.");

            return new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Cancelled,
                StatusLine = removed > 0
                    ? $"Derin test iptal edildi; bu çalışmanın oluşturduğu {removed} QoS ilkesi kaldırıldı."
                    : "Derin test iptal edildi; hiçbir ayar değiştirilmemişti.",
                TargetLabel = request.Target.Describe(),
            };
        }
    }

    private async Task<LatencyOptimizationResult> RunStagesAsync(
        LoadedLaneRequest request,
        CancellationToken cancellationToken)
    {
        // --- 1. target ---------------------------------------------------------------
        Report(LoadedLaneStage.VerifyingTarget, "Ölçüm hedefi belirleniyor.");

        var network = _capture();
        if (!network.IsOnline)
        {
            return Failed(request, "Aktif internet bağlantısı bulunamadı.", network.Key);
        }

        var resolution = await _targets.ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return Failed(request, resolution.Failure ?? "Ölçüm hedefi çözümlenemedi.", network.Key);
        }

        var endpoint = resolution.Endpoints[0];
        var probe = request.Probe.For(endpoint);

        // --- 2. a quiet link, then the idle baseline ---------------------------------
        Report(LoadedLaneStage.WaitingForQuietLink, StopInstruction(LoadDirection.Upload));
        await _load.WaitForQuietLinkAsync(network, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

        Report(LoadedLaneStage.IdleBaseline, string.Empty);
        var idle = await _probe.MeasureAsync(network, probe, cancellationToken).ConfigureAwait(false);

        if (!idle.HasRemoteConnectivity)
        {
            return Failed(request, "Hedef yanıt vermedi; yük altındaki gecikme ölçülemedi.", network.Key, endpoint);
        }

        _log?.Invoke($"latency.loaded.started: {endpoint.Label} · {endpoint.ProtocolLabel}");

        // --- 3. the upload half ------------------------------------------------------
        var upload = await _load.RunAsync(
            network,
            new LoadExperimentRequest
            {
                Endpoint = endpoint,
                Direction = LoadDirection.Upload,
                Capacity = request.Capacity,
                Probe = request.Probe,
                Baseline = idle,
                WaitingStage = LoadedLaneStage.AwaitingUploadStart,
                MeasuringStage = LoadedLaneStage.MeasuringUploadBaseline,
                Instruction = Instruction(LoadDirection.Upload),
            },
            cancellationToken).ConfigureAwait(false);

        var capacity = upload.Capacity;
        var dataUsed = upload.DataUsedBytes;

        // --- 4. the download half, which is a separate stage and says so -------------
        LoadExperimentResult? download = null;
        if (request.MeasureDownload)
        {
            Report(LoadedLaneStage.AwaitingUploadStop, StopInstruction(LoadDirection.Upload));
            await _load.WaitForQuietLinkAsync(network, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);

            download = await _load.RunAsync(
                network,
                new LoadExperimentRequest
                {
                    Endpoint = endpoint,
                    Direction = LoadDirection.Download,
                    Capacity = capacity,
                    Probe = request.Probe,
                    Baseline = idle,
                    WaitingStage = LoadedLaneStage.AwaitingDownloadStart,
                    MeasuringStage = LoadedLaneStage.MeasuringDownload,
                    Instruction = Instruction(LoadDirection.Download),
                },
                cancellationToken).ConfigureAwait(false);

            capacity = download.Capacity;
            dataUsed += download.DataUsedBytes;
        }

        var path = LatencyPathAnalysis.Describe(
            upload.Idle ?? idle,
            upload.ProvesQueueing ? upload.Loaded : null,
            download is { ProvesQueueing: true } ? download.Loaded : null);

        // --- 5. the Traffic Guard, which owns its own stages -------------------------
        var guard = TrafficGuardState.Off;
        if (request.RunTrafficGuard)
        {
            guard = await RunGuardAsync(request, network, endpoint, capacity, cancellationToken)
                .ConfigureAwait(false);

            dataUsed += guard.DataUsedBytes;
        }

        _log?.Invoke(
            $"latency.loaded.completed: gönderim kuyruğu {Describe(upload.QueueingMs)} · "
            + $"indirme kuyruğu {Describe(download?.QueueingMs)}");

        var notices = BuildNotices(resolution, upload, download, capacity);
        var status = guard.IsActive
            ? LatencyOptimizationStatus.TrafficGuardActive
            : upload.Succeeded || download is { Succeeded: true }
                ? LatencyOptimizationStatus.MonitoringOnly
                : LatencyOptimizationStatus.NeedsDeepTest;

        Report(
            guard.IsActive ? LoadedLaneStage.Committed
                : guard.Status == TrafficGuardStatus.RolledBack ? LoadedLaneStage.RolledBack
                : LoadedLaneStage.NoGain,
            string.Empty,
            guard.Summary,
            dataUsed);

        return new LatencyOptimizationResult
        {
            Status = status,
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
            Capacity = capacity,
            DataUsedBytes = dataUsed,
            Candidates = resolution.Candidates,
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

        // Free text becomes a running process here or the guard does not run. A match
        // condition nobody checked is a policy that silently governs nothing.
        var application = _applications.Resolve(request.BulkApplication);
        if (application is not { IsRunning: true })
        {
            return new TrafficGuardState
            {
                Status = TrafficGuardStatus.ApplicationNotRunning,
                Summary = $"'{request.BulkApplication}' çalışan süreçler arasında bulunamadı; "
                    + "sınırlanacak bir gönderim yok.",
                Mode = request.GuardMode,
            };
        }

        var guard = new TrafficGuard(_qos, _load, _log, _flows, _stages, now: _now);
        var outcome = await guard.RunAsync(
            new TrafficGuardRequest
            {
                Network = network,
                Endpoint = endpoint,
                ProfileId = network.Key,
                BulkApplication = application,
                Capacity = capacity,
                Mode = request.GuardMode,
                Probe = request.Probe,
                MaximumTrials = request.MaximumTrialsForTest ?? new TrafficGuardRequest
                {
                    Network = network,
                    Endpoint = endpoint,
                    ProfileId = network.Key,
                    BulkApplication = application,
                }.MaximumTrials,
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

    /// <summary>
    /// Everything the user should know that is not a number, including the honest limits.
    /// </summary>
    /// <remarks>
    /// The download notice matters most. A rate limit applied on this machine paces what
    /// this machine sends; the queue that fills during a download is in the operator's
    /// equipment, upstream of anything a Windows QoS policy can reach. Where that is what
    /// the measurements show, it is reported as a diagnosis with the fix that would
    /// actually work, rather than as something this application is about to do.
    /// </remarks>
    private static IReadOnlyList<string> BuildNotices(
        LatencyTargetResolution resolution,
        LoadExperimentResult upload,
        LoadExperimentResult? download,
        LinkCapacityEstimate capacity)
    {
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

        if (!capacity.IsConfident(LoadDirection.Upload))
        {
            notices.Add("Gönderim kapasitesi plato yapacak kadar uzun ölçülemedi; hattın dolup dolmadığı "
                + "belirlenemedi. Bu, kuyruklanma yok anlamına gelmez.");
        }

        if (download is { ProvesQueueing: true, QueueingMs: > LatencyPathAnalysis.QueueingThresholdMs } measured)
        {
            notices.Add($"İndirme sırasında gecikme {measured.QueueingMs:F0} ms artıyor. Bu kuyruk operatörün "
                + "ekipmanında oluşur; bu bilgisayarda uygulanan bir gönderim sınırı ona ulaşamaz. "
                + "Kalıcı çözüm yönlendiricide SQM/CAKE veya FQ-CoDel gibi bir kuyruk yönetimidir.");
        }

        return notices;
    }

    private void Report(
        LoadedLaneStage stage,
        string instruction,
        string? outcome = null,
        long dataUsed = 0)
        => _stages.Report(new LoadedLaneProgress
        {
            Stage = stage,
            Title = LoadedLaneProgress.TitleFor(stage),
            Instruction = instruction,
            Outcome = outcome,
            DataUsedBytes = dataUsed,
            CanCancel = stage is not (LoadedLaneStage.Committed or LoadedLaneStage.NoGain
                or LoadedLaneStage.RolledBack or LoadedLaneStage.Cancelled or LoadedLaneStage.Failed),
        });

    private LatencyOptimizationResult Failed(
        LoadedLaneRequest request,
        string reason,
        string networkKey,
        LatencyEndpoint? endpoint = null)
    {
        Report(LoadedLaneStage.Failed, string.Empty, reason);

        return new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Offline,
            StatusLine = reason,
            NetworkKey = networkKey,
            TargetLabel = endpoint?.Label ?? request.Target.Describe(),
            TargetProtocol = endpoint?.ProtocolLabel ?? string.Empty,
        };
    }

    private static string Describe(double? queueing) => queueing is { } value ? $"{value:F0} ms" : "ölçülmedi";
}
