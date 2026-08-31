using System.Globalization;

namespace DpiBypass.Core.Network;

public enum TrafficGuardStatus
{
    /// <summary>Not asked for.</summary>
    Off = 0,

    /// <summary>Windows cannot do this here, so nothing was attempted.</summary>
    Unavailable = 1,

    /// <summary>Someone else's QoS policy is in the way; nothing was changed.</summary>
    ConflictSkipped = 2,

    /// <summary>Measured, and there is no queueing to remove.</summary>
    NoQueueing = 3,

    /// <summary>A cap is in place, confirmed in its own round to reduce loaded latency.</summary>
    Active = 4,

    /// <summary>Caps were created, none earned its place, and all have been removed.</summary>
    RolledBack = 5,

    /// <summary>The experiment could not be completed.</summary>
    Failed = 6,

    /// <summary>The measurement needed for a verdict was never taken.</summary>
    NotMeasured = 7,

    /// <summary>
    /// A policy exists but no new flow appeared for it to attach to.
    /// </summary>
    /// <remarks>
    /// Its own state because it is the one failure the user can fix: Windows matches a QoS
    /// policy when a transport endpoint is created, so a transfer that was already running
    /// when the policy appeared is not covered by it and never will be. The answer is to
    /// restart the transfer, not to retry the measurement.
    /// </remarks>
    NeedsNewConnection = 8,

    /// <summary>The application the user named is not running.</summary>
    ApplicationNotRunning = 9,
}

/// <summary>What the loaded-latency lane has established, and what it left behind.</summary>
public sealed record TrafficGuardState
{
    public static readonly TrafficGuardState Off = new()
    {
        Status = TrafficGuardStatus.Off,
        Summary = "Kapalı.",
    };

    public required TrafficGuardStatus Status { get; init; }

    public required string Summary { get; init; }

    /// <summary>The policy currently in place, if any. Always in this app's namespace.</summary>
    public string? PolicyName { get; init; }

    public ulong? ThrottleBitsPerSecond { get; init; }

    /// <summary>The application whose outbound bulk traffic is being paced.</summary>
    public string? ThrottledApplication { get; init; }

    /// <summary>What the policy actually matches, read back from the store.</summary>
    public string? PolicyMatch { get; init; }

    /// <summary>Which trade-off the cap was chosen for.</summary>
    public TrafficGuardMode Mode { get; init; } = TrafficGuardMode.Balanced;

    public double? UploadQueueingBeforeMs { get; init; }

    public double? UploadQueueingAfterMs { get; init; }

    public double? UplinkBeforeKbps { get; init; }

    public double? UplinkAfterKbps { get; init; }

    /// <summary>Loaded tail before and after, which is what the cap is chosen on.</summary>
    public double? LoadedP95BeforeMs { get; init; }

    public double? LoadedP95AfterMs { get; init; }

    /// <summary>Every cap that was applied and measured, for the report.</summary>
    public IReadOnlyList<string> Trials { get; init; } = [];

    /// <summary>Foreign policies that made this stand down, for the report.</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>Bytes the user's own transfers moved across the whole experiment.</summary>
    public long DataUsedBytes { get; init; }

    public bool IsActive => Status == TrafficGuardStatus.Active;

    /// <summary>The queueing actually removed, only when both halves were measured.</summary>
    public double? ImprovementMs => UploadQueueingBeforeMs is { } before && UploadQueueingAfterMs is { } after
        ? Math.Max(0, before - after)
        : null;

    /// <summary>The share of the unthrottled rate the kept cap leaves, when there is one.</summary>
    public double? RetainedThroughputShare => UplinkBeforeKbps is > 0 && UplinkAfterKbps is { } after
        ? after / UplinkBeforeKbps.Value
        : null;
}

public sealed record TrafficGuardRequest
{
    public required NetworkFingerprint Network { get; init; }

    public required LatencyEndpoint Endpoint { get; init; }

    /// <summary>Profile identity, used only to name the policy.</summary>
    public required string ProfileId { get; init; }

