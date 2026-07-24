using AtomDpi.Core.Config;
using AtomDpi.Core.Diagnostics;
using AtomDpi.Core.Dns;
using AtomDpi.Core.Engine;
using AtomDpi.Core.Interop;
using AtomDpi.Core.Logging;
using AtomDpi.Core.Network;

namespace AtomDpi.Core;

public enum ProtectionState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,

    /// <summary>Running, but discord.com still does not answer.</summary>
    Degraded = 3,
    Stopping = 4,
}

/// <summary>
/// The one object the UI talks to. Owns the engine, the encrypted resolver, the
/// network watcher and the auto-tuner, and keeps them consistent with each other.
/// </summary>
public sealed class ProtectionService : IAsyncDisposable
{
    private readonly ConfigStore _store;
    private readonly TargetMatcher _matcher = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DohResolver? _resolver;
    private DnsProxyServer? _dnsProxy;
    private DnsConfigurator? _dnsConfigurator;
    private ProcessPortMap? _portMap;
    private BypassEngine? _engine;
    private NetworkMonitor? _monitor;
    private ConnectivityTester? _tester;
    private StrategyTuner? _tuner;
    private CancellationTokenSource? _lifetime;
    private Task? _networkWork;

    public ProtectionService(ConfigStore? store = null)
    {
        _store = store ?? new ConfigStore();
        Settings = _store.Load();
        ApplySettingsToMatcher();
    }

    public AppSettings Settings { get; }

    public ProtectionState State { get; private set; } = ProtectionState.Stopped;

    public EngineStatistics? Stats => _engine?.Stats;

    public NetworkFingerprint Network { get; private set; } = new();

    public IspProfile Isp { get; private set; } = IspCatalog.Unknown;

    public IspDetection? Detection { get; private set; }

    public BypassStrategy Strategy => _engine?.Strategy ?? StrategyLibrary.Default;

    public DnsMode ActiveDnsMode => _dnsConfigurator?.CurrentMode ?? DnsMode.SystemDefault;

    public string? DnsProviderInUse => _resolver?.ActiveProvider;

    public long DnsQueriesServed => _dnsProxy?.QueriesServed ?? 0;

    public long DnsCacheHits => _dnsProxy?.CacheHits ?? 0;

    /// <summary>Last verification result against discord.com.</summary>
    public ProbeResult? LastProbe { get; private set; }

    public string? StatusDetail { get; private set; }

    public event Action? Changed;

    public event Action<string, string>? HostRewritten;

