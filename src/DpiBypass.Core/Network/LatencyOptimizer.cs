using DpiBypass.Core.Logging;

namespace DpiBypass.Core.Network;

/// <summary>How thorough a run is allowed to be.</summary>
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
}

/// <summary>
/// Measures, applies and independently verifies safe NIC changes, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// The shape of a run is measure, change one thing, measure again, keep or put back -
/// never apply a list of settings and assume. Each candidate is judged by repeated
/// paired cycles against the same target under the same load, and the aggregate has to
/// beat how much the cycles disagree with each other before anything is kept. See
/// <see cref="LatencyComparison"/> for the rule itself.
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

    public LatencyOptimizer(
        ILatencyAdapterController? controller = null,
        ILatencyProbe? probe = null,
        ILatencySnapshotStore? snapshots = null,
        Func<NetworkMonitor>? monitorFactory = null,
        Action<string>? log = null,
        ILatencyProfileStore? profiles = null,
        LatencyOptimizerOptions? options = null,
        Func<DateTimeOffset>? now = null)
    {
        _log = log ?? AppLog.InfoSink;
        _controller = controller ?? new WindowsLatencyAdapterController(_log);
        _probe = probe ?? new LatencyProbe();
        _snapshots = snapshots ?? new LatencySnapshotStore();
        _profiles = profiles ?? new LatencyProfileStore(log: _log);
        _monitorFactory = monitorFactory ?? (() => new NetworkMonitor(log: _log));
        _options = options ?? LatencyOptimizerOptions.Default;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public LatencyOptimizationResult Current { get; private set; } = new()
    {
        Status = LatencyOptimizationStatus.Disabled,
        StatusLine = "Kapalı.",
    };

    public bool IsBusy => Current.Status is LatencyOptimizationStatus.Measuring
        or LatencyOptimizationStatus.Optimizing
        or LatencyOptimizationStatus.Restoring;

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
            + $"{snapshot.Settings.Count} ayar, bekleyen '{snapshot.PendingProperty ?? "-"}').");

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

            var restored = await RestoreSnapshotCoreAsync(cancellationToken).ConfigureAwait(false);
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

            var restored = await RestoreSnapshotCoreAsync(cancellationToken).ConfigureAwait(false);
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

            var restored = await RestoreSnapshotCoreAsync(cancellationToken).ConfigureAwait(false);
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
    public async Task<LatencyOptimizationResult> TestAsync(CancellationToken cancellationToken = default)
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

        var survey = await _probe.MeasureAsync(network, _options.Survey, cancellationToken).ConfigureAwait(false);
        var measurement = survey.HasRemoteConnectivity
            ? await _probe.MeasureAsync(
                network,
                _options.Benchmark.For(survey.RemoteEndpoint),
                cancellationToken).ConfigureAwait(false)
            : survey;

        var path = LatencyPathAnalysis.Describe(measurement);

        return new LatencyOptimizationResult
        {
            Status = measurement.HasRemoteConnectivity
                ? LatencyOptimizationStatus.Disabled
                : LatencyOptimizationStatus.Offline,
            StatusLine = LatencyReport.Measurement(network, measurement, path),
            AdapterName = network.AdapterName ?? network.DisplayName,
            NetworkKey = network.Key,
            After = measurement,
            Path = path,
        };
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
        if (!await RestoreSnapshotCoreAsync(token).ConfigureAwait(false))
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
                StatusLine = "Desteklenen aktif fiziksel düşük-gecikme NIC ayarı bulunamadı.",
                AdapterName = adapter?.AdapterName ?? network.AdapterName ?? network.DisplayName,
                NetworkKey = network.Key,
            });
        }

        // One short pass settles which target answers here; every later measurement in
        // this run uses that same target so the numbers can be subtracted from each other.
        _log?.Invoke($"latency.baseline.started: {adapter.AdapterName} · ağ {network.Key}");
        var survey = await _probe.MeasureAsync(network, _options.Survey, token).ConfigureAwait(false);
        var benchmark = _options.Benchmark.For(survey.RemoteEndpoint);

        var baseline = survey.HasRemoteConnectivity
            ? await _probe.MeasureAsync(network, benchmark, token).ConfigureAwait(false)
            : survey;

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
            });
        }

        var path = LatencyPathAnalysis.Describe(baseline);
        _log?.Invoke($"latency.baseline.completed: {LatencyReport.Compact(baseline)} · {path.Bottleneck}");

        var profile = await LoadUsableProfileAsync(network, adapter, token).ConfigureAwait(false);

        // A profile verified here, on this adapter, against this driver is worth
        // re-applying rather than re-earning: a full paired benchmark on every logon
        // would spend minutes pinging to reach an answer that is already known. It is
        // still applied through the same snapshot and confirmed against a fresh
        // measurement, and a profile that no longer holds up is deleted on the spot.
        if (profile is { AcceptedProperties.Count: > 0 })
        {
            var replayed = await ReplayProfileAsync(network, adapter, profile, baseline, benchmark, path, token)
                .ConfigureAwait(false);

            if (replayed is not null)
            {
                return replayed;
            }

            // The replay only returns null after deleting the profile, so what it said
            // about the candidates it turned down is no longer worth anything either:
            // the full benchmark below starts from every candidate again.
            profile = null;
        }

        var candidates = SelectCandidates(adapter, profile);
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
            });
        }

        return await RunCandidatesAsync(network, adapter, baseline, benchmark, path, candidates, token)
            .ConfigureAwait(false);
    }

    private async Task<LatencyOptimizationResult> RunCandidatesAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
        LatencyMeasurement baseline,
        LatencyProbeRequest benchmark,
        LatencyPathAnalysis path,
        IReadOnlyList<LatencyOptimizationCandidate> candidates,
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

        for (var index = 0; index < candidates.Count; index++)
        {
            token.ThrowIfCancellationRequested();
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
            });

            var outcome = await RunPairedCyclesAsync(network, adapter, snapshot, candidate, benchmark, token)
                .ConfigureAwait(false);

            if (outcome.LostConnectivity)
            {
                _log?.Invoke("latency.rollback.started: NIC değişikliğinden sonra ağ yanıt vermedi.");
                await RestoreSnapshotCoreAsync(CancellationToken.None).ConfigureAwait(false);

                return Publish(NoGain(
                    adapter,
                    network,
                    baseline,
                    null,
                    path,
                    verdicts,
                    "Bağlantı denetimi başarısız oldu; özgün NIC ayarları geri yüklendi."));
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
            if (!await ApplyAndTrackAsync(adapter, snapshot, candidate, token).ConfigureAwait(false))
            {
                verdicts[^1] = outcome.Verdict with
                {
                    Outcome = LatencyVerdictOutcome.Rejected,
                    Reason = "doğrulandıktan sonra sürücü değeri kalıcı olarak uygulamadı",
                };
                continue;
            }

            accepted.Add(candidate);
            reference = outcome.Last ?? reference;
        }

        if (accepted.Count == 0)
        {
            // Restore rather than clear. Every candidate is put back inside its own
            // cycles, so this is normally a no-op - but clearing a snapshot that still
            // described a value on the adapter would abandon it, and the whole point of
            // the file is that this cannot happen.
            await RestoreSnapshotCoreAsync(token).ConfigureAwait(false);
            await SaveProfileAsync(network, adapter, baseline, null, verdicts, path, token).ConfigureAwait(false);

            return Publish(NoGain(
                adapter,
                network,
                baseline,
                reference,
                path,
                verdicts,
                "Bu ağda doğrulanmış bir gecikme iyileşmesi bulunamadı. Özgün ayarlar geri yüklendi."));
        }

        // Everything accepted is on the adapter now. One last independent measurement
        // decides whether the machine as a whole is better, not just each step.
        await SaveSnapshotAsync(snapshot with { State = LatencyTransactionState.Verifying }, token).ConfigureAwait(false);

        var connectivity = await _probe
            .CheckConnectivityAsync(network, baseline.RemoteEndpoint, token)
            .ConfigureAwait(false);

        var final = connectivity.IsUsable
            ? await _probe.MeasureAsync(network, benchmark, token).ConfigureAwait(false)
            : null;

        if (final is null || !LatencyComparison.ConfirmsImprovement(baseline, final))
        {
            _log?.Invoke("latency.verification.completed: son ölçüm başlangıcın gerisinde kaldı.");
            _log?.Invoke("latency.rollback.started: son doğrulama geçilemedi.");
            var undone = await RestoreSnapshotCoreAsync(CancellationToken.None).ConfigureAwait(false);
            _log?.Invoke(undone ? "latency.rollback.completed" : "latency.rollback.failed");

            return Publish(NoGain(
                adapter,
                network,
                baseline,
                final,
                path,
                verdicts,
                final is null
                    ? "Son doğrulamada bağlantı ölçülemedi; özgün NIC ayarları geri yüklendi."
                    : "Son doğrulama başlangıç ölçümünün gerisinde kaldı; özgün NIC ayarları geri yüklendi."));
        }

        _log?.Invoke($"latency.verification.completed: {LatencyReport.Compact(final)}");

        await SaveSnapshotAsync(
            snapshot with { State = LatencyTransactionState.Committed, PendingProperty = null },
            token).ConfigureAwait(false);

        await SaveProfileAsync(network, adapter, baseline, final, verdicts, path, token).ConfigureAwait(false);

        _lastNetworkKey = network.Key;

        // The headline number comes from the paired cycles, not from the single final
        // sample: that is the measurement the acceptance rule actually rests on, and one
        // lucky or unlucky last sample must not be able to inflate or erase it.
        var improvement = LatencyDelta.Sum([.. verdicts.Where(v => v.Accepted).Select(v => v.Delta)]);
        var applied = accepted.Select(candidate => candidate.Description).ToArray();

        _log?.Invoke($"latency.committed: {applied.Length} ayar · median {improvement.MedianMs:F1} ms · p95 {improvement.P95Ms:F1} ms");

        return Publish(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Active,
            StatusLine = LatencyReport.Verified(adapter.AdapterName, baseline, final, improvement, applied, path),
            AdapterName = adapter.AdapterName,
            NetworkKey = network.Key,
            Before = baseline,
            After = final,
            AppliedChanges = applied,
            Path = path,
            Verdicts = verdicts,
            VerifiedImprovement = improvement,
        });
    }

    private sealed record CandidateOutcome(LatencyVerdict Verdict, LatencyMeasurement? Last, bool LostConnectivity);

    /// <summary>
    /// Runs paired A/B cycles for one candidate, leaving the adapter exactly as it found it.
    /// </summary>
    /// <remarks>
    /// Each cycle measures the machine without the change and then with it, back to back
    /// against the same target. Alternating like this - rather than one baseline followed
    /// by every candidate - is what keeps a network that simply got quieter halfway
    /// through the run from being reported as an improvement.
    /// </remarks>
    private async Task<CandidateOutcome> RunPairedCyclesAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
        LatencyOptimizationSnapshot snapshot,
        LatencyOptimizationCandidate candidate,
        LatencyProbeRequest benchmark,
        CancellationToken token)
    {
        var pairs = new List<LatencyPair>();
        var loadRetries = 0;
        LatencyMeasurement? last = null;
        var verdict = LatencyComparison.Evaluate(candidate, pairs, _options.MinimumCycles, _options.MaximumCycles);

        while (pairs.Count < _options.MaximumCycles)
        {
            token.ThrowIfCancellationRequested();

            var before = await _probe.MeasureAsync(network, benchmark, token).ConfigureAwait(false);

            if (!await ApplyAndTrackAsync(adapter, snapshot, candidate, token).ConfigureAwait(false))
            {
                return new CandidateOutcome(
                    verdict with
                    {
                        Outcome = LatencyVerdictOutcome.Rejected,
                        Reason = "sürücü değeri canlı olarak uygulamadı",
                        Cycles = pairs.Count,
                    },
                    last,
                    LostConnectivity: false);
            }

            var connectivity = await _probe
                .CheckConnectivityAsync(network, before.RemoteEndpoint, token)
                .ConfigureAwait(false);

            if (!connectivity.IsUsable)
            {
                return new CandidateOutcome(
                    verdict with { Outcome = LatencyVerdictOutcome.Rejected, Reason = "bağlantı koptu" },
                    last,
                    LostConnectivity: true);
            }

            var after = await _probe.MeasureAsync(network, benchmark, token).ConfigureAwait(false);
            last = after;

            await RestoreCandidateAndTrimAsync(snapshot, candidate, token).ConfigureAwait(false);

            var pair = new LatencyPair { Baseline = before, Candidate = after };

            // The link being busy for one half only makes the pair a measurement of the
            // traffic rather than of the setting. Re-run it a bounded number of times
            // before giving up on getting a clean window.
            if (!pair.IsComparable && loadRetries < _options.MaximumLoadRetries)
            {
                loadRetries++;
                _log?.Invoke(
                    $"latency.cycle.discarded: {candidate.PropertyName} · "
                    + $"yük durumu eşleşmedi ({before.Load.State} / {after.Load.State}), tekrarlanıyor.");
                continue;
            }

            pairs.Add(pair);
            verdict = LatencyComparison.Evaluate(candidate, pairs, _options.MinimumCycles, _options.MaximumCycles);

            _log?.Invoke(
                $"latency.cycle.completed: {candidate.PropertyName} · tur {pairs.Count} · "
                + $"median {pair.Delta.MedianMs:+0.0;-0.0;0.0} ms · karar {verdict.Outcome}");

            if (verdict.Outcome != LatencyVerdictOutcome.Inconclusive)
            {
                break;
            }
        }

        if (verdict.Outcome == LatencyVerdictOutcome.Inconclusive)
        {
            verdict = verdict with
            {
                Outcome = LatencyVerdictOutcome.Rejected,
                Reason = $"{pairs.Count} turda kararlı bir sonuç çıkmadı ({verdict.Reason})",
            };
        }

        return new CandidateOutcome(verdict, last, LostConnectivity: false);
    }

    /// <summary>
    /// Records the original value, then writes the new one.
    /// </summary>
    /// <remarks>
    /// In that order, always. A crash between the two leaves a snapshot describing a
    /// change that was never made, and undoing that is a write of the value the adapter
    /// already holds. Writing first and recording afterwards would lose a real change.
    /// </remarks>
    private async Task<bool> ApplyAndTrackAsync(
        AdapterLatencyCapability adapter,
        LatencyOptimizationSnapshot snapshot,
        LatencyOptimizationCandidate candidate,
        CancellationToken token)
    {
        var captured = candidate.ToSnapshot(adapter);
        snapshot.Settings.Add(captured);

        await SaveSnapshotAsync(
            snapshot with { State = LatencyTransactionState.CandidateApplied, PendingProperty = candidate.PropertyName },
            token).ConfigureAwait(false);

        var applied = await _controller.ApplyAsync(adapter, candidate, token).ConfigureAwait(false);
        if (applied.Applied)
        {
            _log?.Invoke($"latency.candidate.applied: {candidate.PropertyName}");
            return true;
        }

        _log?.Invoke($"latency.candidate.skipped: {candidate.PropertyName} · {applied.Reason ?? "canlı uygulanamadı"}");
        await RestoreCandidateAndTrimAsync(snapshot, candidate, token).ConfigureAwait(false);
        return false;
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
        if (!IsTerminalRestore(outcome))
        {
            throw new InvalidOperationException($"'{setting.PropertyName}' özgün değerine geri alınamadı ({outcome}).");
        }

        snapshot.Settings.Remove(setting);

        if (snapshot.Settings.Count == 0)
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

    private async Task<bool> RestoreSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _snapshots.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return true;
        }

        var unresolved = new List<LatencySettingSnapshot>();
        foreach (var setting in snapshot.Settings.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();

            LatencyRestoreOutcome outcome;
            try
            {
                outcome = await _controller.RestoreAsync(setting, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"latency.rollback.failed: '{setting.PropertyName}' ({ex.Message}).");
                outcome = LatencyRestoreOutcome.Failed;
            }

            if (!IsTerminalRestore(outcome))
            {
                // Add at the front to preserve original apply order in the retained file.
                unresolved.Insert(0, setting);
            }
        }

        if (unresolved.Count == 0)
        {
            await _snapshots.ClearAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        await _snapshots.SaveAsync(
            snapshot with
            {
                Settings = unresolved,
                State = LatencyTransactionState.CandidateApplied,
                PendingProperty = unresolved[0].PropertyName,
            },
            cancellationToken).ConfigureAwait(false);

        return false;
    }

    private async Task<bool> TryRestoreAfterFailureAsync()
    {
        try
        {
            _log?.Invoke("latency.rollback.started: hata veya iptal sonrası geri yükleme.");
            var restored = await RestoreSnapshotCoreAsync(CancellationToken.None).ConfigureAwait(false);
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
    /// The saved result for this exact network, adapter and driver, if there is one.
    /// </summary>
    /// <remarks>
    /// All three have to match, and the answer has to be recent. A different adapter, a
    /// driver update or a month-old result is treated as unknown and measured again,
    /// because a setting proved on an Ethernet card says nothing about the Wi-Fi one
    /// next to it.
    /// </remarks>
    private async Task<LatencyProfile?> LoadUsableProfileAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
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

        if (!profile.Matches(network.Key, adapter) || !profile.IsFresh(_now()))
        {
            _log?.Invoke("latency.profile: kayıtlı sonuç bu bağdaştırıcı/sürücü için geçersiz; yeniden ölçülecek.");
            return null;
        }

        return profile;
    }

    /// <summary>Drops candidates a matching profile already measured and turned down.</summary>
    private IReadOnlyList<LatencyOptimizationCandidate> SelectCandidates(
        AdapterLatencyCapability adapter,
        LatencyProfile? profile)
    {
        var candidates = adapter.BuildSafeCandidates();
        if (candidates.Count == 0 || profile is null)
        {
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

    /// <summary>
    /// Re-applies a previously verified profile and confirms the machine is no worse for it.
    /// </summary>
    /// <returns>The published result, or null to fall through to a full benchmark.</returns>
    private async Task<LatencyOptimizationResult?> ReplayProfileAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
        LatencyProfile profile,
        LatencyMeasurement baseline,
        LatencyProbeRequest benchmark,
        LatencyPathAnalysis path,
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
            if (!await ApplyAndTrackAsync(adapter, snapshot, candidate, token).ConfigureAwait(false))
            {
                await RestoreSnapshotCoreAsync(token).ConfigureAwait(false);
                await _profiles.RemoveAsync(network.Key, adapter.AdapterId, token).ConfigureAwait(false);
                return null;
            }
        }

        await SaveSnapshotAsync(snapshot with { State = LatencyTransactionState.Verifying }, token).ConfigureAwait(false);

        var connectivity = await _probe
            .CheckConnectivityAsync(network, baseline.RemoteEndpoint, token)
            .ConfigureAwait(false);

        var confirmation = connectivity.IsUsable
            ? await _probe.MeasureAsync(network, benchmark, token).ConfigureAwait(false)
            : null;

        if (confirmation is null || !LatencyComparison.ConfirmsImprovement(baseline, confirmation))
        {
            _log?.Invoke("latency.profile: kayıtlı ayarlar bu oturumda doğrulanamadı; geri alınıp baştan ölçülecek.");
            await RestoreSnapshotCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await _profiles.RemoveAsync(network.Key, adapter.AdapterId, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        await SaveSnapshotAsync(
            snapshot with { State = LatencyTransactionState.Committed, PendingProperty = null },
            token).ConfigureAwait(false);

        _lastNetworkKey = network.Key;
        var applied = candidates.Select(candidate => candidate.Description).ToArray();
        _log?.Invoke($"latency.profile.replayed: {applied.Length} ayar yeniden uygulandı ve doğrulandı.");

        return Publish(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Active,
            StatusLine = LatencyReport.Replayed(adapter.AdapterName, profile, confirmation, applied, path),
            AdapterName = adapter.AdapterName,
            NetworkKey = network.Key,
            Before = baseline,
            After = confirmation,
            AppliedChanges = applied,
            Path = path,
        });
    }

    private async Task SaveProfileAsync(
        NetworkFingerprint network,
        AdapterLatencyCapability adapter,
        LatencyMeasurement baseline,
        LatencyMeasurement? optimized,
        IReadOnlyList<LatencyVerdict> verdicts,
        LatencyPathAnalysis path,
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

    private static bool IsTerminalRestore(LatencyRestoreOutcome outcome) => outcome is
        LatencyRestoreOutcome.Restored
        or LatencyRestoreOutcome.AlreadyOriginal
        or LatencyRestoreOutcome.MissingProperty;

    private static LatencyOptimizationResult NoGain(
        AdapterLatencyCapability adapter,
        NetworkFingerprint network,
        LatencyMeasurement before,
        LatencyMeasurement? after,
        LatencyPathAnalysis path,
        IReadOnlyList<LatencyVerdict> verdicts,
        string headline) => new()
        {
            Status = LatencyOptimizationStatus.NoGain,
            StatusLine = LatencyReport.NoGain(headline, after ?? before, verdicts, path),
            AdapterName = adapter.AdapterName,
            NetworkKey = network.Key,
            Before = before,
            After = after,
            Path = path,
            Verdicts = verdicts,
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
