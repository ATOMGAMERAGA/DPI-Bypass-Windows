using System.Collections.ObjectModel;
using System.Windows.Threading;
using AtomDpi.App.Infrastructure;
using AtomDpi.Core;
using AtomDpi.Core.Apps;
using AtomDpi.Core.Diagnostics;
using AtomDpi.Core.Dns;
using AtomDpi.Core.Engine;
using AtomDpi.Core.Logging;
using AtomDpi.Core.Network;
using AtomDpi.Core.Startup;

namespace AtomDpi.App.ViewModels;

public sealed record IspOption(string? Id, string Display)
{
    public override string ToString() => Display;
}

public sealed record StrategyOption(string? Id, string Display, string Description)
{
    public override string ToString() => Display;
}

public sealed record ScopeOption(ProtectionScope Scope, string Title, string Description);

public sealed record DnsOption(DnsMode Mode, string Display, string Description)
{
    public override string ToString() => Display;
}

/// <summary>Everything the main window binds to.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ProtectionService _service;
    private readonly AutoStartManager _autoStart;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;

    private string _statusHeadline = "Koruma kapalı";
    private string _statusDetail = "Başlatmak için düğmeye dokunun.";
    private string _statusSeverity = "off";
    private bool _isRunning;
    private bool _isBusy;
    private string _networkName = "-";
    private string _ispSummary = "-";
    private string _strategySummary = "-";
    private string _probeSummary = "Henüz test edilmedi.";
    private string _tuningStatus = string.Empty;
    private string _engineCounters = "-";
    private string _dnsSummary = "-";
    private string _discordSummary = "Aranıyor…";
    private string _browserSummary = "Aranıyor…";
    private bool _tuningInProgress;
    private IspOption _selectedIsp;
    private StrategyOption _selectedStrategy;
    private DnsOption _selectedDns;
    private ScopeOption _selectedScope;
    private bool _suppressPersist;

    public MainViewModel(ProtectionService service, Dispatcher dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
        _autoStart = new AutoStartManager(log: AppLog.InfoSink);

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
                ProtectionScope.DiscordAndBrowsers,
                "Discord + tarayıcılar",
                "Discord uygulamasına ek olarak tarayıcılardaki tüm siteler de korunur."),
            new ScopeOption(
                ProtectionScope.Everything,
                "Tüm sistem",
                "Bilgisayardaki bütün programların HTTPS/HTTP bağlantıları korunur."),
        ];

        _selectedIsp = IspOptions.FirstOrDefault(o => o.Id == _service.Settings.ManualIspProfileId) ?? IspOptions[0];
        _selectedStrategy = StrategyOptions.FirstOrDefault(o => o.Id == _service.Settings.ManualStrategyId) ?? StrategyOptions[0];
        _selectedDns = DnsOptions.First(o => o.Mode == _service.Settings.DnsMode);
        _selectedScope = ScopeOptions.First(o => o.Scope == _service.Settings.Scope);

        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        TestCommand = new AsyncRelayCommand(TestAsync, () => _isRunning);
        RetuneCommand = new AsyncRelayCommand(RetuneAsync, () => _isRunning && !_tuningInProgress);
        TestAllCommand = new AsyncRelayCommand(TestAllAsync, () => _isRunning);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);

        _service.Changed += OnServiceChanged;
        _service.HostRewritten += OnHostRewritten;
        _service.TuningProgress += OnTuningProgress;
        AppLog.Written += OnLogWritten;

        foreach (var entry in AppLog.Snapshot())
        {
            LogLines.Add(entry.ToString());
        }

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshCounters();
        _refreshTimer.Start();

        _ = LoadInstalledAppsAsync();
        _ = LoadAutoStartStateAsync();
    }

    public ObservableCollection<IspOption> IspOptions { get; }

    public ObservableCollection<StrategyOption> StrategyOptions { get; }

    public ObservableCollection<DnsOption> DnsOptions { get; }

    public ObservableCollection<ScopeOption> ScopeOptions { get; }

    public ObservableCollection<string> LogLines { get; } = [];

    public ObservableCollection<string> ProtectedHosts { get; } = [];

    public AsyncRelayCommand ToggleCommand { get; }

    public AsyncRelayCommand TestCommand { get; }

    public AsyncRelayCommand RetuneCommand { get; }

    public AsyncRelayCommand TestAllCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

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

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public string ToggleCaption => _isRunning ? "Korumayı durdur" : "Korumayı başlat";

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
        ProbeSummary = "discord.com test ediliyor…";
        var result = await _service.VerifyAsync().ConfigureAwait(true);
        ProbeSummary = FormatProbe("discord.com", result);
    }

    private async Task TestAllAsync()
    {
        ProbeSummary = "Tüm Discord adresleri test ediliyor…";
        var results = await _service.VerifyAllAsync().ConfigureAwait(true);
        ProbeSummary = string.Join(Environment.NewLine, results.Select(r => FormatProbe(r.Host, r.Result)));
    }

    private async Task RetuneAsync()
    {
        _tuningInProgress = true;
        RetuneCommand.RaiseCanExecuteChanged();
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
            _tuningInProgress = false;
            RetuneCommand.RaiseCanExecuteChanged();
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
        StateChanged?.Invoke();
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

    private void OnLogWritten(LogEntry entry)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnLogWritten(entry));
            return;
        }

        LogLines.Add(entry.ToString());
        while (LogLines.Count > 500)
        {
            LogLines.RemoveAt(0);
        }
    }

    private async Task LoadInstalledAppsAsync()
    {
        var discord = await Task.Run(DiscordDetector.FindDiscord).ConfigureAwait(true);
        var browsers = await Task.Run(DiscordDetector.FindBrowsers).ConfigureAwait(true);

        DiscordSummary = discord.Count == 0
            ? "Discord bulunamadı. Koruma yine de Discord alan adları için çalışır."
            : string.Join(" · ", discord.Select(d => d.Version is null ? d.Name : $"{d.Name} {d.Version}"));

        BrowserSummary = browsers.Count == 0
            ? "Kurulu tarayıcı bulunamadı."
            : string.Join(" · ", browsers.Select(b => b.Name));
    }

    private async Task LoadAutoStartStateAsync()
    {
        var enabled = await _autoStart.IsEnabledAsync().ConfigureAwait(true);

        // Reconcile: the setting says it should be on but the task is missing (for
        // example after the app folder moved), so put it back.
        if (_service.Settings.StartWithWindows && !enabled)
        {
            await _autoStart.EnableAsync(_service.Settings.StartMinimised).ConfigureAwait(true);
        }
        else if (!_service.Settings.StartWithWindows && enabled)
        {
            await _autoStart.DisableAsync().ConfigureAwait(true);
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

    public void Detach()
    {
        _refreshTimer.Stop();
        _service.Changed -= OnServiceChanged;
        _service.HostRewritten -= OnHostRewritten;
        _service.TuningProgress -= OnTuningProgress;
        AppLog.Written -= OnLogWritten;
    }
}
