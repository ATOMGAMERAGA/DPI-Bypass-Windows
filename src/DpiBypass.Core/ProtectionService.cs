using DpiBypass.Core.Config;
using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Logging;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;

namespace DpiBypass.Core;

public enum ProtectionState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,

    /// <summary>Running, but discord.com still does not answer.</summary>
    Degraded = 3,
    Stopping = 4,
}

/// <summary>What the mobile hotspot feature is doing right now.</summary>
public sealed record HotspotStatus(
    bool VodafoneModeEnabled,
    bool DiagnosticsEnabled,
    bool RegisteredHere,
    int RegisteredNetworks,
    string NetworkName,
    string AdapterName,
    DateTimeOffset? LegacyCleanedAt,
    HotspotDiagnosticResult? LastResult);

/// <summary>
/// The one object the UI talks to. Owns the engine, the encrypted resolver, the
/// network watcher and the auto-tuner, and keeps them consistent with each other.
/// </summary>
public sealed class ProtectionService : IAsyncDisposable
{
    private readonly ConfigStore _store;
    private readonly LearnedDomainStore _learned;
    private readonly TargetMatcher _matcher = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _hotspotDiagnosticsGate = new();
    private readonly IMobileHotspotDiagnostics _hotspot;
    private readonly LatencyOptimizer _latencyOptimizer;
    private readonly LoadedLatencyLane _loadedLatency;

    /// <summary>
    /// One latency operation at a time, across both lanes.
    /// </summary>
    /// <remarks>
    /// The optimizer guards its own runs, but the loaded lane is a separate object that
    /// measures the same link and can create a QoS policy while the optimizer is
    /// alternating adapter settings. Two of those at once would measure each other.
    /// </remarks>
    private readonly SemaphoreSlim _latencyGate = new(1, 1);

    /// <summary>
    /// The passive flow observer the deep test needs, started only while it is running.
    /// </summary>
    /// <remarks>
    /// Two things need it and neither can work without it: finding a UDP game's server,
    /// and proving a QoS policy attached to a genuinely new connection. It observes and
    /// nothing else - a SNIFF|RECV_ONLY handle on WinDivert's FLOW layer, which carries
    /// connection events rather than packets - and it is closed the moment the run ends
    /// so nothing is listening while the user is not asking for anything.
    /// </remarks>
    private readonly IProcessFlowObserver _flowObserver;

    private readonly Lock _latencyStageGate = new();

    private DohResolver? _resolver;
    private DnsProxyServer? _dnsProxy;
    private DnsConfigurator? _dnsConfigurator;
    private ProcessPortMap? _portMap;
    private BypassEngine? _engine;
    private NetworkMonitor? _monitor;
    private ConnectivityTester? _tester;
    private StrategyTuner? _tuner;
    private BlockedSiteDiscovery? _discovery;
    private CancellationTokenSource? _lifetime;
    private Task? _networkWork;
    private Task? _recheckWork;
    private CancellationTokenSource? _hotspotDiagnosticsCancellation;
    private CancellationTokenSource? _loadedLatencyCancellation;

