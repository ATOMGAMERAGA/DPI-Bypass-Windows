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
            var sweep = await SweepOwnedPoliciesAsync(CancellationToken.None).ConfigureAwait(false);
            Report(LoadedLaneStage.Cancelled, string.Empty, $"İptal edildi. {sweep.Describe()}");

            // A sweep that failed leaves a rate limit on the machine. That is a failure
            // with a recovery action, not a tidy cancellation, and it says so.
            return new LatencyOptimizationResult
            {
                Status = sweep.IsClean ? LatencyOptimizationStatus.Cancelled : LatencyOptimizationStatus.Failed,
                StatusLine = sweep.IsClean
                    ? $"Derin test iptal edildi. {sweep.Describe()}"
                    : "Derin test iptal edildi, ancak bu uygulamanın oluşturduğu QoS ilkeleri kaldırılamadı; "
                        + $"gönderim hızınız hâlâ sınırlı olabilir. {sweep.Failure} "
                        + "\"Ayarları geri al\" ile yeniden deneyin.",
                RestoreFailed = !sweep.IsClean,
                TargetLabel = request.Target.Describe(),
                Lanes =
                [
                    new LatencyLaneReport
                    {
                        Lane = LatencyLane.TrafficGuard,
                        State = sweep.IsClean ? LatencyLaneState.Incomplete : LatencyLaneState.Blocked,
                        Detail = sweep.Describe(),
                    },
                ],
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

        // The same choice the idle lane makes, made the same way: survey the target's own
        // addresses and pin the one that answers. Taking Endpoints[0] regardless meant a
        // target whose first address is silent produced a failure on one lane and a
        // measurement on the other.
        var choice = await LatencyEndpointSelector.ChooseAsync(
            resolution,
            (candidate, token) => _probe.MeasureAsync(network, LatencyProbeRequest.Survey.For(candidate), token),
            cancellationToken).ConfigureAwait(false);

        var endpoint = choice.Endpoint;
        var probe = request.Probe.For(endpoint);

        if (choice.Notice is not null)
        {
            return Failed(request, choice.Notice, network.Key, endpoint);
        }

        // --- 2. a quiet link, then the idle baseline ---------------------------------
        Report(LoadedLaneStage.WaitingForQuietLink, StopInstruction(LoadDirection.Upload));

        // A link that never went quiet means the "idle" baseline was measured over
        // somebody's download. Every queueing number in this run is that baseline
        // subtracted from a loaded one, so continuing without saying so would report a
        // queue that had already been counted into both halves.
        var quiet = await _load
            .WaitForQuietLinkAsync(network, TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

        if (!quiet)
        {
            return Incomplete(
                request,
                "Hat ölçüm için yeterince boşalmadı; boştaki gecikme ölçülemedi. "
                    + "Süren aktarımları durdurup yeniden deneyin.",
                network.Key,
                endpoint);
        }

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
        var downloadSkipped = (string?)null;

        if (request.MeasureDownload)
        {
            Report(LoadedLaneStage.AwaitingUploadStop, StopInstruction(LoadDirection.Upload));
            var settled = await _load
                .WaitForQuietLinkAsync(network, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);

            // The download half is measured against the idle baseline, so it needs the
            // upload to have actually stopped. If it did not, the half is skipped and
            // said to be skipped, rather than producing a difference of two loaded
            // windows and labelling it download queueing.
            if (!settled)
            {
                downloadSkipped = "Gönderim durmadığı için indirme ölçümü yapılmadı.";
            }
        }

        if (request.MeasureDownload && downloadSkipped is null)
        {
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

        var notices = BuildNotices(resolution, upload, download, capacity, downloadSkipped);
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

            // Deliberately null. This lane changes no adapter setting, so there is no
            // "idle after" to report; putting the loaded window here is what let the card
            // print a round trip measured mid-upload as the user's idle ping.
            After = null,
            UploadLoaded = upload.Loaded,
            DownloadLoaded = download?.Loaded,
            UploadLoadedAfter = guard.LoadedAfter,
            Lanes = BuildLanes(endpoint, upload, download, downloadSkipped, guard),
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

    /// <summary>
    /// What a sweep of this application's own QoS policies actually achieved.
    /// </summary>
    /// <remarks>
    /// Returning a bare count made "there was nothing to remove" and "removal failed"
    /// both come back as zero, so a rollback that left a rate limit in place printed the
    /// same reassuring sentence as a clean run. A user whose upload is still capped needs
    /// to be told, and told what to do about it.
    /// </remarks>
    public readonly record struct QosSweepResult(bool Succeeded, int Removed, string? Failure)
    {
        public static readonly QosSweepResult Nothing = new(true, 0, null);

        /// <summary>Whether the machine is now free of policies this application made.</summary>
        public bool IsClean => Succeeded;

        public string Describe() => (Succeeded, Removed) switch
        {
            (false, _) => $"QoS ilkeleri kaldırılamadı: {Failure}",
            (true, 0) => "Kaldırılacak bir QoS ilkesi yoktu.",
            _ => $"Bu çalışmanın oluşturduğu {Removed} QoS ilkesi kaldırıldı.",
        };
    }

    /// <summary>Removes every QoS policy this application owns, wherever it came from.</summary>
    public async Task<int> ClearOwnedPoliciesAsync(CancellationToken cancellationToken = default)
        => (await SweepOwnedPoliciesAsync(cancellationToken).ConfigureAwait(false)).Removed;

    /// <summary>The same sweep, with the failure kept rather than flattened into zero.</summary>
    public async Task<QosSweepResult> SweepOwnedPoliciesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var removed = await _qos.RemoveAllOwnedAsync(cancellationToken).ConfigureAwait(false);
            return new QosSweepResult(true, removed, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"latency.guard: ilkeler temizlenemedi ({ex.Message}).");
            return new QosSweepResult(false, 0, ex.Message);
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
                MaximumTrials = request.MaximumTrialsForTest ?? TrafficGuardRequest.DefaultMaximumTrials,
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
        LinkCapacityEstimate capacity,
        string? downloadSkipped)
    {
        var notices = new List<string>();

        if (resolution.Notice is not null)
        {
            notices.Add(resolution.Notice);
        }

        if (downloadSkipped is not null)
        {
            notices.Add(downloadSkipped);
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

    /// <summary>What this run measured and what it did not, one line each.</summary>
    private static IReadOnlyList<LatencyLaneReport> BuildLanes(
        LatencyEndpoint endpoint,
        LoadExperimentResult upload,
        LoadExperimentResult? download,
        string? downloadSkipped,
        TrafficGuardState guard)
    {
        var lanes = new List<LatencyLaneReport>
        {
            new()
            {
                Lane = LatencyLane.TargetMeasurement,
                State = LatencyLaneState.Completed,
                Detail = $"{endpoint.Label} ölçüldü ({endpoint.ProtocolLabel}).",
            },
            new()
            {
                Lane = LatencyLane.LoadedLatency,

                // "Succeeded" here means enough load and enough samples to compare. A run
                // where the user never started a transfer is incomplete, not a finding of
                // no queueing.
                State = upload.Succeeded || download is { Succeeded: true }
                    ? LatencyLaneState.Completed
                    : LatencyLaneState.Incomplete,
                Detail = (upload.Succeeded, download?.Succeeded, downloadSkipped) switch
                {
                    (true, true, _) => "Gönderim ve indirme sırasındaki gecikme ölçüldü.",
                    (true, _, { } skipped) => $"Gönderim sırasındaki gecikme ölçüldü. {skipped}",
                    (true, _, _) => "Gönderim sırasındaki gecikme ölçüldü.",
                    (false, true, _) => "İndirme sırasındaki gecikme ölçüldü; gönderim ölçümü tamamlanamadı.",
                    _ => upload.Failure ?? "Yeterli yük oluşmadığı için ölçüm tamamlanamadı.",
                },
            },
        };

        lanes.Add(new LatencyLaneReport
        {
            Lane = LatencyLane.TrafficGuard,
            State = guard.Status switch
            {
                TrafficGuardStatus.Off => LatencyLaneState.Available,
                TrafficGuardStatus.Active => LatencyLaneState.Completed,
                TrafficGuardStatus.RolledBack => LatencyLaneState.Completed,
                TrafficGuardStatus.ApplicationNotRunning => LatencyLaneState.Blocked,
                TrafficGuardStatus.NotMeasured => LatencyLaneState.Incomplete,
                _ => LatencyLaneState.Incomplete,
            },
            Detail = string.IsNullOrWhiteSpace(guard.Summary)
                ? "Gönderim sınırı denenmedi."
                : guard.Summary,
        });

        return lanes;
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

    /// <summary>
    /// The run could not finish measuring, which is not the same as finding no gain.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Failed"/> because the user's next step differs: this one
    /// names the condition that was missing and can be retried immediately.
    /// </remarks>
    private LatencyOptimizationResult Incomplete(
        LoadedLaneRequest request,
        string reason,
        string networkKey,
        LatencyEndpoint? endpoint = null)
    {
        Report(LoadedLaneStage.Failed, string.Empty, reason);

        return new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.NeedsDeepTest,
            StatusLine = reason,
            NetworkKey = networkKey,
            TargetLabel = endpoint?.Label ?? request.Target.Describe(),
            TargetProtocol = endpoint?.ProtocolLabel ?? string.Empty,
            Lanes =
            [
                new LatencyLaneReport
                {
                    Lane = LatencyLane.LoadedLatency,
                    State = LatencyLaneState.Incomplete,
                    Detail = reason,
                },
            ],
        };
    }

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
