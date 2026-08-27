using DpiBypass.Core.Logging;

namespace DpiBypass.Core.Network;

/// <summary>
/// Measures, applies and independently validates one safe NIC change at a time.
/// </summary>
public sealed class LatencyOptimizer : IAsyncDisposable
{
    private readonly ILatencyAdapterController _controller;
    private readonly ILatencyProbe _probe;
    private readonly ILatencySnapshotStore _snapshots;
    private readonly Func<NetworkMonitor> _monitorFactory;
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
        Action<string>? log = null)
    {
        _log = log ?? AppLog.InfoSink;
        _controller = controller ?? new WindowsLatencyAdapterController(_log);
        _probe = probe ?? new LatencyProbe();
        _snapshots = snapshots ?? new LatencySnapshotStore();
        _monitorFactory = monitorFactory ?? (() => new NetworkMonitor(log: _log));
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

    /// <summary>Starts the independent network-change lifecycle and performs the first measurement.</summary>
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

        var measurement = await _probe.MeasureAsync(network, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new LatencyOptimizationResult
        {
            Status = measurement.HasRemoteConnectivity
                ? LatencyOptimizationStatus.Disabled
                : LatencyOptimizationStatus.Offline,
            StatusLine = FormatMeasurement(network, measurement),
            AdapterName = network.AdapterName ?? network.DisplayName,
            NetworkKey = network.Key,
            After = measurement,
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

        LatencyOptimizationSnapshot? workingSnapshot = null;

        try
        {
            if (!_enabled)
            {
                return Current;
            }

            // A crash or an earlier network must be returned to its exact baseline
            // before a fresh baseline is measured. Never overwrite an unrestorable
            // snapshot with values from another adapter.
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

            var candidates = adapter.BuildSafeCandidates();
            var baseline = await _probe.MeasureAsync(network, cancellationToken: token).ConfigureAwait(false);
            if (!baseline.HasRemoteConnectivity)
            {
                return Publish(new LatencyOptimizationResult
                {
                    Status = LatencyOptimizationStatus.Offline,
                    StatusLine = "Uzak IP gecikmesi ölçülemedi; hiçbir NIC ayarı değiştirilmedi.",
                    AdapterName = adapter.AdapterName,
                    NetworkKey = network.Key,
                    Before = baseline,
                });
            }

            if (candidates.Count == 0)
            {
                return Publish(new LatencyOptimizationResult
                {
                    Status = LatencyOptimizationStatus.NoGain,
                    StatusLine = $"Etkin · {adapter.AdapterName} zaten güvenli düşük-gecikme ayarlarında; değişiklik yapılmadı.\n{FormatCompact(baseline)}",
                    AdapterName = adapter.AdapterName,
                    NetworkKey = network.Key,
                    Before = baseline,
                    After = baseline,
                });
            }

            workingSnapshot = new LatencyOptimizationSnapshot
            {
                AdapterId = adapter.AdapterId,
                AdapterName = adapter.AdapterName,
                NetworkKey = network.Key,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var accepted = new List<string>();
            var currentMeasurement = baseline;

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
                    After = currentMeasurement,
                    AppliedChanges = [.. accepted],
                });

                var captured = candidate.ToSnapshot(adapter);
                workingSnapshot.Settings.Add(captured);
                await _snapshots.SaveAsync(workingSnapshot, token).ConfigureAwait(false);

                var applied = await _controller.ApplyAsync(adapter, candidate, token).ConfigureAwait(false);
                if (!applied.Applied)
                {
                    _log?.Invoke($"NIC ayarı atlandı ({candidate.PropertyName}): {applied.Reason ?? "canlı uygulanamadı"}");
                    await RestoreCandidateAndTrimAsync(workingSnapshot, captured, token).ConfigureAwait(false);
                    continue;
                }

                var connectivity = await _probe.CheckConnectivityAsync(network, baseline.RemoteEndpoint, token).ConfigureAwait(false);
                if (!connectivity.IsUsable)
                {
                    _log?.Invoke("NIC değişikliğinden sonra ağ geçidi ve uzak uç yanıt vermedi; tüm ayarlar geri alınıyor.");
                    await RestoreSnapshotCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    workingSnapshot = null;
                    return Publish(NoGain(adapter, network, baseline, null,
                        "Bağlantı denetimi başarısız oldu; özgün NIC ayarları geri yüklendi."));
                }

                var measured = await _probe.MeasureAsync(network, baseline.RemoteEndpoint, token).ConfigureAwait(false);
                if (HasVerifiedImprovement(currentMeasurement, measured))
                {
                    accepted.Add(candidate.Description);
                    currentMeasurement = measured;
                    _log?.Invoke($"NIC ayarı ölçümle doğrulandı: {candidate.Description}");
                    continue;
                }

                _log?.Invoke($"NIC ayarı kazanç sağlamadı ve geri alındı: {candidate.Description}");
                await RestoreCandidateAndTrimAsync(workingSnapshot, captured, token).ConfigureAwait(false);
            }

            if (accepted.Count == 0)
            {
                await _snapshots.ClearAsync(token).ConfigureAwait(false);
                workingSnapshot = null;
                return Publish(NoGain(adapter, network, baseline, currentMeasurement,
                    "Bu ağda doğrulanmış bir kazanç bulunamadı; değişiklikler geri alındı."));
            }

            var final = await _probe.MeasureAsync(network, baseline.RemoteEndpoint, token).ConfigureAwait(false);
            if (!HasVerifiedImprovement(baseline, final))
            {
                await RestoreSnapshotCoreAsync(CancellationToken.None).ConfigureAwait(false);
                workingSnapshot = null;
                return Publish(NoGain(adapter, network, baseline, final,
                    "Kazanç son doğrulamada tekrarlanmadı; özgün NIC ayarları geri yüklendi."));
            }

            _lastNetworkKey = network.Key;
            workingSnapshot = null; // Intentionally retained on disk until off/shutdown.

            return Publish(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.Active,
                StatusLine = FormatVerifiedResult(adapter.AdapterName, baseline, final, accepted),
                AdapterName = adapter.AdapterName,
                NetworkKey = network.Key,
                Before = baseline,
                After = final,
                AppliedChanges = [.. accepted],
            });
        }
        catch (OperationCanceledException)
        {
            // Cancellation can arrive between an apply and its measurement. Restore
            // without the cancelled token before allowing the next network through.
            var restored = await TryRestoreAfterFailureAsync().ConfigureAwait(false);
            workingSnapshot = null;

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
            _log?.Invoke($"Düşük gecikme optimizasyonu başarısız: {ex.Message}");
            var restored = await TryRestoreAfterFailureAsync().ConfigureAwait(false);
            workingSnapshot = null;

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

    private async Task RestoreCandidateAndTrimAsync(
        LatencyOptimizationSnapshot snapshot,
        LatencySettingSnapshot setting,
        CancellationToken cancellationToken)
    {
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
            await _snapshots.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

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
                _log?.Invoke($"'{setting.PropertyName}' geri yükleme hatası: {ex.Message}");
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

        await _snapshots.SaveAsync(snapshot with { Settings = unresolved }, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> TryRestoreAfterFailureAsync()
    {
        try
        {
            return await RestoreSnapshotCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Hata sonrası NIC geri yükleme tamamlanamadı: {ex.Message}");
            return false;
        }
    }

    internal static bool HasVerifiedImprovement(LatencyMeasurement before, LatencyMeasurement after)
    {
        if (!before.HasRemoteConnectivity || !after.HasRemoteConnectivity)
        {
            return false;
        }

        if (after.PacketLossPercent > before.PacketLossPercent + 0.01)
        {
            return false;
        }

        var medianRegressionLimit = Math.Max(1.5, before.MedianRttMs * 0.08);
        if (after.MedianRttMs > before.MedianRttMs + medianRegressionLimit)
        {
            return false;
        }

        var p95RegressionLimit = Math.Max(2.0, before.P95RttMs * 0.10);
        if (after.P95RttMs > before.P95RttMs + p95RegressionLimit)
        {
            return false;
        }

        var medianGain = before.MedianRttMs - after.MedianRttMs;
        var jitterGain = before.JitterMs - after.JitterMs;
        var p95Gain = before.P95RttMs - after.P95RttMs;

        var meaningfulMedian = medianGain >= Math.Max(1.0, before.MedianRttMs * 0.05)
            && after.JitterMs <= before.JitterMs + Math.Max(0.5, before.JitterMs * 0.10);
        var meaningfulJitter = jitterGain >= Math.Max(0.75, before.JitterMs * 0.15)
            && after.MedianRttMs <= before.MedianRttMs + 0.5;
        var meaningfulTail = p95Gain >= Math.Max(1.5, before.P95RttMs * 0.05)
            && after.MedianRttMs <= before.MedianRttMs + 0.5;

        return meaningfulMedian || meaningfulJitter || meaningfulTail;
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
        string line) => new()
    {
        Status = LatencyOptimizationStatus.NoGain,
        StatusLine = line,
        AdapterName = adapter.AdapterName,
        NetworkKey = network.Key,
        Before = before,
        After = after,
    };

    private static string FormatVerifiedResult(
        string adapterName,
        LatencyMeasurement before,
        LatencyMeasurement after,
        IReadOnlyList<string> applied) =>
        $"Etkin · {adapterName}\n"
        + $"Median RTT {before.MedianRttMs:F1} → {after.MedianRttMs:F1} ms · "
        + $"jitter {before.JitterMs:F1} → {after.JitterMs:F1} ms · "
        + $"kayıp %{before.PacketLossPercent:F0} → %{after.PacketLossPercent:F0}\n"
        + $"Uygulanan: {string.Join(" · ", applied)}";

    private static string FormatCompact(LatencyMeasurement measurement) =>
        $"Median {measurement.MedianRttMs:F1} ms · p95 {measurement.P95RttMs:F1} ms · "
        + $"jitter {measurement.JitterMs:F1} ms · kayıp %{measurement.PacketLossPercent:F0}";

    public static string FormatMeasurement(NetworkFingerprint network, LatencyMeasurement measurement)
    {
        var gateway = measurement.GatewayMedianRttMs is { } gatewayMs ? $"{gatewayMs:F1} ms median" : "yanıt yok";
        var internet = measurement.HasRemoteConnectivity ? $"{measurement.MedianRttMs:F1} ms median" : "yanıt yok";

        return $"Ağ            : {network.DisplayName}\n"
            + $"Bağdaştırıcı   : {network.AdapterName ?? "-"}\n"
            + $"Gateway       : {gateway}\n"
            + $"Internet      : {internet} ({measurement.Protocol}, {measurement.RemoteEndpoint})\n"
            + $"p95           : {measurement.P95RttMs:F1} ms\n"
            + $"Jitter        : {measurement.JitterMs:F1} ms\n"
            + $"Kayıp         : %{measurement.PacketLossPercent:F0}";
    }

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

        CancelActiveOperation();
        _ = Task.Run(async () =>
        {
            try
            {
                await RunForNetworkAsync(network, force: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Ağ değişikliği sonrası düşük gecikme ölçümü başarısız: {ex.Message}");
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
        finally
        {
            _disposed = true;
            _lifetime?.Dispose();
            _operationCancellation?.Dispose();
            _operationGate.Dispose();
        }
    }
}
