using DpiBypass.Core.Logging;

namespace DpiBypass.Core.Network;

/// <summary>How thorough a run is allowed to be, and what it is allowed to measure.</summary>
public sealed record LatencyOptimizerOptions
{
    public static readonly LatencyOptimizerOptions Default = new();

    /// <summary>Paired A/B cycles every candidate gets before a verdict is possible.</summary>
    public int MinimumCycles { get; init; } = 2;

    /// <summary>Extra cycles a noisy network is allowed, before the answer is "no".</summary>
    public int MaximumCycles { get; init; } = 4;

    /// <summary>Re-runs allowed when the link was busy for one half of a pair only.</summary>
    public int MaximumLoadRetries { get; init; } = 2;

    public LatencyProbeRequest Survey { get; init; } = LatencyProbeRequest.Survey;

    public LatencyProbeRequest Benchmark { get; init; } = LatencyProbeRequest.Benchmark;

    /// <summary>Skip candidates a fresh profile already measured and rejected here.</summary>
    public bool UseProfileCache { get; init; } = true;

    /// <summary>What to measure. The default is the general-internet reference.</summary>
    public LatencyTargetSpec Target { get; init; } = LatencyTargetSpec.Reference;

    /// <summary>Fixes the A/B ordering sequence, so a run can be reproduced exactly.</summary>
    public int Seed { get; init; }

    /// <summary>Wall-clock ceiling for one candidate's experiment.</summary>
    public TimeSpan CandidateBudget { get; init; } = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Wall-clock ceiling for a whole run, across every candidate.
    /// </summary>
    /// <remarks>
    /// A driver offering seven candidates on a link that never settles could otherwise
    /// spend half an hour proving nothing. Candidates not reached inside the budget are
    /// simply not measured this time; the run still commits whatever it did verify, and
    /// the rest are tried again on the next pass.
    /// </remarks>
    public TimeSpan TotalBudget { get; init; } = TimeSpan.FromMinutes(12);

    /// <summary>Sample size an inconclusive experiment grows to before giving up.</summary>
    public int AdaptiveProbeCount { get; init; } = LatencyProbeRequest.Deep.ProbeCount;

    /// <summary>The acceptance rules. Production keeps every guard on.</summary>
    public LatencyEvaluationOptions Evaluation { get; init; } = LatencyEvaluationOptions.Strict;

    /// <summary>
    /// What this run may do to a live adapter to make a setting take effect.
    /// </summary>
    /// <remarks>
    /// Off by default. A keyword Windows offers no operational query for cannot be proved
    /// to be in effect without restarting the miniport, and restarting the miniport drops
    /// every connection on it - so without consent such a candidate is reported as
    /// needing a restart and is never measured.
    /// </remarks>
    public AdapterRestartPolicy Restart { get; init; } = AdapterRestartPolicy.Never;
}

/// <summary>
/// Measures, applies and independently verifies safe NIC changes, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// The shape of a run is measure, change one thing, measure again, keep or put back -
/// never apply a list of settings and assume. Each candidate is judged by repeated
/// alternating cycles against the same pinned endpoint under the same load and the same
/// machine conditions, and the aggregate has to beat how much the cycles disagree with
/// each other before anything is kept. See <see cref="LatencyComparison"/> for the rule
/// and <see cref="PairedLatencyExperimentRunner"/> for how a cycle is run.
/// </para>
/// <para>
/// Accepting each change on its own does not prove the machine is better with all of
/// them, so the accepted set is re-measured as a bundle, alternating original against
/// optimised, before anything is committed.
/// </para>
/// <para>
/// This subsystem never touches the packet path. It has no WinDivert handle, opens no
/// filter, and cannot divert a single packet: game traffic, voice and ICMP stay on the
/// ordinary OS path whether latency mode is on or off. What it changes are reversible
/// adapter properties, and every one of them is written to a snapshot before it is
/// written to the adapter so an interrupted run can be undone on the next launch.
/// </para>
/// </remarks>
public sealed class LatencyOptimizer : IAsyncDisposable
{
    private readonly ILatencyAdapterController _controller;
    private readonly ILatencyProbe _probe;
    private readonly ILatencySnapshotStore _snapshots;
    private readonly ILatencyProfileStore _profiles;
    private readonly ILatencyTargetResolver _targets;
    private readonly ILatencyEnvironmentSampler _environmentSampler;
    private readonly ILatencyExperimentRunner _runner;
    private readonly LatencySnapshotRestorer _restorer;
    private readonly Func<NetworkMonitor> _monitorFactory;
    private readonly LatencyOptimizerOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Lock _cancellationGate = new();

    private NetworkMonitor? _monitor;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _operationCancellation;
    private string? _lastNetworkKey;
    private bool _enabled;
    private bool _disposed;
    private bool _ignoreCacheOnce;

    public LatencyOptimizer(
        ILatencyAdapterController? controller = null,
        ILatencyProbe? probe = null,
        ILatencySnapshotStore? snapshots = null,
        Func<NetworkMonitor>? monitorFactory = null,
        Action<string>? log = null,
        ILatencyProfileStore? profiles = null,
        LatencyOptimizerOptions? options = null,
        Func<DateTimeOffset>? now = null,
        ILatencyTargetResolver? targets = null,
        ILatencyEnvironmentSampler? environmentSampler = null,
        ILatencyExperimentRunner? runner = null,
        IReadOnlyList<ILatencyResourceRestorer>? resourceRestorers = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _log = log ?? AppLog.InfoSink;
        _controller = controller ?? new WindowsLatencyAdapterController(_log);
        _probe = probe ?? new LatencyProbe();
        _snapshots = snapshots ?? new LatencySnapshotStore();
        _profiles = profiles ?? new LatencyProfileStore(log: _log);
        _targets = targets ?? new LatencyTargetResolver(log: _log);
        _environmentSampler = environmentSampler ?? new WindowsLatencyEnvironmentSampler(_log);
        _runner = runner ?? new PairedLatencyExperimentRunner(_probe, _environmentSampler, delay, _log);
        _restorer = new LatencySnapshotRestorer(
            _snapshots,
            _controller,
            resourceRestorers ?? [new QosResourceRestorer(new WindowsQosController(_log), _log)],
            _log);
        _monitorFactory = monitorFactory ?? (() => new NetworkMonitor(log: _log));
        _options = options ?? LatencyOptimizerOptions.Default;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Target = _options.Target;
        Restart = _options.Restart;
    }

    public LatencyOptimizationResult Current { get; private set; } = new()
    {
        Status = LatencyOptimizationStatus.Disabled,
        StatusLine = "Kapalı.",
    };

    public bool IsBusy => Current.Status is LatencyOptimizationStatus.Measuring
        or LatencyOptimizationStatus.Optimizing
        or LatencyOptimizationStatus.QuickTesting
        or LatencyOptimizationStatus.LoadTesting
        or LatencyOptimizationStatus.Restoring;

    /// <summary>
    /// What to measure. Changing it invalidates nothing by itself; the profile cache is
    /// keyed on the target, so the next run simply finds no saved answer for the new one.
    /// </summary>
    public LatencyTargetSpec Target { get; set; }

    /// <summary>
    /// What the next run may do to a live adapter, as the user has currently set it.
    /// </summary>
    /// <remarks>
    /// Settable rather than fixed at construction because the consent lives in the user's
    /// settings and can be withdrawn between runs, and a run that started before it was
    /// withdrawn must not go on restarting adapters.
    /// </remarks>
    public AdapterRestartPolicy Restart { get; set; }

    public event Action<LatencyOptimizationResult>? Changed;