    public ProtectionService(
        ConfigStore? store = null,
        LearnedDomainStore? learnedDomains = null,
        LatencyOptimizer? latencyOptimizer = null,
        IMobileHotspotDiagnostics? hotspotDiagnostics = null,
        LoadedLatencyLane? loadedLatency = null,
        IProcessFlowObserver? flowObserver = null)
    {
        _store = store ?? new ConfigStore();
        _learned = learnedDomains ?? new LearnedDomainStore();
        _flowObserver = flowObserver ?? new WinDivertFlowObserver(AppLog.InfoSink);

        // Both lanes resolve application targets, so both need the observer: without it
        // the idle lane would still be unable to find a UDP game's server, which is the
        // thing the observer exists for.
        _latencyOptimizer = latencyOptimizer ?? new LatencyOptimizer(
            log: AppLog.InfoSink,
            targets: new LatencyTargetResolver(log: AppLog.InfoSink, flows: _flowObserver));
        _latencyOptimizer.Changed += OnLatencyChanged;
        _loadedLatency = loadedLatency ?? new LoadedLatencyLane(
            log: AppLog.InfoSink,
            flows: _flowObserver,
            stages: new DelegateStageReporter(PublishLatencyStage));
        _hotspot = hotspotDiagnostics ?? new MobileHotspotDiagnostics(log: AppLog.InfoSink);

        // Load already disabled the obsolete TTL sub-feature and preserved any reusable
        // Vodafone-mode settings in memory. Persist the one-time migration immediately.
        Settings = _store.Load();
        if (Settings.LegacyHotspotCleaned)
        {
            _store.Save(Settings);
        }

        _latencyResult = _latencyOptimizer.Current;
        ApplyLatencyPreferences();

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

    /// <summary>
    /// The most recent latency result from either lane.
    /// </summary>
    /// <remarks>
    /// Two things produce one: the idle optimizer, which publishes as it works, and the
    /// loaded lane, which runs on demand. Whichever ran last is what the user is looking
    /// at, so both write here rather than the UI having to know which to read.
    /// </remarks>
    private LatencyOptimizationResult _latencyResult;

    public LatencyOptimizationResult LatencyResult => _latencyResult;

    /// <summary>The whole latency picture, for the UI and for the JSON status command.</summary>
    public LatencyStatusView LatencyStatus => LatencyStatusView.From(Settings.LowLatencyMode, _latencyResult);

    public bool IsLatencyBusy => _latencyOptimizer.IsBusy || _loadedLatencyBusy;

    private volatile bool _loadedLatencyBusy;

    private LoadedLaneProgress _latencyStage = LoadedLaneProgress.Off;

    /// <summary>
    /// Which stage of the deep test is running, and what it is waiting for.
    /// </summary>
    /// <remarks>
    /// The card binds to this rather than to a fixed instruction string. The run asks the
    /// user to start a transfer, stop it, and start a fresh one after the policy exists,
    /// and each of those is a state with its own name, live rate and remaining time.
    /// </remarks>
    public LoadedLaneProgress LatencyStage
    {
        get
        {
            lock (_latencyStageGate)
            {
                return _latencyStage;
            }
        }
    }

    /// <summary>Raised as the deep test moves between stages, off the UI thread.</summary>
    public event Action<LoadedLaneProgress>? LatencyStageChanged;

    /// <summary>Whether a deep test is running and can still be stopped.</summary>
    public bool CanCancelLatencyRun => _loadedLatencyCancellation is { IsCancellationRequested: false };

    /// <summary>Last verification result against discord.com.</summary>
    public ProbeResult? LastProbe { get; private set; }

    public string? StatusDetail { get; private set; }

    /// <summary>Domains the discovery pass has found to be filtered here.</summary>
    public IReadOnlyList<string> LearnedDomains => _learned.Domains;

    /// <summary>Everything currently protected by hostname: shipped, learned and manual.</summary>
    public IReadOnlyList<string> ProtectedDomains()
    {
        var excluded = new HashSet<string>(Settings.ExcludedDomains, StringComparer.OrdinalIgnoreCase);

        return
        [
            .. BlockedSiteCatalog.All
                .Concat(_learned.Domains)
                .Concat(Settings.ExtraDomains)
                .Where(d => !excluded.Contains(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal),
        ];
    }

    public int ProtectedDomainCount => ProtectedDomains().Count;

    /// <summary>Probes any host through whatever the engine is currently doing.</summary>
    public async Task<ProbeResult> ProbeAsync(string host, CancellationToken cancellationToken = default)
    {
        if (_tester is null)
        {
            return new ProbeResult(ProbeOutcome.DnsFailed, TimeSpan.Zero, "servis çalışmıyor");
        }

        return await _tester.ProbeAsync(host, fetchHttp: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Live state of the mobile hotspot compatibility feature.</summary>
    public HotspotStatus HotspotStatus => new(
        VodafoneModeEnabled: Settings.VodafoneModeEnabled,
        DiagnosticsEnabled: Settings.HotspotDiagnostics,
        RegisteredHere: Settings.VodafoneNetworkRegistered(Network.Key),
        RegisteredNetworks: Settings.VodafoneModeNetworks.Count,
        NetworkName: Network.DisplayName,
        AdapterName: Network.AdapterName ?? "-",
        LegacyCleanedAt: Settings.HotspotLegacyMigratedAt,
        LastResult: LastHotspotDiagnostics);

    /// <summary>The most recent diagnostics pass, if one has run this session.</summary>
    public HotspotDiagnosticResult? LastHotspotDiagnostics { get; private set; }

    public event Action? Changed;

    public event Action<string, string>? HostRewritten;

    public event Action<string, int, int>? TuningProgress;

    /// <summary>Raised when the discovery pass adds a domain.</summary>
    public event Action<string>? DomainLearned;

    /// <summary>
    /// Starts settings that belong to the application rather than the DPI engine.
    /// </summary>
    /// <remarks>
    /// Recovery runs first and runs unconditionally. A machine that lost power while a
    /// candidate was applied is carrying adapter values nothing ever verified, and
    /// whether the user has since switched the mode off has no bearing on that - gating
    /// the rollback on the setting is how such a machine keeps them forever.
    /// </remarks>
    public async Task StartIndependentFeaturesAsync(CancellationToken cancellationToken = default)
    {
        var recovered = await _latencyOptimizer.RecoverAsync(cancellationToken).ConfigureAwait(false);

        if (!Settings.LowLatencyMode)
        {
            // RecoverAsync deliberately leaves a committed snapshot alone because it
            // describes settings the mode chose. If the persisted mode is now off,
            // nothing owns those settings any more and startup must put them back.
            if (recovered)
            {
                await _latencyOptimizer.RestoreAsync(cancellationToken).ConfigureAwait(false);
            }

            // A QoS policy created by this app outlives the snapshot that recorded it if
            // the machine died between the two. With the mode off, nothing owns one.
            await _loadedLatency.ClearOwnedPoliciesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        ApplyLatencyPreferences();
        await _latencyOptimizer.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LatencyOptimizationResult> SetLowLatencyModeAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        Settings.LowLatencyMode = enabled;
        _store.Save(Settings);
        Changed?.Invoke();

        if (enabled)
        {
            ApplyLatencyPreferences();

            if (Settings.Latency.TargetKind == LatencyTargetKind.Application)
            {
                await StartFlowObserverAsync(cancellationToken).ConfigureAwait(false);
            }

            return PublishLatency(await _latencyOptimizer.StartAsync(cancellationToken).ConfigureAwait(false));
        }

        var stopped = await _latencyOptimizer.StopAndRestoreAsync(cancellationToken).ConfigureAwait(false);
        await _loadedLatency.ClearOwnedPoliciesAsync(cancellationToken).ConfigureAwait(false);
        await StopFlowObserverAsync().ConfigureAwait(false);
        return PublishLatency(stopped);
    }

    /// <summary>The quick test: idle latency to the chosen target, changing nothing.</summary>
    public async Task<LatencyOptimizationResult> TestLatencyAsync(CancellationToken cancellationToken = default)
    {
        await _latencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PublishLatency(Working(LatencyOptimizationStatus.QuickTesting, "Hızlı test çalışıyor…"));

            if (Settings.Latency.TargetKind == LatencyTargetKind.Application)
            {
                await StartFlowObserverAsync(cancellationToken).ConfigureAwait(false);
            }

            var result = await _latencyOptimizer
                .TestAsync(Settings.Latency.ToSpec(), cancellationToken)
                .ConfigureAwait(false);

            return PublishLatency(result);
        }
        finally
        {
            _latencyGate.Release();
        }
    }

    /// <summary>
    /// The deep test: latency while the link is genuinely busy, and the Traffic Guard.
    /// </summary>
    /// <remarks>
    /// Runs only when the user asks, because it needs them to start a real transfer.
    /// Nothing is sent by the application; see <see cref="ObservedLoadExperiment"/>.
    /// </remarks>
    public async Task<LatencyOptimizationResult> RunLoadedLatencyTestAsync(CancellationToken cancellationToken = default)
    {
        await _latencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _loadedLatencyBusy = true;

        // The user's own cancel button and the caller's token are both honoured, and the
        // source is disposed in the finally so a cancelled run cannot leave the card
        // showing a stop button that does nothing.
        var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadedLatencyCancellation = run;

        try
        {
            PublishLatencyStage(new LoadedLaneProgress
            {
                Stage = LoadedLaneStage.VerifyingTarget,
                Title = LoadedLaneProgress.TitleFor(LoadedLaneStage.VerifyingTarget),
                Instruction = string.Empty,
            });

            PublishLatency(Working(LatencyOptimizationStatus.LoadTesting, "Derin test başlatılıyor…"));

            // Started here rather than at launch: the observer exists for this run and is
            // closed with it, so nothing is watching connections while nobody asked.
            await StartFlowObserverAsync(run.Token).ConfigureAwait(false);


            var result = await _loadedLatency.RunAsync(
                new LoadedLaneRequest
                {
                    Target = Settings.Latency.ToSpec(),
                    RunTrafficGuard = Settings.Latency.TrafficGuardEnabled,
                    BulkApplication = Settings.Latency.TrafficGuardApplication,
                    GuardMode = Settings.Latency.GuardMode,
                    Capacity = Settings.Latency.ToCapacity(),
                },
                run.Token).ConfigureAwait(false);

            return PublishLatency(result);
        }
        finally
        {
            await ReleaseFlowObserverAsync().ConfigureAwait(false);

            _loadedLatencyBusy = false;
            _loadedLatencyCancellation = null;
            run.Dispose();
            _latencyGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Stops a running deep test. The run itself puts everything back.</summary>
    public void CancelLoadedLatencyTest()
    {
        var run = _loadedLatencyCancellation;
        if (run is { IsCancellationRequested: false })
        {
            AppLog.Info("latency.loaded.cancelled: kullanıcı derin testi durdurdu.");
            run.Cancel();
        }
    }

    private async Task StartFlowObserverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _flowObserver.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not fatal: the run reports what it therefore cannot establish rather than
            // producing a result it has no evidence for.
            AppLog.Info($"latency.flow: gözlem başlatılamadı ({ex.Message}).");
        }
    }

    /// <summary>
    /// Closes the observer unless the idle lane still needs it.
    /// </summary>
    /// <remarks>
    /// An application target is re-resolved on every network change, and the flow layer
    /// cannot report a connection created before the handle opened - so closing it while
    /// the mode is on and pointed at an application would mean the next run rediscovers
    /// nothing. Any other target has no use for it and it is closed.
    /// </remarks>
    private async Task ReleaseFlowObserverAsync()
    {
        if (Settings.LowLatencyMode && Settings.Latency.TargetKind == LatencyTargetKind.Application)
        {
            return;
        }

        await StopFlowObserverAsync().ConfigureAwait(false);
    }

    private async Task StopFlowObserverAsync()
    {
        try
        {
            await _flowObserver.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Info($"latency.flow: gözlem durdurulamadı ({ex.Message}).");
        }
    }

    private void PublishLatencyStage(LoadedLaneProgress progress)
    {
        lock (_latencyStageGate)
        {
            _latencyStage = progress;
        }

        LatencyStageChanged?.Invoke(progress);
    }

    /// <summary>Programs holding a live remote connection, for the target picker.</summary>
    /// <remarks>
    /// Reads the connection table and the process list, both of which take long enough to
    /// be noticed on a UI thread, so it is deliberately asynchronous.
    /// </remarks>
    public Task<IReadOnlyList<string>> ListConnectedProcessesAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<string>>(
            () => new WindowsProcessEndpointProvider(AppLog.InfoSink).ConnectedProcesses(),
            cancellationToken);

    private LatencyOptimizationResult Working(LatencyOptimizationStatus status, string line) => new()
    {
        Status = status,
        StatusLine = line,
        AdapterName = Network.AdapterName ?? Network.DisplayName,
        NetworkKey = Network.Key,
        TargetLabel = Settings.Latency.ToSpec().Describe(),
    };

    /// <summary>What the user has to do for the deep test to have anything to measure.</summary>
    public string LatencyLoadInstruction(LoadDirection direction) => _loadedLatency.Instruction(direction);

    /// <summary>Applications currently running that the Traffic Guard could pace.</summary>
    /// <remarks>
    /// The same list the target picker uses. A QoS match condition is only as good as the
    /// name in it, so the guard's application is chosen from processes that exist rather
    /// than typed.
    /// </remarks>
    public Task<IReadOnlyList<string>> ListBulkApplicationsAsync(CancellationToken cancellationToken = default)
        => ListConnectedProcessesAsync(cancellationToken);

    /// <summary>Measures again from scratch, ignoring any saved answer for this network.</summary>
    public async Task<LatencyOptimizationResult> RetestLatencyAsync(CancellationToken cancellationToken = default)
    {
        await _latencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _latencyOptimizer
                .RetestAsync(NetworkFingerprint.Capture(), cancellationToken)
                .ConfigureAwait(false);

            return PublishLatency(result);
        }
        finally
        {
            _latencyGate.Release();
        }
    }

    /// <summary>Applies a new latency target and persists it.</summary>
    public void SetLatencyPreferences(LatencyPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        Settings.Latency = preferences;
        _store.Save(Settings);
        ApplyLatencyPreferences();
        Changed?.Invoke();
    }

    /// <summary>
    /// Pushes the saved target and restart consent onto the optimizer before a run.
    /// </summary>
    /// <remarks>
    /// Both are read afresh here rather than captured once, because a user can withdraw
    /// restart consent between runs and the next run has to honour that, and because the
    /// remote-session half of the answer can change without any setting changing.
    /// </remarks>
    private void ApplyLatencyPreferences()
    {
        _latencyOptimizer.Target = Settings.Latency.ToSpec();
        _latencyOptimizer.Restart = Settings.Latency.ToRestartPolicy();
    }

    /// <summary>
    /// Puts every latency change back: adapter properties and this app's QoS policies.
    /// </summary>
    /// <remarks>
    /// The policy sweep runs whatever the snapshot says, because a policy created by a
    /// build that crashed before it could record one would otherwise outlive every other
    /// trace of this feature.
    /// </remarks>
    public async Task<LatencyOptimizationResult> RestoreLatencyAsync(CancellationToken cancellationToken = default)
    {
        var result = await _latencyOptimizer.RestoreAsync(cancellationToken).ConfigureAwait(false);
        await _loadedLatency.ClearOwnedPoliciesAsync(cancellationToken).ConfigureAwait(false);
        return PublishLatency(result);
    }

    /// <summary>
    /// Deletes every saved per-network latency result.
    /// </summary>
    /// <remarks>
    /// The file is a cache and nothing else, so removing it costs one re-measurement and
    /// can never leave a machine in a changed state. Anything actually applied lives in
    /// the snapshot, which this does not touch.
    /// </remarks>
    public bool ClearLatencyProfiles()
    {
        try
        {
            if (!File.Exists(AppPaths.LatencyProfilesFile))
            {
                return false;
            }

            File.Delete(AppPaths.LatencyProfilesFile);
            AppLog.Info("latency.profile: kayıtlı sonuçlar kullanıcı isteğiyle silindi.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Info($"latency.profile: kayıtlı sonuçlar silinemedi ({ex.Message}).");
            return false;
        }
    }

    private LatencyOptimizationResult PublishLatency(LatencyOptimizationResult result)
    {
        _latencyResult = result;
        Changed?.Invoke();
        return result;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Degraded is a running engine that cannot reach discord.com, not a stopped
            // one. Leaving it out let a second start build a whole new engine, DNS proxy,
            // socket watcher and network monitor on top of the live ones - two sets of
            // driver handles at the same priority, a second listener fighting for port 53,
            // and the first set leaked because the fields were simply overwritten.
            if (State is ProtectionState.Running or ProtectionState.Starting or ProtectionState.Degraded)
            {
                return;
            }

            if (!Elevation.IsElevated)
            {
                throw new InvalidOperationException(
                    "DPI Bypass yönetici hakları olmadan çalışamaz. Uygulamayı yönetici olarak başlatın.");
            }

            SetState(ProtectionState.Starting, "Başlatılıyor…");

            // A stop/start cycle would otherwise leave the previous source behind.
            _lifetime?.Dispose();
            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;

            // Opening the driver, enumerating the adapters and shelling out to netsh take
            // seconds. The caller is the dispatcher, and an uncontended gate hands control
            // straight back to it, so without a hop the window never gets to paint.
            await Task.Run(async () =>
            {
                _resolver = new DohResolver();
                _dnsConfigurator = new DnsConfigurator(AppPaths.StateDirectory, AppLog.InfoSink);
                _tester = new ConnectivityTester(_resolver);

                await ConfigureDnsAsync(lifetime.Token).ConfigureAwait(false);

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

                _discovery = new BlockedSiteDiscovery(_tester, _engine, _learned, _matcher, AppLog.InfoSink)
                {
                    Enabled = Settings.AutoDiscoverBlockedSites,
                };
                _discovery.DomainLearned += OnDomainLearned;

                if (_dnsProxy is not null)
                {
                    var token = lifetime.Token;
                    _dnsProxy.NameResolved += name => _discovery?.Observe(name, token);
                }

                _monitor = new NetworkMonitor(log: AppLog.InfoSink);
                _monitor.Changed += OnNetworkChanged;
                _monitor.Start();
                Network = _monitor.Current;
            }).ConfigureAwait(false);

            SetState(ProtectionState.Running, "Koruma etkin");
        }
        catch (Exception ex)
        {
            // A driver that refuses to open would otherwise leave the machine's DNS
            // pointed at us and the state stuck on Starting, which the guard above turns
            // into a start button that does nothing for the rest of the session.
            await TeardownAsync().ConfigureAwait(false);
            SetState(ProtectionState.Stopped, $"Başlatılamadı: {ex.Message}");
            throw;
        }
        finally
        {
            _gate.Release();
        }

        // Detection and tuning are slow; do them after the UI is already responsive.
        _networkWork = Task.Run(() => InitialiseNetworkAsync(_lifetime!.Token));
        _recheckWork = Task.Run(() => RecheckLoopAsync(_lifetime!.Token));
    }

    private void OnDomainLearned(string domain)
    {
        DomainLearned?.Invoke(domain);
        Changed?.Invoke();
    }

    private void OnLatencyChanged(LatencyOptimizationResult result)
    {
        _latencyResult = result;
        Changed?.Invoke();
    }

    /// <summary>
    /// Re-verifies the chosen recipe on a timer, so an operator changing its rules
    /// while the machine sits idle is noticed rather than waited out.
    /// </summary>
    private async Task RecheckLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = Settings.RecheckIntervalSeconds;

            try
            {
                // Poll the setting on a fixed cadence so a change takes effect without
                // a restart, rather than sleeping for the old interval first.
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(interval <= 0 ? 300 : interval, 60, 21600)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Settings.RecheckIntervalSeconds <= 0 || State is not (ProtectionState.Running or ProtectionState.Degraded))
            {
                continue;
            }

            try
            {
                var probe = await VerifyAsync(cancellationToken).ConfigureAwait(false);
                if (probe.Success)
                {
                    continue;
                }

                AppLog.Warning($"Düzenli denetim başarısız ({DescribeOutcome(probe.Outcome)}); yöntem yeniden aranıyor.");
                await DetectAndTuneAsync("düzenli denetim başarısız", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error("Düzenli denetim hatası", ex);
            }
        }
    }

    private async Task ConfigureDnsAsync(CancellationToken cancellationToken)
    {
        // "Leave the system alone" is applied rather than skipped. A run that was killed
        // - by a crash, by the takeover path, or by the machine losing power - leaves the
        // resolvers pointed at our loopback proxy and a snapshot on disk. Returning early
        // here meant that starting with this mode selected left the machine resolving
        // against a socket nobody is listening on: no name resolution at all, and no way
        // out of it from inside the app. Applying it puts the original servers back.
        var mode = Settings.DnsMode;
        var loopbackHasIPv6 = false;

        if (mode == DnsMode.EncryptedLoopback)
        {
            _dnsProxy = new DnsProxyServer(_resolver!, AppLog.InfoSink);
            if (_dnsProxy.TryStart())
            {
                // The ::1 listeners are best effort, and pointing the machine's IPv6
                // resolver at a socket that never bound stalls name resolution for
                // everything that prefers IPv6 - which on Windows is everything.
                loopbackHasIPv6 = _dnsProxy.HasIPv6;
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

        await ResolveIspAsync(cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Settles which operator profile the sweep should be ordered by: the one the user
    /// forced, or the one detection finds.
    /// </summary>
    private async Task ResolveIspAsync(CancellationToken cancellationToken)
    {
        if (Settings.ManualIspProfileId is { Length: > 0 } manualIsp)
        {
            Isp = IspCatalog.ById(manualIsp);
            Detection = new IspDetection(Isp, null, null, null, null, WasAutomatic: false);
        }
        else if (_resolver is not null)
        {
            Detection = await new IspDetector(_resolver, AppLog.InfoSink)
                .DetectAsync(Network, cancellationToken)
                .ConfigureAwait(false);
            Isp = Detection.Profile;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Runs the read-only mobile hotspot checks against the network we are on now.
    /// </summary>
    /// <remarks>
    /// No persistent network state is changed or left behind. The ordinary diagnostic
    /// probes are safe to run at any time, on any network, however many times.
    /// </remarks>
    public async Task<HotspotDiagnosticResult> RunHotspotDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var network = NetworkFingerprint.Capture();
        Network = network;
        var operation = CreateHotspotDiagnosticsCancellation(cancellationToken);

        try
        {
            var result = await _hotspot.RunAsync(network, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();

            if (string.Equals(Network.Key, network.Key, StringComparison.Ordinal))
            {
                LastHotspotDiagnostics = result;
                Changed?.Invoke();
            }

            return result;
        }
        finally
        {
            ClearHotspotDiagnosticsCancellation(operation);
        }
    }

    /// <summary>
    /// Whether a network change should run the checks by itself.
    /// </summary>
    /// <remarks>
    /// Off by default. When on, moving to a different network costs one short pass -
    /// nine probes and a handful of reachability checks - and writes the result to the
    /// log, which is what turns "it stopped working when I switched to my phone" into
    /// something with a timestamp and an answer next to it. It is switched on for anyone
    /// the migration found using the retired TTL mode, since that is the audience.
    /// </remarks>
    public void SetHotspotDiagnostics(bool enabled)
    {
        if (Settings.HotspotDiagnostics == enabled)
        {
            return;
        }

        Settings.HotspotDiagnostics = enabled;
        _store.Save(Settings);

        if (!enabled)
        {
            CancelHotspotDiagnostics();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Enables Vodafone Sınırsız Modu's safe diagnostics for the current network.
    /// </summary>
    /// <remarks>
    /// This deliberately records only the network identity and enables diagnostic
    /// checks. It does not install a packet filter, change TTL/hop-limit values, block
    /// IPv6 or attempt to influence carrier accounting.
    /// </remarks>
    public void EnableVodafoneModeHere()
        => EnableVodafoneMode(NetworkFingerprint.Capture());

    internal void EnableVodafoneMode(NetworkFingerprint network)
    {
        ArgumentNullException.ThrowIfNull(network);

        if (!network.IsOnline)
        {
            throw new InvalidOperationException("Şu anda bir ağa bağlı değilsiniz.");
        }

        Network = network;
        Settings.RememberVodafoneNetwork(
            network.Key,
            network.DisplayName,
            network.AdapterName ?? string.Empty);
        Settings.VodafoneModeEnabled = true;
        Settings.HotspotDiagnostics = true;
        _store.Save(Settings);
        Changed?.Invoke();
    }

    /// <summary>
    /// Disables the mode and its automatic runs without erasing remembered networks or
    /// removing the always-available manual diagnostics.
    /// </summary>
    public void DisableVodafoneMode()
    {
        if (!Settings.VodafoneModeEnabled && !Settings.HotspotDiagnostics)
        {
            return;
        }

        Settings.VodafoneModeEnabled = false;
        Settings.HotspotDiagnostics = false;
        _store.Save(Settings);
        CancelHotspotDiagnostics();
        Changed?.Invoke();
    }

    /// <summary>Forgets one safe per-network registration.</summary>
    public void ForgetVodafoneNetwork(string key)
    {
        if (!Settings.ForgetVodafoneNetwork(key))
        {
            return;
        }

        _store.Save(Settings);
        Changed?.Invoke();
    }

    /// <summary>
    /// Removes anything an older build's hotspot TTL mode left behind.
    /// </summary>
    /// <remarks>
    /// Idempotent, and safe on a machine that never had the old mode: the migration is a
    /// pure function of the legacy fields, so calling this twice - or on a clean install -
    /// does nothing the second time. It exists as its own entry point because "off" has
    /// to keep working from the command line and the uninstaller, not only through a
    /// settings load.
    /// </remarks>
    public HotspotMigrationResult CleanUpLegacyHotspotConfiguration()
    {
        var migration = HotspotLegacyMigration.Apply(Settings, DateTimeOffset.UtcNow);

        if (migration.Changed)
        {
            AppLog.Info($"Eski hotspot yapılandırması temizlendi. {migration.Summary}");
        }

        // Saved either way: the switch is forced off on every pass, and persisting that
        // is the whole point of an explicit cleanup call.
        _store.Save(Settings);
        Changed?.Invoke();

        return migration;
    }

    private void OnNetworkChanged(NetworkFingerprint fingerprint)
    {
        Network = fingerprint;

        // A different network means the previous diagnostics describe a link that is no
        // longer under us, so the panel goes back to "not run here yet".
        LastHotspotDiagnostics = null;
        Changed?.Invoke();

        var token = _lifetime?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (ShouldRunHotspotDiagnostics(Settings, fingerprint))
        {
            var diagnostics = CreateHotspotDiagnosticsCancellation(token);
            // Always enter the delegate so its finally block disposes the linked source,
            // even if shutdown cancels it between creation and queueing.
            _ = Task.Run(() => RunHotspotDiagnosticsOnTransitionAsync(fingerprint, diagnostics));
        }
        else
        {
            CancelHotspotDiagnostics();
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

    internal static bool ShouldRunHotspotDiagnostics(AppSettings settings, NetworkFingerprint fingerprint)
        => settings.HotspotDiagnostics
            && (!settings.VodafoneModeEnabled
                || settings.VodafoneModeNetworks.Count == 0
                || settings.VodafoneNetworkRegistered(fingerprint.Key));

    /// <summary>
    /// Runs the checks after a transition, off the notification thread.
    /// </summary>
    /// <remarks>
    /// Failure here is worth a log line and nothing else: the diagnostics are an
    /// explanation of the network, not a part of making it work, so nothing downstream
    /// waits on them and a network that is still settling simply produces a poor report.
    /// </remarks>
    private async Task RunHotspotDiagnosticsOnTransitionAsync(
        NetworkFingerprint fingerprint,
        CancellationTokenSource operation)
    {
        var token = operation.Token;

        try
        {
            AppLog.Info($"hotspot.diagnostics: '{fingerprint.DisplayName}' ağına geçildi; bağlantı inceleniyor.");

            var result = await _hotspot.RunAsync(fingerprint, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // Only if we are still on the network this describes. A second transition
            // while this ran would otherwise leave the panel showing the wrong link.
            if (string.Equals(Network.Key, fingerprint.Key, StringComparison.Ordinal))
            {
                LastHotspotDiagnostics = result;
                Changed?.Invoke();
            }

            AppLog.Info($"hotspot.diagnostics: internet {(result.HasInternet ? "var" : "yok")}, "
                + $"DNS {(result.DnsWorks ? "çalışıyor" : "çalışmıyor")}, IPv6 {(result.Ipv6Works ? "çalışıyor" : "yok")}.");
        }
        catch (OperationCanceledException)
        {
            // Shutting down, or a newer network change took over.
        }
        catch (Exception ex)
        {
            AppLog.Error("Ağ değişikliği sonrası hotspot tanılaması başarısız", ex);
        }
        finally
        {
            ClearHotspotDiagnosticsCancellation(operation);
        }
    }

    private CancellationTokenSource CreateHotspotDiagnosticsCancellation(CancellationToken callerToken)
    {
        var lifetimeToken = State == ProtectionState.Stopped
            ? CancellationToken.None
            : _lifetime?.Token ?? CancellationToken.None;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, lifetimeToken);
        CancellationTokenSource? previous;

        lock (_hotspotDiagnosticsGate)
        {
            previous = _hotspotDiagnosticsCancellation;
            _hotspotDiagnosticsCancellation = operation;
        }

        CancelWithoutRacingDispose(previous);
        return operation;
    }

    private void CancelHotspotDiagnostics()
    {
        CancellationTokenSource? operation;
        lock (_hotspotDiagnosticsGate)
        {
            operation = _hotspotDiagnosticsCancellation;
            _hotspotDiagnosticsCancellation = null;
        }

        CancelWithoutRacingDispose(operation);
    }

    private void ClearHotspotDiagnosticsCancellation(CancellationTokenSource operation)
    {
        lock (_hotspotDiagnosticsGate)
        {
            if (ReferenceEquals(_hotspotDiagnosticsCancellation, operation))
            {
                _hotspotDiagnosticsCancellation = null;
            }
        }

        operation.Dispose();
    }

    private static void CancelWithoutRacingDispose(CancellationTokenSource? operation)
    {
        try
        {
            operation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion can win the race after the source was removed from the field.
        }
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

        // Persisted straight away: a sweep that ends without a winner used to leave the
        // discarded profile on disk, so the next launch started on the very recipe the
        // user had just asked us to stop using.
        if (Settings.Networks.Remove(Network.Key))
        {
            _store.SaveNetworks(Settings);
        }

        // The operator is settled again first. Without this, switching back to automatic
        // detection left the sweep ordered by the profile the user had just deselected.
        await ResolveIspAsync(cancellationToken).ConfigureAwait(false);

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

        if (ispProfileId is null)
        {
            // Back to automatic. Keeping the forced profile as the answer is what made
            // "Otomatik algıla" look like it did nothing: the status line went on naming
            // the operator the user had just deselected, and the next sweep was still
            // ordered by it. Cleared here; detection fills it in again.
            Isp = IspCatalog.Unknown;
            Detection = null;
        }
        else
        {
            Isp = IspCatalog.ById(ispProfileId);
            Detection = new IspDetection(Isp, null, null, null, null, WasAutomatic: false);
        }

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

            // Putting the DNS back shells out to netsh and closing the driver blocks
            // until the packet threads come out of the kernel. The stop button is on
            // the dispatcher, so this gets the same hop off it that starting does.
            await Task.Run(TeardownAsync).ConfigureAwait(false);

            _store.Save(Settings);
            SetState(ProtectionState.Stopped, "Koruma kapalı");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Puts everything the start built back the way it was. Each step stands on its own
    /// so a start that fell over half way through can call it with the same result.
    /// </summary>
    /// <remarks>Expects the gate to be held, and never touches it.</remarks>
    private async Task TeardownAsync()
    {
        // Everything running in the background hangs off this token, so it goes first:
        // nothing should still be reaching for the objects about to be disposed.
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        CancelHotspotDiagnostics();

        if (_discovery is not null)
        {
            _discovery.DomainLearned -= OnDomainLearned;
            _discovery.Dispose();
            _discovery = null;
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

        // Whole lists at a time: the packet path must never see one half emptied.
        _matcher.ExtraDomains.Replace(Settings.ExtraDomains);
        _matcher.ExcludedDomains.Replace(Settings.ExcludedDomains);
        _matcher.LearnedDomains.Replace(_learned.Domains);

        if (_discovery is not null)
        {
            _discovery.Enabled = Settings.AutoDiscoverBlockedSites;
        }
    }

    /// <summary>Adds a domain the user typed in. Returns false when it was already covered.</summary>
    public bool AddDomain(string domain)
    {
        var normalised = domain?.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(normalised) || !normalised.Contains('.'))
        {
            return false;
        }

        Settings.ExcludedDomains.RemoveAll(d => string.Equals(d, normalised, StringComparison.OrdinalIgnoreCase));

        if (Settings.ExtraDomains.Any(d => string.Equals(d, normalised, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Settings.ExtraDomains.Add(normalised);
        SaveSettings();
        AppLog.Info($"Alan adı listeye eklendi: {normalised}");
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Stops protecting a domain. A shipped or learned entry cannot be deleted, so it
    /// is added to the exclusion list instead - which also stops discovery relearning it.
    /// </summary>
    public bool RemoveDomain(string domain)
    {
        var normalised = domain?.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(normalised))
        {
            return false;
        }

        var removed = Settings.ExtraDomains.RemoveAll(d => string.Equals(d, normalised, StringComparison.OrdinalIgnoreCase)) > 0;
        removed |= _learned.Remove(normalised);

        if (TargetMatcher.IsKnownBlockedDomain(normalised)
            && !Settings.ExcludedDomains.Any(d => string.Equals(d, normalised, StringComparison.OrdinalIgnoreCase)))
        {
            Settings.ExcludedDomains.Add(normalised);
            removed = true;
        }

        if (!removed)
        {
            return false;
        }

        SaveSettings();
        AppLog.Info($"Alan adı listeden çıkarıldı: {normalised}");
        Changed?.Invoke();
        return true;
    }

    /// <summary>Restores a domain that was excluded from the shipped list.</summary>
    public bool RestoreDomain(string domain)
    {
        var normalised = domain?.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(normalised)
            || Settings.ExcludedDomains.RemoveAll(d => string.Equals(d, normalised, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return false;
        }

        SaveSettings();
        Changed?.Invoke();
        return true;
    }

    public void ApplyDiscoverySetting(bool enabled)
    {
        Settings.AutoDiscoverBlockedSites = enabled;
        _store.Save(Settings);

        if (_discovery is not null)
        {
            _discovery.Enabled = enabled;
        }
    }

    public void ApplyRecheckInterval(int seconds)
    {
        Settings.RecheckIntervalSeconds = seconds < 0 ? 0 : seconds;
        _store.Save(Settings);
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
        ProtectionScope.BlockedSites => "Engelli site listesi",
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
        CancelHotspotDiagnostics();

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Kapatma sırasında hata", ex);
        }

        foreach (var work in new[] { _networkWork, _recheckWork })
        {
            if (work is null)
            {
                continue;
            }

            try
            {
                await work.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        // Whatever the mode says, nothing is left listening after the service is gone.
        await _flowObserver.DisposeAsync().ConfigureAwait(false);

        _latencyOptimizer.Changed -= OnLatencyChanged;
        await _latencyOptimizer.DisposeAsync().ConfigureAwait(false);
        _latencyGate.Dispose();
        _lifetime?.Dispose();
        _gate.Dispose();
    }
}