    /// <summary>
    /// The executable whose outbound bulk traffic may be paced.
    /// </summary>
    /// <remarks>
    /// Chosen by the user, never guessed, and resolved against the running process list so
    /// the policy's match condition is a real image name or path rather than free text.
    /// Pacing the wrong process would slow something they care about to speed up something
    /// they do not, and no measurement this side can tell which is which.
    /// </remarks>
    public required BulkApplicationSelection BulkApplication { get; init; }

    public LinkCapacityEstimate Capacity { get; init; } = LinkCapacityEstimate.Unknown;

    /// <summary>Which trade-off to search for.</summary>
    public TrafficGuardMode Mode { get; init; } = TrafficGuardMode.Balanced;

    /// <summary>How many caps the search is allowed to apply and measure.</summary>
    /// <remarks>
    /// Two by default. Each one costs the user a stop and a restart of their transfer, so
    /// the search is deliberately short: the shares are ordered least-disruptive first and
    /// the confirmation round is what actually decides.
    /// </remarks>
    public int MaximumTrials { get; init; } = DefaultMaximumTrials;

    /// <summary>The default, exposed so a caller can bound the search without guessing.</summary>
    public const int DefaultMaximumTrials = 2;

    /// <summary>How long to wait for the paced application to open a new flow.</summary>
    public TimeSpan NewFlowTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public LatencyProbeRequest Probe { get; init; } = LatencyProbeRequest.Benchmark;
}

/// <summary>
/// The loaded-latency lane: pace this machine's own bulk sending, if that measurably helps.
/// </summary>
/// <remarks>
/// <para>
/// On most home connections the largest number a user can actually move is not idle
/// ping - that is distance - but the tens or hundreds of milliseconds that appear the
/// moment something starts uploading. That delay is a queue, usually in the router,
/// filled by this machine faster than the uplink drains it, and the one place it can be
/// prevented is at the sender.
/// </para>
/// <para>
/// Windows already has the mechanism: a policy-based QoS rule with a throttle action hands
/// the flow to Pacer.sys, which schedules its packets. Three things follow from how that
/// works, and this class exists to respect all three. The QoS inspection module matches
/// policies as transport endpoints are created, so the transfer running when a policy
/// appears is not covered by it - a new flow is required and waited for, never assumed.
/// Windows cannot say whether a policy helped, so the loaded round trip is measured before
/// and after and the policy is deleted unless the queueing actually fell. And no single cap
/// is right for every link, so several are measured and the one that produced the best
/// trade is confirmed in a round of its own before anything is kept.
/// </para>
/// </remarks>
public sealed class TrafficGuard
{
    /// <summary>Queueing has to fall by at least this for a policy to be worth keeping.</summary>
    public const double MinimumQueueingReductionMs = TrafficGuardCapPlanner.MinimumQueueingReductionMs;

    /// <summary>And by at least this share of what it was.</summary>
    public const double MinimumQueueingReductionShare = TrafficGuardCapPlanner.MinimumQueueingReductionShare;

    /// <summary>Throughput may not fall below this share of the unthrottled rate.</summary>
    public const double MinimumRetainedThroughputShare = TrafficGuardCapPlanner.BalancedThroughputFloor;

    private readonly IQosController _qos;
    private readonly ILoadExperiment _load;
    private readonly IProcessFlowObserver? _flows;
    private readonly ILatencyStageReporter _stages;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;

    public TrafficGuard(
        IQosController qos,
        ILoadExperiment load,
        Action<string>? log = null,
        IProcessFlowObserver? flows = null,
        ILatencyStageReporter? stages = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null)
    {
        _qos = qos ?? throw new ArgumentNullException(nameof(qos));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _flows = flows;
        _stages = stages ?? NullStageReporter.Instance;
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    /// <summary>The policy left in place by a successful run, so the caller can record it.</summary>
    public sealed record TrafficGuardOutcome(TrafficGuardState State, LatencyResourceSnapshot? Resource);

    public async Task<TrafficGuardOutcome> RunAsync(
        TrafficGuardRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.BulkApplication.IsRunning)
        {
            return Plain(
                TrafficGuardStatus.ApplicationNotRunning,
                $"'{request.BulkApplication.ExecutableName}' çalışmıyor; sınırlanacak bir gönderim yok.");
        }

        var capability = await _qos.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.Available)
        {
            return Plain(
                TrafficGuardStatus.Unavailable,
                $"Windows QoS ilkesi kullanılamıyor: {capability.Reason ?? "bilinmeyen neden"}.");
        }