    public event Action<string, int, int>? TuningProgress;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is ProtectionState.Running or ProtectionState.Starting)
            {
                return;
            }

            if (!Elevation.IsElevated)
            {
                throw new InvalidOperationException(
                    "Atom DPI Bypass yönetici hakları olmadan çalışamaz. Uygulamayı yönetici olarak başlatın.");
            }

            SetState(ProtectionState.Starting, "Başlatılıyor…");
            _lifetime = new CancellationTokenSource();

            _resolver = new DohResolver();
            _dnsConfigurator = new DnsConfigurator(AppPaths.StateDirectory, AppLog.InfoSink);
            _tester = new ConnectivityTester(_resolver);

            await ConfigureDnsAsync(_lifetime.Token).ConfigureAwait(false);

            _portMap = new ProcessPortMap(AppLog.InfoSink);
            if (!_portMap.TryStart())
            {
                // Without process attribution "Discord only" cannot be honoured, so widen
                // to hostname matching rather than silently protecting nothing.
                AppLog.Warning("Süreç eşlemesi başlatılamadı; koruma yalnızca alan adına göre uygulanacak.");
            }

            _engine = new BypassEngine(_matcher, _portMap, AppLog.InfoSink)
            {
                BlockQuicHandshakes = Settings.BlockQuicHandshakes,
                Strategy = ResolveInitialStrategy(),
            };
            _engine.HostRewritten += OnHostRewritten;
            _engine.Start();

            _tuner = new StrategyTuner(_engine, _tester, AppLog.InfoSink);
            _tuner.Progress += (name, index, total) => TuningProgress?.Invoke(name, index, total);

            _monitor = new NetworkMonitor(log: AppLog.InfoSink);
            _monitor.Changed += OnNetworkChanged;
            _monitor.Start();
            Network = _monitor.Current;

            SetState(ProtectionState.Running, "Koruma etkin");
        }
        finally
        {
            _gate.Release();
        }

        // Detection and tuning are slow; do them after the UI is already responsive.
        _networkWork = Task.Run(() => InitialiseNetworkAsync(_lifetime!.Token));
    }

    private async Task ConfigureDnsAsync(CancellationToken cancellationToken)
    {
        if (Settings.DnsMode == DnsMode.SystemDefault)
        {
            return;
        }

        var mode = Settings.DnsMode;
        var loopbackHasIPv6 = false;

        if (mode == DnsMode.EncryptedLoopback)
        {
            _dnsProxy = new DnsProxyServer(_resolver!, AppLog.InfoSink);
            if (_dnsProxy.TryStart())
            {
                loopbackHasIPv6 = true;
            }
            else
            {
                await _dnsProxy.DisposeAsync().ConfigureAwait(false);
                _dnsProxy = null;
                mode = DnsMode.PublicResolvers;
                AppLog.Warning("53 numaralı port kullanımda; şifreli DNS yerine genel çözümleyicilere geçiliyor.");
            }
        }

        await _dnsConfigurator!.ApplyAsync(mode, loopbackHasIPv6, cancellationToken).ConfigureAwait(false);
    }

    private async Task InitialiseNetworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            Network = NetworkFingerprint.Capture();
            await DetectAndTuneAsync(reason: "ilk başlatma", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            AppLog.Error("Ağ hazırlığı başarısız", ex);
        }
    }

    /// <summary>
    /// Detects the operator, reuses a cached recipe for this network if there is one,
    /// verifies it for real, and only runs the full sweep when verification fails.
    /// </summary>
    private async Task DetectAndTuneAsync(string reason, CancellationToken cancellationToken)
    {
        if (_engine is null || _tuner is null || _resolver is null)
        {
            return;
        }

        SetState(State, $"Ağ inceleniyor ({reason})…");

        if (Settings.ManualIspProfileId is { Length: > 0 } manualIsp)
        {
            Isp = IspCatalog.ById(manualIsp);
            Detection = new IspDetection(Isp, null, null, null, null, WasAutomatic: false);
        }
        else
        {
            Detection = await new IspDetector(_resolver, AppLog.InfoSink)
                .DetectAsync(Network, cancellationToken)
                .ConfigureAwait(false);
            Isp = Detection.Profile;
        }

        Changed?.Invoke();

        if (Settings.ManualStrategyId is { Length: > 0 } manualStrategy)
        {
            var forced = StrategyLibrary.Find(manualStrategy);
            if (forced is not null)
            {
                _engine.Strategy = forced;
                await VerifyAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (Settings.Networks.TryGetValue(Network.Key, out var cached)
            && StrategyLibrary.Find(cached.StrategyId) is { } remembered)
        {
            _engine.Strategy = remembered;
            SetState(State, $"Kayıtlı yöntem deneniyor: {remembered.Name}");

            if (await _tuner.VerifyCurrentAsync(cancellationToken).ConfigureAwait(false))
            {
                RecordNetworkResult(remembered, success: true, wasUnfiltered: cached.WasUnfiltered);
                SetState(ProtectionState.Running, $"Koruma etkin · {remembered.Name}");
                await VerifyAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            AppLog.Info("Kayıtlı yöntem artık işe yaramıyor; yeni yöntem aranıyor.");
            RecordNetworkResult(remembered, success: false, wasUnfiltered: false);
        }

        if (!Settings.AutoTuneOnNetworkChange && Settings.Networks.ContainsKey(Network.Key))
        {
            return;
        }

        var result = await _tuner.FindBestAsync(Isp, checkUnfilteredFirst: true, cancellationToken).ConfigureAwait(false);

        if (result.Winner is not null)
        {
            _engine.Strategy = result.Winner;
            RecordNetworkResult(result.Winner, success: true, result.NetworkWasAlreadyOpen);
            SetState(
                ProtectionState.Running,
                result.NetworkWasAlreadyOpen
                    ? "Bu ağda engel yok · koruma bekleme modunda"
                    : $"Koruma etkin · {result.Winner.Name}");
        }
        else
        {
            SetState(ProtectionState.Degraded, "Çalışan bir yöntem bulunamadı");
        }

        await VerifyAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnNetworkChanged(NetworkFingerprint fingerprint)
    {
        Network = fingerprint;
        Changed?.Invoke();

        var token = _lifetime?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested)
        {
            return;
        }

        // Re-tuning happens off the event thread so the OS notification returns at once.
        _networkWork = Task.Run(async () =>
        {
            try
            {
                await DetectAndTuneAsync($"'{fingerprint.DisplayName}' ağına geçildi", token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Error("Ağ değişikliği sonrası ayarlama başarısız", ex);
            }
        }, token);
    }

    private void OnHostRewritten(string host, string strategyId) => HostRewritten?.Invoke(host, strategyId);

    /// <summary>Runs the full discord.com check and updates the visible status.</summary>
    public async Task<ProbeResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        if (_tester is null)
        {
            var offline = new ProbeResult(ProbeOutcome.DnsFailed, TimeSpan.Zero, "servis çalışmıyor");
            LastProbe = offline;
            return offline;
        }

        var result = await _tester.ProbeAsync(ConnectivityTester.PrimaryHost, fetchHttp: true, cancellationToken)
            .ConfigureAwait(false);

        LastProbe = result;

        if (State is ProtectionState.Running or ProtectionState.Degraded)
        {
            SetState(
                result.Success ? ProtectionState.Running : ProtectionState.Degraded,
                result.Success
                    ? $"discord.com erişilebilir · {result.Elapsed.TotalMilliseconds:F0} ms"
                    : $"discord.com erişilemiyor · {DescribeOutcome(result.Outcome)}");
        }

        return result;
    }

    public async Task<IReadOnlyList<(string Host, ProbeResult Result)>> VerifyAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<(string, ProbeResult)>();
        if (_tester is null)
        {
            return results;
        }

        foreach (var host in ConnectivityTester.DiscordEndpoints)
        {
            var probe = await _tester.ProbeAsync(host, fetchHttp: false, cancellationToken).ConfigureAwait(false);
            results.Add((host, probe));
        }

        return results;
    }

    /// <summary>Forces a fresh sweep, ignoring anything cached for this network.</summary>
    public async Task<TuningResult?> RetuneAsync(CancellationToken cancellationToken = default)
    {
        if (_engine is null || _tuner is null)
        {
            return null;
        }

        Settings.Networks.Remove(Network.Key);
        var result = await _tuner.FindBestAsync(Isp, checkUnfilteredFirst: true, cancellationToken).ConfigureAwait(false);

        if (result.Winner is not null)
        {
            _engine.Strategy = result.Winner;
            RecordNetworkResult(result.Winner, success: true, result.NetworkWasAlreadyOpen);
            SetState(ProtectionState.Running, $"Koruma etkin · {result.Winner.Name}");
        }
        else
        {
            SetState(ProtectionState.Degraded, "Çalışan bir yöntem bulunamadı");
        }

        await VerifyAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public void ApplyScope(ProtectionScope scope)
    {
        Settings.Scope = scope;
        _matcher.Scope = scope;
        _store.Save(Settings);
        SetState(State, StatusDetail);
        AppLog.Info($"Koruma kapsamı: {DescribeScope(scope)}");
    }

    public void ApplyManualStrategy(string? strategyId)
    {
        Settings.ManualStrategyId = strategyId;
        _store.Save(Settings);

        if (_engine is not null && StrategyLibrary.Find(strategyId) is { } strategy)
        {
            _engine.Strategy = strategy;
            SetState(State, $"Koruma etkin · {strategy.Name}");
        }
    }

    public void ApplyManualIsp(string? ispProfileId)
    {
        Settings.ManualIspProfileId = ispProfileId;
        _store.Save(Settings);
        Isp = ispProfileId is null ? Isp : IspCatalog.ById(ispProfileId);
        Changed?.Invoke();
    }

    public void ApplyQuicSetting(bool blockQuic)
    {
        Settings.BlockQuicHandshakes = blockQuic;
        _store.Save(Settings);

        if (_engine is not null)
        {
            _engine.BlockQuicHandshakes = blockQuic;
        }
    }

    public void SaveSettings()
    {
        ApplySettingsToMatcher();
        _store.Save(Settings);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ProtectionState.Stopped)
            {
                return;
            }

            SetState(ProtectionState.Stopping, "Durduruluyor…");

            if (_lifetime is not null)
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
            }

            if (_engine is not null)
            {
                _engine.HostRewritten -= OnHostRewritten;
                _engine.Dispose();
                _engine = null;
            }

            if (_monitor is not null)
            {
                _monitor.Changed -= OnNetworkChanged;
                _monitor.Dispose();
                _monitor = null;
            }

            _portMap?.Dispose();
            _portMap = null;

            // DNS must go back before the proxy dies, or the machine is left pointing
            // at a socket nobody is listening on.
            if (_dnsConfigurator is not null)
            {
                await _dnsConfigurator.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (_dnsProxy is not null)
            {
                await _dnsProxy.DisposeAsync().ConfigureAwait(false);
                _dnsProxy = null;
            }

            _resolver?.Dispose();
            _resolver = null;
            _tester = null;
            _tuner = null;

            _store.Save(Settings);
            SetState(ProtectionState.Stopped, "Koruma kapalı");
        }
        finally
        {
            _gate.Release();
        }
    }

    private BypassStrategy ResolveInitialStrategy()
    {
        if (StrategyLibrary.Find(Settings.ManualStrategyId) is { } manual)
        {
            return manual;
        }

        var key = NetworkFingerprint.Capture().Key;
        if (Settings.Networks.TryGetValue(key, out var cached) && StrategyLibrary.Find(cached.StrategyId) is { } remembered)
        {
            return remembered;
        }

        return StrategyLibrary.Default;
    }

    private void RecordNetworkResult(BypassStrategy strategy, bool success, bool wasUnfiltered)
    {
        var existing = Settings.Networks.GetValueOrDefault(Network.Key);

        Settings.Networks[Network.Key] = new NetworkProfile
        {
            Key = Network.Key,
            DisplayName = Network.DisplayName,
            IspProfileId = Isp.Id,
            StrategyId = strategy.Id,
            WasUnfiltered = wasUnfiltered,
            LastVerified = DateTimeOffset.UtcNow,
            SuccessCount = (existing?.SuccessCount ?? 0) + (success ? 1 : 0),
            FailureCount = (existing?.FailureCount ?? 0) + (success ? 0 : 1),
        };

        _store.SaveNetworks(Settings);
    }

    private void ApplySettingsToMatcher()
    {
        _matcher.Scope = Settings.Scope;

        _matcher.ExtraDomains.Clear();
        foreach (var domain in Settings.ExtraDomains)
        {
            _matcher.ExtraDomains.Add(domain);
        }

        _matcher.ExcludedDomains.Clear();
        foreach (var domain in Settings.ExcludedDomains)
        {
            _matcher.ExcludedDomains.Add(domain);
        }
    }

    private void SetState(ProtectionState state, string? detail)
    {
        State = state;
        StatusDetail = detail;
        Changed?.Invoke();
    }

    public static string DescribeScope(ProtectionScope scope) => scope switch
    {
        ProtectionScope.DiscordOnly => "Yalnızca Discord",
        ProtectionScope.DiscordAndBrowsers => "Discord + tarayıcılar",
        _ => "Tüm sistem",
    };

    public static string DescribeOutcome(ProbeOutcome outcome) => outcome switch
    {
        ProbeOutcome.Reachable => "erişilebilir",
        ProbeOutcome.DnsFailed => "alan adı çözülemedi",
        ProbeOutcome.ConnectRefused => "bağlantı reddedildi",
        ProbeOutcome.ConnectTimedOut => "bağlantı zaman aşımına uğradı",
        ProbeOutcome.HandshakeReset => "el sıkışma sıfırlandı (DPI)",
        ProbeOutcome.HandshakeTimedOut => "el sıkışma yanıtsız kaldı (DPI)",
        ProbeOutcome.CertificateRejected => "sertifika doğrulanamadı (araya girme)",
        _ => "HTTP yanıtı alınamadı",
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Kapatma sırasında hata", ex);
        }

        if (_networkWork is not null)
        {
            try
            {
                await _networkWork.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        _lifetime?.Dispose();
        _gate.Dispose();
    }
}
