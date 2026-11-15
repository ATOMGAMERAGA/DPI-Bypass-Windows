using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DpiBypass.Core.Config;
using DpiBypass.Core.Engine;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;
using DpiBypass.Core.Network.Latency;
using DpiBypass.Core.SocialAccess;

namespace DpiBypass.Core.Ipc;

/// <summary>User-process facade. It never constructs the driver-owning service.</summary>
public sealed partial class RemoteProtectionClient
{
    private readonly Func<CancellationToken, Task<ProtectionRpcClient>> _connect;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _snapshotGate = new();
    private ProtectionRpcClient? _rpc;
    private Task? _updates;
    private ProtectionSnapshot _snapshot;
    private AppSettings _lastSettings;
    private long _eventSequence;
    private bool _disposed;
    private volatile bool _connectionLost;
    /// <summary>Consecutive status polls that may fail before the helper is declared gone.</summary>
    private const int SnapshotFailureLimit = 3;
    private const string EngineUnavailable = "Ayrıcalıklı motor bağlantısı kesildi; yeniden başlatın.";
    private readonly bool _isolatedUiTest;
    private Task _saving = Task.CompletedTask;
    private WindowsSocialRuntime? _social;
    public AppSettings Settings { get; }

    public RemoteProtectionClient(Func<CancellationToken, Task<ProtectionRpcClient>> connect, AppSettings? settings = null, bool isolatedUiTest = false)
    {
        _connect = connect;
        _isolatedUiTest = isolatedUiTest;
        Settings = settings ?? new ConfigStore().Load();
        _lastSettings = Clone(Settings);
        var latency = new LatencyOptimizationResult { Status = LatencyOptimizationStatus.Disabled, StatusLine = "Ölçülmedi" };
        _snapshot = new()
        {
            Settings = Settings, Network = new(), Isp = IspCatalog.Unknown, Strategy = StrategyLibrary.Default,
            LearnedDomains = [], LatencyResult = latency, LatencyStatus = LatencyStatusView.From(false, latency),
            ProtectedHosts = BlockedSiteCatalog.All.Concat(Settings.ExtraDomains).Except(Settings.ExcludedDomains, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            LunarAdBlock = new(Settings.BlockLunarAds, false, false, 0, "Motor henüz bağlanmadı"),
            LatencyStage = LoadedLaneProgress.Off, HotspotView = HotspotStatusView.Empty,
            HotspotStatus = new(false, false, false, 0, "Bilinmeyen ağ", "-", null, null),
            ThroughputCostBytes = Diagnostics.StrategyTuner.DefaultThroughputCostBytes,
        };
    }

    public WindowsSocialRuntime SocialExperiments => _social ??= new WindowsSocialRuntime(new RemoteStrategies(this),
        Settings.SocialAccess, () => State is ProtectionState.Running or ProtectionState.Degraded && !IsFilterDown,
        host => CanProtectHost(host, "chrome.exe"), host => CanProtectHost(host, "DpiBypass.exe"));

    private async Task<ProtectionRpcClient> ConnectionAsync(CancellationToken token)
    {
        // A helper that is known to be gone fails at once. Trying its pipe again cost every
        // later click a five second connect timeout on the UI thread - a frozen window
        // after the helper had already said goodbye - and a silent new UAC launch is
        // exactly what the update loop below promises never to do.
        if (_connectionLost) throw new InvalidOperationException(EngineUnavailable);
        if (_rpc is not null) return _rpc;
        if (_disposed || _isolatedUiTest) throw new ObjectDisposedException(nameof(RemoteProtectionClient));
        await _connectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_rpc is not null) return _rpc;
            _rpc = await _connect(token).ConfigureAwait(false);
            _updates = Task.Run(UpdateLoopAsync);
            return _rpc;
        }
        finally { _connectionGate.Release(); }
    }

    private async Task UpdateLoopAsync()
    {
        var failures = 0;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                var response = await _rpc!.SendAsync(Request("__snapshot", []), _stopping.Token).ConfigureAwait(false);
                Accept(response);
                failures = 0;
                await Task.Delay(1000, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
            catch (Exception) when (++failures < SnapshotFailureLimit)
            {
                // One poll that collided with a busy pipe or a reply that could not be
                // written is not a dead helper. Declaring it lost on the first miss told
                // the user to restart an engine that was still running fine.
                try { await Task.Delay(500 * failures, _stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            catch (Exception)
            {
                // A missing helper is never replaced with a local engine or a silent new UAC launch.
                _connectionLost = true;
                try
                {
                    if (_social is not null) await _social.Coordinator.StopAsync("ENGINE_UNAVAILABLE").ConfigureAwait(false);
                }
                catch (Exception) { /* The engine is gone either way; the state below must still be published. */ }
                lock (_snapshotGate)
                    _snapshot = _snapshot with { State = ProtectionState.Stopped, StatusDetail = EngineUnavailable };
                Changed?.Invoke();
                return;
            }
        }
    }

    private ProtectionRequest Request(string method, object?[] arguments) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Method = method, AfterEvent = Interlocked.Read(ref _eventSequence),
        Arguments = arguments.Select(a => JsonSerializer.SerializeToElement(a, a?.GetType() ?? typeof(object), ProtectionWireJson.Options)).ToArray(),
        Settings = method == nameof(SaveSettings) ? Clone(Settings) : null,
    };
    private async Task<ProtectionResponse> InvokeAsync(string method, object?[] arguments, CancellationToken token)
    {
        // A queued network-settings save may first roll back Alpha. Its rollback
        // RPC must not wait for the save task that is awaiting that same rollback.
        if (method is not (nameof(TryAcquireExperiment) or nameof(TryWriteExperiment)
            or nameof(TryReleaseExperiment) or nameof(CanProtectHost)))
            await _saving.WaitAsync(token).ConfigureAwait(false);
        if (_social is not null && (InvalidatesSocialContext(method)
            || method == nameof(SaveSettings) && (Settings.DnsMode != _lastSettings.DnsMode || Settings.Scope != _lastSettings.Scope)))
            await _social.Coordinator.StopAsync("CONTEXT_CHANGED").ConfigureAwait(false);
        var rpc = await ConnectionAsync(token).ConfigureAwait(false);
        var request = Request(method, arguments);
        using var registration = token.Register(() => _ = CancelRequestAsync(rpc, request.Id));
        var response = await rpc.SendAsync(request, token).ConfigureAwait(false);
        Accept(response);
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "HELPER_OPERATION_FAILED");
        return response;
    }
    private async Task CancelRequestAsync(ProtectionRpcClient rpc, string id)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await rpc.SendAsync(Request("__cancel", [id]), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }
    private static bool InvalidatesSocialContext(string method) => method is nameof(StopAsync) or nameof(ApplyScope)
        or nameof(ApplyManualStrategy) or nameof(ApplyManualIsp) or nameof(ApplyQuicSetting) or nameof(OnSystemResumed);

    /// <summary>
    /// Sends a fire-and-forget setting change in order, without making the caller wait.
    /// </summary>
    /// <remarks>
    /// These are called from property setters on the UI thread. Waiting there for the
    /// helper meant waiting for whatever was ahead of it - a queued settings save, a
    /// social experiment being rolled back, or the helper's own UAC prompt on the first
    /// connection - and every one of those froze the window until it finished. The change
    /// is mirrored onto <see cref="Settings"/> straight away so the control that made it
    /// reads back what the user chose, and it joins the same queue as settings saves so
    /// every later command still sees it applied first. A change the helper refuses is
    /// put back to the helper's value and reported, rather than left showing as applied.
    /// </remarks>
    private void Post(string method, object?[] arguments, Action<AppSettings>? mirror = null, params string[] touched)
    {
        lock (_snapshotGate)
        {
            mirror?.Invoke(Settings);
            if (_isolatedUiTest) return;
            var previous = _saving;
            _saving = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                catch (Exception) { /* Each queued item reports its own failure. */ }
                try
                {
                    if (_social is not null && InvalidatesSocialContext(method))
                        await _social.Coordinator.StopAsync("CONTEXT_CHANGED").ConfigureAwait(false);
                    var rpc = await ConnectionAsync(_stopping.Token).ConfigureAwait(false);
                    var response = await rpc.SendAsync(Request(method, arguments), _stopping.Token).ConfigureAwait(false);
                    Accept(response);
                    if (!response.Ok) throw new InvalidOperationException(response.Error ?? "HELPER_OPERATION_FAILED");
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
                catch (Exception)
                {
                    lock (_snapshotGate)
                    {
                        foreach (var property in SettingProperties)
                        {
                            if (Array.IndexOf(touched, property.Name) < 0) continue;
                            var serverValue = JsonSerializer.SerializeToUtf8Bytes(
                                property.GetValue(_lastSettings), property.PropertyType, ProtectionWireJson.Options);
                            property.SetValue(Settings, JsonSerializer.Deserialize(serverValue, property.PropertyType, ProtectionWireJson.Options));
                        }
                        _snapshot = _snapshot with
                        {
                            StatusDetail = _connectionLost ? EngineUnavailable : "Ayar koruma motoruna iletilemedi; tekrar deneyin.",
                        };
                    }
                    Changed?.Invoke();
                }
            });
        }
    }

    /// <summary>
    /// Sends a cancellation straight to the helper, ahead of anything queued.
    /// </summary>
    /// <remarks>
    /// A stop button that first waited for the settings queue - or, worse, held the UI
    /// thread while it did - is a stop button that does not stop. There is nothing to
    /// cancel when no helper is connected, so that case is a no-op.
    /// </remarks>
    private void SendNow(string method)
    {
        if (_isolatedUiTest || _connectionLost || _rpc is not { } rpc) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var response = await rpc.SendAsync(Request(method, []), _stopping.Token).ConfigureAwait(false);
                Accept(response);
            }
            catch (Exception) { /* The next status poll reports whatever the helper is doing. */ }
        });
    }

    private async Task CallAsync(string method, object?[] arguments, CancellationToken token)
        => await InvokeAsync(method, arguments, token).ConfigureAwait(false);
    private async Task<T> CallAsync<T>(string method, object?[] arguments, CancellationToken token)
        => (await InvokeAsync(method, arguments, token).ConfigureAwait(false)).Result.Deserialize<T>(ProtectionWireJson.Options)!;

    private void SaveSettingsWithoutBlockingWindow()
    {
        if (_isolatedUiTest) return;
        // ContentRendered records the first successful window using this void API.
        // Waiting for UAC there would freeze that very window before its next frame.
        var pending = Clone(Settings);
        var changesNetwork = pending.DnsMode != _lastSettings.DnsMode || pending.Scope != _lastSettings.Scope;
        lock (_snapshotGate)
        {
            var previous = _saving;
            _saving = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    if (changesNetwork && _social is not null) await _social.Coordinator.StopAsync("CONTEXT_CHANGED").ConfigureAwait(false);
                    var rpc = await ConnectionAsync(_stopping.Token).ConfigureAwait(false);
                    var request = Request(nameof(SaveSettings), []) with { Settings = pending };
                    var response = await rpc.SendAsync(request, _stopping.Token).ConfigureAwait(false);
                    Accept(response);
                    if (!response.Ok) throw new InvalidOperationException("SETTINGS_SAVE_FAILED");
                }
                catch (Exception)
                {
                    var failure = new ConfigSaveResult(ConfigSaveFailure.AccessDenied, "HELPER_UNAVAILABLE");
                    lock (_snapshotGate) _snapshot = _snapshot with { LastSaveFailure = failure, StatusDetail = "Ayarlar yardımcı sürece kaydedilemedi; tekrar deneyin." };
                    SaveStatusChanged?.Invoke(failure);
                    Changed?.Invoke();
                }
            });
        }
    }

    private static readonly PropertyInfo[] SettingProperties = typeof(AppSettings).GetProperties()
        .Where(p => p.CanRead && p.SetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0
            && p.GetCustomAttribute<JsonIgnoreAttribute>() is null).ToArray();
    private static AppSettings Clone(AppSettings value) => JsonSerializer.Deserialize<AppSettings>(
        JsonSerializer.SerializeToUtf8Bytes(value, ProtectionWireJson.Options), ProtectionWireJson.Options)!;
    private void Accept(ProtectionResponse response)
    {
        List<ProtectionEvent> events;
        bool networkChanged = false;
        lock (_snapshotGate)
        {
            if (response.Snapshot is { } snapshot && snapshot.Sequence > _snapshot.Sequence)
            {
                networkChanged = _snapshot.Network.Key != snapshot.Network.Key;
                // Preserve unsaved GUI edits while importing independently changed helper settings.
                foreach (var property in SettingProperties)
                {
                    if (property.Name == nameof(AppSettings.SocialAccess)) continue; // active one-session consent is GUI-owned
                    var current = JsonSerializer.Serialize(property.GetValue(Settings), property.PropertyType, ProtectionWireJson.Options);
                    var previous = JsonSerializer.Serialize(property.GetValue(_lastSettings), property.PropertyType, ProtectionWireJson.Options);
                    if (current == previous) property.SetValue(Settings, property.GetValue(snapshot.Settings));
                }
                _lastSettings = Clone(snapshot.Settings);
                _snapshot = snapshot;
            }
            events = response.Events.Where(e => e.Sequence > _eventSequence).OrderBy(e => e.Sequence).ToList();
            if (events.Count > 0) Interlocked.Exchange(ref _eventSequence, events[^1].Sequence);
        }
        if (networkChanged && _social is not null) _ = _social.Coordinator.StopAsync("NETWORK_CHANGED");
        foreach (var item in events) Publish(item);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_social is not null) try { await _social.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        try { await _saving.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        try
        {
            if (_rpc is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _rpc.SendAsync(Request(nameof(StopAsync), []), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) { /* The owning helper also disposes its service when this GUI exits. */ }
        if (_updates is not null) try { await _updates.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        _stopping.Dispose();
    }
    private sealed class RemoteStrategies(RemoteProtectionClient client) : IExperimentStrategyControl
    {
        public BypassStrategy CurrentStrategy => client.Strategy;
        public bool TryAcquire(string operationId, string expectedBeforeId) => client.TryAcquireExperiment(operationId, expectedBeforeId);
        public bool TryWrite(string operationId, string strategyId) => client.TryWriteExperiment(operationId, strategyId);
        public bool TryRelease(string operationId, string beforeId, string installedId) => client.TryReleaseExperiment(operationId, beforeId, installedId);
    }
}