        if (capability.HasConflict)
        {
            _log?.Invoke(
                $"latency.guard.conflict: {capability.CompetingPolicies.Count} yabancı QoS ilkesi bulundu; "
                + "otomatik müdahale atlandı.");

            return new TrafficGuardOutcome(
                new TrafficGuardState
                {
                    Status = TrafficGuardStatus.ConflictSkipped,
                    Summary = "Bu makinede zaten hız sınırlayan bir QoS ilkesi var; "
                        + "mevcut ilkeye dokunulmaması için otomatik müdahale yapılmadı.",
                    Conflicts = capability.CompetingPolicies,
                    Mode = request.Mode,
                },
                null);
        }

        // --- the unthrottled reference ------------------------------------------------
        var before = await MeasureUploadAsync(
            request,
            LoadedLaneStage.AwaitingUploadStart,
            LoadedLaneStage.MeasuringUploadBaseline,
            _load.Instruction(LoadDirection.Upload),
            cancellationToken).ConfigureAwait(false);

        var dataUsed = before.DataUsedBytes;

        if (!before.Succeeded)
        {
            return Plain(TrafficGuardStatus.NotMeasured, before.Failure ?? "Yük altındaki gecikme ölçülemedi.", dataUsed);
        }

        if (!before.ProvesQueueing)
        {
            return Plain(
                TrafficGuardStatus.NotMeasured,
                "Gönderim hattı ölçüm penceresi boyunca doygunluğa ulaşmadı; kuyruklanma ölçülemedi. "
                    + "Bu, kuyruklanma yok demek değildir.",
                dataUsed);
        }

        var queueingBefore = before.QueueingMs ?? 0;
        if (queueingBefore < LatencyPathAnalysis.QueueingThresholdMs)
        {
            return new TrafficGuardOutcome(
                new TrafficGuardState
                {
                    Status = TrafficGuardStatus.NoQueueing,
                    Summary = $"Hat doluyken gecikme yalnız {queueingBefore:F0} ms artıyor; "
                        + "sınırlanacak bir kuyruklanma yok.",
                    UploadQueueingBeforeMs = queueingBefore,
                    UplinkBeforeKbps = before.ThroughputKbps,
                    LoadedP95BeforeMs = before.Loaded?.P95RttMs,
                    DataUsedBytes = dataUsed,
                    Mode = request.Mode,
                },
                null);
        }

        var capacity = before.Capacity.CapacityFor(LoadDirection.Upload) ?? before.ThroughputKbps;
        if (capacity <= 0)
        {
            return Plain(
                TrafficGuardStatus.NotMeasured,
                "Gönderim kapasitesi ölçülemediği için güvenli bir sınır hesaplanamadı.",
                dataUsed);
        }

        // --- search: apply a few caps and measure each one ----------------------------
        var policyName = WindowsQosController.NameFor(request.ProfileId, "bulk");
        var shares = TrafficGuardCapPlanner.SharesFor(request.Mode)
            .Take(Math.Max(1, request.MaximumTrials))
            .ToArray();

        var trials = new List<TrafficGuardCapTrial>();
        var descriptions = new List<string>();
        LatencyResourceSnapshot? resource = null;

        try
        {
            foreach (var share in shares)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cap = TrafficGuardCapPlanner.CapFor(capacity, share);
                var trial = await TrialAsync(request, policyName, cap, share, before, cancellationToken)
                    .ConfigureAwait(false);

                if (trial.Snapshot is not null)
                {
                    resource = trial.Snapshot;
                }

                dataUsed += trial.Trial?.Result.DataUsedBytes ?? 0;

                if (trial.Blocked is { } blocked)
                {
                    await RemoveSafelyAsync(policyName).ConfigureAwait(false);
                    return new TrafficGuardOutcome(
                        blocked with { DataUsedBytes = dataUsed, Mode = request.Mode },
                        null);
                }

                if (trial.Trial is { } measured)
                {
                    trials.Add(measured);
                    descriptions.Add(Describe(measured, queueingBefore));
                }
            }