    /// <summary>Starts the independent network-change lifecycle and performs the first run.</summary>
    public async Task<LatencyOptimizationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_enabled)
        {
            _enabled = true;
            _lifetime?.Dispose();
            _lifetime = new CancellationTokenSource();

            _monitor = _monitorFactory();
            _monitor.Changed += OnNetworkChanged;
            _monitor.Start();
        }

        var network = _monitor?.Current ?? NetworkFingerprint.Capture();
        return await RunForNetworkAsync(network, force: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one explicit pass. Tests and the CLI use this without creating a monitor.</summary>
    public Task<LatencyOptimizationResult> OptimizeAsync(
        NetworkFingerprint network,
        CancellationToken cancellationToken = default)
    {
        _enabled = true;
        return RunForNetworkAsync(network, force: true, cancellationToken);
    }

    /// <summary>
    /// Runs again from scratch, ignoring anything already saved for this network.
    /// </summary>
    /// <remarks>
    /// The cache exists so a returning user does not wait through a benchmark they have
    /// already paid for. It is also the one thing that can hide a change in conditions
    /// the app did not notice, so there has to be a way to say "measure it again anyway".
    /// </remarks>
    public Task<LatencyOptimizationResult> RetestAsync(
        NetworkFingerprint network,
        CancellationToken cancellationToken = default)
    {
        _ignoreCacheOnce = true;
        _lastNetworkKey = null;
        return OptimizeAsync(network, cancellationToken);
    }

    /// <summary>Forgets the saved answer for one network and adapter.</summary>
    public Task ForgetProfileAsync(string networkKey, string adapterId, CancellationToken cancellationToken = default)
        => _profiles.RemoveAsync(networkKey, adapterId, cancellationToken);

    internal Task<LatencyOptimizationResult> OptimizeNetworkChangeAsync(
        NetworkFingerprint network,
        CancellationToken cancellationToken = default)
    {
        _enabled = true;
        return RunForNetworkAsync(network, force: false, cancellationToken);
    }

    /// <summary>
    /// Undoes an interrupted run. Called on every launch, whatever the mode is set to.
    /// </summary>
    /// <remarks>
    /// A machine that crashed, lost power or was killed while a candidate was applied has
    /// adapter values on it that nothing ever proved were wanted, and the setting that
    /// says whether latency mode is on has no bearing on that. Recovery is therefore not
    /// gated on the mode: a snapshot whose state is anything but
    /// <see cref="LatencyTransactionState.Committed"/> is put back before the app does
    /// anything else, and one that is committed is left alone for the mode to own.
    /// </remarks>
    public async Task<bool> RecoverAsync(CancellationToken cancellationToken = default)
    {
        LatencyOptimizationSnapshot? snapshot;

        try
        {
            snapshot = await _snapshots.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"latency.recovery: anlık görüntü okunamadı ({ex.Message}).");
            return false;
        }

        if (snapshot is null || !snapshot.IsIncomplete)
        {
            return true;
        }

        _log?.Invoke(
            $"latency.recovery.started: yarım kalmış ayarlama bulundu (durum {snapshot.State}, "
            + $"{snapshot.Settings.Count} ayar, {snapshot.Resources.Count} kaynak, "
            + $"bekleyen '{snapshot.PendingProperty ?? "-"}').");

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read again now the gate is held. A run holds it for its whole length, so
            // reaching here means none is in progress - and a run that started and
            // committed while this was waiting has left a snapshot that is no longer
            // incomplete, which must not be rolled back out from under it.
            snapshot = await _snapshots.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is null || !snapshot.IsIncomplete)
            {
                _log?.Invoke("latency.recovery.completed: kurtarılacak bir şey kalmamıştı.");
                return true;
            }

            var restored = await _restorer.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
            _log?.Invoke(restored
                ? "latency.recovery.completed: özgün NIC ayarları geri yüklendi."
                : "latency.recovery.failed: bazı ayarlar geri yüklenemedi; anlık görüntü korundu.");

            if (!restored)
            {
                SetCurrent(new LatencyOptimizationResult
                {
                    Status = LatencyOptimizationStatus.Failed,
                    StatusLine = "Yarım kalmış bir ayarlama tamamen geri alınamadı; "
                        + "anlık görüntü korundu ve yeni ölçüm başlatılmayacak.",
                });
            }

            return restored;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Stops monitoring and puts back every value changed by this app.</summary>
    public async Task<LatencyOptimizationResult> StopAndRestoreAsync(CancellationToken cancellationToken = default)
    {
        _enabled = false;
        _lastNetworkKey = null;

        var lifetime = _lifetime;
        _lifetime = null;
        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (_monitor is not null)
        {
            _monitor.Changed -= OnNetworkChanged;
            _monitor.Dispose();
            _monitor = null;
        }

        CancelActiveOperation();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetCurrent(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Restoring,
                StatusLine = "Özgün NIC ayarları geri yükleniyor…",
            });

            var restored = await _restorer.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
            var result = new LatencyOptimizationResult
            {
                Status = restored ? LatencyOptimizationStatus.Disabled : LatencyOptimizationStatus.Failed,
                StatusLine = restored
                    ? "Kapalı · özgün NIC ayarları geri yüklendi."
                    : "Bazı NIC ayarları geri yüklenemedi; anlık görüntü kurtarma için korundu.",
            };
            SetCurrent(result);
            return result;
        }
        finally
        {
            _operationGate.Release();
            lifetime?.Dispose();
        }
    }

    /// <summary>Recovery entry point used by the CLI and uninstaller.</summary>
    public async Task<LatencyOptimizationResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        CancelActiveOperation();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetCurrent(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Restoring,
                StatusLine = "Özgün NIC ayarları geri yükleniyor…",
            });

            var restored = await _restorer.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
            var result = new LatencyOptimizationResult
            {
                Status = restored ? LatencyOptimizationStatus.Disabled : LatencyOptimizationStatus.Failed,
                StatusLine = restored
                    ? "Özgün NIC ayarları geri yüklendi."
                    : "Bazı NIC ayarları geri yüklenemedi; anlık görüntü korundu.",
            };
            SetCurrent(result);
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Measures only; it never captures or changes a NIC property.</summary>
    public Task<LatencyOptimizationResult> TestAsync(CancellationToken cancellationToken = default)
        => TestAsync(null, cancellationToken);

    /// <summary>
    /// The quick test: where the delay is right now, against a target the user chose.
    /// </summary>
    /// <remarks>
    /// Nothing is applied and nothing is captured, so this is safe to run at any time and
    /// in any state. What it cannot do is say anything about latency under load, which is
    /// where most of a home connection's worst numbers live - that needs the user to
    /// start a transfer, and therefore needs the deep test.
    /// </remarks>
    public async Task<LatencyOptimizationResult> TestAsync(
        LatencyTargetSpec? target,
        CancellationToken cancellationToken = default)
    {
        var network = NetworkFingerprint.Capture();
        if (!network.IsOnline)
        {
            return new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Offline,
                StatusLine = "Aktif internet bağlantısı bulunamadı.",
                NetworkKey = network.Key,
            };
        }

        var spec = target ?? Target;
        var resolution = await _targets.ResolveAsync(spec, cancellationToken).ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Offline,
                StatusLine = resolution.Failure ?? "Ölçüm hedefi çözümlenemedi.",
                NetworkKey = network.Key,
                TargetLabel = spec.Describe(),
            };
        }

        var (measurement, endpoint) = await MeasureTargetAsync(network, resolution, cancellationToken)
            .ConfigureAwait(false);
        var path = LatencyPathAnalysis.Describe(measurement);

        return new LatencyOptimizationResult
        {
            Status = measurement.HasRemoteConnectivity
                ? LatencyOptimizationStatus.NeedsDeepTest
                : LatencyOptimizationStatus.Offline,
            StatusLine = LatencyReport.Measurement(network, measurement, path, endpoint, resolution.Notice),
            AdapterName = network.AdapterName ?? network.DisplayName,
            NetworkKey = network.Key,
            After = measurement,
            Path = path,
            TargetLabel = endpoint.Label,
            TargetProtocol = endpoint.ProtocolLabel,
            RouteReferenceOnly = endpoint.RouteReferenceOnly,
            Notices = resolution.Notice is null ? [] : [resolution.Notice],
        };
    }

    /// <summary>
    /// Picks the endpoint that answers, then measures it properly.
    /// </summary>
    /// <remarks>
    /// The survey exists to choose; everything after it uses the one endpoint it chose,
    /// because two measurements of two different addresses cannot be subtracted.
    /// </remarks>
    private async Task<(LatencyMeasurement Measurement, LatencyEndpoint Endpoint)> MeasureTargetAsync(
        NetworkFingerprint network,
        LatencyTargetResolution resolution,
        CancellationToken cancellationToken)
    {
        LatencyMeasurement? best = null;
        LatencyEndpoint? chosen = null;

        foreach (var candidate in resolution.Endpoints)
        {
            var survey = await _probe
                .MeasureAsync(network, _options.Survey.For(candidate), cancellationToken)
                .ConfigureAwait(false);

            if (best is null || (survey.HasRemoteConnectivity && !best.HasRemoteConnectivity))
            {
                best = survey;
                chosen = candidate;
            }

            if (survey.HasRemoteConnectivity)
            {
                break;
            }
        }

        var endpoint = chosen ?? resolution.Endpoints[0];
        if (best is null || !best.HasRemoteConnectivity)
        {
            return (best!, endpoint);
        }

        var measurement = await _probe
            .MeasureAsync(network, _options.Benchmark.For(endpoint), cancellationToken)
            .ConfigureAwait(false);

        return (measurement, endpoint);
    }

    private async Task<LatencyOptimizationResult> RunForNetworkAsync(
        NetworkFingerprint network,
        bool force,
        CancellationToken callerToken)
    {
        if (!force && string.Equals(_lastNetworkKey, network.Key, StringComparison.Ordinal))
        {
            return Current;
        }

        var operation = CreateOperationCancellation(callerToken);
        var token = operation.Token;

        try
        {
            await _operationGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            operation.Dispose();
            return Current;
        }

        try
        {
            if (!_enabled)
            {
                return Current;
            }

            return await RunGuardedAsync(network, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation can arrive between an apply and its measurement. Restore
            // without the cancelled token before allowing the next network through.
            var restored = await TryRestoreAfterFailureAsync().ConfigureAwait(false);

            return Publish(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Cancelled,
                StatusLine = restored
                    ? "Ölçüm iptal edildi; uygulanan NIC ayarları geri alındı."
                    : "Ölçüm iptal edildi; bazı NIC ayarları geri yüklenemedi ve snapshot korundu.",
                AdapterName = network.AdapterName ?? network.DisplayName,
                NetworkKey = network.Key,
            });
        }
        catch (Exception ex)
        {
            _log?.Invoke($"latency.failed: düşük gecikme optimizasyonu başarısız ({ex.Message}).");
            var restored = await TryRestoreAfterFailureAsync().ConfigureAwait(false);

            return Publish(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Failed,
                StatusLine = restored
                    ? $"Optimizasyon başarısız oldu; uygulanan değişiklikler geri alındı. {ex.Message}"
                    : $"Optimizasyon başarısız oldu; bazı NIC ayarları geri yüklenemedi ve snapshot korundu. {ex.Message}",
                AdapterName = network.AdapterName ?? network.DisplayName,
                NetworkKey = network.Key,
            });
        }
        finally
        {
            _operationGate.Release();
            ClearOperationCancellation(operation);
        }
    }

    /// <summary>The run itself. The caller owns the gate, the cancellation and the rollback.</summary>
    private async Task<LatencyOptimizationResult> RunGuardedAsync(NetworkFingerprint network, CancellationToken token)
    {
        // A crash or an earlier network must be returned to its exact baseline before a
        // fresh baseline is measured. Never overwrite an unrestorable snapshot with
        // values from another adapter.
        if (!await _restorer.RestoreAllAsync(token).ConfigureAwait(false))
        {
            return Publish(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Failed,
                StatusLine = "Önceki NIC ayarları geri yüklenemedi; yeni optimizasyon güvenlik için başlatılmadı.",
                AdapterName = network.AdapterName ?? network.DisplayName,
                NetworkKey = network.Key,
            });
        }

        if (!network.IsOnline)
        {
            return Publish(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Offline,
                StatusLine = "Aktif internet bağlantısı bulunamadı.",
                NetworkKey = network.Key,
            });
        }

        SetCurrent(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Measuring,
            StatusLine = "Aktif bağdaştırıcı ve başlangıç gecikmesi ölçülüyor…",
            AdapterName = network.AdapterName ?? network.DisplayName,
            NetworkKey = network.Key,
        });

        var adapter = await _controller.DetectAsync(network, token).ConfigureAwait(false);
        if (adapter is null || !adapter.IsEligible)
        {
            return Publish(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Unsupported,
                StatusLine = "Desteklenen aktif fiziksel düşük-gecikme NIC ayarı bulunamadı."
                    + " Hedef ve yük tanılaması yine kullanılabilir.",
                AdapterName = adapter?.AdapterName ?? network.AdapterName ?? network.DisplayName,
                NetworkKey = network.Key,
            });
        }

        var resolution = await _targets.ResolveAsync(Target, token).ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return Publish(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Offline,
                StatusLine = resolution.Failure ?? "Ölçüm hedefi çözümlenemedi; hiçbir NIC ayarı değiştirilmedi.",
                AdapterName = adapter.AdapterName,
                NetworkKey = network.Key,
                TargetLabel = Target.Describe(),
            });
        }

        // One short pass settles which endpoint answers here; every later measurement in
        // this run uses that same pinned endpoint so the numbers can be subtracted.
        _log?.Invoke($"latency.baseline.started: {adapter.AdapterName} · ağ {network.Key}");
        var (baseline, endpoint) = await MeasureTargetAsync(network, resolution, token).ConfigureAwait(false);
        var benchmark = _options.Benchmark.For(endpoint);

        if (!baseline.HasRemoteConnectivity)
        {
            _log?.Invoke("latency.baseline.completed: uzak uç yanıt vermedi.");
            return Publish(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Offline,
                StatusLine = "Uzak IP gecikmesi ölçülemedi; hiçbir NIC ayarı değiştirilmedi.",
                AdapterName = adapter.AdapterName,
                NetworkKey = network.Key,
                Before = baseline,
                TargetLabel = endpoint.Label,
                TargetProtocol = endpoint.ProtocolLabel,
            });
        }

        var path = LatencyPathAnalysis.Describe(baseline);
        _log?.Invoke($"latency.baseline.completed: {LatencyReport.Compact(baseline)} · {path.Bottleneck}");

        var environment = _environmentSampler.Sample(network);
        var context = LatencyProfileContext.From(Target, environment, loadedEvidence: false, qosAvailable: false);

        var ignoreCache = _ignoreCacheOnce;
        _ignoreCacheOnce = false;
        if (ignoreCache)
        {
            _log?.Invoke("latency.profile: kullanıcı yeniden ölçüm istedi; kayıtlı sonuçlar atlanıyor.");
            await RemoveProfileSafelyAsync(network, adapter).ConfigureAwait(false);
        }

        var profile = ignoreCache
            ? null
            : await LoadUsableProfileAsync(network, adapter, context, token).ConfigureAwait(false);

        // A profile verified here, on this adapter, against this driver is worth
        // re-applying rather than re-earning: a full paired benchmark on every logon
        // would spend minutes pinging to reach an answer that is already known. It is
        // still applied through the same snapshot and confirmed against a fresh
        // measurement, and a profile that no longer holds up is deleted on the spot.
        if (profile is { AcceptedProperties.Count: > 0 })
        {
            var replayed = await ReplayProfileAsync(
                network, adapter, profile, baseline, benchmark, path, endpoint, environment, token).ConfigureAwait(false);

            if (replayed is not null)
            {
                return replayed;
            }

            // The replay only returns null after deleting the profile, so what it said
            // about the candidates it turned down is no longer worth anything either:
            // the full benchmark below starts from every candidate again.
            profile = null;
        }

        var candidates = SelectCandidates(adapter, profile, endpoint, environment, context);
        if (candidates.Count == 0)
        {
            return Publish(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.NoGain,
                StatusLine = LatencyReport.NoGain(
                    $"Etkin · {adapter.AdapterName} üzerinde denenecek güvenli bir ayar kalmadı.",
                    baseline,
                    [],
                    path),
                AdapterName = adapter.AdapterName,
                NetworkKey = network.Key,
                Before = baseline,
                After = baseline,
                Path = path,
                TargetLabel = endpoint.Label,
                TargetProtocol = endpoint.ProtocolLabel,
                RouteReferenceOnly = endpoint.RouteReferenceOnly,
                Notices = resolution.Notice is null ? [] : [resolution.Notice],
            });
        }

        return await RunCandidatesAsync(
            network, adapter, baseline, benchmark, path, candidates, endpoint, environment, context, resolution, token)
            .ConfigureAwait(false);
    }

    private async Task<LatencyOptimizationResult> RunCandidatesAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
        LatencyMeasurement baseline,
        LatencyProbeRequest benchmark,
        LatencyPathAnalysis path,
        IReadOnlyList<LatencyOptimizationCandidate> candidates,
        LatencyEndpoint endpoint,
        LatencyEnvironment environment,
        LatencyProfileContext context,
        LatencyTargetResolution resolution,
        CancellationToken token)
    {
        var snapshot = new LatencyOptimizationSnapshot
        {
            AdapterId = adapter.AdapterId,
            AdapterName = adapter.AdapterName,
            NetworkKey = network.Key,
            CreatedAt = _now(),
            State = LatencyTransactionState.SnapshotCreated,
        };

        var verdicts = new List<LatencyVerdict>();
        var accepted = new List<LatencyOptimizationCandidate>();
        var reference = baseline;
        var notices = new List<string>();
        if (resolution.Notice is not null)
        {
            notices.Add(resolution.Notice);
        }

        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        for (var index = 0; index < candidates.Count; index++)
        {
            token.ThrowIfCancellationRequested();

            if (elapsed.Elapsed > _options.TotalBudget)
            {
                _log?.Invoke(
                    $"latency.run.budget: süre sınırına ulaşıldı; kalan {candidates.Count - index} aday "
                    + "bu turda ölçülmedi.");
                notices.Add($"Süre sınırı nedeniyle {candidates.Count - index} aday bu turda ölçülmedi.");
                break;
            }

            var candidate = candidates[index];

            SetCurrent(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Optimizing,
                StatusLine = $"Ölçülüyor ({index + 1}/{candidates.Count}) · {candidate.Description}",
                AdapterName = adapter.AdapterName,
                NetworkKey = network.Key,
                Before = baseline,
                After = reference,
                AppliedChanges = [.. accepted.Select(entry => entry.Description)],
                Path = path,
                Verdicts = [.. verdicts],
                TargetLabel = endpoint.Label,
                TargetProtocol = endpoint.ProtocolLabel,
            });

            var plan = new LatencyExperimentPlan
            {
                Network = network,
                Candidate = candidate,
                Probe = benchmark,
                MinimumCycles = _options.MinimumCycles,
                MaximumCycles = _options.MaximumCycles,
                MaximumDiscardedCycles = _options.MaximumLoadRetries,
                Budget = _options.CandidateBudget,
                Seed = _options.Seed + index,
                Evaluation = _options.Evaluation,
                AdaptiveProbeCount = _options.AdaptiveProbeCount,
                Reference = environment,
            };

            var arm = new CandidateArm(this, adapter, snapshot, candidate, network, endpoint, environment);
            var outcome = await _runner.RunAsync(plan, arm, token).ConfigureAwait(false);

            if (outcome.LostConnectivity)
            {
                _log?.Invoke("latency.rollback.started: NIC değişikliğinden sonra ağ yanıt vermedi.");
                var restored = await _restorer.RestoreAllAsync(CancellationToken.None).ConfigureAwait(false);
                _log?.Invoke(restored ? "latency.rollback.completed" : "latency.rollback.failed");

                if (!restored)
                {
                    return Publish(new LatencyOptimizationResult
                    {
                        Status = LatencyOptimizationStatus.Failed,
                        StatusLine = "Bağlantı denetimi başarısız oldu ve bazı NIC ayarları geri yüklenemedi; "
                            + "snapshot kurtarma için korundu.",
                        AdapterName = adapter.AdapterName,
                        NetworkKey = network.Key,
                        Before = baseline,
                        Path = path,
                        Verdicts = verdicts,
                    });
                }

                return Publish(NoGain(
                    adapter,
                    network,
                    baseline,
                    null,
                    path,
                    verdicts,
                    "Bağlantı denetimi başarısız oldu; özgün NIC ayarları geri yüklendi.",
                    endpoint,
                    notices));
            }

            verdicts.Add(outcome.Verdict);
            _log?.Invoke(outcome.Verdict.Accepted
                ? $"latency.candidate.accepted: {candidate.PropertyName} · {outcome.Verdict.Reason}"
                : $"latency.candidate.rejected: {candidate.PropertyName} · {outcome.Verdict.Reason}");

            if (!outcome.Verdict.Accepted)
            {
                continue;
            }

            // Verified: put it back on and leave it there, so the next candidate is
            // measured against the machine as it will actually be left.
            var kept = await ApplyAndVerifyAsync(adapter, snapshot, candidate, network, endpoint, environment, token)
                .ConfigureAwait(false);

            if (!kept.Applied)
            {
                verdicts[^1] = outcome.Verdict with
                {
                    Outcome = LatencyVerdictOutcome.Rejected,
                    Reason = $"doğrulandıktan sonra kalıcı olarak uygulanamadı: {kept.Reason}",
                };
                continue;
            }

            accepted.Add(candidate);
            reference = outcome.LastOptimised ?? reference;
        }

        if (accepted.Count == 0)
        {
            // Restore rather than clear. Every candidate is put back inside its own
            // cycles, so this is normally a no-op - but clearing a snapshot that still
            // described a value on the adapter would abandon it, and the whole point of
            // the file is that this cannot happen.
            if (!await _restorer.RestoreAllAsync(token).ConfigureAwait(false))
            {
                return Publish(new LatencyOptimizationResult
                {
                    Status = LatencyOptimizationStatus.Failed,
                    StatusLine = "Adaylar elendi ancak bazı NIC ayarları geri yüklenemedi; "
                        + "snapshot kurtarma için korundu.",
                    AdapterName = adapter.AdapterName,
                    NetworkKey = network.Key,
                    Before = baseline,
                    After = reference,
                    Path = path,
                    Verdicts = verdicts,
                });
            }

            await SaveProfileAsync(network, adapter, baseline, null, verdicts, path, context, token).ConfigureAwait(false);

            return Publish(NoGain(
                adapter,
                network,
                baseline,
                reference,
                path,
                verdicts,
                "Bu ağda doğrulanmış bir gecikme iyileşmesi bulunamadı. Özgün ayarlar geri yüklendi.",
                endpoint,
                notices));
        }

        // Everything accepted is on the adapter now. The whole set is re-measured the
        // same way a single candidate is - alternating original against optimised - so
        // the headline is a paired result rather than one reading taken minutes after
        // the baseline it is compared with.
        await SaveSnapshotAsync(snapshot with { State = LatencyTransactionState.Verifying }, token).ConfigureAwait(false);

        var confirmation = await ConfirmBundleAsync(
            network, adapter, snapshot, accepted, benchmark, endpoint, environment, token).ConfigureAwait(false);

        var final = confirmation.Final;
        if (!confirmation.Confirmed)
        {
            _log?.Invoke("latency.verification.completed: eşli paket doğrulaması uçtan uca kazancı doğrulamadı.");
            _log?.Invoke("latency.rollback.started: son doğrulama geçilemedi.");
            var undone = await _restorer.RestoreAllAsync(CancellationToken.None).ConfigureAwait(false);
            _log?.Invoke(undone ? "latency.rollback.completed" : "latency.rollback.failed");

            if (!undone)
            {
                return Publish(new LatencyOptimizationResult
                {
                    Status = LatencyOptimizationStatus.Failed,
                    StatusLine = "Son doğrulama geçilemedi ve bazı NIC ayarları geri yüklenemedi; "
                        + "snapshot kurtarma için korundu.",
                    AdapterName = adapter.AdapterName,
                    NetworkKey = network.Key,
                    Before = baseline,
                    After = final,
                    Path = path,
                    Verdicts = verdicts,
                });
            }

            await SaveProfileAsync(network, adapter, baseline, null, verdicts, path, context, token).ConfigureAwait(false);

            return Publish(NoGain(
                adapter,
                network,
                baseline,
                final,
                path,
                verdicts,
                final is null
                    ? "Son doğrulamada bağlantı ölçülemedi; özgün NIC ayarları geri yüklendi."
                    : "Tüm paket birlikte ölçüldüğünde anlamlı bir uçtan uca iyileşme kanıtlanamadı; "
                        + "özgün NIC ayarları geri yüklendi.",
                endpoint,
                notices));
        }

        _log?.Invoke($"latency.verification.completed: {LatencyReport.Compact(final!)}");

        await SaveSnapshotAsync(
            snapshot with { State = LatencyTransactionState.Committed, PendingProperty = null },
            token).ConfigureAwait(false);

        await SaveProfileAsync(network, adapter, baseline, final, verdicts, path, context, token).ConfigureAwait(false);

        _lastNetworkKey = network.Key;

        // Per-candidate paired deltas remain in Verdicts for diagnostics. The headline
        // is the paired difference the bundle confirmation measured, which is the only
        // number that describes the machine as it is actually being left.
        var improvement = LatencyDelta.Between(baseline, final!);
        var applied = accepted.Select(candidate => candidate.Description).ToArray();

        _log?.Invoke($"latency.committed: {applied.Length} ayar · median {improvement.MedianMs:F1} ms · p95 {improvement.P95Ms:F1} ms");

        return Publish(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Active,
            StatusLine = LatencyReport.Verified(adapter.AdapterName, baseline, final!, improvement, applied, path, endpoint),
            AdapterName = adapter.AdapterName,
            NetworkKey = network.Key,
            Before = baseline,
            After = final,
            AppliedChanges = applied,
            Path = path,
            Verdicts = verdicts,
            VerifiedImprovement = improvement,
            TargetLabel = endpoint.Label,
            TargetProtocol = endpoint.ProtocolLabel,
            RouteReferenceOnly = endpoint.RouteReferenceOnly,
            Notices = notices,
        });
    }

    /// <summary>
    /// Re-measures the whole accepted set against the machine's own original state.
    /// </summary>
    /// <remarks>
    /// Runs through the same experiment machinery as a single candidate, with the
    /// "candidate" being every accepted change at once. That gives the bundle the same
    /// alternating order, the same settling and the same validity checks, and it means
    /// the number the user is shown was measured minutes after the baseline only in the
    /// sense that both halves were.
    /// </remarks>
    private async Task<(bool Confirmed, LatencyMeasurement? Final)> ConfirmBundleAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
        LatencyOptimizationSnapshot snapshot,
        IReadOnlyList<LatencyOptimizationCandidate> accepted,
        LatencyProbeRequest benchmark,
        LatencyEndpoint endpoint,
        LatencyEnvironment environment,
        CancellationToken token)
    {
        // The experiment expects to start from the original state, and the accepted set
        // is currently applied, so it is taken off first and put back at the end.
        foreach (var candidate in accepted.AsEnumerable().Reverse())
        {
            await RestoreCandidateAndTrimAsync(snapshot, candidate, token).ConfigureAwait(false);
        }

        var bundle = new LatencyOptimizationCandidate
        {
            Kind = LatencySettingKind.AdvancedProperty,
            PropertyName = "bundle",
            Description = string.Join(" · ", accepted.Select(candidate => candidate.Description)),
            CpuSensitive = accepted.Any(candidate => candidate.CpuSensitive),
            Descriptor = new InterventionDescriptor
            {
                Id = "bundle",
                Title = "Kabul edilen tüm değişiklikler",
                Mechanism = "Tek tek kabul edilen ayarların tamamı birlikte.",
                SettlingTime = accepted.Max(candidate => candidate.Descriptor.SettlingTime),
            },
        };

        var plan = new LatencyExperimentPlan
        {
            Network = network,
            Candidate = bundle,
            Probe = benchmark,
            MinimumCycles = _options.MinimumCycles,
            MaximumCycles = _options.MaximumCycles,
            MaximumDiscardedCycles = _options.MaximumLoadRetries,
            Budget = _options.CandidateBudget,
            Seed = _options.Seed,
            Evaluation = _options.Evaluation,
            AdaptiveProbeCount = _options.AdaptiveProbeCount,
            Reference = environment,
        };

        var arm = new BundleArm(this, adapter, snapshot, accepted, network, endpoint, environment);
        var outcome = await _runner.RunAsync(plan, arm, token).ConfigureAwait(false);

        if (!outcome.Verdict.Accepted)
        {
            return (false, outcome.LastOptimised);
        }

        // Confirmed: put the set back on and leave it there.
        foreach (var candidate in accepted)
        {
            var reapplied = await ApplyAndVerifyAsync(adapter, snapshot, candidate, network, endpoint, environment, token)
                .ConfigureAwait(false);

            if (!reapplied.Applied)
            {
                return (false, outcome.LastOptimised);
            }
        }

        return (true, outcome.LastOptimised);
    }

    /// <summary>Applies and restores one candidate, with the snapshot kept in step.</summary>
    private sealed class CandidateArm : ILatencyExperimentArm
    {
        private readonly LatencyOptimizer _owner;
        private readonly AdapterLatencyCapability _adapter;
        private readonly LatencyOptimizationSnapshot _snapshot;
        private readonly LatencyOptimizationCandidate _candidate;
        private readonly NetworkFingerprint _network;
        private readonly LatencyEndpoint _endpoint;
        private readonly LatencyEnvironment? _reference;

        public CandidateArm(
            LatencyOptimizer owner,
            AdapterLatencyCapability adapter,
            LatencyOptimizationSnapshot snapshot,
            LatencyOptimizationCandidate candidate,
            NetworkFingerprint network,
            LatencyEndpoint endpoint,
            LatencyEnvironment? reference)
        {
            _owner = owner;
            _adapter = adapter;
            _snapshot = snapshot;
            _candidate = candidate;
            _network = network;
            _endpoint = endpoint;
            _reference = reference;
        }

        public Task<LatencyArmOutcome> ApplyAsync(CancellationToken cancellationToken = default)
            => _owner.ApplyAndVerifyAsync(_adapter, _snapshot, _candidate, _network, _endpoint, _reference, cancellationToken);

        public Task RestoreAsync(CancellationToken cancellationToken = default)
            => _owner.RestoreCandidateAndTrimAsync(_snapshot, _candidate, cancellationToken);

        public async Task<bool> IsUsableAsync(CancellationToken cancellationToken = default)
        {
            var connectivity = await _owner._probe
                .CheckConnectivityAsync(_network, _endpoint.Address.ToString(), cancellationToken)
                .ConfigureAwait(false);

            return connectivity.IsUsable;
        }
    }

    /// <summary>Applies and restores the whole accepted set as one unit.</summary>
    private sealed class BundleArm : ILatencyExperimentArm
    {
        private readonly LatencyOptimizer _owner;
        private readonly AdapterLatencyCapability _adapter;
        private readonly LatencyOptimizationSnapshot _snapshot;
        private readonly IReadOnlyList<LatencyOptimizationCandidate> _candidates;
        private readonly NetworkFingerprint _network;
        private readonly LatencyEndpoint _endpoint;
        private readonly LatencyEnvironment? _reference;

        public BundleArm(
            LatencyOptimizer owner,
            AdapterLatencyCapability adapter,
            LatencyOptimizationSnapshot snapshot,
            IReadOnlyList<LatencyOptimizationCandidate> candidates,
            NetworkFingerprint network,
            LatencyEndpoint endpoint,
            LatencyEnvironment? reference)
        {
            _owner = owner;
            _adapter = adapter;
            _snapshot = snapshot;
            _candidates = candidates;
            _network = network;
            _endpoint = endpoint;
            _reference = reference;
        }

        public async Task<LatencyArmOutcome> ApplyAsync(CancellationToken cancellationToken = default)
        {
            foreach (var candidate in _candidates)
            {
                var applied = await _owner
                    .ApplyAndVerifyAsync(_adapter, _snapshot, candidate, _network, _endpoint, _reference, cancellationToken)
                    .ConfigureAwait(false);

                if (!applied.Applied)
                {
                    return applied;
                }
            }

            return LatencyArmOutcome.Success;
        }

        public async Task RestoreAsync(CancellationToken cancellationToken = default)
        {
            foreach (var candidate in _candidates.AsEnumerable().Reverse())
            {
                await _owner.RestoreCandidateAndTrimAsync(_snapshot, candidate, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<bool> IsUsableAsync(CancellationToken cancellationToken = default)
        {
            var connectivity = await _owner._probe
                .CheckConnectivityAsync(_network, _endpoint.Address.ToString(), cancellationToken)
                .ConfigureAwait(false);

            return connectivity.IsUsable;
        }
    }

    /// <summary>
    /// Records the original value, then writes the new one.
    /// </summary>
    /// <remarks>
    /// In that order, always. A crash between the two leaves a snapshot describing a
    /// change that was never made, and undoing that is a write of the value the adapter
    /// already holds. Writing first and recording afterwards would lose a real change.
    /// </remarks>
    private async Task<LatencyArmOutcome> ApplyAndVerifyAsync(
        AdapterLatencyCapability adapter,
        LatencyOptimizationSnapshot snapshot,
        LatencyOptimizationCandidate candidate,
        NetworkFingerprint network,
        LatencyEndpoint endpoint,
        LatencyEnvironment? reference,
        CancellationToken token)
    {
        var captured = candidate.ToSnapshot(adapter);
        snapshot.Settings.Add(captured);

        await SaveSnapshotAsync(
            snapshot with { State = LatencyTransactionState.CandidateApplied, PendingProperty = candidate.PropertyName },
            token).ConfigureAwait(false);

        var applied = await _controller
            .ApplyAsync(adapter, candidate, Restart, token)
            .ConfigureAwait(false);

        if (applied.IsEffective)
        {
            // A restart changes more than the keyword: the interface can come back on a
            // different route or associated to a different access point, and measuring
            // that against a baseline taken before would be comparing two networks.
            var moved = applied.RestartPerformed
                ? await DescribePostRestartProblemAsync(network, endpoint, reference, token).ConfigureAwait(false)
                : null;

            if (moved is null)
            {
                // The operational answer for this keyword is logged next to the state, so
                // a support log shows what the stack said rather than only what we did.
                var reported = applied.Operational.ForKeyword(candidate.PropertyName) switch
                {
                    true => " · işletim sistemi: etkin",
                    false => " · işletim sistemi: etkin değil",
                    null => string.Empty,
                };

                _log?.Invoke(
                    $"latency.candidate.applied: {candidate.PropertyName} · {applied.Describe()}{reported}"
                    + (applied.RestartPerformed ? " (bağdaştırıcı yeniden başlatıldı)" : string.Empty));
                return LatencyArmOutcome.Success;
            }

            _log?.Invoke($"latency.candidate.skipped: {candidate.PropertyName} · {moved}");
            await RestoreCandidateAndTrimAsync(snapshot, candidate, token).ConfigureAwait(false);
            return LatencyArmOutcome.Failed(moved);
        }

        var reason = applied.Describe();
        _log?.Invoke($"latency.candidate.skipped: {candidate.PropertyName} · {reason}");
        await RestoreCandidateAndTrimAsync(snapshot, candidate, token).ConfigureAwait(false);
        return LatencyArmOutcome.Failed(reason);
    }

    /// <summary>
    /// Whether the machine that came back from a restart is still the one being measured.
    /// </summary>
    /// <returns>Null when everything checks out; otherwise what failed, for the report.</returns>
    /// <remarks>
    /// The adapter identity and the link itself are checked inside the controller, which
    /// re-finds the interface by GUID and requires an address and a default route before
    /// it reports anything. What is checked here is everything above that: the same
    /// interface index, the same first hop, the same access point, and the target still
    /// answering. Any of those changing means the later measurements belong to a
    /// different experiment.
    /// </remarks>
    private async Task<string?> DescribePostRestartProblemAsync(
        NetworkFingerprint network,
        LatencyEndpoint endpoint,
        LatencyEnvironment? reference,
        CancellationToken token)
    {
        var current = _environmentSampler.Sample(network);

        if (reference is not null)
        {
            if (reference.InterfaceIndex != current.InterfaceIndex)
            {
                return "yeniden başlatmadan sonra farklı bir arabirim etkin oldu";
            }

            if (Moved(reference.RouteHash, current.RouteHash))
            {
                return "yeniden başlatmadan sonra ilk atlama değişti";
            }

            if (Moved(reference.AccessPointHash, current.AccessPointHash))
            {
                return "yeniden başlatmadan sonra farklı bir erişim noktasına bağlanıldı";
            }
        }

        var connectivity = await _probe
            .CheckConnectivityAsync(network, endpoint.Address.ToString(), token)
            .ConfigureAwait(false);

        if (!connectivity.RemoteReachable)
        {
            return "yeniden başlatmadan sonra ölçüm hedefine ulaşılamadı";
        }

        return null;

        static bool Moved(string? first, string? second)
            => first is not null && second is not null && !string.Equals(first, second, StringComparison.Ordinal);
    }

    private async Task RestoreCandidateAndTrimAsync(
        LatencyOptimizationSnapshot snapshot,
        LatencyOptimizationCandidate candidate,
        CancellationToken cancellationToken)
    {
        var setting = snapshot.Settings.LastOrDefault(entry =>
            string.Equals(entry.PropertyName, candidate.PropertyName, StringComparison.OrdinalIgnoreCase));

        if (setting is null)
        {
            return;
        }

        var outcome = await _controller.RestoreAsync(setting, cancellationToken).ConfigureAwait(false);
        if (!LatencySnapshotRestorer.IsTerminal(outcome))
        {
            throw new InvalidOperationException($"'{setting.PropertyName}' özgün değerine geri alınamadı ({outcome}).");
        }

        snapshot.Settings.Remove(setting);

        if (snapshot.IsEmpty)
        {
            await _snapshots.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SaveSnapshotAsync(
                snapshot with { State = LatencyTransactionState.CandidateApplied, PendingProperty = null },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SaveSnapshotAsync(LatencyOptimizationSnapshot snapshot, CancellationToken cancellationToken)
        => _snapshots.SaveAsync(snapshot, cancellationToken);

    private async Task<bool> TryRestoreAfterFailureAsync()
    {
        try
        {
            _log?.Invoke("latency.rollback.started: hata veya iptal sonrası geri yükleme.");
            var restored = await _restorer.RestoreAllAsync(CancellationToken.None).ConfigureAwait(false);
            _log?.Invoke(restored ? "latency.rollback.completed" : "latency.rollback.failed");
            return restored;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"latency.rollback.failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The saved result for this exact network, adapter, driver and target, if there is one.
    /// </summary>
    /// <remarks>
    /// All of them have to match, and the answer has to be recent. A different adapter, a
    /// driver update, a different game server or a month-old result is treated as unknown
    /// and measured again, because a setting proved on an Ethernet card says nothing about
    /// the Wi-Fi one next to it, and a setting proved against one server says nothing
    /// about the route to another.
    /// </remarks>
    private async Task<LatencyProfile?> LoadUsableProfileAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
        LatencyProfileContext context,
        CancellationToken token)
    {
        if (!_options.UseProfileCache)
        {
            return null;
        }

        var profile = await _profiles.FindAsync(network.Key, adapter.AdapterId, token).ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        if (!profile.Matches(network.Key, adapter, context) || !profile.IsFresh(_now()))
        {
            _log?.Invoke("latency.profile: kayıtlı sonuç bu bağdaştırıcı/sürücü/hedef için geçersiz; yeniden ölçülecek.");
            return null;
        }

        return profile;
    }

    /// <summary>Drops candidates a matching, still-current profile already turned down.</summary>
    private IReadOnlyList<LatencyOptimizationCandidate> SelectCandidates(
        AdapterLatencyCapability adapter,
        LatencyProfile? profile,
        LatencyEndpoint endpoint,
        LatencyEnvironment environment,
        LatencyProfileContext context)
    {
        var candidates = adapter.BuildSafeCandidates(new LatencyCandidateContext
        {
            Scope = ScopeOf(endpoint.Protocol),
            ApplicationScope = ScopeOf(endpoint.ApplicationProtocol ?? endpoint.Protocol),
            Power = environment.Power,
            IsWireless = adapter.AdapterType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211,
            AllowPowerCost = environment.Power != PowerSource.Battery,
            IncludeThroughputSensitive = false,
        });

        if (candidates.Count == 0 || profile is null)
        {
            return candidates;
        }

        // A rejection is never re-proved, only obeyed, so it expires far sooner than an
        // acceptance and only holds while the conditions it was reached under still do.
        if (!profile.RejectionsUsable(_now(), context))
        {
            _log?.Invoke("latency.profile: eski elemeler bu koşullar için geçerli değil; adaylar yeniden ölçülecek.");
            return candidates;
        }

        var rejected = new HashSet<string>(profile.RejectedProperties, StringComparer.OrdinalIgnoreCase);
        var remaining = candidates.Where(candidate => !rejected.Contains(candidate.PropertyName)).ToArray();

        if (remaining.Length != candidates.Count)
        {
            _log?.Invoke(
                $"latency.profile: {candidates.Count - remaining.Length} aday bu ağda daha önce ölçülüp elendiği için atlandı.");
        }

        return remaining;
    }

    private static LatencyTrafficScope ScopeOf(LatencyProtocol protocol) => protocol switch
    {
        LatencyProtocol.Tcp => LatencyTrafficScope.Tcp,
        LatencyProtocol.Udp => LatencyTrafficScope.Udp,
        _ => LatencyTrafficScope.Icmp,
    };

    /// <summary>
    /// Re-applies a previously verified profile and freshly proves it is still beneficial.
    /// </summary>
    /// <returns>The published result, or null to fall through to a full benchmark.</returns>
    private async Task<LatencyOptimizationResult?> ReplayProfileAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
        LatencyProfile profile,
        LatencyMeasurement baseline,
        LatencyProbeRequest benchmark,
        LatencyPathAnalysis path,
        LatencyEndpoint endpoint,
        LatencyEnvironment environment,
        CancellationToken token)
    {
        var wanted = new HashSet<string>(profile.AcceptedProperties, StringComparer.OrdinalIgnoreCase);
        var candidates = adapter.BuildSafeCandidates()
            .Where(candidate => wanted.Contains(candidate.PropertyName))
            .ToArray();

        // The driver no longer offers what the profile is about, so it describes a
        // machine that does not exist any more.
        if (candidates.Length != wanted.Count)
        {
            _log?.Invoke("latency.profile: kayıtlı ayarlar bu sürücüde artık mevcut değil; baştan ölçülecek.");
            await _profiles.RemoveAsync(network.Key, adapter.AdapterId, token).ConfigureAwait(false);
            return null;
        }

        SetCurrent(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Optimizing,
            StatusLine = $"Bu ağda daha önce doğrulanan {candidates.Length} ayar yeniden uygulanıyor…",
            AdapterName = adapter.AdapterName,
            NetworkKey = network.Key,
            Before = baseline,
            Path = path,
        });

        var snapshot = new LatencyOptimizationSnapshot
        {
            AdapterId = adapter.AdapterId,
            AdapterName = adapter.AdapterName,
            NetworkKey = network.Key,
            CreatedAt = _now(),
            State = LatencyTransactionState.SnapshotCreated,
        };

        foreach (var candidate in candidates)
        {
            var replayApplied = await ApplyAndVerifyAsync(
                adapter, snapshot, candidate, network, endpoint, environment, token).ConfigureAwait(false);

            if (!replayApplied.Applied)
            {
                var restored = await _restorer.RestoreAllAsync(CancellationToken.None).ConfigureAwait(false);
                if (!restored)
                {
                    await RemoveProfileSafelyAsync(network, adapter).ConfigureAwait(false);
                    return Publish(ProfileRollbackFailure(network, adapter, baseline, path));
                }

                await RemoveProfileSafelyAsync(network, adapter).ConfigureAwait(false);
                return null;
            }
        }

        await SaveSnapshotAsync(snapshot with { State = LatencyTransactionState.Verifying }, token).ConfigureAwait(false);

        var connectivity = await _probe
            .CheckConnectivityAsync(network, endpoint.Address.ToString(), token)
            .ConfigureAwait(false);

        var confirmation = connectivity.IsUsable
            ? await _probe.MeasureAsync(network, benchmark, token).ConfigureAwait(false)
            : null;

        var cpuSensitive = candidates.Any(candidate => candidate.CpuSensitive);
        if (confirmation is null
            || !LatencyComparison.ConfirmsMeaningfulImprovement(baseline, confirmation, cpuSensitive))
        {
            _log?.Invoke("latency.profile: kayıtlı ayarlar bu oturumda anlamlı kazanç göstermedi; geri alınıp baştan ölçülecek.");
            var restored = await _restorer.RestoreAllAsync(CancellationToken.None).ConfigureAwait(false);
            if (!restored)
            {
                await RemoveProfileSafelyAsync(network, adapter).ConfigureAwait(false);
                return Publish(ProfileRollbackFailure(network, adapter, baseline, path));
            }

            await RemoveProfileSafelyAsync(network, adapter).ConfigureAwait(false);
            return null;
        }

        await SaveSnapshotAsync(
            snapshot with { State = LatencyTransactionState.Committed, PendingProperty = null },
            token).ConfigureAwait(false);

        _lastNetworkKey = network.Key;
        var applied = candidates.Select(candidate => candidate.Description).ToArray();
        var improvement = LatencyDelta.Between(baseline, confirmation);
        var refreshedProfile = profile with
        {
            VerifiedAt = _now(),
            Baseline = LatencySummary.From(baseline),
            Optimized = LatencySummary.From(confirmation),
            Bottleneck = path.Bottleneck,
        };

        await SaveReplayedProfileSafelyAsync(refreshedProfile, token).ConfigureAwait(false);
        _log?.Invoke($"latency.profile.replayed: {applied.Length} ayar yeniden uygulandı ve doğrulandı.");

        return Publish(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Active,
            StatusLine = LatencyReport.Replayed(adapter.AdapterName, baseline, confirmation, improvement, applied, path, endpoint),
            AdapterName = adapter.AdapterName,
            NetworkKey = network.Key,
            Before = baseline,
            After = confirmation,
            AppliedChanges = applied,
            Path = path,
            VerifiedImprovement = improvement,
            TargetLabel = endpoint.Label,
            TargetProtocol = endpoint.ProtocolLabel,
            RouteReferenceOnly = endpoint.RouteReferenceOnly,
        });
    }

    private LatencyOptimizationResult ProfileRollbackFailure(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
        LatencyMeasurement baseline,
        LatencyPathAnalysis path) => new()
        {
            Status = LatencyOptimizationStatus.Failed,
            StatusLine = "Kayıtlı profil doğrulanamadı ve bazı NIC ayarları geri yüklenemedi; "
                + "snapshot kurtarma için korundu ve yeni ölçüm başlatılmadı.",
            AdapterName = adapter.AdapterName,
            NetworkKey = network.Key,
            Before = baseline,
            Path = path,
        };

    private async Task RemoveProfileSafelyAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter)
    {
        try
        {
            await _profiles.RemoveAsync(network.Key, adapter.AdapterId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"latency.profile: geçersiz kayıt silinemedi ({ex.Message}).");
        }
    }

    private async Task SaveReplayedProfileSafelyAsync(LatencyProfile profile, CancellationToken token)
    {
        try
        {
            await _profiles.SaveAsync(profile, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"latency.profile: yeniden doğrulanan kayıt yenilenemedi ({ex.Message}).");
        }
    }

    private async Task SaveProfileAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
        LatencyMeasurement baseline,
        LatencyMeasurement? optimized,
        IReadOnlyList<LatencyVerdict> verdicts,
        LatencyPathAnalysis path,
        LatencyProfileContext context,
        CancellationToken token)
    {
        try
        {
            await _profiles.SaveAsync(
                new LatencyProfile
                {
                    NetworkKey = network.Key,
                    AdapterId = adapter.AdapterId,
                    AdapterName = adapter.AdapterName,
                    CapabilityFingerprint = adapter.CapabilityFingerprint,
                    VerifiedAt = _now(),
                    AcceptedProperties = [.. verdicts.Where(v => v.Accepted).Select(v => v.PropertyName)],
                    RejectedProperties = [.. verdicts.Where(v => !v.Accepted).Select(v => v.PropertyName)],
                    Baseline = LatencySummary.From(baseline),
                    Optimized = optimized is null ? null : LatencySummary.From(optimized),
                    Bottleneck = path.Bottleneck,
                    Context = context,
                },
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The profile is a cache. Losing it costs one re-measurement, never a result.
            _log?.Invoke($"latency.profile: sonuç kaydedilemedi ({ex.Message}).");
        }
    }

    private static LatencyOptimizationResult NoGain(
        AdapterLatencyCapability adapter,
        NetworkFingerprint network,
        LatencyMeasurement before,
        LatencyMeasurement? after,
        LatencyPathAnalysis path,
        IReadOnlyList<LatencyVerdict> verdicts,
        string headline,
        LatencyEndpoint? endpoint = null,
        IReadOnlyList<string>? notices = null) => new()
        {
            Status = LatencyOptimizationStatus.NoGain,
            StatusLine = LatencyReport.NoGain(headline, after ?? before, verdicts, path),
            AdapterName = adapter.AdapterName,
            NetworkKey = network.Key,
            Before = before,
            After = after,
            Path = path,
            Verdicts = verdicts,
            TargetLabel = endpoint?.Label ?? string.Empty,
            TargetProtocol = endpoint?.ProtocolLabel ?? string.Empty,
            RouteReferenceOnly = endpoint?.RouteReferenceOnly ?? false,
            Notices = notices ?? [],
        };

    private LatencyOptimizationResult Publish(LatencyOptimizationResult result)
    {
        SetCurrent(result);
        return result;
    }

    private void SetCurrent(LatencyOptimizationResult result)
    {
        Current = result;
        Changed?.Invoke(result);
    }

    private CancellationTokenSource CreateOperationCancellation(CancellationToken callerToken)
    {
        lock (_cancellationGate)
        {
            _operationCancellation?.Cancel();
            var lifetimeToken = _lifetime?.Token ?? CancellationToken.None;
            _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, lifetimeToken);
            return _operationCancellation;
        }
    }

    private void CancelActiveOperation()
    {
        lock (_cancellationGate)
        {
            _operationCancellation?.Cancel();
        }
    }

    private void ClearOperationCancellation(CancellationTokenSource operation)
    {
        lock (_cancellationGate)
        {
            if (ReferenceEquals(_operationCancellation, operation))
            {
                _operationCancellation = null;
            }
        }

        operation.Dispose();
    }

    private void OnNetworkChanged(NetworkFingerprint network)
    {
        if (!_enabled || _disposed)
        {
            return;
        }

        _log?.Invoke($"latency.network.changed: '{network.DisplayName}' ({network.Key}); ölçüm yeniden başlatılıyor.");

        CancelActiveOperation();
        _ = Task.Run(async () =>
        {
            try
            {
                await RunForNetworkAsync(network, force: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"latency.network.changed: sonraki ölçüm başarısız ({ex.Message}).");
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await StopAndRestoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Disposal is the last chance to put the adapter back, but throwing here
            // would take the shutdown path down with it and strand everything after.
            // The snapshot survives, so the next launch recovers.
            _log?.Invoke($"latency.rollback.failed: kapatma sırasında geri yükleme tamamlanamadı ({ex.Message}).");
        }
        finally
        {
            _disposed = true;
            _lifetime?.Dispose();
            _operationCancellation?.Dispose();
            _operationGate.Dispose();
        }
    }
}
