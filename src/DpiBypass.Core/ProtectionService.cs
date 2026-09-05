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
    private readonly Lock _networkWatchGate = new();
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

    /// <summary>
    /// The one place the engine's strategy is written from.
    /// </summary>
    /// <remarks>
    /// The first start, a network change, the periodic re-check and the user's re-tune
    /// button all reach strategy selection, and they used to do it concurrently through
    /// one shared engine field. A sweep begun on the cafe's wifi could therefore install
    /// its winner - and file it under the phone hotspot's key - seconds after the machine
    /// had already moved. Every one of those paths now runs under a lease from here, and
    /// a lease that has been superseded writes nothing.
    /// </remarks>
    private readonly StrategyCoordinator _strategies;

    /// <summary>
    /// Background work that must be finished before the objects it uses are disposed.
    /// </summary>
    /// <remarks>
    /// Cancelling is a request, not a completion. Teardown used to cancel the lifetime
    /// token and then immediately dispose the engine, the resolver and the DNS proxy,
    /// which is a race the tuner loses roughly as often as it is mid-probe: an
    /// ObjectDisposedException on a thread nobody awaits.
    /// </remarks>
    private readonly TrackedWork _background = new();

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

    /// <summary>
    /// The cancellation source of whichever latency run is in flight, of any kind.
    /// </summary>
    /// <remarks>
    /// Only the deep test used to be interruptible. Turning the mode on runs a full
    /// paired benchmark that can take minutes, and a quick test still pings for several
    /// seconds; both left the card with a disabled stop button and no way out but waiting.
    /// One source covers all three, and the run that owns it clears it on the way out.
    /// </remarks>
    private CancellationTokenSource? _latencyRunCancellation;

    /// <summary>
    /// Which run's results the card is allowed to accept.
    /// </summary>
    /// <remarks>
    /// Incremented whenever a run starts or the target changes. A run whose stamp is
    /// stale by the time it finishes has been superseded - the user switched targets, or
    /// started another test - and publishing it would put an answer about the old target
    /// under the new one's heading.
    /// </remarks>
    private int _latencyRunGeneration;

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

        _strategies = new StrategyCoordinator(
            read: () => _engine?.Strategy ?? StrategyLibrary.Default,
            write: strategy =>
            {
                if (_engine is { } engine)
                {
                    engine.Strategy = strategy;
                }
            },
            log: AppLog.InfoSink);

        // Load already disabled the obsolete TTL sub-feature and preserved any reusable
        // Vodafone-mode settings in memory. Persist the one-time migration immediately.
        Settings = _store.Load();
        if (Settings.LegacyHotspotCleaned)
        {
            ReportSave(_store.Save(Settings), "ayarlar");
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

    /// <summary>
    /// Whether any latency work is in flight.
    /// </summary>
    /// <remarks>
    /// Includes a run that has been registered but has not yet reached a state the
    /// optimizer calls busy. Without that window the card's progress area flickered off
    /// between the user pressing the button and the first measurement starting, which is
    /// also the window in which the stop button would have read as disabled.
    /// </remarks>
    public bool IsLatencyBusy => _latencyOptimizer.IsBusy || _loadedLatencyBusy || CanCancelLatencyRun;

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

    /// <summary>Whether any latency run is in flight and can still be stopped.</summary>
    public bool CanCancelLatencyRun => _latencyRunCancellation is { IsCancellationRequested: false };

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
        RegisteredHere: Settings.VodafoneNetworkRegistered(Network),
        RegisteredNetworks: Settings.VodafoneModeNetworks.Count,
        NetworkName: Network.DisplayName,
        AdapterName: Network.AdapterName ?? "-",
        LegacyCleanedAt: Settings.HotspotLegacyMigratedAt,
        LastResult: LastHotspotDiagnostics);

    /// <summary>The most recent diagnostics pass, if one has run this session.</summary>
    public HotspotDiagnosticResult? LastHotspotDiagnostics { get; private set; }

    /// <summary>Whether a check is running now, so the card does not show a stale line.</summary>
    public bool IsHotspotBusy => _hotspotDiagnosticsCancellation is { IsCancellationRequested: false };

    /// <summary>The reason the last check could not finish, when it could not.</summary>
    public string? LastHotspotFailure { get; private set; }

    /// <summary>
    /// The whole Vodafone card as structured values, with no report text to parse.
    /// </summary>
    /// <remarks>
    /// Built here rather than in the view model so the card and any other consumer see
    /// the same state, and so a result belonging to a network we have since left cannot
    /// be shown as if it described the one we are on.
    /// </remarks>
    public HotspotStatusView HotspotView => HotspotStatusView.From(
        HotspotStatus,
        IsHotspotBusy,
        LastHotspotFailure,
        HotspotLegacyMigration.HasResidue(Settings));

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

    /// <summary>
    /// Starts watching which network we are on, whether or not the engine is running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be built inside <see cref="StartAsync"/> and torn down with the
    /// engine, and that is the whole of why Vodafone Sınırsız Modu looked dead. With
    /// protection stopped nothing knew which network the machine was on -
    /// <see cref="Network"/> was the empty fingerprint from the field initialiser - so
    /// the card compared the user's remembered networks against a blank key, decided
    /// none of them matched, and reported the network they were sitting on as
    /// unregistered. Nothing ran by itself, and the only way to make the card say
    /// anything true was to press the buttons by hand.
    /// </para>
    /// <para>
    /// Idempotent, so the engine's own start can call it without caring whether the
    /// application already did.
    /// </para>
    /// </remarks>
    public void StartNetworkWatch()
    {
        lock (_networkWatchGate)
        {
            if (_monitor is not null)
            {
                return;
            }

            var monitor = new NetworkMonitor(log: AppLog.InfoSink);
            monitor.Changed += OnNetworkChanged;

            try
            {
                monitor.Start();
            }
            catch (Exception ex)
            {
                monitor.Changed -= OnNetworkChanged;
                monitor.Dispose();
                AppLog.Error("Ağ izleyici başlatılamadı", ex);
                return;
            }

            _monitor = monitor;
            AdoptNetwork(monitor.Current);
        }

        // Outside the lock: the card has been showing an empty network name until now,
        // and this is the notification that fills it in.
        Changed?.Invoke();
    }

    /// <summary>Ends the lifetime network watch. Safe to call more than once.</summary>
    private void StopNetworkWatch()
    {
        NetworkMonitor? monitor;
        lock (_networkWatchGate)
        {
            monitor = _monitor;
            _monitor = null;
        }

        if (monitor is null)
        {
            return;
        }

        monitor.Changed -= OnNetworkChanged;
        monitor.Dispose();
    }

    /// <summary>
    /// Records the network we are on and reconciles the Vodafone registration with it.
    /// </summary>
    /// <remarks>
    /// A phone hotspot is handed a fresh access point MAC on every session, so the
    /// fingerprint key of "the network the user registered" changes underneath us.
    /// Recognising it by name and then writing the current identity back is what keeps
    /// the saved list pointing at the connection actually in front of the user, instead
    /// of at one expired session of it.
    /// </remarks>
    private void AdoptNetwork(NetworkFingerprint network)
    {
        Network = network;

        // Before anything else: a run still going belongs to the link we just left, and
        // from here on its writes are refused rather than landing on the new network.
        if (_strategies.AdoptNetwork(network.Key))
        {
            // Cached names and endpoint health both describe the previous link. A
            // split-horizon answer or a resolver blocked by the last network's operator
            // is correct where it was learned and wrong here.
            _resolver?.OnNetworkChanged();
            _dnsProxy?.OnNetworkChanged();
        }

        if (Settings.VodafoneModeNetworks.Count == 0
            || !network.IsOnline
            || !Settings.RefreshVodafoneNetworkIdentity(network))
        {
            return;
        }

        ReportSave(_store.Save(Settings), "ayarlar");
        AppLog.Info($"vodafone: '{network.DisplayName}' kayıtlı ağ olarak tanındı.");
    }

    /// <summary>
    /// Runs the safe checks once for a launch that starts on a remembered network.
    /// </summary>
    /// <remarks>
    /// Without this the card only ever filled in after a network transition, so the
    /// common case - the machine boots already connected to the hotspot - showed
    /// "henüz kontrol edilmedi" until the user pressed the button. Nothing here changes
    /// any network state; it is the same read-only pass the button runs.
    /// </remarks>
    public async Task RunStartupHotspotCheckAsync(CancellationToken cancellationToken = default)
    {
        StartNetworkWatch();

        var network = Network;
        if (!ShouldRunHotspotDiagnostics(Settings, network)
            || !Settings.VodafoneModeEnabled
            || !network.IsOnline
            || LastHotspotDiagnostics is not null
            || IsHotspotBusy)
        {
            return;
        }

        var operation = CreateHotspotDiagnosticsCancellation(cancellationToken);
        await RunHotspotDiagnosticsOnTransitionAsync(network, operation).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the mode on or off, running the full pass under the same gate as every
    /// other latency operation.
    /// </summary>
    /// <remarks>
    /// The gate matters as much here as anywhere. This path starts a paired benchmark
    /// that writes adapter settings, and it used to take no gate at all - so a quick test
    /// or a deep test started at the same moment measured a link whose driver settings
    /// were being changed underneath it, and each run's snapshot could undo the other's.
    /// Rapid on/off is the same story: the second call has to wait for the first to
    /// finish putting the machine back.
    /// </remarks>
    public async Task<LatencyOptimizationResult> SetLowLatencyModeAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        Settings.LowLatencyMode = enabled;
        ReportSave(_store.Save(Settings), "ayarlar");
        Changed?.Invoke();

        await _latencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var run = BeginLatencyRun(cancellationToken);

        try
        {
            if (enabled)
            {
                ApplyLatencyPreferences();

                if (Settings.Latency.TargetKind == LatencyTargetKind.Application)
                {
                    await StartFlowObserverAsync(run.Token).ConfigureAwait(false);
                }

                return PublishLatency(
                    await _latencyOptimizer.StartAsync(run.Token).ConfigureAwait(false),
                    run.Generation);
            }

            // Restoring runs on the caller's token only. A user who switches the mode off
            // wants their original settings back, and abandoning that half way through
            // leaves the adapter holding values nobody chose.
            var stopped = await _latencyOptimizer.StopAndRestoreAsync(cancellationToken).ConfigureAwait(false);
            await _loadedLatency.ClearOwnedPoliciesAsync(cancellationToken).ConfigureAwait(false);
            await StopFlowObserverAsync().ConfigureAwait(false);
            return PublishLatency(stopped, run.Generation);
        }
        finally
        {
            EndLatencyRun(run);
            _latencyGate.Release();
        }
    }

    /// <summary>Starts a cancellable, stamped latency run. The caller owns the gate.</summary>
    private LatencyRun BeginLatencyRun(CancellationToken callerToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _latencyRunCancellation = source;
        var generation = Interlocked.Increment(ref _latencyRunGeneration);
        Changed?.Invoke();
        return new LatencyRun(source, generation);
    }

    private void EndLatencyRun(LatencyRun run)
    {
        if (ReferenceEquals(_latencyRunCancellation, run.Source))
        {
            _latencyRunCancellation = null;
        }

        run.Source.Dispose();
        Changed?.Invoke();
    }

    /// <summary>One latency operation: its cancellation source and its stamp.</summary>
    private readonly record struct LatencyRun(CancellationTokenSource Source, int Generation)
    {
        public CancellationToken Token => Source.Token;
    }

    /// <summary>The quick test: idle latency to the chosen target, changing nothing.</summary>
    public async Task<LatencyOptimizationResult> TestLatencyAsync(CancellationToken cancellationToken = default)
    {
        await _latencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var run = BeginLatencyRun(cancellationToken);

        try
        {
            PublishLatency(Working(LatencyOptimizationStatus.QuickTesting, "Hızlı test çalışıyor…"));

            if (Settings.Latency.TargetKind == LatencyTargetKind.Application)
            {
                await StartFlowObserverAsync(run.Token).ConfigureAwait(false);
            }

            var result = await _latencyOptimizer
                .TestAsync(Settings.Latency.ToSpec(), run.Token)
                .ConfigureAwait(false);

            return PublishLatency(result, run.Generation);
        }
        finally
        {
            EndLatencyRun(run);
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
        _latencyRunCancellation = run;
        var generation = Interlocked.Increment(ref _latencyRunGeneration);

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

            return PublishLatency(result, generation);
        }
        finally
        {
            await ReleaseFlowObserverAsync().ConfigureAwait(false);

            _loadedLatencyBusy = false;
            _loadedLatencyCancellation = null;

            if (ReferenceEquals(_latencyRunCancellation, run))
            {
                _latencyRunCancellation = null;
            }

            run.Dispose();
            _latencyGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Stops a running deep test. The run itself puts everything back.</summary>
    public void CancelLoadedLatencyTest() => CancelLatencyRun();

    /// <summary>
    /// Stops whichever latency run is in flight, of any kind.
    /// </summary>
    /// <remarks>
    /// Every latency path restores what it changed on cancellation, so this is safe from
    /// any of them: the optimizer rolls its snapshot back and the loaded lane sweeps its
    /// own QoS policies. Cancelling is a state the user chose, not a failure.
    /// </remarks>
    public void CancelLatencyRun()
    {
        var run = _latencyRunCancellation;
        if (run is { IsCancellationRequested: false })
        {
            AppLog.Info("latency.cancelled: kullanıcı çalışan gecikme işlemini durdurdu.");
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
        var run = BeginLatencyRun(cancellationToken);

        try
        {
            var result = await _latencyOptimizer
                .RetestAsync(NetworkFingerprint.Capture(), run.Token)
                .ConfigureAwait(false);

            return PublishLatency(result, run.Generation);
        }
        finally
        {
            EndLatencyRun(run);
            _latencyGate.Release();
        }
    }

    /// <summary>Applies a new latency target and persists it.</summary>
    public void SetLatencyPreferences(LatencyPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var targetChanged = !string.Equals(
            Settings.Latency.ToSpec().CacheKey,
            preferences.ToSpec().CacheKey,
            StringComparison.Ordinal);

        Settings.Latency = preferences;
        ReportSave(_store.Save(Settings), "ayarlar");
        ApplyLatencyPreferences();

        // A run already measuring the old target is now measuring the wrong thing, so its
        // result is retired before it can be published under the new heading.
        if (targetChanged)
        {
            Interlocked.Increment(ref _latencyRunGeneration);
        }

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

    /// <summary>
    /// Publishes a result only if the run that produced it is still the current one.
    /// </summary>
    /// <remarks>
    /// A run started against one target and finishing after the user picked another must
    /// not overwrite the card: the numbers would be real, and they would be filed under a
    /// heading naming a target they say nothing about. The result is still returned to
    /// the caller, which is what awaited it and can decide for itself.
    /// </remarks>
    private LatencyOptimizationResult PublishLatency(LatencyOptimizationResult result, int generation)
    {
        if (Volatile.Read(ref _latencyRunGeneration) != generation)
        {
            AppLog.Info("latency.stale: sonuç geldiğinde hedef değişmişti; ekrana yazılmadı.");
            return result;
        }

        return PublishLatency(result);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? lifetime = null;
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

            // A previous stop that could not put the machine's DNS back left the proxy
            // running on purpose, so names still resolve. Starting again reuses nothing:
            // teardown here is what releases that proxy and its socket before the new
            // start tries to bind the same port.
            if (_dnsProxy is not null || _resolver is not null || _dnsConfigurator is not null)
            {
                await TeardownAsync().ConfigureAwait(false);
            }

            if (!Elevation.IsElevated)
            {
                throw new InvalidOperationException(
                    "DPI Bypass yönetici hakları olmadan çalışamaz. Uygulamayı yönetici olarak başlatın.");
            }

            SetState(ProtectionState.Starting, "Başlatılıyor…");

            // A stop/start cycle would otherwise leave the previous source behind.
            _lifetime?.Dispose();
            lifetime = new CancellationTokenSource();
            _lifetime = lifetime;

            // Two different lifetimes, and confusing them is how a start used to leave
            // half configured DNS behind. The service's own lifetime is `lifetime`, and
            // everything that outlives this call hangs off it. Cancelling the *call* -
            // the user closing the window mid-start - is a separate thing, so the start
            // body runs on a token linked to both: the caller can abandon the start, the
            // catch below undoes what it managed to do, and the running service is never
            // tied to the token of whichever call happened to start it.
            using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var startToken = starting.Token;

            // Opening the driver, enumerating the adapters and shelling out to netsh take
            // seconds. The caller is the dispatcher, and an uncontended gate hands control
            // straight back to it, so without a hop the window never gets to paint.
            await Task.Run(async () =>
            {
                _resolver = new DohResolver();
                _dnsConfigurator = new DnsConfigurator(AppPaths.StateDirectory, AppLog.InfoSink);
                _tester = new ConnectivityTester(_resolver);

                // Each of these steps is seconds of work, and until the state said what
                // it was doing they were all one motionless "Başlatılıyor…" - which is
                // the difference between a slow start and an application that looks
                // wedged. The state stays Starting throughout; only the detail moves.
                SetState(ProtectionState.Starting, "Ad çözümleme ayarlanıyor…");
                await ConfigureDnsAsync(startToken).ConfigureAwait(false);

                SetState(ProtectionState.Starting, "Ağ sürücüsü açılıyor…");
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

                _tuner = new StrategyTuner(_tester, AppLog.InfoSink);
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

                SetState(ProtectionState.Starting, "Ağ izleniyor…");

                // The watch belongs to the service, not to the engine, so it is started
                // rather than rebuilt: it is already running whenever the app is.
                StartNetworkWatch();

                // The engine exists and the network is known, so runs can be stamped.
                // Anything still unwinding from the previous engine is now superseded.
                _strategies.BeginSession(Network.Key);
            }, startToken).ConfigureAwait(false);

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

        // Detection and tuning are slow; do them after the UI is already responsive. The
        // token is the one this start created, read once here rather than off the field:
        // a stop racing in behind us replaces _lifetime, and a background task that reads
        // it late would attach itself to somebody else's session.
        var sessionToken = lifetime!.Token;
        _networkWork = _background.Track(Task.Run(() => InitialiseNetworkAsync(sessionToken), CancellationToken.None));
        _recheckWork = _background.Track(Task.Run(() => RecheckLoopAsync(sessionToken), CancellationToken.None));
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
                await TuneAsync(StrategyWorkKind.Automatic, "düzenli denetim başarısız", cancellationToken)
                    .ConfigureAwait(false);
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
            AdoptNetwork(NetworkFingerprint.Capture());
            await TuneAsync(StrategyWorkKind.Automatic, "ilk başlatma", cancellationToken).ConfigureAwait(false);
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
    /// The single entry point for every re-tune, whoever asked for it.
    /// </summary>
    /// <remarks>
    /// The start-up pass, a network change, the periodic re-check, the re-tune button and
    /// the CLI all come through here. Automatic requests for a network already being
    /// tuned join that run instead of queueing a second sweep behind it; a manual request
    /// supersedes whatever the app decided to do on its own, because the user is standing
    /// in front of it.
    /// </remarks>
    private Task<TuningResult?> TuneAsync(StrategyWorkKind kind, string reason, CancellationToken cancellationToken)
        => _strategies.RunAsync<TuningResult?>(
            kind,
            reason,
            (lease, token) => DetectAndTuneAsync(lease, reason, token),
            coalescedResult: null,
            cancellationToken);

    /// <summary>
    /// Detects the operator, reuses a cached recipe for this network if there is one,
    /// verifies it for real, and only runs the full sweep when verification fails.
    /// </summary>
    /// <remarks>
    /// Everything this writes - the engine's strategy, the network profile - goes through
    /// <paramref name="lease"/>, and the lease knows which network and which engine the
    /// run started on. A pass that is still finishing after the machine has moved
    /// therefore reports its findings to the log and changes nothing.
    /// </remarks>
    private async Task<TuningResult?> DetectAndTuneAsync(
        StrategyLease lease,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_engine is null || _tuner is null || _resolver is null)
        {
            return null;
        }

        SetState(State, $"Ağ inceleniyor ({reason})…");

        await ResolveIspAsync(cancellationToken).ConfigureAwait(false);

        if (Settings.ManualStrategyId is { Length: > 0 } manualStrategy)
        {
            var forced = StrategyLibrary.Find(manualStrategy);
            if (forced is not null)
            {
                if (!lease.TryWrite(forced))
                {
                    return null;
                }

                await VerifyAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        if (Settings.Networks.TryGetValue(lease.NetworkKey, out var cached)
            && StrategyLibrary.Find(cached.StrategyId) is { } remembered)
        {
            if (!lease.TryWrite(remembered))
            {
                return null;
            }

            SetState(State, $"Kayıtlı yöntem deneniyor: {remembered.Name}");

            if (await _tuner.VerifyCurrentAsync(cancellationToken).ConfigureAwait(false))
            {
                RecordNetworkResult(lease, remembered, success: true, wasUnfiltered: cached.WasUnfiltered);
                SetState(ProtectionState.Running, $"Koruma etkin · {remembered.Name}");
                await VerifyAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            AppLog.Info("Kayıtlı yöntem artık işe yaramıyor; yeni yöntem aranıyor.");
            RecordNetworkResult(lease, remembered, success: false, wasUnfiltered: false);
        }

        if (!Settings.AutoTuneOnNetworkChange && Settings.Networks.ContainsKey(lease.NetworkKey))
        {
            return null;
        }

        var result = await _tuner
            .FindBestAsync(lease, Isp, checkUnfilteredFirst: true, cancellationToken)
            .ConfigureAwait(false);

        if (result.Winner is not null && lease.TryWrite(result.Winner))
        {
            RecordNetworkResult(lease, result.Winner, success: true, result.NetworkWasAlreadyOpen);
            SetState(
                ProtectionState.Running,
                result.NetworkWasAlreadyOpen
                    ? "Bu ağda engel yok · koruma bekleme modunda"
                    : $"Koruma etkin · {result.Winner.Name}");
        }
        else if (result.Winner is null && lease.IsCurrent)
        {
            SetState(ProtectionState.Degraded, "Çalışan bir yöntem bulunamadı");
        }

        if (!lease.IsCurrent)
        {
            return result;
        }

        await VerifyAsync(cancellationToken).ConfigureAwait(false);
        return result;
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

            // The check took seconds, and the user may have moved networks inside them.
            // A result is only ever shown against the network it was measured on.
            if (string.Equals(Network.Key, network.Key, StringComparison.Ordinal))
            {
                LastHotspotDiagnostics = result;
                LastHotspotFailure = null;
                Changed?.Invoke();
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Kept so the card can say the check did not finish, rather than silently
            // continuing to show the previous network's numbers as if they were fresh.
            if (string.Equals(Network.Key, network.Key, StringComparison.Ordinal))
            {
                LastHotspotFailure = ex.Message;
                Changed?.Invoke();
            }

            throw;
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
        ReportSave(_store.Save(Settings), "ayarlar");

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
        Settings.RememberVodafoneNetwork(network);
        Settings.VodafoneModeEnabled = true;
        Settings.HotspotDiagnostics = true;
        ReportSave(_store.Save(Settings), "ayarlar");
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
        ReportSave(_store.Save(Settings), "ayarlar");
        CancelHotspotDiagnostics();
        Changed?.Invoke();
    }

    /// <summary>
    /// Remembers the network we are on now, without changing whether the mode is on.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EnableVodafoneModeHere"/> because they are separate
    /// intentions. Registering was only reachable by enabling, so a user already running
    /// the mode who moved to a new network was told to switch it off and on again to
    /// record the network they were sitting on - two state changes and a diagnostics
    /// cancellation to achieve a list insertion.
    /// </remarks>
    /// <returns>False when there is no network to remember, or it is already known.</returns>
    public bool RememberCurrentVodafoneNetwork()
    {
        var network = NetworkFingerprint.Capture();
        if (!network.IsOnline)
        {
            throw new InvalidOperationException("Şu anda bir ağa bağlı değilsiniz.");
        }

        Network = network;
        if (Settings.VodafoneNetworkRegistered(network))
        {
            // Already known, but the session identity may have moved on. Writing it back
            // keeps the entry describing this connection rather than an expired one.
            if (Settings.RefreshVodafoneNetworkIdentity(network))
            {
                ReportSave(_store.Save(Settings), "ayarlar");
                Changed?.Invoke();
            }

            return false;
        }

        Settings.RememberVodafoneNetwork(network);

        ReportSave(_store.Save(Settings), "ayarlar");
        Changed?.Invoke();
        return true;
    }

    /// <summary>Forgets one safe per-network registration.</summary>
    public void ForgetVodafoneNetwork(string key)
    {
        if (!Settings.ForgetVodafoneNetwork(key))
        {
            return;
        }

        ReportSave(_store.Save(Settings), "ayarlar");
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
        ReportSave(_store.Save(Settings), "ayarlar");
        Changed?.Invoke();

        return migration;
    }

    /// <summary>
    /// Everything that happens when the machine moves to a different network.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the transition can be driven in a test: the real
    /// entry point is a Windows notification, and the behaviour that matters here - the
    /// hotspot checks still running with the engine stopped - cannot be reached any
    /// other way off Windows.
    /// </remarks>
    internal void OnNetworkChanged(NetworkFingerprint fingerprint)
    {
        AdoptNetwork(fingerprint);

        // A different network means the previous diagnostics describe a link that is no
        // longer under us, so the panel goes back to "not run here yet".
        LastHotspotDiagnostics = null;
        LastHotspotFailure = null;
        Changed?.Invoke();

        // The checks come first and are not gated on the engine's lifetime. They are a
        // description of the connection, not part of protecting it, and tying them to
        // the engine token meant that with protection stopped - which is when somebody
        // is most likely to be looking at why their hotspot is not working - moving
        // networks did nothing at all. CreateHotspotDiagnosticsCancellation still links
        // the engine's token whenever there is a running engine to shut them down with.
        if (ShouldRunHotspotDiagnostics(Settings, fingerprint))
        {
            var diagnostics = CreateHotspotDiagnosticsCancellation(CancellationToken.None);
            // Always enter the delegate so its finally block disposes the linked source,
            // even if shutdown cancels it between creation and queueing.
            _ = Task.Run(() => RunHotspotDiagnosticsOnTransitionAsync(fingerprint, diagnostics));
        }
        else
        {
            CancelHotspotDiagnostics();
        }

        var token = _lifetime?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested)
        {
            // No engine to re-tune. Everything above has already run.
            return;
        }

        // Re-tuning happens off the event thread so the OS notification returns at once.
        // Tracked and started with CancellationToken.None: a task the scheduler never ran
        // because its token was already cancelled leaves teardown waiting on something
        // that will not run, and the run's own cancellation is handled inside instead.
        _networkWork = _background.Track(Task.Run(
            async () =>
            {
                try
                {
                    await TuneAsync(
                            StrategyWorkKind.Automatic,
                            $"'{fingerprint.DisplayName}' ağına geçildi",
                            token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    AppLog.Error("Ağ değişikliği sonrası ayarlama başarısız", ex);
                }
            },
            CancellationToken.None));
    }

    internal static bool ShouldRunHotspotDiagnostics(AppSettings settings, NetworkFingerprint fingerprint)
        => settings.HotspotDiagnostics
            && (!settings.VodafoneModeEnabled
                || settings.VodafoneModeNetworks.Count == 0
                || settings.VodafoneNetworkRegistered(fingerprint));

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
    /// <remarks>
    /// A manual run through the same coordinator every automatic one uses, so the button,
    /// the tray menu and the CLI cannot end up sweeping alongside the timer: this one
    /// supersedes whatever the app had started for itself.
    /// </remarks>
    public Task<TuningResult?> RetuneAsync(CancellationToken cancellationToken = default)
    {
        if (_engine is null || _tuner is null)
        {
            return Task.FromResult<TuningResult?>(null);
        }

        return _strategies.RunAsync<TuningResult?>(
            StrategyWorkKind.Manual,
            "elle yeniden ayarlama",
            RetuneAsync,
            coalescedResult: null,
            cancellationToken);
    }

    private async Task<TuningResult?> RetuneAsync(StrategyLease lease, CancellationToken cancellationToken)
    {
        if (_tuner is null)
        {
            return null;
        }

        // Persisted straight away: a sweep that ends without a winner used to leave the
        // discarded profile on disk, so the next launch started on the very recipe the
        // user had just asked us to stop using.
        if (Settings.Networks.Remove(lease.NetworkKey))
        {
            ReportSave(_store.SaveNetworks(Settings), "ağ profilleri");
        }

        // The operator is settled again first. Without this, switching back to automatic
        // detection left the sweep ordered by the profile the user had just deselected.
        await ResolveIspAsync(cancellationToken).ConfigureAwait(false);

        var result = await _tuner
            .FindBestAsync(lease, Isp, checkUnfilteredFirst: true, cancellationToken)
            .ConfigureAwait(false);

        if (result.Winner is not null && lease.TryWrite(result.Winner))
        {
            RecordNetworkResult(lease, result.Winner, success: true, result.NetworkWasAlreadyOpen);
            SetState(ProtectionState.Running, $"Koruma etkin · {result.Winner.Name}");
        }
        else if (result.Winner is null && lease.IsCurrent)
        {
            SetState(ProtectionState.Degraded, "Çalışan bir yöntem bulunamadı");
        }

        if (!lease.IsCurrent)
        {
            return result;
        }

        await VerifyAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public void ApplyScope(ProtectionScope scope)
    {
        Settings.Scope = scope;
        _matcher.Scope = scope;
        ReportSave(_store.Save(Settings), "ayarlar");
        SetState(State, StatusDetail);
        AppLog.Info($"Koruma kapsamı: {DescribeScope(scope)}");
    }

    public void ApplyManualStrategy(string? strategyId)
    {
        Settings.ManualStrategyId = strategyId;
        ReportSave(_store.Save(Settings), "ayarlar");

        if (_engine is not null && StrategyLibrary.Find(strategyId) is { } strategy)
        {
            // Through the coordinator like every other write, and as a supersede: the
            // user picking a recipe by hand outranks a sweep that is still measuring
            // candidates, whose restore on the way out would otherwise undo this.
            _strategies.ApplyImmediate("elle seçilen yöntem", strategy);
            SetState(State, $"Koruma etkin · {strategy.Name}");
        }
    }

    public void ApplyManualIsp(string? ispProfileId)
    {
        Settings.ManualIspProfileId = ispProfileId;
        ReportSave(_store.Save(Settings), "ayarlar");

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
        ReportSave(_store.Save(Settings), "ayarlar");

        if (_engine is not null)
        {
            _engine.BlockQuicHandshakes = blockQuic;
        }
    }

    public void SaveSettings()
    {
        ApplySettingsToMatcher();
        ReportSave(_store.Save(Settings), "ayarlar");
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
            var outcome = await Task.Run(TeardownAsync).ConfigureAwait(false);

            ReportSave(_store.Save(Settings), "ayarlar");

            if (outcome.DnsRestoreFailure is { } dnsFailure)
            {
                // The engine is down but the machine's resolvers are still pointed at our
                // proxy, which is why the proxy is deliberately still running: reporting
                // this as a clean stop would leave the user with working name resolution
                // that disappears the moment they close the app.
                DnsRestorePending = dnsFailure.Message;
                SetState(ProtectionState.Stopped, $"Koruma kapalı · DNS geri yüklenemedi: {dnsFailure.Message}");
                throw new InvalidOperationException(
                    $"Koruma durduruldu, ancak DNS ayarları geri yüklenemedi: {dnsFailure.Message}",
                    dnsFailure);
            }

            DnsRestorePending = null;
            SetState(ProtectionState.Stopped, "Koruma kapalı");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Set when a stop could not put the machine's DNS back, cleared when one could.
    /// </summary>
    /// <remarks>
    /// The recovery snapshot is kept on disk and the loopback proxy is left running, so
    /// names still resolve; the separate recovery process and the watchdog handle the
    /// rest. What must not happen is the app reporting a clean shutdown over it.
    /// </remarks>
    public string? DnsRestorePending { get; private set; }

    /// <summary>
    /// Puts everything the start built back the way it was. Each step stands on its own
    /// so a start that fell over half way through can call it with the same result.
    /// </summary>
    /// <remarks>Expects the gate to be held, and never touches it.</remarks>
    private async Task<TeardownOutcome> TeardownAsync()
    {
        // Everything running in the background hangs off this token, so it goes first:
        // nothing should still be reaching for the objects about to be disposed.
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        // The engine is going away, so every lease handed out against it is invalid from
        // here on - including the ones held by work that has not noticed the cancellation
        // yet, and which would otherwise write its findings onto the engine that replaces
        // it during a quick stop/start.
        _strategies.EndSession();

        CancelHotspotDiagnostics();

        // Asking is not finishing. Waiting here is what stops a probe that is still in
        // flight from reaching a disposed resolver; the budget is short because a task
        // wedged in a kernel call must delay shutdown by seconds rather than forever, and
        // anything still running has already lost the right to write anything.
        if (!await _background.DrainAsync(TeardownBudget).ConfigureAwait(false))
        {
            AppLog.Warning(
                $"Arka plan işleri {TeardownBudget.TotalSeconds:F0} saniyede bitmedi; kapatmaya devam ediliyor.");
        }

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

        _portMap?.Dispose();
        _portMap = null;

        // DNS must go back before the proxy dies, or the machine is left pointing
        // at a socket nobody is listening on.
        Exception? dnsRestoreFailure = null;
        if (_dnsConfigurator is not null)
        {
            try
            {
                await _dnsConfigurator.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Kept and reported rather than thrown from here. Throwing abandoned the
                // rest of the teardown, so the resolver and the tester stayed live under
                // a service that said it was stopping, and the caller got an exception
                // with a half dismantled object behind it.
                dnsRestoreFailure = ex;
                AppLog.Error("DNS ayarları geri yüklenemedi", ex);
            }
        }

        if (dnsRestoreFailure is null)
        {
            if (_dnsProxy is not null)
            {
                await _dnsProxy.DisposeAsync().ConfigureAwait(false);
                _dnsProxy = null;
            }

            _resolver?.Dispose();
            _resolver = null;
            _tester = null;
        }
        else
        {
            // The adapters still point at 127.0.0.1, so closing the listener here would
            // take name resolution off the machine entirely - a far worse outcome than a
            // proxy that outlives the engine until the restore can be retried.
            AppLog.Warning(
                "Yerel DNS sunucusu açık bırakıldı: bağdaştırıcılar hâlâ 127.0.0.1 adresine bakıyor.");
            _tester = null;
        }

        _tuner = null;
        _dnsConfigurator = dnsRestoreFailure is null ? null : _dnsConfigurator;
        return new TeardownOutcome(dnsRestoreFailure);
    }

    /// <summary>How long a stop waits for background work before carrying on without it.</summary>
    private static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(5);

    /// <summary>What a teardown could not finish.</summary>
    private readonly record struct TeardownOutcome(Exception? DnsRestoreFailure);

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

    /// <summary>
    /// Writes what a run learned into the profile of the network that run was measuring.
    /// </summary>
    /// <remarks>
    /// Keyed off the lease rather than off <see cref="Network"/>. Reading the live field
    /// at write time is what let a sweep begun on one network file its verdict under the
    /// key of the network the machine had since moved to - the numbers were real, the
    /// heading was not, and the next launch started on a recipe measured somewhere else.
    /// </remarks>
    private void RecordNetworkResult(StrategyLease lease, BypassStrategy strategy, bool success, bool wasUnfiltered)
    {
        if (!lease.IsCurrent)
        {
            AppLog.Info(
                $"network.stale: '{lease.Reason}' işi '{lease.NetworkKey}' ağının profilini yazmak istedi; "
                + "ağ değiştiği için yazılmadı.");
            return;
        }

        var existing = Settings.Networks.GetValueOrDefault(lease.NetworkKey);

        Settings.Networks[lease.NetworkKey] = new NetworkProfile
        {
            Key = lease.NetworkKey,
            DisplayName = Network.DisplayName,
            IspProfileId = Isp.Id,
            StrategyId = strategy.Id,
            WasUnfiltered = wasUnfiltered,
            LastVerified = DateTimeOffset.UtcNow,
            SuccessCount = (existing?.SuccessCount ?? 0) + (success ? 1 : 0),
            FailureCount = (existing?.FailureCount ?? 0) + (success ? 0 : 1),
        };

        ReportSave(_store.SaveNetworks(Settings), "ağ profilleri");
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

    /// <summary>
    /// The last settings write that did not land, or null when the file is current.
    /// </summary>
    /// <remarks>
    /// The store used to swallow write failures, so a machine whose profile directory was
    /// read-only showed a settings screen that quietly reverted itself on every launch.
    /// The setting still applies for this session - the user asked for it - but they are
    /// now told it is not saved, and can ask for it to be written again.
    /// </remarks>
    public ConfigSaveResult? LastSaveFailure { get; private set; }

    /// <summary>Raised when a save starts or stops failing, never on every repeat.</summary>
    public event Action<ConfigSaveResult?>? SaveStatusChanged;

    private readonly Lock _saveStatusGate = new();

    /// <summary>
    /// Records the outcome of a save and notifies only when the answer changed.
    /// </summary>
    /// <remarks>
    /// The periodic re-check writes profiles, so reporting every failure would put an
    /// identical banner on screen every few minutes for as long as the disk stayed full.
    /// A repeat of the same failure is silent; recovering from one is not.
    /// </remarks>
    private void ReportSave(ConfigSaveResult result, string what)
    {
        bool changed;
        lock (_saveStatusGate)
        {
            var previous = LastSaveFailure;
            if (result.Succeeded)
            {
                changed = previous is not null;
                LastSaveFailure = null;
            }
            else
            {
                changed = previous is null || previous.Value.Failure != result.Failure;
                LastSaveFailure = result;
            }
        }

        if (!changed)
        {
            return;
        }

        if (result.Succeeded)
        {
            AppLog.Info($"config.save: {what} yeniden kaydedilebiliyor.");
        }
        else
        {
            AppLog.Warning($"config.save: {what} kaydedilemedi ({result.Detail}). {result.Describe()}");
        }

        SaveStatusChanged?.Invoke(LastSaveFailure);
        Changed?.Invoke();
    }

    /// <summary>
    /// Tries the failed write again, on the user's request.
    /// </summary>
    /// <returns>True when the settings are on disk again.</returns>
    public bool RetrySave()
    {
        var result = _store.Save(Settings);
        ReportSave(result, "ayarlar");
        return result.Succeeded;
    }

    /// <summary>
    /// Takes a consistent, masked picture of the app as it is right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads state that already exists and starts nothing: no probe, no load test, no
    /// connection change. A user asking what their machine looks like must be answered
    /// about the machine they asked about, not about one the question disturbed.
    /// </para>
    /// <para>
    /// Every identifying value goes through the redactor here, at the one point where the
    /// snapshot is assembled, so nothing downstream can leak one by forgetting to call it.
    /// Where there is no measurement the row says "ölçülmedi" rather than a zero.
    /// </para>
    /// </remarks>
    public DiagnosticSnapshot CaptureDiagnostics()
    {
        var redactor = new DiagnosticRedactor();
        var network = Network;

        // Registered first so these values are already aliased when the log tail and the
        // free-text rows below are masked.
        redactor.Register(RedactionKind.Network, network.Ssid, network.DisplayName, network.Key);
        redactor.Register(RedactionKind.Bssid, network.Bssid);
        redactor.Register(RedactionKind.Mac, network.GatewayMac);
        redactor.Register(RedactionKind.Address, network.GatewayAddress);
        redactor.Register(RedactionKind.Adapter, network.AdapterName, network.AdapterId);
        redactor.Register(RedactionKind.Host, Settings.Latency.TargetHost, Settings.Latency.PinnedEndpoint);
        redactor.Register(RedactionKind.Host, [.. Settings.ExtraDomains]);
        redactor.Register(RedactionKind.Host, [.. Settings.ExcludedDomains]);
        redactor.Register(RedactionKind.Network, [.. Settings.VodafoneModeNetworks.Select(n => n.Ssid)]);
        redactor.Register(RedactionKind.Network, [.. Settings.VodafoneModeNetworks.Select(n => n.DisplayName)]);

        var networkAlias = redactor.Alias(RedactionKind.Network, network.Key) ?? "ölçülmedi";

        return new DiagnosticSnapshot
        {
            GeneratedAt = DateTimeOffset.Now,
            AppVersion = BuildVersion(),
            OperatingSystem = Environment.OSVersion.VersionString,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Elevated = Elevation.IsElevated,
            RemoteSession = SessionKind.IsRemoteSession(),
            EngineSession = _strategies.Session,
            NetworkAlias = networkAlias,
            Sections =
            [
                ProtectionSection(redactor),
                NetworkSection(redactor, network),
                DnsSection(redactor),
                LatencySection(redactor),
                HotspotSection(redactor),
            ],
            LogExcerpt = DiagnosticReportWriter.MaskedLogTail(redactor, AppLog.Snapshot()),
            MaskedValues = redactor.MaskedValues,
            DroppedLogLines = AppLog.DroppedFileEntries,
        };
    }

    private static string BuildVersion()
        => typeof(ProtectionService).Assembly.GetName().Version?.ToString() ?? "bilinmiyor";

    private static string Measured(string? value) => string.IsNullOrWhiteSpace(value) ? "ölçülmedi" : value;

    private ReportSection ProtectionSection(DiagnosticRedactor redactor) => new(
        "Koruma",
        [
            new("Durum", State.ToString()),
            new("Ayrıntı", Measured(redactor.Redact(StatusDetail))),
            new("Kapsam", DescribeScope(Settings.Scope)),
            new("Etkin yöntem", State == ProtectionState.Stopped ? "ölçülmedi" : Strategy.Name),
            new("Yöntem elle seçildi", Settings.ManualStrategyId is { Length: > 0 } ? "evet" : "hayır"),
            new("Operatör", Isp.DisplayName),
            new("Operatör algılandı", Detection?.WasAutomatic == true ? "otomatik" : "elle seçildi"),
            new("QUIC engelleme", Settings.BlockQuicHandshakes ? "açık" : "kapalı"),
            // Invariant formatting throughout the report: it is a file the user sends on,
            // and a decimal comma that depends on the machine that wrote it makes two
            // reports of the same fault impossible to line up.
            new("Son doğrulama", LastProbe is null
                ? "ölçülmedi"
                : FormattableString.Invariant(
                    $"{DescribeOutcome(LastProbe.Outcome)} · {LastProbe.Elapsed.TotalMilliseconds:F0} ms")),
            new("Motor sayaçları", Stats is { } stats
                ? FormattableString.Invariant(
                    $"incelendi {stats.Inspected}, yeniden yazıldı {stats.Rewritten}, hata {stats.Errors}")
                : "ölçülmedi"),
            new("Korunan alan adı sayısı", ProtectedDomainCount.ToString()),
            new("Ayar kaydı", LastSaveFailure is { } failure ? failure.Describe() : "sorunsuz"),
            new("Bekleyen DNS geri yükleme", Measured(DnsRestorePending)),
        ]);

    private static ReportSection NetworkSection(DiagnosticRedactor redactor, NetworkFingerprint network) => new(
        "Ağ",
        [
            new("Ağ", redactor.Alias(RedactionKind.Network, network.Key) ?? "ölçülmedi"),
            new("Görünen ad", redactor.Alias(RedactionKind.Network, network.DisplayName) ?? "ölçülmedi"),
            new("Bağdaştırıcı", redactor.Alias(RedactionKind.Adapter, network.AdapterName) ?? "ölçülmedi"),
            new("Bağdaştırıcı türü", network.AdapterType.ToString()),
            new("Ağ geçidi", redactor.Alias(RedactionKind.Address, network.GatewayAddress) ?? "ölçülmedi"),
            new("Çevrim içi", network.IsOnline ? "evet" : "hayır"),
        ]);

    private ReportSection DnsSection(DiagnosticRedactor redactor)
    {
        var rows = new List<KeyValuePair<string, string>>
        {
            new("Seçilen mod", Settings.DnsMode.ToString()),
            new("Etkin mod", ActiveDnsMode.ToString()),
            new("Son cevaplayan sağlayıcı", Measured(DnsProviderInUse)),
            new("Doğrulanmış sağlıklı sağlayıcı", Measured(_resolver?.VerifiedProvider)),
            new("Sunulan sorgu", _dnsProxy is null ? "ölçülmedi" : DnsQueriesServed.ToString()),
            new("Önbellek isabeti", _dnsProxy is null ? "ölçülmedi" : DnsCacheHits.ToString()),
            new("Kesilen yanıt", _dnsProxy is null ? "ölçülmedi" : _dnsProxy.TruncatedAnswers.ToString()),
            new("Ağ değişiminde atılan yanıt", _dnsProxy is null ? "ölçülmedi" : _dnsProxy.CrossNetworkDrops.ToString()),
            new("Birleştirilen sorgu", _resolver is null ? "ölçülmedi" : _resolver.CoalescedQueries.ToString()),
        };

        if (_resolver is null)
        {
            rows.Add(new("Sağlayıcı sağlığı", "ölçülmedi"));
            return new ReportSection("Ad çözümleme", rows);
        }

        foreach (var endpoint in _resolver.EndpointStatus())
        {
            var state = endpoint.LastFailure is null
                ? FormattableString.Invariant($"sağlıklı · {endpoint.LastLatencyMs} ms")
                : endpoint.PenaltyRemaining is { } remaining
                    ? FormattableString.Invariant(
                        $"{redactor.Redact(endpoint.LastFailure)} · {remaining.TotalSeconds:F0} sn sonra yeniden denenir")
                    : redactor.Redact(endpoint.LastFailure);

            rows.Add(new($"Sağlayıcı: {endpoint.Provider}", state));
        }

        return new ReportSection("Ad çözümleme", rows);
    }

    private ReportSection LatencySection(DiagnosticRedactor redactor)
    {
        var status = LatencyStatus;
        var rows = new List<KeyValuePair<string, string>>
        {
            new("Mod", status.ModeEnabled ? "açık" : "kapalı"),
            new("Durum", status.State.ToString()),
            new("Özet", Measured(redactor.Redact(status.Headline))),
            new("Hedef", Measured(redactor.Redact(status.Target))),
            new("Ölçüm aracı", Measured(status.Protocol)),
            new("Yalnızca yol referansı", status.RouteReferenceOnly ? "evet" : "hayır"),
            new("Bağdaştırıcı", redactor.Alias(RedactionKind.Adapter, status.AdapterName) ?? "ölçülmedi"),
        };

        AppendMeasurement(rows, "Boştayken (önce)", redactor, status.IdleBefore ?? status.Idle);
        AppendMeasurement(rows, "Boştayken (sonra)", redactor, status.IdleAfter);
        AppendMeasurement(rows, "Yük altında (yükleme)", redactor, status.UploadLoaded);
        AppendMeasurement(rows, "Yük altında (yükleme, sonra)", redactor, status.UploadLoadedAfter);
        AppendMeasurement(rows, "Yük altında (indirme)", redactor, status.DownloadLoaded);

        rows.Add(new("Doğrulanmış kazanç", status.Improvement is { } gain
            ? FormattableString.Invariant($"{status.ImprovedMetric ?? "ortanca"} · ortanca {gain.MedianMs:F1} ms, ")
                + FormattableString.Invariant($"p95 {gain.P95Ms:F1} ms, p99 {gain.P99Ms:F1} ms, ")
                + FormattableString.Invariant($"dalgalanma {gain.JitterMs:F1} ms")
                + (gain.LossPercent is { } loss
                    ? FormattableString.Invariant($", kayıp {loss:F1}%")
                    : ", kayıp ölçülmedi")
            : "ölçülmedi"));
        rows.Add(new("Ham başlangıç-son farkı (nedensel değil)", status.BaselineComparison is { } raw
            ? FormattableString.Invariant($"ortanca {raw.MedianMs:F1} ms")
            : "ölçülmedi"));
        rows.Add(new("Uygulanan değişiklikler", status.Applied.Count == 0
            ? "yok"
            : string.Join(" · ", status.Applied.Select(redactor.Redact))));
        rows.Add(new("Elenen adaylar", status.Rejected.Count == 0
            ? "yok"
            : string.Join(" · ", status.Rejected.Select(r => redactor.Redact(r.ToString())))));
        rows.Add(new("Notlar", status.Notices.Count == 0
            ? "yok"
            : string.Join(" · ", status.Notices.Select(redactor.Redact))));
        rows.Add(new("Trafik Koruması", status.TrafficGuard is null
            ? "ölçülmedi"
            : redactor.Redact(status.TrafficGuard.ToString())));
        rows.Add(new("Bir sonraki adım", Measured(redactor.Redact(status.Suggestion))));

        return new ReportSection("Gecikme", rows);
    }

    private static void AppendMeasurement(
        List<KeyValuePair<string, string>> rows,
        string label,
        DiagnosticRedactor redactor,
        LatencyMeasurement? measurement)
    {
        if (measurement is null)
        {
            rows.Add(new(label, "ölçülmedi"));
            return;
        }

        // Loss is only ever printed when the instrument could measure it. Writing a zero
        // for an instrument that does not count attempts would be inventing a result.
        var loss = measurement.PacketLossPercent is { } percent
            ? FormattableString.Invariant($", kayıp {percent:F1}%")
            : ", kayıp ölçülmedi";

        rows.Add(new(
            label,
            FormattableString.Invariant($"{measurement.MedianRttMs:F1} ms ortanca, {measurement.P95RttMs:F1} ms p95, ")
                + FormattableString.Invariant($"{measurement.P99RttMs:F1} ms p99, {measurement.JitterMs:F1} ms dalgalanma{loss} ")
                + FormattableString.Invariant($"· {measurement.RemoteReplies}/{measurement.RemoteAttempts} örnek ")
                + $"· {measurement.Source} · {redactor.Alias(RedactionKind.Host, measurement.RemoteEndpoint)} "
                + FormattableString.Invariant($"· {measurement.MeasuredAt:yyyy-MM-dd HH:mm:ss zzz}")));
    }

    private ReportSection HotspotSection(DiagnosticRedactor redactor)
    {
        var rows = new List<KeyValuePair<string, string>>
        {
            new("Vodafone modu", Settings.VodafoneModeEnabled ? "açık" : "kapalı"),
            new("Otomatik tanılama", Settings.HotspotDiagnostics ? "açık" : "kapalı"),
            new("Kayıtlı ağ sayısı", Settings.VodafoneModeNetworks.Count.ToString()),
            new("Bu ağ kayıtlı", Settings.VodafoneNetworkRegistered(Network) ? "evet" : "hayır"),
        };

        if (LastHotspotDiagnostics is not { } result)
        {
            rows.Add(new("Son tanılama", Measured(redactor.Redact(LastHotspotFailure)) is { } failure && failure != "ölçülmedi"
                ? $"başarısız: {failure}"
                : "ölçülmedi"));
            return new ReportSection("Mobil bağlantı", rows);
        }

        rows.Add(new("Son tanılama", FormattableString.Invariant($"{result.CompletedAt:yyyy-MM-dd HH:mm:ss zzz}")));
        rows.Add(new("Ölçülen ağ", redactor.Alias(RedactionKind.Network, result.NetworkKey) ?? "ölçülmedi"));
        rows.Add(new("İnternet", result.HasInternet ? "var" : "yok"));
        rows.Add(new("DNS", result.DnsWorks ? "çalışıyor" : "çalışmıyor"));
        rows.Add(new("IPv4", result.Ipv4Works ? "çalışıyor" : "çalışmıyor"));
        rows.Add(new("IPv6", result.Ipv6Works ? "çalışıyor" : result.HasIpv6 ? "adres var, çalışmıyor" : "yok"));
        rows.Add(new("Ortanca RTT", result.MedianRttMs is { } rtt
            ? FormattableString.Invariant($"{rtt:F1} ms")
            : "ölçülmedi"));

        return new ReportSection("Mobil bağlantı", rows);
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
        ReportSave(_store.Save(Settings), "ayarlar");

        if (_discovery is not null)
        {
            _discovery.Enabled = enabled;
        }
    }

    public void ApplyRecheckInterval(int seconds)
    {
        Settings.RecheckIntervalSeconds = seconds < 0 ? 0 : seconds;
        ReportSave(_store.Save(Settings), "ayarlar");
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
        StopNetworkWatch();

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Kapatma sırasında hata", ex);
        }

        // Everything long lived is registered, so this covers the network change work
        // that used to be missed here: only the two fields were waited for, and a re-tune
        // started by a transition overwrote one of them.
        if (!await _background.DrainAsync(TeardownBudget).ConfigureAwait(false))
        {
            AppLog.Warning("Kapatmada arka plan işleri zamanında bitmedi.");
        }

        // Whatever the mode says, nothing is left listening after the service is gone.
        await _flowObserver.DisposeAsync().ConfigureAwait(false);

        _latencyOptimizer.Changed -= OnLatencyChanged;
        await _latencyOptimizer.DisposeAsync().ConfigureAwait(false);
        _latencyGate.Dispose();
        _strategies.Dispose();
        _lifetime?.Dispose();
        _gate.Dispose();
    }
}