            var choice = TrafficGuardCapPlanner.Choose(trials, before, request.Mode);
            if (choice is null)
            {
                var removed = await RemoveSafelyAsync(policyName).ConfigureAwait(false);
                var reason = TrafficGuardCapPlanner.ExplainRejection(trials, before, request.Mode);
                _log?.Invoke($"latency.guard.rolledback: {reason}");

                return new TrafficGuardOutcome(
                    new TrafficGuardState
                    {
                        Status = removed ? TrafficGuardStatus.RolledBack : TrafficGuardStatus.Failed,
                        Summary = removed
                            ? $"Gönderim sınırı kaldırıldı: {reason}"
                            : $"Gönderim sınırı kaldırılamadı ({reason}); ilke kurtarma için kayıtlı.",
                        PolicyName = removed ? null : policyName,
                        UploadQueueingBeforeMs = queueingBefore,
                        UplinkBeforeKbps = before.ThroughputKbps,
                        LoadedP95BeforeMs = before.Loaded?.P95RttMs,
                        Trials = descriptions,
                        DataUsedBytes = dataUsed,
                        Mode = request.Mode,
                    },
                    removed ? null : resource);
            }

            // --- confirmation: a fresh round, on measurements the search never saw -----
            _stages.Report(Stage(request, LoadedLaneStage.Confirming, "Seçilen sınır bağımsız bir turda doğrulanıyor.", dataUsed));

            var confirmation = await TrialAsync(
                request, policyName, choice.BitsPerSecond, choice.Trial.Share, before, cancellationToken)
                .ConfigureAwait(false);

            if (confirmation.Snapshot is not null)
            {
                resource = confirmation.Snapshot;
            }

            dataUsed += confirmation.Trial?.Result.DataUsedBytes ?? 0;

            if (confirmation.Blocked is { } confirmBlocked)
            {
                await RemoveSafelyAsync(policyName).ConfigureAwait(false);
                return new TrafficGuardOutcome(
                    confirmBlocked with { DataUsedBytes = dataUsed, Mode = request.Mode },
                    null);
            }

            var confirmed = confirmation.Trial is { } round
                && TrafficGuardCapPlanner.Choose([round], before, request.Mode) is not null;

            if (!confirmed)
            {
                var removed = await RemoveSafelyAsync(policyName).ConfigureAwait(false);
                _log?.Invoke("latency.guard.rolledback: bağımsız doğrulama turu kazancı tekrarlamadı.");

                return new TrafficGuardOutcome(
                    new TrafficGuardState
                    {
                        Status = removed ? TrafficGuardStatus.RolledBack : TrafficGuardStatus.Failed,
                        Summary = removed
                            ? "Seçilen sınır bağımsız doğrulama turunda kazancı tekrarlamadı; ilke kaldırıldı."
                            : "Doğrulama turu geçilemedi ve ilke kaldırılamadı; kurtarma için kayıtlı.",
                        PolicyName = removed ? null : policyName,
                        UploadQueueingBeforeMs = queueingBefore,
                        UploadQueueingAfterMs = confirmation.Trial?.QueueingMs,
                        UplinkBeforeKbps = before.ThroughputKbps,
                        UplinkAfterKbps = confirmation.Trial?.ThroughputKbps,
                        LoadedP95BeforeMs = before.Loaded?.P95RttMs,
                        LoadedP95AfterMs = confirmation.Trial?.LoadedP95Ms,
                        Trials = descriptions,
                        DataUsedBytes = dataUsed,
                        Mode = request.Mode,
                    },
                    removed ? null : resource);
            }

            var kept = confirmation.Trial!;
            _log?.Invoke(
                $"latency.guard.applied: {policyName} · {request.BulkApplication.MatchCondition} · "
                + $"{choice.BitsPerSecond / 1_000_000d:F1} Mbit/s · {choice.Why}");

