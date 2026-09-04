using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using DpiBypass.App.Infrastructure;
using DpiBypass.Core;
using DpiBypass.Core.Apps;
using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Logging;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;
using DpiBypass.Core.Startup;

namespace DpiBypass.App.ViewModels;

public sealed record IspOption(string? Id, string Display)
{
    public override string ToString() => Display;
}

public sealed record StrategyOption(string? Id, string Display, string Description)
{
    public override string ToString() => Display;
}

public sealed record ScopeOption(ProtectionScope Scope, string Title, string Description, bool IsRecommended = false);

/// <summary>Where a protected domain came from, so the UI can say so.</summary>
public enum DomainOrigin
{
    BuiltIn,
    Learned,
    Manual,
}

public sealed record DomainEntry(string Domain, DomainOrigin Origin)
{
    public string OriginLabel => Origin switch
    {
        DomainOrigin.Learned => "kendiliğinden bulundu",
        DomainOrigin.Manual => "elle eklendi",
        _ => "yerleşik liste",
    };

    public bool CanRemove => true;
}

/// <summary>One remembered network, and whether it is the one we are on now.</summary>
public sealed record VodafoneNetworkEntry(string Key, string Display, bool IsCurrent)
{
    /// <summary>Marks the current network in the list, in words rather than by colour.</summary>
    public string Marker => IsCurrent ? "Şu anki ağ" : string.Empty;
}

/// <summary>One choice in the latency target picker.</summary>
public sealed record LatencyTargetOption(LatencyTargetKind Kind, string Display, string Description)
{
    public override string ToString() => Display;
}

/// <summary>A change that was not kept, with the reason and whether it was measured.</summary>
public sealed record LatencyRejectionEntry(string Change, string Reason, bool WasMeasured)
{
    /// <summary>"Ölçüldü" or "Denenmedi": the two are different facts and read as such.</summary>
    public string Kind => WasMeasured ? "Ölçüldü" : "Denenmedi";
}

/// <summary>
/// One short result card on the main screen: a number, or an honest absence of one.
/// </summary>
/// <remarks>
/// <see cref="Measured"/> exists so a card can never print 0 for something nobody
/// measured. Zero milliseconds of jitter and "we did not measure jitter" look identical
/// once they are both formatted as a number, and only one of them is good news.
/// </remarks>
public sealed record LatencyCard(string Title, string Value, bool Measured, string Note = "")
{
    public static LatencyCard NotMeasured(string title, string note = "Ölçülmedi")
        => new(title, "—", false, note);
}

/// <summary>One lane of the engine, and how far it got on this run.</summary>
public sealed record LatencyLaneEntry(string Title, string Detail, string State);

/// <summary>One endpoint discovery found for the chosen application.</summary>
public sealed record LatencyEndpointEntry(string Key, string Display, string Why)
{
    public override string ToString() => Display;
}

/// <summary>Which trade-off the send-rate cap search should optimise for.</summary>
public sealed record TrafficGuardModeOption(TrafficGuardMode Mode, string Display, string Description)
{
    public override string ToString() => Display;
}

public sealed record RecheckOption(int Seconds, string Display)
{
    public override string ToString() => Display;
}

public sealed record DnsOption(DnsMode Mode, string Display, string Description)
{
    public override string ToString() => Display;
}

