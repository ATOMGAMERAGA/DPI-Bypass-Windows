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

    /// <summary>A policy is in place and was measured to reduce loaded latency.</summary>
    Active = 4,

    /// <summary>A policy was created, failed to help, and has been removed.</summary>
    RolledBack = 5,

    /// <summary>The experiment could not be completed.</summary>
    Failed = 6,

    /// <summary>The measurement needed for a verdict was never taken.</summary>
    NotMeasured = 7,
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

    public double? UploadQueueingBeforeMs { get; init; }

    public double? UploadQueueingAfterMs { get; init; }

    public double? UplinkBeforeKbps { get; init; }

    public double? UplinkAfterKbps { get; init; }

    /// <summary>Foreign policies that made this stand down, for the report.</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    public bool IsActive => Status == TrafficGuardStatus.Active;

    /// <summary>The queueing actually removed, only when both halves were measured.</summary>
    public double? ImprovementMs => UploadQueueingBeforeMs is { } before && UploadQueueingAfterMs is { } after
        ? Math.Max(0, before - after)
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
    /// Chosen by the user, never guessed. Pacing the wrong process would slow something
    /// they care about to speed up something they do not, and no measurement this side
    /// can tell which is which.
    /// </remarks>
    public required string BulkApplication { get; init; }

    public LinkCapacityEstimate Capacity { get; init; } = LinkCapacityEstimate.Unknown;

    /// <summary>
    /// Share of the measured uplink the paced application is allowed.
    /// </summary>
    /// <remarks>
    /// A queue only forms once the sender pushes more than the link can carry, so the
    /// limit has to sit below capacity to keep it empty - and not so far below that the
    /// transfer takes twice as long for a few milliseconds.
    /// </remarks>
    public double ThrottleShare { get; init; } = 0.85;

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
/// Windows already has the mechanism: a policy-based QoS rule with a throttle action
/// hands the flow to Pacer.sys, which schedules its packets. What Windows cannot do is
/// tell whether it worked, so this measures the loaded round trip before and after and
/// deletes the policy unless the queueing actually fell.
/// </para>
/// </remarks>
public sealed class TrafficGuard
{
    /// <summary>Queueing has to fall by at least this for the policy to be worth keeping.</summary>
    public const double MinimumQueueingReductionMs = 10;

    /// <summary>And by at least this share of what it was.</summary>
    public const double MinimumQueueingReductionShare = 0.25;

    /// <summary>Throughput may not fall below this share of the unthrottled rate.</summary>
    public const double MinimumRetainedThroughputShare = 0.55;

    private readonly IQosController _qos;
    private readonly ILoadExperiment _load;
    private readonly Action<string>? _log;

    public TrafficGuard(IQosController qos, ILoadExperiment load, Action<string>? log = null)
    {
        _qos = qos ?? throw new ArgumentNullException(nameof(qos));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _log = log;
    }

    /// <summary>The policy created by a successful run, so the caller can record it.</summary>
    public sealed record TrafficGuardOutcome(TrafficGuardState State, LatencyResourceSnapshot? Resource);