            return new TrafficGuardOutcome(
                new TrafficGuardState
                {
                    Status = TrafficGuardStatus.Active,
                    Summary = $"Gönderim kuyruklanması {queueingBefore:F0} ms → {kept.QueueingMs ?? 0:F0} ms düştü; "
                        + $"{request.BulkApplication.ExecutableName} gönderimi {choice.Describe()} "
                        + $"olacak şekilde sınırlandı ({choice.Why}). "
                        + "Sınırlanan, oyun değil toplu aktarım yapan uygulamadır.",
                    PolicyName = policyName,
                    ThrottleBitsPerSecond = choice.BitsPerSecond,
                    ThrottledApplication = request.BulkApplication.Describe(),
                    PolicyMatch = confirmation.PolicyMatch,
                    UploadQueueingBeforeMs = queueingBefore,
                    UploadQueueingAfterMs = kept.QueueingMs,
                    UplinkBeforeKbps = before.ThroughputKbps,
                    UplinkAfterKbps = kept.ThroughputKbps,
                    LoadedP95BeforeMs = before.Loaded?.P95RttMs,
                    LoadedP95AfterMs = kept.LoadedP95Ms,
                    Trials = descriptions,
                    DataUsedBytes = dataUsed,
                    Mode = request.Mode,
                },
                resource);
        }
        catch (Exception)
        {
            // Cancellation, a probe failure, anything: the machine goes back to how it was
            // found before the exception leaves this method.
            await RemoveSafelyAsync(policyName).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>One cap: stop, apply, restart the transfer, measure.</summary>
    private sealed record TrialOutcome(
        TrafficGuardCapTrial? Trial,
        TrafficGuardState? Blocked,
        LatencyResourceSnapshot? Snapshot,
        string? PolicyMatch);

    private async Task<TrialOutcome> TrialAsync(
        TrafficGuardRequest request,
        string policyName,
        ulong capBitsPerSecond,
        double share,
        LoadExperimentResult baseline,
        CancellationToken cancellationToken)
    {
        // The policy has to exist before the flow it will govern, so the current transfer
        // is stopped first. Creating it under a running transfer and then measuring that
        // same transfer is precisely the mistake this ordering exists to prevent.
        _stages.Report(Stage(
            request,
            LoadedLaneStage.AwaitingUploadStop,
            _load.StopInstruction(LoadDirection.Upload),
            0));

        await _load.WaitForQuietLinkAsync(request.Network, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        _stages.Report(Stage(request, LoadedLaneStage.ApplyingPolicy, "Gönderim sınırı uygulanıyor.", 0));

        var policy = new QosPolicyRequest
        {
            Name = policyName,
            AppPathName = request.BulkApplication.MatchCondition,
            ThrottleBitsPerSecond = capBitsPerSecond,
        };

        var created = await _qos.CreateAsync(policy, cancellationToken).ConfigureAwait(false);
        if (!created.Created)
        {
            return new TrialOutcome(
                null,
                new TrafficGuardState
                {
                    Status = TrafficGuardStatus.Failed,
                    Summary = $"QoS ilkesi oluşturulamadı veya beklenenden farklı yazıldı: "
                        + $"{created.Reason ?? "bilinmeyen hata"}.",
                    UploadQueueingBeforeMs = baseline.QueueingMs,
                },
                null,
                created.Verified?.Describe());
        }

        var snapshot = Snapshot(request, policyName, capBitsPerSecond, created.Verified);
        var since = _now();

        // A new transport endpoint is what the policy attaches to, so a new one is what
        // this waits for. Without the observer the requirement cannot be proved, and the
        // measurement is refused rather than taken on trust.
        var flowCheck = await WaitForNewFlowAsync(request, since, cancellationToken).ConfigureAwait(false);
        if (flowCheck is { } blocked)
        {
            return new TrialOutcome(null, blocked, snapshot, created.Verified?.Describe());
        }

        var measured = await MeasureUploadAsync(
            request,
            LoadedLaneStage.AwaitingFreshUpload,
            LoadedLaneStage.MeasuringUploadCandidate,
            "Yeni bir gönderim başlatın. Windows QoS ilkesi yalnız ilke oluşturulduktan sonra açılan "
                + "bağlantılara uygulanır, bu yüzden önceki aktarımın devamı ölçülemez.",
            cancellationToken).ConfigureAwait(false);

        if (!measured.Succeeded)
        {
            return new TrialOutcome(
                new TrafficGuardCapTrial
                {
                    BitsPerSecond = capBitsPerSecond,
                    Share = share,
                    Result = measured,
                    RateHonoured = false,
                },
                null,
                snapshot,
                created.Verified?.Describe());
        }

        var honoured = TrafficGuardCapPlanner.RateHonoured(capBitsPerSecond, measured.ThroughputKbps);
        if (!honoured)
        {
            _log?.Invoke(
                $"latency.guard: ilke {capBitsPerSecond / 1000d:F0} kbit/s sınırı koyarken ölçülen hız "
                + $"{measured.ThroughputKbps:F0} kbit/s; sınır bu trafiğe uygulanmıyor.");
        }

        return new TrialOutcome(
            new TrafficGuardCapTrial
            {
                BitsPerSecond = capBitsPerSecond,
                Share = share,
                Result = measured,
                RateHonoured = honoured,
            },
            null,
            snapshot,
            created.Verified?.Describe());
    }

    /// <summary>
    /// Waits for the paced application to create a transport endpoint after the policy.
    /// </summary>
    /// <returns>Null when a new flow appeared; otherwise why the trial cannot proceed.</returns>
    private async Task<TrafficGuardState?> WaitForNewFlowAsync(
        TrafficGuardRequest request,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        if (_flows is not { IsRunning: true } observer)
        {
            var reason = _flows?.Unavailable;

            return new TrafficGuardState
            {
                Status = TrafficGuardStatus.NeedsNewConnection,
                Summary = "Akış gözlemi çalışmadığı için ilkenin yeni bir bağlantıya uygulandığı "
                    + "doğrulanamadı; sonuç üretilmedi."
                    + (reason is { Length: > 0 } ? $" ({reason})" : string.Empty),
                Mode = request.Mode,
            };
        }

        var pids = request.BulkApplication.ProcessIds.ToHashSet();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (elapsed.Elapsed < request.NewFlowTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (observer.Flows().Any(flow => pids.Contains(flow.ProcessId) && flow.EstablishedAt >= since))
            {
                return null;
            }

            _stages.Report(Stage(
                request,
                LoadedLaneStage.AwaitingFreshUpload,
                $"{request.BulkApplication.ExecutableName} için yeni bir gönderim başlatın. "
                    + "İlke yalnız yeni açılan bağlantılara uygulanır.",
                0,
                request.NewFlowTimeout - elapsed.Elapsed));

            await _delay(TimeSpan.FromMilliseconds(700), cancellationToken).ConfigureAwait(false);
        }

        return new TrafficGuardState
        {
            Status = TrafficGuardStatus.NeedsNewConnection,
            Summary = $"İlke oluşturulduktan sonra {request.BulkApplication.ExecutableName} yeni bir bağlantı "
                + "açmadı. Windows QoS ilkesi yalnız yeni bağlantılara uygulandığı için aktarımı "
                + "yeniden başlatmanız gerekiyor; bu turdan sonuç üretilmedi.",
            Mode = request.Mode,
        };
    }

    private Task<LoadExperimentResult> MeasureUploadAsync(
        TrafficGuardRequest request,
        LoadedLaneStage waiting,
        LoadedLaneStage measuring,
        string instruction,
        CancellationToken cancellationToken)
        => _load.RunAsync(
            request.Network,
            new LoadExperimentRequest
            {
                Endpoint = request.Endpoint,
                Direction = LoadDirection.Upload,
                Capacity = request.Capacity,
                Probe = request.Probe,
                WaitingStage = waiting,
                MeasuringStage = measuring,
                Instruction = instruction,
                RequireSaturation = true,
            },
            cancellationToken);

    private LoadedLaneProgress Stage(
        TrafficGuardRequest request,
        LoadedLaneStage stage,
        string instruction,
        long dataUsed,
        TimeSpan? remaining = null) => new()
        {
            Stage = stage,
            Title = LoadedLaneProgress.TitleFor(stage),
            Instruction = instruction,
            Direction = LoadDirection.Upload,
            Target = request.Endpoint.Label,
            DataUsedBytes = dataUsed,
            Remaining = remaining,
        };

    private static TrafficGuardOutcome Plain(TrafficGuardStatus status, string summary, long dataUsed = 0)
        => new(
            new TrafficGuardState
            {
                Status = status,
                Summary = summary,
                DataUsedBytes = dataUsed,
            },
            null);

    private static string Describe(TrafficGuardCapTrial trial, double queueingBefore)
    {
        var honoured = trial.RateHonoured ? string.Empty : " · sınır uygulanmadı";

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{trial.BitsPerSecond / 1_000_000d:F1} Mbit/s (%{trial.Share * 100:F0}): kuyruklanma {queueingBefore:F0} → {trial.QueueingMs ?? double.NaN:F0} ms · p95 {trial.LoadedP95Ms:F0} ms · hız {trial.ThroughputKbps / 1000:F1} Mbit/s{honoured}");
    }

    private static LatencyResourceSnapshot Snapshot(
        TrafficGuardRequest request,
        string policyName,
        ulong throttleBitsPerSecond,
        QosPolicyInfo? verified) => new()
        {
            Kind = LatencyResourceKind.QosPolicy,
            InterventionId = "qos.bulk-upload-throttle",
            TargetId = policyName,
            TargetName = policyName,
            Description = $"{request.BulkApplication.ExecutableName} gönderim sınırı "
                + $"({throttleBitsPerSecond / 1_000_000d:F1} Mbit/s)",
            CapturedAt = DateTimeOffset.UtcNow,
            OriginalState =
            {
                ["policyName"] = policyName,
                ["policyStore"] = QosPolicyStores.Active,
                ["match"] = verified?.Describe() ?? $"uygulama={request.BulkApplication.MatchCondition}",
                ["throttleBitsPerSecond"] = throttleBitsPerSecond.ToString(CultureInfo.InvariantCulture),
                ["existedBefore"] = "false",
                ["networkKey"] = request.Network.Key,
                ["profileId"] = request.ProfileId,
                ["createdBy"] = typeof(TrafficGuard).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            },
        };

    private async Task<bool> RemoveSafelyAsync(string policyName)
    {
        try
        {
            var outcome = await _qos.RemoveAsync(policyName, QosPolicyStores.Active, CancellationToken.None)
                .ConfigureAwait(false);

            return outcome is LatencyRestoreOutcome.Restored
                or LatencyRestoreOutcome.AlreadyOriginal
                or LatencyRestoreOutcome.MissingProperty;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"latency.guard: '{policyName}' kaldırılamadı ({ex.Message}).");
            return false;
        }
    }
}