/// <summary>Everything the main window binds to.</summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>How many lines the log page keeps. The file keeps everything.</summary>
    private const int LogCapacity = 500;

    private readonly ProtectionService _service;
    private readonly AutoStartManager _autoStart;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>Lines waiting to be handed to the UI thread, oldest first.</summary>
    private readonly ConcurrentQueue<string> _pendingLogLines = new();

    private int _logDrainQueued;

    private string _statusHeadline = "Koruma kapalı";
    private string _statusDetail = "Başlatmak için düğmeye dokunun.";
    private string _statusSeverity = "off";
    private bool _isRunning;
    private bool _isBusy;
    private ProtectionState _protectionState = ProtectionState.Stopped;
    private string _networkName = "-";
    private string _ispSummary = "-";
    private string _strategySummary = "-";
    private string _probeSummary = "Henüz test edilmedi.";
    private string _tuningStatus = string.Empty;
    private string _engineCounters = "-";
    private string _dnsSummary = "-";
    private string _discordSummary = "Aranıyor…";
    private string _browserSummary = "Aranıyor…";
    private IspOption _selectedIsp;
    private StrategyOption _selectedStrategy;
    private DnsOption _selectedDns;
    private ScopeOption _selectedScope;
    private RecheckOption _selectedRecheck;
    private string _newDomain = string.Empty;
    private DomainEntry? _selectedDomain;
    private bool _isHotspotBusy;
    private string _hotspotStatusLine = "Henüz çalıştırılmadı.";
    private bool _lowLatencyMode;
    private bool _isLatencyBusy;
    private string _latencyStatusLine = "Kapalı.";
    private string _latencyHeadline = "Kapalı";
    private string _latencyStatusSeverity = "off";
    private string _latencyTargetSummary = "Genel internet referansı — oyun sunucusu değildir";
    private string _latencyPathSummary = string.Empty;
    private string _latencyGuardSummary = "Traffic Guard kapalı.";
    private LatencyTargetOption _selectedLatencyTarget;
    private string _latencyCustomTarget = string.Empty;
    private string? _selectedLatencyProcess;
    private string _latencyTargetError = string.Empty;
    private string _latencyStageTitle = "Kapalı";
    private string _latencyStageInstruction = string.Empty;
    private string _latencyStageRate = string.Empty;
    private string _latencyStageRemaining = string.Empty;
    private string _latencyStageData = string.Empty;
    private bool _isDeepTestRunning;
    private bool _latencyStageCanCancel = true;
    private LatencyEndpointEntry? _selectedLatencyEndpoint;
    private TrafficGuardModeOption _selectedGuardMode;
    private string _latencyResultSummary = string.Empty;
    private string _latencyDataUsedSummary = string.Empty;
    private string _latencySuggestion = string.Empty;
    private string _latencyPrimaryAction = "Bağlantımı analiz et";
    private string _latencyMeasuredAt = string.Empty;
    private bool _isLatencyTargetValid = true;
    private HotspotStatusView _hotspotView = HotspotStatusView.Empty;
    private string _domainStatus = string.Empty;
    private string _domainStatusSeverity = string.Empty;
    private string _domainFilter = string.Empty;
    private VodafoneNetworkEntry? _selectedVodafoneNetwork;
    private string _vodafoneStatusLine = "Kapalı.";
    private string _hotspotStatusSeverity = "off";
    private string _hotspotSuggestion = string.Empty;
    private string _hotspotCheckedAt = string.Empty;
    private string _hotspotReport = string.Empty;
    private bool _isTuning;
    private bool _suppressPersist;

    public MainViewModel(ProtectionService service, Dispatcher dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
        _autoStart = new AutoStartManager(log: AppLog.InfoSink);

        // Created before the first RefreshDomains so its raise finds the view in
        // place. The view reads Domains and never owns it.
        FilteredDomains = CollectionViewSource.GetDefaultView(Domains);
        FilteredDomains.Filter = FilterDomain;

        IspOptions = [new IspOption(null, "Otomatik algıla (önerilen)")];
        foreach (var profile in IspCatalog.All)
        {
            IspOptions.Add(new IspOption(profile.Id, profile.DisplayName));
        }

        StrategyOptions =
        [
            new StrategyOption(null, "Otomatik seç (önerilen)", "Ağa göre en hızlı çalışan yöntem ölçülerek seçilir."),
        ];
        foreach (var strategy in StrategyLibrary.All)
        {
            StrategyOptions.Add(new StrategyOption(strategy.Id, strategy.Name, strategy.Description));
        }

        DnsOptions =
        [
            new DnsOption(
                DnsMode.EncryptedLoopback,
                "Şifreli DNS (DoH) · önerilen",
                "Cloudflare birincil, Google ve Quad9 yedek. Sorgular HTTPS içinde taşınır, sonuçlar önbelleğe alınır."),
            new DnsOption(
                DnsMode.PublicResolvers,
                "Genel çözümleyiciler (şifresiz)",
                "1.1.1.1, 8.8.8.8 ve 9.9.9.9 doğrudan kullanılır. 53 numaralı port başka bir program tarafından kullanılıyorsa uygundur."),
            new DnsOption(
                DnsMode.SystemDefault,
                "Sistem ayarına dokunma",
                "DNS ayarları değiştirilmez. Operatörünüz alan adı yanıtlarını değiştiriyorsa engel aşılamayabilir."),
        ];

        ScopeOptions =
        [
            new ScopeOption(
                ProtectionScope.DiscordOnly,
                "Yalnızca Discord",
                "Sadece Discord uygulamasının ve Discord alan adlarının trafiği işlenir. En düşük etki."),
            new ScopeOption(
                ProtectionScope.BlockedSites,
                "Engelli site listesi",
                "Discord ve Türkiye'de engellendiği bilinen diğer siteler, hangi program açarsa açsın korunur. "
                    + "Yeni engelli siteler kendiliğinden bulunup listeye eklenir.",
                IsRecommended: true),
            new ScopeOption(
                ProtectionScope.DiscordAndBrowsers,
                "Engelli siteler + tarayıcılar",
                "Listeye ek olarak tarayıcılardaki tüm siteler de korunur."),
            new ScopeOption(
                ProtectionScope.Everything,
                "Tüm sistem",
                "Bilgisayardaki bütün programların HTTPS/HTTP bağlantıları korunur."),
        ];

        RecheckOptions =
        [
            new RecheckOption(0, "Kapalı"),
            new RecheckOption(900, "15 dakikada bir"),
            new RecheckOption(1800, "30 dakikada bir"),
            new RecheckOption(3600, "Saatte bir"),
            new RecheckOption(21600, "6 saatte bir"),
        ];

        // FirstOrDefault throughout: a settings file carrying a value this build no
        // longer offers must fall back to a default, not take the window down with it.
        _selectedIsp = IspOptions.FirstOrDefault(o => o.Id == _service.Settings.ManualIspProfileId) ?? IspOptions[0];
        _selectedStrategy = StrategyOptions.FirstOrDefault(o => o.Id == _service.Settings.ManualStrategyId) ?? StrategyOptions[0];
        _selectedDns = DnsOptions.FirstOrDefault(o => o.Mode == _service.Settings.DnsMode) ?? DnsOptions[0];
        _selectedScope = ScopeOptions.FirstOrDefault(o => o.Scope == _service.Settings.Scope) ?? ScopeOptions[1];
        _selectedRecheck = RecheckOptions.FirstOrDefault(o => o.Seconds == _service.Settings.RecheckIntervalSeconds)
            ?? RecheckOptions[2];
        GuardModeOptions =
        [
            new TrafficGuardModeOption(
                TrafficGuardMode.Balanced,
                "Dengeli",
                "Kuyruklanmayı kaldırırken aktarım hızının olabildiğince çoğunu korur."),
            new TrafficGuardModeOption(
                TrafficGuardMode.LowestLatency,
                "En düşük gecikme",
                "Daha yavaş aktarımı kabul eder; ölçülen hız kaybı sonuç ekranında gösterilir."),
        ];

        LatencyTargetOptions =
        [
            new LatencyTargetOption(
                LatencyTargetKind.Reference,
                "Genel internet referansı",
                "1.1.1.1 · 8.8.8.8 · 9.9.9.9. Genel bağlantı sağlığını gösterir; oyun sunucunuz değildir."),
            new LatencyTargetOption(
                LatencyTargetKind.Application,
                "Çalışan oyun / uygulama",
                "Seçilen programın açık TCP bağlantısındaki gerçek uzak uç ölçülür. "
                    + "Yalnız UDP kullanan oyunlarda Windows uzak adresi bildirmez."),
            new LatencyTargetOption(
                LatencyTargetKind.Custom,
                "Özel sunucu (host / IP / port)",
                "Örnek: mc.sunucu.com:25565 · tcp://1.2.3.4:443 · udp://1.2.3.4:7777 (rota referansı)"),
        ];

        _lowLatencyMode = _service.Settings.LowLatencyMode;
        _latencyStatusLine = _service.LatencyResult.StatusLine;

        var latency = _service.Settings.Latency;
        _selectedLatencyTarget = LatencyTargetOptions.FirstOrDefault(option => option.Kind == latency.TargetKind)
            ?? LatencyTargetOptions[0];
        _latencyCustomTarget = latency.TargetPort is { } port
            ? $"{latency.TargetHost}:{port}"
            : latency.TargetHost ?? string.Empty;
        _selectedLatencyProcess = latency.TargetProcess;
        _selectedGuardMode = GuardModeOptions.FirstOrDefault(option => option.Mode == latency.GuardMode)
            ?? GuardModeOptions[0];

        // Gated on the service rather than only on this command: protection also starts
        // and stops from the launch path, the tray menu and the control channel, and a
        // button that offers to start something already starting does nothing when it is
        // pressed.
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => !_isBusy && !IsTransitioning);
        TestCommand = new AsyncRelayCommand(TestAsync);
        RetuneCommand = new AsyncRelayCommand(RetuneAsync, () => _isRunning && !IsTuning);
        TestAllCommand = new AsyncRelayCommand(TestAllAsync);
        // Every command that measures is gated on the target being valid as well as on the
        // card being free. Without the first half, a user who typed a bad address saw the
        // error, pressed the button anyway and got a measurement of the previous target
        // filed under the new one's name.
        LatencyTestCommand = new AsyncRelayCommand(TestLatencyAsync, CanRunLatency);
        LatencyDeepTestCommand = new AsyncRelayCommand(RunLatencyDeepTestAsync, CanRunLatency);
        LatencyRetestCommand = new AsyncRelayCommand(RetestLatencyAsync, CanRunLatency);
        LatencyRestoreCommand = new AsyncRelayCommand(RestoreLatencyAsync, () => !_isLatencyBusy);
        LatencyClearProfilesCommand = new RelayCommand(ClearLatencyProfiles);
        RefreshLatencyProcessesCommand = new AsyncRelayCommand(RefreshLatencyProcessesAsync);
        LatencyPrimaryCommand = new AsyncRelayCommand(RunLatencyPrimaryActionAsync, CanRunLatency);

        // Cancellation is no longer tied to the deep test: turning the mode on runs a
        // paired benchmark that takes minutes, and a quick test pings for several
        // seconds. Both are stoppable now, so the button follows the service.
        LatencyCancelCommand = new RelayCommand(
            CancelLatencyRun,
            () => _service.CanCancelLatencyRun);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        AddDomainCommand = new RelayCommand(AddDomain, () => !string.IsNullOrWhiteSpace(_newDomain));
        RemoveDomainCommand = new RelayCommand(RemoveSelectedDomain, () => _selectedDomain is not null);
        HotspotDiagnoseCommand = new AsyncRelayCommand(RunHotspotDiagnosticsAsync, () => !_isHotspotBusy);
        HotspotCleanupCommand = new RelayCommand(CleanUpLegacyHotspot);
        RememberVodafoneNetworkCommand = new RelayCommand(
            RememberCurrentVodafoneNetwork,
            () => _hotspotView.CanRememberThisNetwork);
        ForgetVodafoneNetworkCommand = new RelayCommand(
            ForgetSelectedVodafoneNetwork,
            () => _selectedVodafoneNetwork is not null);
        ClearDomainFilterCommand = new RelayCommand(() => DomainFilter = string.Empty, () => HasFilter);
        CopyLogCommand = new RelayCommand(CopyLogToClipboard);
        ClearLogCommand = new RelayCommand(ClearLogView);

        _service.Changed += OnServiceChanged;
        _service.LatencyStageChanged += OnLatencyStageChanged;
        _service.HostRewritten += OnHostRewritten;
        _service.TuningProgress += OnTuningProgress;
        _service.DomainLearned += OnDomainLearned;
        AppLog.Written += OnLogWritten;

        RefreshDomains();
        RefreshVodafoneNetworks();
        RefreshHotspotStatus();
        ApplyLatencyStatus(_service.LatencyStatus);

        foreach (var entry in AppLog.Snapshot())
        {
            LogLines.Add(entry.ToString());
        }

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshCounters();
        _refreshTimer.Start();

        // Both are discarded on purpose - nothing waits for them - so both have to be
        // answerable for themselves. A fault here used to disappear with the task and
        // leave the two summaries reading "Aranıyor…" for the life of the process.
        _ = LoadInstalledAppsAsync();
        _ = LoadAutoStartStateAsync();
    }

    public ObservableCollection<IspOption> IspOptions { get; }

    public ObservableCollection<StrategyOption> StrategyOptions { get; }

    public ObservableCollection<DnsOption> DnsOptions { get; }

    public ObservableCollection<ScopeOption> ScopeOptions { get; }

    public ObservableCollection<RecheckOption> RecheckOptions { get; }

    public ObservableCollection<string> LogLines { get; } = [];

    public ObservableCollection<string> ProtectedHosts { get; } = [];

    /// <summary>The full protected domain list: shipped, discovered and hand-added.</summary>
    public ObservableCollection<DomainEntry> Domains { get; } = [];

    public ObservableCollection<VodafoneNetworkEntry> VodafoneNetworks { get; } = [];

    public AsyncRelayCommand ToggleCommand { get; }

    public AsyncRelayCommand TestCommand { get; }

    public AsyncRelayCommand RetuneCommand { get; }

    public AsyncRelayCommand TestAllCommand { get; }

    public AsyncRelayCommand LatencyTestCommand { get; }

    public AsyncRelayCommand LatencyDeepTestCommand { get; }

    public AsyncRelayCommand LatencyRetestCommand { get; }

    public AsyncRelayCommand LatencyRestoreCommand { get; }

    public RelayCommand LatencyClearProfilesCommand { get; }

    /// <summary>Stops a running deep test. The run puts everything back on its way out.</summary>
    public RelayCommand LatencyCancelCommand { get; }

    /// <summary>
    /// The one button on the card. What it does follows the result.
    /// </summary>
    /// <remarks>
    /// The card used to offer five equally weighted buttons - quick test, deep test,
    /// force re-measure, clear saved results, restore everything - with nothing to say
    /// which one a person in front of an unmeasured connection should press. The engine
    /// already knows what the sensible next step is, so it names it.
    /// </remarks>
    public AsyncRelayCommand LatencyPrimaryCommand { get; }

    public AsyncRelayCommand RefreshLatencyProcessesCommand { get; }

    public ObservableCollection<LatencyTargetOption> LatencyTargetOptions { get; }

    public ObservableCollection<TrafficGuardModeOption> GuardModeOptions { get; }

    /// <summary>Endpoints discovery found for the chosen application, best first.</summary>
    public ObservableCollection<LatencyEndpointEntry> LatencyEndpoints { get; } = [];

    /// <summary>Programs with a live remote connection, refreshed on demand.</summary>
    public ObservableCollection<string> LatencyProcesses { get; } = [];

    public ObservableCollection<string> LatencyAppliedChanges { get; } = [];

    public ObservableCollection<LatencyRejectionEntry> LatencyRejectedChanges { get; } = [];

    /// <summary>The four short result cards the main screen shows.</summary>
    public ObservableCollection<LatencyCard> LatencyCards { get; } = [];

    /// <summary>What the run tried and what it did not, one row each.</summary>
    public ObservableCollection<LatencyLaneEntry> LatencyLanes { get; } = [];

    /// <summary>The Vodafone card's summary checks.</summary>
    public ObservableCollection<HotspotCheckCard> HotspotCards { get; } = [];

    /// <summary>Addresses, MTU, adapter and VPN, for the details section.</summary>
    public ObservableCollection<HotspotCheckCard> HotspotDetails { get; } = [];

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand AddDomainCommand { get; }

    public RelayCommand RemoveDomainCommand { get; }

    public AsyncRelayCommand HotspotDiagnoseCommand { get; }

    public RelayCommand HotspotCleanupCommand { get; }

    public RelayCommand ForgetVodafoneNetworkCommand { get; }

    /// <summary>Adds the network we are on now to the remembered list, in place.</summary>
    public RelayCommand RememberVodafoneNetworkCommand { get; }

    public RelayCommand ClearDomainFilterCommand { get; }

    public RelayCommand CopyLogCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public string ProductTitle => AppPaths.ProductName;

    public string AuthorLine => $"Geliştirici: {AppPaths.Author}";

    public string VersionLine
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null ? "Sürüm -" : $"Sürüm {version.ToString(3)}";
        }
    }

    public string StatusHeadline { get => _statusHeadline; private set => Set(ref _statusHeadline, value); }

    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }

    /// <summary>"ok", "warn" or "off" - drives the colour of the status indicator.</summary>
    public string StatusSeverity { get => _statusSeverity; private set => Set(ref _statusSeverity, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                Raise(nameof(ToggleCaption));
                TestCommand.RaiseCanExecuteChanged();
                RetuneCommand.RaiseCanExecuteChanged();
                TestAllCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                ToggleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// What the service is doing, so the primary button can say so.
    /// </summary>
    /// <remarks>
    /// The button used to read only "is it running", which is two states short. A start
    /// takes seconds - the driver, the resolvers, the adapters - and during all of it the
    /// button offered to start protection that was already starting. Pressing it did
    /// nothing, because the service refuses a second start, so the one control on every
    /// page looked broken exactly while the app was working hardest.
    /// </remarks>
    public ProtectionState ProtectionState
    {
        get => _protectionState;
        private set
        {
            if (Set(ref _protectionState, value))
            {
                Raise(nameof(ToggleCaption));
                Raise(nameof(IsTransitioning));
                ToggleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether protection is on its way in or out, and cannot be told otherwise.</summary>
    public bool IsTransitioning => _protectionState is ProtectionState.Starting or ProtectionState.Stopping;

    /// <summary>A strategy sweep is measuring on the network; the retune action is out.</summary>
    public bool IsTuning
    {
        get => _isTuning;
        private set
        {
            if (Set(ref _isTuning, value))
            {
                RetuneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ToggleCaption => _protectionState switch
    {
        ProtectionState.Starting => "Başlatılıyor…",
        ProtectionState.Stopping => "Durduruluyor…",
        _ => _isRunning ? "Korumayı durdur" : "Korumayı başlat",
    };

    public string NetworkName { get => _networkName; private set => Set(ref _networkName, value); }

    public string IspSummary { get => _ispSummary; private set => Set(ref _ispSummary, value); }

    public string StrategySummary { get => _strategySummary; private set => Set(ref _strategySummary, value); }

    public string ProbeSummary { get => _probeSummary; private set => Set(ref _probeSummary, value); }

    public string TuningStatus { get => _tuningStatus; private set => Set(ref _tuningStatus, value); }

    public string EngineCounters { get => _engineCounters; private set => Set(ref _engineCounters, value); }

    public string DnsSummary { get => _dnsSummary; private set => Set(ref _dnsSummary, value); }

    public string DiscordSummary { get => _discordSummary; private set => Set(ref _discordSummary, value); }

    public string BrowserSummary { get => _browserSummary; private set => Set(ref _browserSummary, value); }

    public ScopeOption SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (Set(ref _selectedScope, value) && !_suppressPersist)
            {
                _service.ApplyScope(value.Scope);
            }
        }
    }

    public IspOption SelectedIsp
    {
        get => _selectedIsp;
        set
        {
            if (Set(ref _selectedIsp, value) && !_suppressPersist)
            {
                _service.ApplyManualIsp(value.Id);
                if (_isRunning)
                {
                    _ = RetuneAsync();
                }
            }
        }
    }

    public StrategyOption SelectedStrategy
    {
        get => _selectedStrategy;
        set
        {
            if (Set(ref _selectedStrategy, value) && !_suppressPersist)
            {
                Raise(nameof(SelectedStrategyDescription));
                _service.ApplyManualStrategy(value.Id);

                if (_isRunning)
                {
                    _ = value.Id is null ? RetuneAsync() : TestAsync();
                }
            }
        }
    }

    public string SelectedStrategyDescription => _selectedStrategy.Description;

    public DnsOption SelectedDns
    {
        get => _selectedDns;
        set
        {
            if (Set(ref _selectedDns, value) && !_suppressPersist)
            {
                _service.Settings.DnsMode = value.Mode;
                _service.SaveSettings();
                Raise(nameof(SelectedDnsDescription));

                // The resolver and the loopback listener are wired up during start, so
                // a mode change only takes effect after a restart of the service.
                if (_isRunning)
                {
                    _ = RestartAsync();
                }
            }
        }
    }

    public string SelectedDnsDescription => _selectedDns.Description;

    public bool BlockQuic
    {
        get => _service.Settings.BlockQuicHandshakes;
        set
        {
            if (_service.Settings.BlockQuicHandshakes == value)
            {
                return;
            }

            _service.ApplyQuicSetting(value);
            Raise();
        }
    }

    public bool AutoTuneOnNetworkChange
    {
        get => _service.Settings.AutoTuneOnNetworkChange;
        set
        {
            if (_service.Settings.AutoTuneOnNetworkChange == value)
            {
                return;
            }

            _service.Settings.AutoTuneOnNetworkChange = value;
            _service.SaveSettings();
            Raise();
        }
    }

    public bool StartWithWindows
    {
        get => _service.Settings.StartWithWindows;
        set
        {
            if (_service.Settings.StartWithWindows == value)
            {
                return;
            }

            _service.Settings.StartWithWindows = value;
            _service.SaveSettings();
            Raise();

            _ = value
                ? _autoStart.EnableAsync(_service.Settings.StartMinimised)
                : _autoStart.DisableAsync();
        }
    }

    public bool StartMinimised
    {
        get => _service.Settings.StartMinimised;
        set
        {
            if (_service.Settings.StartMinimised == value)
            {
                return;
            }

            _service.Settings.StartMinimised = value;
            _service.SaveSettings();
            Raise();

            if (_service.Settings.StartWithWindows)
            {
                _ = _autoStart.EnableAsync(value);
            }
        }
    }

    public bool MinimiseToTrayOnClose
    {
        get => _service.Settings.MinimiseToTrayOnClose;
        set
        {
            if (_service.Settings.MinimiseToTrayOnClose == value)
            {
                return;
            }

            _service.Settings.MinimiseToTrayOnClose = value;
            _service.SaveSettings();
            Raise();
        }
    }

    public bool LowLatencyMode
    {
        get => _lowLatencyMode;
        set
        {
            if (!Set(ref _lowLatencyMode, value))
            {
                return;
            }

            _ = ApplyLowLatencyModeAsync(value);
        }
    }

    public bool IsLatencyBusy
    {
        get => _isLatencyBusy;
        private set
        {
            if (Set(ref _isLatencyBusy, value))
            {
                RaiseLatencyCommandStates();
                Raise(nameof(IsLatencyProgressVisible));
                Raise(nameof(LatencyProgressTitle));
            }
        }
    }

    public string LatencyStatusLine
    {
        get => _latencyStatusLine;
        private set => Set(ref _latencyStatusLine, value);
    }

    /// <summary>
    /// The one line that says what the mode is doing.
    /// </summary>
    /// <remarks>
    /// Never "off" while the switch is on. A run that found nothing locally fixable is a
    /// mode that is on and watching, and saying otherwise would misdescribe the user's
    /// own settings back to them.
    /// </remarks>
    public string LatencyHeadline
    {
        get => _latencyHeadline;
        private set => Set(ref _latencyHeadline, value);
    }

    /// <summary>"off", "ok", "warn" or "info" - colour only; the wording carries meaning.</summary>
    public string LatencyStatusSeverity
    {
        get => _latencyStatusSeverity;
        private set => Set(ref _latencyStatusSeverity, value);
    }

    public string LatencyTargetSummary
    {
        get => _latencyTargetSummary;
        private set => Set(ref _latencyTargetSummary, value);
    }




    /// <summary>Where the measurements say the delay is, in the user's own words.</summary>
    public string LatencyPathSummary
    {
        get => _latencyPathSummary;
        private set => Set(ref _latencyPathSummary, value);
    }

    public string LatencyGuardSummary
    {
        get => _latencyGuardSummary;
        private set => Set(ref _latencyGuardSummary, value);
    }

    public string LatencyTargetError
    {
        get => _latencyTargetError;
        private set => Set(ref _latencyTargetError, value);
    }

    /// <summary>
    /// The stage the deep test is in right now, by name.
    /// </summary>
    /// <remarks>
    /// The card used to show one fixed sentence about starting an upload, while the run
    /// went on to need the upload stopped, a policy applied and a fresh upload started.
    /// Nobody could complete it without reading the source. These five properties are the
    /// run telling the user what it is actually waiting for.
    /// </remarks>
    public string LatencyStageTitle
    {
        get => _latencyStageTitle;
        private set => Set(ref _latencyStageTitle, value);
    }

    public string LatencyStageInstruction
    {
        get => _latencyStageInstruction;
        private set => Set(ref _latencyStageInstruction, value);
    }

    /// <summary>The rate the adapter counters show, and how close it is to capacity.</summary>
    public string LatencyStageRate
    {
        get => _latencyStageRate;
        private set => Set(ref _latencyStageRate, value);
    }

    public string LatencyStageRemaining
    {
        get => _latencyStageRemaining;
        private set => Set(ref _latencyStageRemaining, value);
    }

    public string LatencyStageData
    {
        get => _latencyStageData;
        private set => Set(ref _latencyStageData, value);
    }

    public bool IsDeepTestRunning
    {
        get => _isDeepTestRunning;
        private set
        {
            if (Set(ref _isDeepTestRunning, value))
            {
                LatencyCancelCommand.RaiseCanExecuteChanged();
                Raise(nameof(IsLatencyProgressVisible));
            }
        }
    }

    /// <summary>
    /// Whether the progress area is shown at all.
    /// </summary>
    /// <remarks>
    /// Any running measurement gets one, not only the deep test. A user who turned the
    /// mode on and waited three minutes for a paired benchmark previously had a bare
    /// indeterminate bar and a stop button that did nothing.
    /// </remarks>
    public bool IsLatencyProgressVisible => _isLatencyBusy;

    /// <summary>The stage line, or a plain sentence for the runs that have no stages.</summary>
    public string LatencyProgressTitle => _isDeepTestRunning
        ? _latencyStageTitle
        : _isLatencyBusy ? "Bağlantı ölçülüyor" : string.Empty;

    /// <summary>Everything the finished run measured, as the result panel prints it.</summary>
    public string LatencyResultSummary
    {
        get => _latencyResultSummary;
        private set => Set(ref _latencyResultSummary, value);
    }

    /// <summary>How much of the user's own data the run watched go past.</summary>
    public string LatencyDataUsedSummary
    {
        get => _latencyDataUsedSummary;
        private set => Set(ref _latencyDataUsedSummary, value);
    }

    /// <summary>The endpoint to measure, when discovery found more than one.</summary>
    public LatencyEndpointEntry? SelectedLatencyEndpoint
    {
        get => _selectedLatencyEndpoint;
        set
        {
            if (!Set(ref _selectedLatencyEndpoint, value))
            {
                return;
            }

            _service.SetLatencyPreferences(_service.Settings.Latency with { PinnedEndpoint = value?.Key });
        }
    }

    public TrafficGuardModeOption SelectedGuardMode
    {
        get => _selectedGuardMode;
        set
        {
            if (value is null || !Set(ref _selectedGuardMode, value))
            {
                return;
            }

            _service.SetLatencyPreferences(_service.Settings.Latency with { GuardMode = value.Mode });
        }
    }

    /// <summary>
    /// Whether a measurement may restart the adapter to make a setting take effect.
    /// </summary>
    /// <remarks>
    /// Off by default and worth the explanation next to it: most NDIS advanced keywords
    /// only take effect once the miniport restarts, and that drops every connection on it
    /// for a few seconds. Without this the run reports such candidates as needing a
    /// restart rather than measuring a value the driver is not using.
    /// </remarks>
    public bool AllowAdapterRestart
    {
        get => _service.Settings.Latency.AllowAdapterRestart;
        set
        {
            if (_service.Settings.Latency.AllowAdapterRestart == value)
            {
                return;
            }

            _service.SetLatencyPreferences(_service.Settings.Latency with { AllowAdapterRestart = value });
            Raise();
        }
    }

    /// <summary>Downlink capacity in Mbit/s, when the user knows it.</summary>
    public string ManualDownlinkMbps
    {
        get => _service.Settings.Latency.ManualDownlinkMbps?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)
            ?? string.Empty;
        set
        {
            double? parsed = double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture,
                out var number) && number > 0
                ? number
                : null;

            _service.SetLatencyPreferences(_service.Settings.Latency with { ManualDownlinkMbps = parsed });
            Raise();
        }
    }

    public LatencyTargetOption SelectedLatencyTarget
    {
        get => _selectedLatencyTarget;
        set
        {
            if (value is null || !Set(ref _selectedLatencyTarget, value))
            {
                return;
            }

            Raise(nameof(IsCustomLatencyTarget));
            Raise(nameof(IsApplicationLatencyTarget));

            if (value.Kind == LatencyTargetKind.Application)
            {
                _ = RefreshLatencyProcessesAsync();
            }

            PersistLatencyPreferences();
        }
    }

    /// <summary>Whether discovery offered more than one endpoint to choose between.</summary>
    public bool HasLatencyEndpointChoice => LatencyEndpoints.Count > 1;

    /// <summary>Whether the host/port box applies to the current choice.</summary>
    public bool IsCustomLatencyTarget => _selectedLatencyTarget.Kind == LatencyTargetKind.Custom;

    public bool IsApplicationLatencyTarget => _selectedLatencyTarget.Kind == LatencyTargetKind.Application;

    public string LatencyCustomTarget
    {
        get => _latencyCustomTarget;
        set
        {
            if (Set(ref _latencyCustomTarget, value))
            {
                PersistLatencyPreferences();
            }
        }
    }

    public string? SelectedLatencyProcess
    {
        get => _selectedLatencyProcess;
        set
        {
            if (Set(ref _selectedLatencyProcess, value))
            {
                PersistLatencyPreferences();
            }
        }
    }

    /// <summary>
    /// Whether the loaded-latency lane may create a send-rate limit.
    /// </summary>
    /// <remarks>
    /// Off unless the user turns it on, because it creates a Windows QoS policy. One that
    /// appeared without being asked for is exactly what a user should never find.
    /// </remarks>
    public bool TrafficGuardEnabled
    {
        get => _service.Settings.Latency.TrafficGuardEnabled;
        set
        {
            if (_service.Settings.Latency.TrafficGuardEnabled == value)
            {
                return;
            }

            _service.SetLatencyPreferences(_service.Settings.Latency with { TrafficGuardEnabled = value });
            Raise();
        }
    }

    /// <summary>The one executable whose bulk sending may be paced. Never guessed.</summary>
    public string TrafficGuardApplication
    {
        get => _service.Settings.Latency.TrafficGuardApplication ?? string.Empty;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_service.Settings.Latency.TrafficGuardApplication, trimmed, StringComparison.Ordinal))
            {
                return;
            }

            _service.SetLatencyPreferences(_service.Settings.Latency with { TrafficGuardApplication = trimmed });
            Raise();
        }
    }

    /// <summary>Uplink capacity in Mbit/s, when the user knows it better than a measurement.</summary>
    public string ManualUplinkMbps
    {
        get => _service.Settings.Latency.ManualUplinkMbps?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)
            ?? string.Empty;
        set
        {
            double? parsed = double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture,
                out var number) && number > 0
                ? number
                : null;

            _service.SetLatencyPreferences(_service.Settings.Latency with { ManualUplinkMbps = parsed });
            Raise();
        }
    }

    /// <summary>
    /// Writes the target choice through to the service, rejecting anything unparseable.
    /// </summary>
    /// <remarks>
    /// A malformed host is reported next to the box rather than silently falling back to
    /// the reference target, which would leave the user measuring something they did not
    /// ask for and being told nothing about it.
    /// </remarks>
    private void PersistLatencyPreferences()
    {
        var preferences = _service.Settings.Latency with
        {
            TargetKind = _selectedLatencyTarget.Kind,
            TargetProcess = _selectedLatencyProcess,
        };

        LatencyTargetError = string.Empty;

        if (_selectedLatencyTarget.Kind == LatencyTargetKind.Custom)
        {
            if (string.IsNullOrWhiteSpace(_latencyCustomTarget))
            {
                InvalidTarget("Ölçülecek bir ana bilgisayar veya IP girin.");
                return;
            }

            if (!LatencyTargetSpec.TryParse(_latencyCustomTarget, out var spec, out var error))
            {
                InvalidTarget(error ?? "Hedef ayrıştırılamadı.");
                return;
            }

            preferences = preferences with
            {
                TargetHost = spec.Host,
                TargetPort = spec.Port,
                TargetProtocol = spec.Protocol,
            };
        }

        if (_selectedLatencyTarget.Kind == LatencyTargetKind.Application
            && string.IsNullOrWhiteSpace(_selectedLatencyProcess))
        {
            InvalidTarget("Ölçülecek çalışan bir uygulama seçin.");
            return;
        }

        IsLatencyTargetValid = true;
        _service.SetLatencyPreferences(preferences);
        LatencyTargetSummary = preferences.ToSpec().Describe();
    }

    /// <summary>
    /// Records that what is on screen is not measurable, and stops the buttons.
    /// </summary>
    /// <remarks>
    /// The service keeps its previous target, which is right - nothing should be silently
    /// pointed somewhere the user did not choose - but the commands must not run against
    /// it while the screen shows something else. Writing the error and leaving the buttons
    /// live is how a measurement of the old target ended up under the new target's name.
    /// </remarks>
    private void InvalidTarget(string message)
    {
        LatencyTargetError = message;
        IsLatencyTargetValid = false;
    }

    /// <summary>Whether a measurement may start: nothing running, and a usable target.</summary>
    private bool CanRunLatency() => !_isLatencyBusy && _isLatencyTargetValid;

    /// <summary>
    /// Whether the target on screen can actually be measured.
    /// </summary>
    /// <remarks>
    /// Bound by the card so the error text and the disabled buttons cannot disagree, and
    /// checked by every measuring command's <c>CanExecute</c> - which also covers the
    /// keyboard, since a disabled command does not run on Enter either.
    /// </remarks>
    public bool IsLatencyTargetValid
    {
        get => _isLatencyTargetValid;
        private set
        {
            if (Set(ref _isLatencyTargetValid, value))
            {
                RaiseLatencyCommandStates();
            }
        }
    }

    private void RaiseLatencyCommandStates()
    {
        LatencyTestCommand.RaiseCanExecuteChanged();
        LatencyDeepTestCommand.RaiseCanExecuteChanged();
        LatencyRetestCommand.RaiseCanExecuteChanged();
        LatencyRestoreCommand.RaiseCanExecuteChanged();
        LatencyPrimaryCommand.RaiseCanExecuteChanged();
        LatencyCancelCommand.RaiseCanExecuteChanged();
    }

    /// <summary>One sentence saying what to do next, from the engine's own verdict.</summary>
    public string LatencySuggestion
    {
        get => _latencySuggestion;
        private set => Set(ref _latencySuggestion, value);
    }

    /// <summary>The label on the single primary button, which follows the result.</summary>
    public string LatencyPrimaryAction
    {
        get => _latencyPrimaryAction;
        private set => Set(ref _latencyPrimaryAction, value);
    }

    /// <summary>When the numbers on screen were measured.</summary>
    public string LatencyMeasuredAt
    {
        get => _latencyMeasuredAt;
        private set => Set(ref _latencyMeasuredAt, value);
    }

    /// <summary>
    /// Runs whatever the card is currently offering as the next step.
    /// </summary>
    /// <remarks>
    /// The two actions that are not measurements - allowing adapter restarts and
    /// restoring - are performed here too, so the button always does what its label says
    /// rather than sending the user hunting through the secondary area.
    /// </remarks>
    private async Task RunLatencyPrimaryActionAsync()
    {
        switch (_service.LatencyStatus.NextAction)
        {
            case LatencyNextAction.LoadTest:
                await RunLatencyDeepTestAsync().ConfigureAwait(true);
                return;

            case LatencyNextAction.Remeasure:
                await RetestLatencyAsync().ConfigureAwait(true);
                return;

            case LatencyNextAction.Restore:
            case LatencyNextAction.Recover:
                await RestoreLatencyAsync().ConfigureAwait(true);
                return;

            case LatencyNextAction.AllowRestart:
                // Consent, then immediately measure the candidates it unblocks - which is
                // the whole reason the user pressed it.
                AllowAdapterRestart = true;
                Raise(nameof(AllowAdapterRestart));
                await RetestLatencyAsync().ConfigureAwait(true);
                return;

            case LatencyNextAction.TryCandidates:
                await ApplyLowLatencyModeAsync(enabled: true).ConfigureAwait(true);
                return;

            default:
                await TestLatencyAsync().ConfigureAwait(true);
                return;
        }
    }

    /// <summary>The button label for one next action.</summary>
    private static string DescribeAction(LatencyNextAction action) => action switch
    {
        LatencyNextAction.LoadTest => "Yük altında test et",
        LatencyNextAction.Remeasure => "Yeniden ölç",
        LatencyNextAction.TryCandidates => "Uygun ayarları dene",
        LatencyNextAction.Restore => "Ayarları geri al",
        LatencyNextAction.Recover => "Kurtarmayı çalıştır",
        LatencyNextAction.AllowRestart => "İzin ver ve ölç",
        LatencyNextAction.ViewResult => "Yeniden ölç",
        _ => "Bağlantımı analiz et",
    };

    /// <summary>
    /// Fills the application picker from the live connection table.
    /// </summary>
    /// <remarks>
    /// Reading the process list and the TCP table takes long enough to be noticed, so the
    /// work happens off the UI thread and only the collection update comes back to it.
    /// </remarks>
    private async Task RefreshLatencyProcessesAsync()
    {
        try
        {
            var running = await _service.ListConnectedProcessesAsync().ConfigureAwait(true);

            LatencyProcesses.Clear();
            foreach (var name in running)
            {
                LatencyProcesses.Add(name);
            }

            if (_selectedLatencyProcess is { Length: > 0 } && !LatencyProcesses.Contains(_selectedLatencyProcess))
            {
                LatencyProcesses.Insert(0, _selectedLatencyProcess);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Çalışan uygulama listesi okunamadı", ex);
            LatencyTargetError = "Çalışan uygulamalar okunamadı.";
        }
    }

    /// <summary>
    /// Renders everything a finished run measured, in one block the user can read or copy.
    /// </summary>
    /// <remarks>
    /// Deliberately exhaustive and deliberately explicit about what each number is. The
    /// distinction that matters most is whether the figure is the application's own round
    /// trip or a route reference to the same address, because those are different claims
    /// and only one of them is a game's ping.
    /// </remarks>
    /// <summary>
    /// The four numbers the main screen shows, each either measured or plainly absent.
    /// </summary>
    /// <remarks>
    /// Before and after are only ever offered for the same condition against the same
    /// target: idle against idle, loaded against loaded. A card never prints a zero for
    /// something that was not measured, which is why loss is a nullable all the way down
    /// from the probe.
    /// </remarks>
    private void BuildLatencyCards(LatencyStatusView status)
    {
        LatencyCards.Clear();

        LatencyCards.Add(status.Idle is { } idle
            ? new LatencyCard(
                "Boştaki ping",
                $"{idle.MedianRttMs:F0} ms",
                true,
                IdleChange(status))
            : LatencyCard.NotMeasured("Boştaki ping", "Analiz çalıştırın"));

        LatencyCards.Add(status.UploadLoaded is { } loaded
            ? new LatencyCard(
                "Yük altında ping",
                $"{loaded.MedianRttMs:F0} ms",
                true,
                LoadedChange(status))
            : LatencyCard.NotMeasured("Yük altında ping", "Yük altında test edilmedi"));

        LatencyCards.Add(status.Idle is { } jitter
            ? new LatencyCard("Ping dalgalanması", $"{jitter.JitterMs:F0} ms", true)
            : LatencyCard.NotMeasured("Ping dalgalanması", "Analiz çalıştırın"));

        // Loss is null, not zero, whenever the instrument does not count packets - a
        // passive read of the TCP stack's own estimate, for one.
        LatencyCards.Add(status.Idle?.PacketLossPercent is { } loss
            ? new LatencyCard("Paket kaybı", $"%{loss:F1}", true)
            : LatencyCard.NotMeasured("Paket kaybı", "Bu ölçümde sayılmadı"));
    }

    /// <summary>
    /// The idle before/after note, from two measurements of the same condition.
    /// </summary>
    /// <remarks>
    /// Both halves are real readings taken with the link idle against the same target.
    /// Reconstructing the "before" by adding the delta back onto the "after" would put a
    /// number on screen that was never measured.
    /// </remarks>
    private static string IdleChange(LatencyStatusView status)
        => status is { IdleBefore: { } before, IdleAfter: { } after }
            && !ReferenceEquals(before, after)
            ? $"Önce {before.MedianRttMs:F0} ms → sonra {after.MedianRttMs:F0} ms"
            : string.Empty;

    /// <summary>The loaded before/after note, only when a cap was measured both ways.</summary>
    private static string LoadedChange(LatencyStatusView status)
        => status is { UploadLoaded: { } before, UploadLoadedAfter: { } after }
            ? $"Sınır öncesi {before.MedianRttMs:F0} ms → sonrası {after.MedianRttMs:F0} ms"
            : string.Empty;

    private static string DescribeLane(LatencyLaneState state) => state switch
    {
        LatencyLaneState.Completed => "Yapıldı",
        LatencyLaneState.Available => "Denenmedi",
        LatencyLaneState.NotApplicable => "Uygun değil",
        LatencyLaneState.Blocked => "Engelli",
        _ => "Tamamlanmadı",
    };

    private void ApplyLatencyResultSummary(LatencyStatusView status)
    {
        var result = _service.LatencyResult;
        var lines = new List<string>
        {
            $"Hedef: {(string.IsNullOrWhiteSpace(status.Target) ? "—" : status.Target)} · {status.Protocol}",
            status.RouteReferenceOnly
                ? "Bu değer aynı adrese rota referansıdır; uygulamanın kendi gidiş-dönüş süresi değildir."
                : "Bu değer ölçülen protokolün gerçek gidiş-dönüş süresidir.",
        };

        if (status.Idle is { } idle)
        {
            lines.Add($"Boşta: {Numbers(idle)}");
            lines.Add($"Ölçüm çözünürlüğü: {idle.ClockResolutionMs:F2} ms · "
                + $"kayıp eşiği {idle.RemoteAttempts} denemede bir probe = %{idle.LossQuantumPercent:F1}");
        }

        lines.Add(status.UploadLoaded is { } upload
            ? $"Gönderim altında: {Numbers(upload)}"
            : "Gönderim altında: ölçülmedi.");

        lines.Add(status.DownloadLoaded is { } download
            ? $"İndirme altında: {Numbers(download)}"
            : "İndirme altında: ölçülmedi.");

        if (status.Path is { } path)
        {
            lines.Add($"Kuyruklanma — gönderim: {Milliseconds(path.UploadQueueingMs)} · "
                + $"indirme: {Milliseconds(path.DownloadQueueingMs)}");
        }

        if (result.Capacity.HasUplink || result.Capacity.HasDownlink)
        {
            lines.Add($"Ölçülen hat kapasitesi: {result.Capacity.Describe()}");
        }

        if (status.TrafficGuard is { } guard && guard.Status != TrafficGuardStatus.Off)
        {
            lines.Add($"Traffic Guard: {guard.Summary}");

            if (guard.ThrottleBitsPerSecond is { } cap)
            {
                lines.Add($"Kullanılan sınır: {cap / 1_000_000d:F1} Mbit/s"
                    + (guard.RetainedThroughputShare is { } retained
                        ? $" · korunan throughput %{retained * 100:F0}"
                        : string.Empty));
            }

            if (guard.UplinkBeforeKbps is { } before)
            {
                lines.Add($"Gönderim hızı: {before / 1000:F1} Mbit/s → "
                    + $"{(guard.UplinkAfterKbps is { } after ? $"{after / 1000:F1}" : "—")} Mbit/s");
            }

            foreach (var trial in guard.Trials)
            {
                lines.Add($"  denenen sınır · {trial}");
            }
        }

        if (status.Improvement is { } improvement)
        {
            lines.Add($"Doğrulanmış kazanç: median {improvement.MedianMs:F1} ms · p95 {improvement.P95Ms:F1} ms");
        }

        foreach (var verdict in result.Verdicts.Where(entry => entry.ConfidenceLowerMs is not null))
        {
            lines.Add($"  {verdict.Description}: güven aralığı "
                + $"[{verdict.ConfidenceLowerMs:F1}, {verdict.ConfidenceUpperMs:F1}] ms");
        }

        lines.Add(status.Applied.Count > 0
            ? $"Uygulanan: {string.Join(" · ", status.Applied)}"
            : "Uygulanan değişiklik yok.");

        if (status.Rejected.Count > 0)
        {
            lines.Add($"Geri alınan: {string.Join(" · ", status.Rejected.Select(entry => $"{entry.Change} ({entry.Reason})"))}");
        }

        if (status.Applied.Count == 0 && status.State is LatencyModeState.NoLocalGain or LatencyModeState.MonitoringOnly)
        {
            lines.Add("Kazanç bulunamadı: bu bir hata değildir. Ölçülen koşullarda yerel olarak "
                + "uygulanabilir, tekrarlanan bir iyileşme yoktu ve hiçbir ayar tutulmadı.");
        }

        foreach (var notice in status.Notices)
        {
            lines.Add($"Not: {notice}");
        }

        LatencyResultSummary = string.Join(Environment.NewLine, lines);
        LatencyDataUsedSummary = result.DataUsedBytes > 0
            ? $"Bu testte izlenen veri: {Bytes(result.DataUsedBytes)}"
            : "Bu testte veri sayacı okunmadı.";

        static string Numbers(LatencyMeasurement measurement) =>
            $"median {measurement.MedianRttMs:F1} ms · p95 {measurement.P95RttMs:F1} ms · "
            + (measurement.RemoteReplies >= 100 ? $"p99 {measurement.P99RttMs:F1} ms · " : "p99 için örnek yetersiz · ")
            + $"jitter {measurement.JitterMs:F1} ms · kayıp %{measurement.PacketLossPercent:F1} "
            + $"({measurement.RemoteReplies}/{measurement.RemoteAttempts})";

        static string Milliseconds(double? value) => value is { } number ? $"{number:F0} ms" : "ölçülmedi";

        static string Bytes(long value) => value switch
        {
            < 1024 * 1024 => $"{value / 1024d:F0} KB",
            < 1024L * 1024 * 1024 => $"{value / (1024d * 1024):F1} MB",
            _ => $"{value / (1024d * 1024 * 1024):F2} GB",
        };
    }

    private void ClearLatencyProfiles()
    {
        LatencyStatusLine = _service.ClearLatencyProfiles()
            ? "Kayıtlı gecikme sonuçları silindi; sonraki ölçüm baştan yapılacak."
            : "Silinecek kayıtlı gecikme sonucu yoktu.";
    }

    private async Task RunLatencyDeepTestAsync()
    {
        IsLatencyBusy = true;
        IsDeepTestRunning = true;
        LatencyHeadline = "Açık · yük altında derin test yapılıyor";

        try
        {
            var result = await _service.RunLoadedLatencyTestAsync().ConfigureAwait(true);
            ApplyLatencyStatus(_service.LatencyStatus);
            LatencyStatusLine = result.StatusLine;
        }
        catch (OperationCanceledException)
        {
            LatencyStatusLine = "Derin test durduruldu; bu çalışmanın yaptığı değişiklikler geri alındı.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Yük altında gecikme ölçümü başarısız", ex);
            LatencyStatusLine = $"Yük altında ölçüm yapılamadı: {ex.Message}";
        }
        finally
        {
            IsDeepTestRunning = false;
            IsLatencyBusy = false;
            ApplyLatencyStage(_service.LatencyStage);
        }
    }

    /// <summary>
    /// Stops whatever latency work is running, not only the deep test.
    /// </summary>
    /// <remarks>
    /// Every path restores what it changed on the way out, so this is safe from any of
    /// them; the button used to be inert during the ordinary optimization, which is the
    /// longest running of the three.
    /// </remarks>
    private void CancelLatencyRun() => _service.CancelLatencyRun();

    /// <summary>The service publishes stages off the UI thread; marshal before binding.</summary>
    private void OnLatencyStageChanged(LoadedLaneProgress progress)
    {
        if (_dispatcher.CheckAccess())
        {
            ApplyLatencyStage(progress);
            return;
        }

        _dispatcher.BeginInvoke(() => ApplyLatencyStage(progress));
    }

    private void ApplyLatencyStage(LoadedLaneProgress progress)
    {
        LatencyStageTitle = progress.Title;
        LatencyStageInstruction = progress.Instruction;
        LatencyStageRate = progress.DescribeRate();
        LatencyStageRemaining = progress.Remaining is { TotalSeconds: > 0 } remaining
            ? $"Kalan süre: {remaining.TotalSeconds:F0} sn"
            : string.Empty;
        LatencyStageData = progress.DataUsedBytes > 0
            ? $"Bu testte izlenen veri: {progress.DescribeData()}"
            : string.Empty;

        // A stage that has already committed or rolled back has nothing left to stop.
        _latencyStageCanCancel = progress.CanCancel;
        LatencyCancelCommand.RaiseCanExecuteChanged();
        Raise(nameof(IsLatencyProgressVisible));

        if (progress.Outcome is { Length: > 0 } outcome)
        {
            LatencyStatusLine = outcome;
        }
    }

    private async Task RetestLatencyAsync()
    {
        IsLatencyBusy = true;
        LatencyStatusLine = "Kayıtlı sonuç yok sayılarak baştan ölçülüyor…";

        try
        {
            await _service.RetestLatencyAsync().ConfigureAwait(true);
            ApplyLatencyStatus(_service.LatencyStatus);
        }
        catch (Exception ex)
        {
            AppLog.Error("Gecikme yeniden ölçülemedi", ex);
            LatencyStatusLine = $"Yeniden ölçüm başarısız: {ex.Message}";
        }
        finally
        {
            IsLatencyBusy = false;
        }
    }

    /// <summary>Copies one structured status into the properties the card binds to.</summary>
    private void ApplyLatencyStatus(LatencyStatusView status)
    {
        LatencyHeadline = status.Headline;
        LatencyStatusSeverity = status.Severity;
        LatencyStatusLine = string.IsNullOrWhiteSpace(status.Detail) ? status.Headline : status.Detail;
        LatencyTargetSummary = string.IsNullOrWhiteSpace(status.Target)
            ? _service.Settings.Latency.ToSpec().Describe()
            : $"{status.Target} ({status.Protocol})"
                + (status.RouteReferenceOnly ? " · rota referansı, uygulamanın kendi RTT'si değil" : string.Empty);

        LatencyPathSummary = status.Path?.Summary ?? string.Empty;
        LatencyGuardSummary = status.TrafficGuard?.Summary ?? "Traffic Guard kapalı.";

        // One sentence and one action, both from the engine's own structured verdict
        // rather than assembled here from words in a report.
        LatencySuggestion = status.Suggestion;
        LatencyPrimaryAction = DescribeAction(status.NextAction);
        LatencyMeasuredAt = status.Idle is { } measured
            ? $"Son ölçüm: {measured.MeasuredAt.ToLocalTime():HH:mm}"
            : string.Empty;

        BuildLatencyCards(status);

        LatencyLanes.Clear();
        foreach (var lane in status.Lanes)
        {
            LatencyLanes.Add(new LatencyLaneEntry(lane.Title, lane.Detail, DescribeLane(lane.State)));
        }

        RaiseLatencyCommandStates();

        LatencyAppliedChanges.Clear();
        foreach (var change in status.Applied)
        {
            LatencyAppliedChanges.Add(change);
        }

        LatencyRejectedChanges.Clear();
        foreach (var rejection in status.Rejected)
        {
            LatencyRejectedChanges.Add(new LatencyRejectionEntry(
                rejection.Change,

                // The cause is shown next to the reason so "measured and useless" and
                // "never tried" cannot read as the same outcome.
                $"{rejection.CauseLabel} — {rejection.Reason}",
                rejection.WasMeasured));
        }

        // Discovery can find several endpoints for one application, and which of them is
        // the session is not something this can decide for the user.
        var pinned = _service.Settings.Latency.PinnedEndpoint;
        LatencyEndpoints.Clear();
        foreach (var candidate in _service.LatencyResult.Candidates)
        {
            LatencyEndpoints.Add(new LatencyEndpointEntry(
                LatencyTargetResolver.EndpointKey(candidate.Endpoint),
                candidate.Display,
                candidate.Why));
        }

        _selectedLatencyEndpoint = LatencyEndpoints.FirstOrDefault(entry => entry.Key == pinned);
        Raise(nameof(SelectedLatencyEndpoint));
        Raise(nameof(HasLatencyEndpointChoice));

        ApplyLatencyResultSummary(status);
    }

    private async Task ApplyLowLatencyModeAsync(bool enabled)
    {
        IsLatencyBusy = true;
        LatencyStatusLine = enabled
            ? "Aktif bağdaştırıcı ölçülüyor; yalnız doğrulanan değişiklikler tutulacak…"
            : "Özgün NIC ayarları geri yükleniyor…";

        try
        {
            await _service.SetLowLatencyModeAsync(enabled).ConfigureAwait(true);
            ApplyLatencyStatus(_service.LatencyStatus);
        }
        catch (Exception ex)
        {
            AppLog.Error("Ping düşürme durumu değiştirilemedi", ex);
            LatencyStatusLine = $"Ping düşürme değiştirilemedi: {ex.Message}";
        }
        finally
        {
            _lowLatencyMode = _service.Settings.LowLatencyMode;
            Raise(nameof(LowLatencyMode));
            IsLatencyBusy = false;
        }
    }

    private async Task TestLatencyAsync()
    {
        IsLatencyBusy = true;
        LatencyStatusLine = "NIC ayarı değiştirilmeden gateway ve internet gecikmesi ölçülüyor…";

        try
        {
            await _service.TestLatencyAsync().ConfigureAwait(true);
            ApplyLatencyStatus(_service.LatencyStatus);
        }
        catch (Exception ex)
        {
            AppLog.Error("Gecikme ölçümü başarısız", ex);
            LatencyStatusLine = $"Gecikme ölçülemedi: {ex.Message}";
        }
        finally
        {
            IsLatencyBusy = false;
        }
    }

    /// <summary>
    /// Puts everything back: adapter properties from the snapshot, and every QoS policy
    /// this application owns.
    /// </summary>
    /// <remarks>
    /// The dedicated restore path is called explicitly rather than reached sideways by
    /// switching the mode off, because it is the one entry point that sweeps policies a
    /// crashed run may have left behind as well as the snapshot's adapter settings. The
    /// mode is then turned off so the card does not claim to be optimising a machine it
    /// has just put back.
    /// </remarks>
    private async Task RestoreLatencyAsync()
    {
        IsLatencyBusy = true;
        LatencyStatusLine = "Özgün NIC ayarları ve bu uygulamanın QoS ilkeleri geri yükleniyor…";

        try
        {
            await _service.RestoreLatencyAsync().ConfigureAwait(true);

            if (_service.Settings.LowLatencyMode)
            {
                await _service.SetLowLatencyModeAsync(false).ConfigureAwait(true);
            }

            ApplyLatencyStatus(_service.LatencyStatus);
        }
        catch (Exception ex)
        {
            AppLog.Error("Gecikme ayarları geri yüklenemedi", ex);
            LatencyStatusLine = $"Geri yükleme başarısız: {ex.Message}";
        }
        finally
        {
            _lowLatencyMode = _service.Settings.LowLatencyMode;
            Raise(nameof(LowLatencyMode));
            IsLatencyBusy = false;
        }
    }

    // --- Siteler -------------------------------------------------------------

    public string NewDomain
    {
        get => _newDomain;
        set
        {
            if (Set(ref _newDomain, value))
            {
                AddDomainCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DomainEntry? SelectedDomain
    {
        get => _selectedDomain;
        set
        {
            if (Set(ref _selectedDomain, value))
            {
                RemoveDomainCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DomainStatus { get => _domainStatus; private set => Set(ref _domainStatus, value); }

    /// <summary>"ok", "error" or "" - the colour of <see cref="DomainStatus"/>; the wording stays the source of truth.</summary>
    public string DomainStatusSeverity { get => _domainStatusSeverity; private set => Set(ref _domainStatusSeverity, value); }

    /// <summary>Text the domain list is narrowed by. Empty shows everything.</summary>
    public string DomainFilter
    {
        get => _domainFilter;
        set
        {
            if (!Set(ref _domainFilter, value))
            {
                return;
            }

            // The predicate reads the field, so one refresh re-runs it. The list
            // itself stays virtualised; only its rows change.
            FilteredDomains.Refresh();
            Raise(nameof(HasFilter));
            Raise(nameof(DomainsViewEmpty));
        }
    }

    public bool HasFilter => !string.IsNullOrWhiteSpace(_domainFilter);

    /// <summary>
    /// The domain list as the filter narrows it. The underlying collection stays
    /// complete, so the engine's learned-domain events need no extra bookkeeping.
    /// </summary>
    public ICollectionView FilteredDomains { get; }

    /// <summary>Nothing survives the current filter (or the list is empty) - drives the empty state.</summary>
    public bool DomainsViewEmpty => FilteredDomains.IsEmpty;

    private bool FilterDomain(object item)
    {
        return item is DomainEntry entry
            && (string.IsNullOrWhiteSpace(_domainFilter)
                || entry.Domain.Contains(_domainFilter.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public string DomainSummary =>
        $"{Domains.Count} alan adı korunuyor · {Domains.Count(d => d.Origin == DomainOrigin.Learned)} tanesi kendiliğinden bulundu";

    public bool AutoDiscover
    {
        get => _service.Settings.AutoDiscoverBlockedSites;
        set
        {
            if (_service.Settings.AutoDiscoverBlockedSites == value)
            {
                return;
            }

            _service.ApplyDiscoverySetting(value);
            Raise();
        }
    }

    public RecheckOption SelectedRecheck
    {
        get => _selectedRecheck;
        set
        {
            if (Set(ref _selectedRecheck, value) && !_suppressPersist)
            {
                _service.ApplyRecheckInterval(value.Seconds);
            }
        }
    }

    private void AddDomain()
    {
        var domain = _newDomain;

        if (!_service.AddDomain(domain))
        {
            DomainStatusSeverity = "error";
            DomainStatus = $"'{domain.Trim()}' zaten korunuyor ya da geçerli bir alan adı değil.";
            return;
        }

        DomainStatusSeverity = "ok";
        DomainStatus = $"'{domain.Trim().ToLowerInvariant()}' eklendi. Alt alan adları da kapsanır.";
        NewDomain = string.Empty;
        RefreshDomains();
    }

    private void RemoveSelectedDomain()
    {
        if (_selectedDomain is not { } entry)
        {
            return;
        }

        var removed = _service.RemoveDomain(entry.Domain);
        DomainStatusSeverity = removed ? "ok" : "error";
        DomainStatus = removed
            ? $"'{entry.Domain}' artık korunmuyor."
            : $"'{entry.Domain}' çıkarılamadı.";

        RefreshDomains();
    }

    private void RefreshDomains()
    {
        var excluded = new HashSet<string>(_service.Settings.ExcludedDomains, StringComparer.OrdinalIgnoreCase);
        var manual = new HashSet<string>(_service.Settings.ExtraDomains, StringComparer.OrdinalIgnoreCase);
        var learned = new HashSet<string>(_service.LearnedDomains, StringComparer.OrdinalIgnoreCase);

        var entries = new List<DomainEntry>();

        foreach (var domain in BlockedSiteCatalog.All.Where(d => !excluded.Contains(d)))
        {
            entries.Add(new DomainEntry(domain, DomainOrigin.BuiltIn));
        }

        foreach (var domain in learned.Where(d => !excluded.Contains(d)))
        {
            entries.Add(new DomainEntry(domain, DomainOrigin.Learned));
        }

        foreach (var domain in manual.Where(d => !excluded.Contains(d) && !learned.Contains(d)))
        {
            entries.Add(new DomainEntry(domain, DomainOrigin.Manual));
        }

        Domains.Clear();
        foreach (var entry in entries.DistinctBy(e => e.Domain, StringComparer.OrdinalIgnoreCase).OrderBy(e => e.Domain, StringComparer.Ordinal))
        {
            Domains.Add(entry);
        }

        SelectedDomain = null;
        Raise(nameof(DomainSummary));
        Raise(nameof(DomainsViewEmpty));
    }

    // --- Vodafone Sınırsız Modu / hotspot diagnostics ------------------------

    /// <summary>
    /// The restored product feature controls safe per-network compatibility checks.
    /// It never enables the retired TTL/accounting rewrite.
    /// </summary>
    public bool VodafoneModeEnabled
    {
        get => _service.Settings.VodafoneModeEnabled;
        set
        {
            if (_service.Settings.VodafoneModeEnabled == value)
            {
                return;
            }

            try
            {
                if (value)
                {
                    _service.EnableVodafoneModeHere();
                }
                else
                {
                    _service.DisableVodafoneMode();
                }
            }
            catch (InvalidOperationException ex)
            {
                Raise();
                VodafoneStatusLine = ex.Message;
                return;
            }

            Raise();
            Raise(nameof(HotspotDiagnostics));
            RefreshVodafoneNetworks();
        }
    }

    /// <summary>
    /// Whether moving to a different network runs the checks by itself.
    /// </summary>
    /// <remarks>
    /// Anyone who had the retired TTL mode enabled gets this switched on by the config
    /// migration, so the upgrade leaves them with the replacement rather than a gap. The
    /// "Tanıla" button works whatever this is set to.
    /// </remarks>
    public bool HotspotDiagnostics
    {
        get => _service.Settings.HotspotDiagnostics;
        set
        {
            if (_service.Settings.HotspotDiagnostics == value)
            {
                return;
            }

            _service.SetHotspotDiagnostics(value);
            Raise();
        }
    }

    public bool IsHotspotBusy
    {
        get => _isHotspotBusy;
        private set
        {
            if (Set(ref _isHotspotBusy, value))
            {
                HotspotDiagnoseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string HotspotStatusLine
    {
        get => _hotspotStatusLine;
        private set => Set(ref _hotspotStatusLine, value);
    }

    public string VodafoneStatusLine
    {
        get => _vodafoneStatusLine;
        private set => Set(ref _vodafoneStatusLine, value);
    }

    public VodafoneNetworkEntry? SelectedVodafoneNetwork
    {
        get => _selectedVodafoneNetwork;
        set
        {
            if (Set(ref _selectedVodafoneNetwork, value))
            {
                ForgetVodafoneNetworkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private async Task RunHotspotDiagnosticsAsync()
    {
        IsHotspotBusy = true;
        HotspotStatusLine = "Bağlantı kontrol ediliyor…";
        ApplyHotspotView();

        try
        {
            await _service.RunHotspotDiagnosticsAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The user moved networks or switched the mode off; the service has already
            // dropped the result that would no longer describe where we are.
        }
        catch (Exception ex)
        {
            AppLog.Error("Hotspot tanılaması başarısız", ex);
        }
        finally
        {
            IsHotspotBusy = false;

            // The card is rebuilt from the service's own view, so the result shown is
            // always the one the service accepted for the network we are on now.
            ApplyHotspotView();
        }
    }

    /// <summary>Remembers the network we are on, without switching the mode off and on.</summary>
    private void RememberCurrentVodafoneNetwork()
    {
        try
        {
            _service.RememberCurrentVodafoneNetwork();
        }
        catch (InvalidOperationException ex)
        {
            HotspotSuggestion = ex.Message;
            return;
        }

        RefreshVodafoneNetworks();
    }

    /// <summary>
    /// Copies the service's structured hotspot state onto the card.
    /// </summary>
    /// <remarks>
    /// Nothing here reads <c>ToReport()</c>. That text is written for a person to paste
    /// into a support thread; it lives under "Teknik ayrıntılar" and is never the source
    /// of what the interface believes.
    /// </remarks>
    private void ApplyHotspotView()
    {
        var view = _service.HotspotView;
        _hotspotView = view;

        HotspotCards.Clear();
        foreach (var card in view.Cards)
        {
            HotspotCards.Add(card);
        }

        HotspotDetails.Clear();
        foreach (var detail in view.TechnicalDetails)
        {
            HotspotDetails.Add(detail);
        }

        VodafoneStatusLine = view.Headline;
        HotspotStatusSeverity = view.Severity;
        HotspotSuggestion = view.Suggestion ?? string.Empty;
        HotspotReport = view.Report;
        HotspotCheckedAt = view.CheckedAt is { } checkedAt
            ? $"Son kontrol: {checkedAt.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "Bu ağda henüz kontrol yapılmadı.";
        HotspotStatusLine = view.Run == HotspotRunState.Running
            ? "Bağlantı kontrol ediliyor…"
            : view.Headline;

        Raise(nameof(HasHotspotResult));
        Raise(nameof(HasVodafoneNetworks));
        Raise(nameof(IsLegacyCleanupAvailable));
        RememberVodafoneNetworkCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Whether there are numbers to show, as opposed to an empty state.</summary>
    public bool HasHotspotResult => _hotspotView.Run == HotspotRunState.Completed;

    /// <summary>Whether the remembered-network list has anything in it.</summary>
    public bool HasVodafoneNetworks => VodafoneNetworks.Count > 0;

    /// <summary>
    /// Whether an older build actually left something to clean up on this machine.
    /// </summary>
    /// <remarks>
    /// The button is hidden otherwise. A migration note about a retired sub-feature is
    /// not something to show somebody who installed the app last week.
    /// </remarks>
    public bool IsLegacyCleanupAvailable => _hotspotView.LegacyCleanupAvailable;

    public string HotspotStatusSeverity
    {
        get => _hotspotStatusSeverity;
        private set => Set(ref _hotspotStatusSeverity, value);
    }

    /// <summary>The single suggested next step for this network.</summary>
    public string HotspotSuggestion
    {
        get => _hotspotSuggestion;
        private set => Set(ref _hotspotSuggestion, value);
    }

    /// <summary>When the shown result was measured.</summary>
    public string HotspotCheckedAt
    {
        get => _hotspotCheckedAt;
        private set => Set(ref _hotspotCheckedAt, value);
    }

    /// <summary>The full report, for the details section only.</summary>
    public string HotspotReport
    {
        get => _hotspotReport;
        private set => Set(ref _hotspotReport, value);
    }

    private void CleanUpLegacyHotspot()
    {
        var migration = _service.CleanUpLegacyHotspotConfiguration();
        Raise(nameof(HotspotDiagnostics));
        Raise(nameof(VodafoneModeEnabled));
        RefreshVodafoneNetworks();
        HotspotSuggestion = migration.Summary;
    }

    private void ForgetSelectedVodafoneNetwork()
    {
        if (_selectedVodafoneNetwork is not { } network)
        {
            return;
        }

        _service.ForgetVodafoneNetwork(network.Key);
        RefreshVodafoneNetworks();
    }

    /// <summary>
    /// Rebuilds the saved-network list, and only when it has actually changed.
    /// </summary>
    /// <remarks>
    /// The rebuild is conditional because this runs on every service notification, and
    /// the service notifies often - counters, state transitions, a learned domain. An
    /// unconditional clear-and-refill emptied the selection several times a second, so
    /// "select a network and press forget" was a race the user could not reliably win:
    /// the row was deselected out from under the pointer and the button greyed itself
    /// out again. Comparing first means the list only moves when its contents differ.
    /// </remarks>
    private void RefreshVodafoneNetworks()
    {
        var current = _service.Network;
        var match = _service.Settings.MatchVodafoneNetwork(current);

        var entries = _service.Settings.VodafoneModeNetworks
            .Select(network =>
            {
                var name = string.IsNullOrWhiteSpace(network.DisplayName) ? network.Key : network.DisplayName;
                var adapter = string.IsNullOrWhiteSpace(network.AdapterName)
                    ? string.Empty
                    : $"  ({network.AdapterName})";

                // "Current" is the matcher's answer, not a key comparison: a phone
                // hotspot arrives under a new key every session, and the row the user
                // is sitting on has to be marked as such.
                return new VodafoneNetworkEntry(
                    network.Key,
                    name + adapter,
                    match is not null && ReferenceEquals(match, network));
            })
            .ToList();

        if (!entries.SequenceEqual(VodafoneNetworks))
        {
            var selected = _selectedVodafoneNetwork?.Key;

            VodafoneNetworks.Clear();
            foreach (var entry in entries)
            {
                VodafoneNetworks.Add(entry);
            }

            SelectedVodafoneNetwork = VodafoneNetworks.FirstOrDefault(
                entry => entry.Key == selected);
        }

        Raise(nameof(HasVodafoneNetworks));
        RefreshVodafoneModeStatus();
    }

    /// <summary>The headline and the cards both come from the one structured view.</summary>
    private void RefreshVodafoneModeStatus() => ApplyHotspotView();

    private void RefreshHotspotStatus()
    {
        // A run in progress owns the panel. Any unrelated service change - a counter, a
        // state transition - would otherwise replace "checking..." with the stale line.
        if (IsHotspotBusy)
        {
            return;
        }

        ApplyHotspotView();
    }

    public bool StartEngineOnLaunch
    {
        get => _service.Settings.StartEngineOnLaunch;
        set
        {
            if (_service.Settings.StartEngineOnLaunch == value)
            {
                return;
            }

            _service.Settings.StartEngineOnLaunch = value;
            _service.SaveSettings();
            Raise();
        }
    }

    public async Task ToggleAsync()
    {
        IsBusy = true;
        try
        {
            if (_isRunning)
            {
                await _service.StopAsync().ConfigureAwait(true);
            }
            else
            {
                await _service.StartAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Koruma durumu değiştirilemedi", ex);
            StatusHeadline = "Başlatılamadı";
            StatusDetail = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestartAsync()
    {
        IsBusy = true;
        try
        {
            await _service.StopAsync().ConfigureAwait(true);
            await _service.StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Yeniden başlatma başarısız", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task TestAsync()
    {
        if (!_isRunning)
        {
            ProbeSummary = "Test için önce korumayı başlatın. Bu düğme DPI motorunun gerçek sonucunu sınar.";
            return;
        }

        ProbeSummary = "discord.com test ediliyor…";
        try
        {
            var result = await _service.VerifyAsync().ConfigureAwait(true);
            ProbeSummary = FormatProbe("discord.com", result);
        }
        catch (Exception ex)
        {
            AppLog.Error("discord.com testi başarısız", ex);
            ProbeSummary = $"discord.com testi çalıştırılamadı: {ex.Message}";
        }
    }

    private async Task TestAllAsync()
    {
        if (!_isRunning)
        {
            ProbeSummary = "Test için önce korumayı başlatın.";
            return;
        }

        ProbeSummary = "Tüm Discord adresleri test ediliyor…";
        try
        {
            var results = await _service.VerifyAllAsync().ConfigureAwait(true);
            ProbeSummary = results.Count == 0
                ? "Test sonucu alınamadı; günlük ayrıntılarını denetleyin."
                : string.Join(Environment.NewLine, results.Select(r => FormatProbe(r.Host, r.Result)));
        }
        catch (Exception ex)
        {
            AppLog.Error("Discord adresleri testi başarısız", ex);
            ProbeSummary = $"Discord adresleri test edilemedi: {ex.Message}";
        }
    }

    private async Task RetuneAsync()
    {
        IsTuning = true;
        TuningStatus = "Yöntemler ölçülüyor…";

        try
        {
            var result = await _service.RetuneAsync().ConfigureAwait(true);
            TuningStatus = result?.Winner is null
                ? "Çalışan bir yöntem bulunamadı. Farklı bir DNS modu veya kapsam deneyin."
                : $"Seçilen yöntem: {result.Winner.Name} ({result.Trials.Count} deneme)";
        }
        finally
        {
            IsTuning = false;
        }
    }

    private static string FormatProbe(string host, ProbeResult result) => result.Success
        ? $"{host}: erişilebilir · {result.Elapsed.TotalMilliseconds:F0} ms{(result.HttpStatus is null ? string.Empty : $" · HTTP {result.HttpStatus}")}"
        : $"{host}: {ProtectionService.DescribeOutcome(result.Outcome)}";

    private void OnServiceChanged()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(OnServiceChanged);
            return;
        }

        IsRunning = _service.State is ProtectionState.Running or ProtectionState.Degraded;
        ProtectionState = _service.State;

        StatusHeadline = _service.State switch
        {
            ProtectionState.Running => "Koruma etkin",
            ProtectionState.Degraded => "Koruma etkin, engel sürüyor",
            ProtectionState.Starting => "Başlatılıyor…",
            ProtectionState.Stopping => "Durduruluyor…",
            _ => "Koruma kapalı",
        };

        StatusSeverity = _service.State switch
        {
            ProtectionState.Running => "ok",
            ProtectionState.Degraded => "warn",
            _ => "off",
        };

        StatusDetail = _service.StatusDetail ?? string.Empty;
        NetworkName = _service.Network.DisplayName;
        IspSummary = _service.Detection?.Summary ?? _service.Isp.DisplayName;
        StrategySummary = _service.State == ProtectionState.Stopped ? "-" : _service.Strategy.Name;

        // Reflect what the service actually settled on without writing it back.
        _suppressPersist = true;
        try
        {
            var isp = IspOptions.FirstOrDefault(o => o.Id == _service.Settings.ManualIspProfileId);
            if (isp is not null)
            {
                SelectedIsp = isp;
            }

            var dns = DnsOptions.FirstOrDefault(o => o.Mode == _service.Settings.DnsMode);
            if (dns is not null)
            {
                SelectedDns = dns;
            }
        }
        finally
        {
            _suppressPersist = false;
        }

        RefreshCounters();
        Raise(nameof(VodafoneModeEnabled));
        Raise(nameof(HotspotDiagnostics));
        RefreshVodafoneNetworks();
        RefreshHotspotStatus();
        _lowLatencyMode = _service.Settings.LowLatencyMode;
        Raise(nameof(LowLatencyMode));
        ApplyLatencyStatus(_service.LatencyStatus);
        IsLatencyBusy = _service.IsLatencyBusy;
        Raise(nameof(TrafficGuardEnabled));
        Raise(nameof(TrafficGuardApplication));
        StateChanged?.Invoke();
    }

    private void OnDomainLearned(string domain)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnDomainLearned(domain));
            return;
        }

        RefreshDomains();
        DomainStatusSeverity = "ok";
        DomainStatus = $"Yeni engelli site bulundu ve eklendi: {domain}";
    }

    public event Action? StateChanged;

    private void RefreshCounters()
    {
        var stats = _service.Stats;
        EngineCounters = stats is null
            ? "Motor çalışmıyor."
            : $"El sıkışma incelendi: {stats.Inspected:N0} · yeniden yazıldı: {stats.Rewritten:N0} · "
                + $"parça: {stats.SegmentsSent:N0} · sahte paket: {stats.DecoysSent:N0} · "
                + $"QUIC engellendi: {stats.QuicHandshakesBlocked:N0} · hata: {stats.Errors:N0}";

        DnsSummary = _service.ActiveDnsMode switch
        {
            DnsMode.EncryptedLoopback =>
                $"Şifreli DNS etkin · sağlayıcı: {_service.DnsProviderInUse ?? "Cloudflare"} · "
                    + $"sorgu: {_service.DnsQueriesServed:N0} · önbellek: {_service.DnsCacheHits:N0}",
            DnsMode.PublicResolvers => "Genel çözümleyiciler kullanılıyor (1.1.1.1 · 8.8.8.8 · 9.9.9.9)",
            _ => "Sistem DNS ayarları değiştirilmedi.",
        };
    }

    private void OnHostRewritten(string host, string strategyId)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnHostRewritten(host, strategyId));
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss}  {host}";
        if (ProtectedHosts.Contains(line))
        {
            return;
        }

        ProtectedHosts.Insert(0, line);
        while (ProtectedHosts.Count > 100)
        {
            ProtectedHosts.RemoveAt(ProtectedHosts.Count - 1);
        }
    }

    private void OnTuningProgress(string name, int index, int total)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnTuningProgress(name, index, total));
            return;
        }

        TuningStatus = $"Deneniyor ({index}/{total}): {name}";
    }

    /// <summary>
    /// Queues a log line for the UI, batching whatever arrives before the dispatcher
    /// next comes up for air.
    /// </summary>
    /// <remarks>
    /// This is called from every thread in the app - the packet workers, the tuner,
    /// the DNS proxy, the IPC loop - and the engine logs in bursts. Posting one
    /// dispatcher operation per line meant a start-up sweep could queue hundreds of
    /// them ahead of layout and rendering, and a dispatcher with a backlog it never
    /// clears is a window that never paints: the blank rectangle that reads as a
    /// crashed application. One operation per burst puts the lines on screen just as
    /// promptly and leaves the UI thread free between them.
    /// </remarks>
    private void OnLogWritten(LogEntry entry)
    {
        _pendingLogLines.Enqueue(entry.ToString());

        // Already queued: the pending drain will pick this line up as well.
        if (Interlocked.Exchange(ref _logDrainQueued, 1) == 1)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainLogLines));
        }
        catch (Exception)
        {
            // The dispatcher is shutting down. The lines are already in the log file,
            // and throwing here would take down whichever engine thread logged.
            Interlocked.Exchange(ref _logDrainQueued, 0);
        }
    }

    private void DrainLogLines()
    {
        // Cleared first, so a line written while this is running queues its own drain
        // rather than being left in the queue with nobody coming back for it.
        Interlocked.Exchange(ref _logDrainQueued, 0);

        while (_pendingLogLines.TryDequeue(out var line))
        {
            LogLines.Add(line);
        }

        while (LogLines.Count > LogCapacity)
        {
            LogLines.RemoveAt(0);
        }
    }

    /// <summary>
    /// Fills in what is installed on this machine. Each half stands on its own so a
    /// folder Windows refuses to enumerate costs one line rather than both.
    /// </summary>
    private async Task LoadInstalledAppsAsync()
    {
        try
        {
            var discord = await Task.Run(DiscordDetector.FindDiscord).ConfigureAwait(true);

            DiscordSummary = discord.Count == 0
                ? "Discord bulunamadı. Koruma yine de Discord alan adları için çalışır."
                : string.Join(" · ", discord.Select(d => d.Version is null ? d.Name : $"{d.Name} {d.Version}"));
        }
        catch (Exception ex)
        {
            AppLog.Error("Discord aranamadı", ex);
            DiscordSummary = "Discord aranamadı. Koruma yine de Discord alan adları için çalışır.";
        }

        try
        {
            var browsers = await Task.Run(DiscordDetector.FindBrowsers).ConfigureAwait(true);

            BrowserSummary = browsers.Count == 0
                ? "Kurulu tarayıcı bulunamadı."
                : string.Join(" · ", browsers.Select(b => b.Name));
        }
        catch (Exception ex)
        {
            AppLog.Error("Tarayıcılar aranamadı", ex);
            BrowserSummary = "Tarayıcılar aranamadı.";
        }
    }

    /// <summary>
    /// Reads the Windows startup registration, off the UI thread from the first line.
    /// </summary>
    /// <remarks>
    /// Started from the constructor, which runs before the window has drawn anything,
    /// so the part of it that executes synchronously matters. Every step here shells out
    /// to <c>schtasks.exe</c>, and launching a process is synchronous work up to the
    /// first await - on a machine still busy with the installer that started this one,
    /// that is real time spent in front of the first frame for a checkbox nobody is
    /// looking at yet. Handed to the thread pool so none of it is.
    /// </remarks>
    private async Task LoadAutoStartStateAsync()
    {
        try
        {
            // A registration the user has asked for is repaired rather than merely
            // reported. Between two runs a task can be deleted, a Run value cleaned
            // away, or a task written by an older build found to have no trigger of its
            // own - and the old behaviour was to read that as "the user turned it off"
            // and untick the checkbox, which is how autostart went missing and stayed
            // missing. A switch genuinely turned off in Windows is still respected:
            // EnsureRegisteredAsync leaves that case alone.
            var wanted = _service.Settings.StartWithWindows;
            var status = await Task.Run(() => wanted
                ? _autoStart.EnsureRegisteredAsync(_service.Settings.StartMinimised)
                : _autoStart.InspectAsync()).ConfigureAwait(true);

            var enabled = status.IsEnabled;

            // Windows Settings owns its Startup Apps switch too. If the user changes
            // it there, that decision must flow back into our checkbox instead of the
            // app immediately registering itself again and undoing their choice.
            if (_service.Settings.StartWithWindows != enabled)
            {
                _service.Settings.StartWithWindows = enabled;
                _service.SaveSettings();
                Raise(nameof(StartWithWindows));
            }
        }
        catch (Exception ex)
        {
            // Autostart is a convenience. Losing it must not cost the window the rest
            // of its start-up, which is what an unobserved fault here used to do.
            AppLog.Error("Otomatik başlatma durumu okunamadı", ex);
        }
    }

    private static void OpenLogFolder()
    {
        try
        {
            AppPaths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Günlük klasörü açılamadı", ex);
        }
    }

    /// <summary>
    /// Copies the on-screen log view to the clipboard. The clipboard can be briefly
    /// owned by another process, so a refusal is logged rather than thrown - the log
    /// file is untouched either way.
    /// </summary>
    private void CopyLogToClipboard()
    {
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, LogLines));
            AppLog.Info($"Günlük görünümündeki {LogLines.Count} satır panoya kopyalandı.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Günlük panoya kopyalanamadı", ex);
        }
    }

    /// <summary>
    /// Clears the on-screen view only. The file on disk keeps every line, which is
    /// what the page caption says; pending lines queued for display are dropped with
    /// the view so "temizle" does what it says.
    /// </summary>
    private void ClearLogView()
    {
        _pendingLogLines.Clear();
        LogLines.Clear();
    }

    public void Detach()
    {
        _refreshTimer.Stop();
        _service.Changed -= OnServiceChanged;
        _service.HostRewritten -= OnHostRewritten;
        _service.TuningProgress -= OnTuningProgress;
        _service.DomainLearned -= OnDomainLearned;
        AppLog.Written -= OnLogWritten;
    }
}