    public async Task<TrafficGuardOutcome> RunAsync(
        TrafficGuardRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var capability = await _qos.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.Available)
        {
            return new TrafficGuardOutcome(
                new TrafficGuardState
                {
                    Status = TrafficGuardStatus.Unavailable,
                    Summary = $"Windows QoS ilkesi kullanılamıyor: {capability.Reason ?? "bilinmeyen neden"}.",
                },
                null);
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
                },
                null);
        }

        var before = await _load
            .RunAsync(request.Network, LoadRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (!before.Succeeded)
        {
            return new TrafficGuardOutcome(
                new TrafficGuardState
                {
                    Status = TrafficGuardStatus.NotMeasured,
                    Summary = before.Failure ?? "Yük altındaki gecikme ölçülemedi.",
                },
                null);
        }

        var queueing = before.QueueingMs ?? 0;
        if (queueing < LatencyPathAnalysis.QueueingThresholdMs)
        {
            return new TrafficGuardOutcome(
                new TrafficGuardState
                {
                    Status = TrafficGuardStatus.NoQueueing,
                    Summary = $"Gönderim sırasında gecikme yalnız {queueing:F0} ms artıyor; "
                        + "sınırlanacak bir kuyruklanma yok.",
                    UploadQueueingBeforeMs = queueing,
                    UplinkBeforeKbps = before.ObservedLoad.UplinkKbps,
                },
                null);
        }

        var uplinkKbps = Math.Max(before.ObservedLoad.UplinkKbps, before.Capacity.UplinkKbps ?? 0);
        if (uplinkKbps <= 0)
        {
            return new TrafficGuardOutcome(
                new TrafficGuardState
                {
                    Status = TrafficGuardStatus.NotMeasured,
                    Summary = "Gönderim kapasitesi ölçülemediği için güvenli bir sınır hesaplanamadı.",
                    UploadQueueingBeforeMs = queueing,
                },
                null);
        }

        var throttleBitsPerSecond = (ulong)Math.Max(64_000, uplinkKbps * request.ThrottleShare * 1000);
        var policyName = WindowsQosController.NameFor(request.ProfileId, "bulk");

        var created = await _qos.CreateAsync(
            new QosPolicyRequest
            {
                Name = policyName,
                AppPathName = request.BulkApplication,
                ThrottleBitsPerSecond = throttleBitsPerSecond,
            },
            cancellationToken).ConfigureAwait(false);

        if (!created.Created)
        {
            return new TrafficGuardOutcome(
                new TrafficGuardState
                {
                    Status = TrafficGuardStatus.Failed,
                    Summary = $"QoS ilkesi oluşturulamadı: {created.Reason ?? "bilinmeyen hata"}.",
                    UploadQueueingBeforeMs = queueing,
                },
                null);
        }

        var resource = Snapshot(request, policyName, throttleBitsPerSecond);
        _log?.Invoke(
            $"latency.guard.applied: {policyName} · {request.BulkApplication} · "
            + $"{throttleBitsPerSecond / 1_000_000d:F1} Mbit/s");

        LoadExperimentResult after;
        try
        {
            after = await _load
                .RunAsync(request.Network, LoadRequest(request) with { Capacity = before.Capacity }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RemoveSafelyAsync(policyName).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await RemoveSafelyAsync(policyName).ConfigureAwait(false);
            throw;
        }

        var verdict = Judge(before, after, queueing);
        if (verdict.Keep)
        {
            return new TrafficGuardOutcome(
                new TrafficGuardState
                {
                    Status = TrafficGuardStatus.Active,
                    Summary = $"Gönderim kuyruklanması {queueing:F0} ms → {after.QueueingMs:F0} ms düştü; "
                        + $"{request.BulkApplication} gönderimi {throttleBitsPerSecond / 1_000_000d:F1} Mbit/s ile sınırlandı.",
                    PolicyName = policyName,
                    ThrottleBitsPerSecond = throttleBitsPerSecond,
                    ThrottledApplication = request.BulkApplication,
                    UploadQueueingBeforeMs = queueing,
                    UploadQueueingAfterMs = after.QueueingMs,
                    UplinkBeforeKbps = before.ObservedLoad.UplinkKbps,
                    UplinkAfterKbps = after.ObservedLoad.UplinkKbps,
                },
                resource);
        }

        var removed = await RemoveSafelyAsync(policyName).ConfigureAwait(false);
        _log?.Invoke($"latency.guard.rolledback: {verdict.Reason}");

        return new TrafficGuardOutcome(
            new TrafficGuardState
            {
                Status = removed ? TrafficGuardStatus.RolledBack : TrafficGuardStatus.Failed,
                Summary = removed
                    ? $"Gönderim sınırı kaldırıldı: {verdict.Reason}"
                    : $"Gönderim sınırı kaldırılamadı ({verdict.Reason}); ilke kurtarma için kayıtlı.",
                PolicyName = removed ? null : policyName,
                UploadQueueingBeforeMs = queueing,
                UploadQueueingAfterMs = after.QueueingMs,
                UplinkBeforeKbps = before.ObservedLoad.UplinkKbps,
                UplinkAfterKbps = after.ObservedLoad.UplinkKbps,
            },
            removed ? null : resource);
    }

    private sealed record Verdict(bool Keep, string Reason);

    /// <summary>
    /// Whether the policy earned its place: less queueing, no new loss, and a transfer
    /// that is still worth calling a transfer.
    /// </summary>
    private static Verdict Judge(LoadExperimentResult before, LoadExperimentResult after, double queueingBefore)
    {
        if (!after.Succeeded)
        {
            return new Verdict(false, after.Failure ?? "sınır uygulandıktan sonra ölçüm tamamlanamadı");
        }

        var queueingAfter = after.QueueingMs ?? 0;
        var removed = queueingBefore - queueingAfter;

        if (removed < MinimumQueueingReductionMs || removed < queueingBefore * MinimumQueueingReductionShare)
        {
            return new Verdict(
                false,
                $"kuyruklanma {queueingBefore:F0} ms → {queueingAfter:F0} ms, anlamlı bir azalma değil");
        }

        if (after.Loaded is { } loadedAfter && before.Loaded is { } loadedBefore)
        {
            var addedLoss = loadedAfter.PacketLossPercent - loadedBefore.PacketLossPercent;
            if (addedLoss > Math.Max(1.0, loadedAfter.LossQuantumPercent))
            {
                return new Verdict(false, $"paket kaybı %{addedLoss:F1} arttı");
            }
        }

        var throughputBefore = before.ObservedLoad.UplinkKbps;
        var throughputAfter = after.ObservedLoad.UplinkKbps;
        if (throughputBefore > 0 && throughputAfter < throughputBefore * MinimumRetainedThroughputShare)
        {
            return new Verdict(
                false,
                $"gönderim hızı {throughputBefore / 1000:F1} → {throughputAfter / 1000:F1} Mbit/s ile fazla düştü");
        }

        return new Verdict(true, "kuyruklanma ölçülerek azaldı");
    }

    private static LoadExperimentRequest LoadRequest(TrafficGuardRequest request) => new()
    {
        Endpoint = request.Endpoint,
        Direction = LoadDirection.Upload,
        Capacity = request.Capacity,
        Probe = request.Probe,
    };

    private static LatencyResourceSnapshot Snapshot(
        TrafficGuardRequest request,
        string policyName,
        ulong throttleBitsPerSecond) => new()
        {
            Kind = LatencyResourceKind.QosPolicy,
            InterventionId = "qos.bulk-upload-throttle",
            TargetId = policyName,
            TargetName = policyName,
            Description = $"{request.BulkApplication} gönderim sınırı "
                + $"({throttleBitsPerSecond / 1_000_000d:F1} Mbit/s)",
            CapturedAt = DateTimeOffset.UtcNow,
            OriginalState =
            {
                ["policyName"] = policyName,
                ["policyStore"] = QosPolicyStores.Active,
                ["match"] = $"uygulama={request.BulkApplication}",
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