/// <summary>
/// Undoes a QoS policy from its snapshot alone, on the next launch if need be.
/// </summary>
/// <remarks>
/// Undoing a policy this application created means deleting it, because it did not exist
/// before - which the snapshot records explicitly rather than assuming. A policy that was
/// already there is never ours and is never touched.
/// </remarks>
public sealed class QosResourceRestorer : ILatencyResourceRestorer
{
    private readonly IQosController _qos;
    private readonly Action<string>? _log;

    public QosResourceRestorer(IQosController qos, Action<string>? log = null)
    {
        _qos = qos ?? throw new ArgumentNullException(nameof(qos));
        _log = log;
    }

    public bool CanRestore(LatencyResourceKind kind) => kind == LatencyResourceKind.QosPolicy;

    public async Task<LatencyRestoreOutcome> RestoreAsync(
        LatencyResourceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!CanRestore(snapshot.Kind))
        {
            return LatencyRestoreOutcome.MissingProperty;
        }

        var name = snapshot.OriginalState.GetValueOrDefault("policyName", snapshot.TargetId);
        var store = snapshot.OriginalState.GetValueOrDefault("policyStore", QosPolicyStores.Active);

        if (!WindowsQosController.IsOwnedName(name))
        {
            // A snapshot naming somebody else's policy is corrupt, not an instruction.
            _log?.Invoke($"latency.qos: '{name}' bu uygulamaya ait değil; anlık görüntü yok sayıldı.");
            return LatencyRestoreOutcome.MissingProperty;
        }

        if (snapshot.OriginalState.GetValueOrDefault("existedBefore") == "true")
        {
            _log?.Invoke($"latency.qos: '{name}' bizden önce vardı; dokunulmadı.");
            return LatencyRestoreOutcome.AlreadyOriginal;
        }

        try
        {
            return await _qos.RemoveAsync(name, store, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"latency.qos: '{name}' kaldırılamadı ({ex.Message}).");
            return LatencyRestoreOutcome.Failed;
        }
    }
}
