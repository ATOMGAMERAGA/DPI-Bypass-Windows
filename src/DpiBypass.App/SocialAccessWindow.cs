using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DpiBypass.Core;
using DpiBypass.Core.SocialAccess;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace DpiBypass.App;

/// <summary>One form and one coordinator for both modes; URL remains in this window/session only.</summary>
public sealed class SocialAccessWindow : Window
{
    private readonly IProtectionController _service;
    private readonly WindowsSocialRuntime _runtime;
    private readonly CheckBox _enabled = new() { Content = "Sosyal Erişim Deneyleri — Alpha etkin" };
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "Manuel Test", "Otomatik Test — Beta" }, SelectedIndex = 0 };
    private readonly ComboBox _platform = new() { ItemsSource = new[] { "YouTube", "Instagram" }, SelectedIndex = 0 };
    private readonly ComboBox _problem = new() { ItemsSource = new[] { "Site/uygulama açılmıyor", "Yaş veya kimlik doğrulaması isteniyor", "Bot doğrulaması isteniyor", "İçerik açılıyor ama medya çalışmıyor", "Yavaşlık", "Emin değilim" }, SelectedIndex = 0 };
    private readonly ComboBox _goal = new() { ItemsSource = new[] { "Mevcut hesabımla seçtiğim işlemi yapmak", "Herkese açık içeriği görüntülemek" }, SelectedIndex = 0 };
    private readonly ComboBox _operation = new() { ItemsSource = new[] { "Seçilen içeriği açmak", "Video oynatmak", "Hesabın akışını görmek" }, SelectedIndex = 0 };
    private readonly ComboBox _surface = new() { ItemsSource = new[] { "Mevcut resmî uygulama", "Resmî tarayıcı sekmesi" }, SelectedIndex = 0 };
    private readonly ComboBox _baseline = new() { ItemsSource = new[] { "Evet, sorun bu içerikte/hesapta hâlâ var", "Hayır, şu anda açık", "Başlangıç durumu bilinmiyor" }, SelectedIndex = 0 };
    private readonly CheckBox _allowWeb = new() { Content = "Aynı içeriği resmî web'de denemeye izin veriyorum" };
    private readonly CheckBox _network = new() { Content = "Mevcut motorun koruma kapsamındaki yeni bağlantılarda profil değişimi denenebilir (tek sekme izolasyonu yok)" };
    private readonly CheckBox _data = new() { Content = "Bu turda test sekmesine izin ver (ağ maliyeti bilinmiyor, tarayıcı bayt sınırı tahmini; ölçülü veri harcayabilir)" };
    private readonly TextBox _url = new() { MaxLength = 2048 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 12) };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<Button> _feedback = [];
    private Button _start = null!;
    private bool _closing;
    public SocialAccessWindow(IProtectionController service)
    {
        _service = service; _runtime = service.SocialExperiments;
        Title = "Sosyal Erişim Deneyleri — Alpha"; Width = 680; Height = 850; MinWidth = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(new TextBlock { Text = "Sosyal Erişim Deneyleri — Alpha", FontSize = 22, FontWeight = FontWeights.SemiBold });
        Note(body, "Uygun yöntemleri sırayla dener. Her denemeden sonra sonucu sen bildirirsin. Her platformda çalışmayabilir.");
        Note(body, "Otomatik modda yalnızca yeterli hedef kanıtı karar üretir. Bu cihazda bağlantı testi otomatik; uygulamadaki hesap ekranını kendin kontrol etmen gerekebilir. HTTP yanıtı içerik/hesap başarısı değildir.");
        body.Children.Add(_enabled); Field(body, "Test modu", _mode); Field(body, "Platform", _platform); Field(body, "Sorun", _problem);
        Field(body, "Kullanım hedefi", _goal); Field(body, "Seçilen işlem", _operation); Field(body, "Özgün kullanım yüzeyi", _surface);
        Field(body, "Seçtiğiniz içerik URL'si (yalnızca resmî HTTPS video/gönderi)", _url);
        Field(body, "Başlangıç: sorun şu anda aynı içerikte/hesapta var mı?", _baseline);
        body.Children.Add(_allowWeb); body.Children.Add(_network); body.Children.Add(_data);
        foreach (var check in new[] { _enabled, _allowWeb, _network, _data }) check.Margin = new Thickness(0, 8, 0, 4);
        Note(body, "Otomatik web gözlemi için browser-companion klasöründeki yerel companion ayrıca kurulmalı ve seçilen hedef sekmeye bağlanmalı. Bu sürümün içerik detector'ı YouTube /watch sayfasıyla sınırlıdır. Instagram/native hesap ekranları manuel incelenir. Hesabınıza yalnızca resmî sitede kendiniz giriş yapın.");
        _start = Action(body, "Başlat", StartAsync);
        Action(body, "Manuel devam et", () => _runtime.Coordinator.ContinueManuallyAsync());
        Action(body, "Yaptım, otomatik teste devam et", () => _runtime.Coordinator.ResumeObservationAsync());
        var responses = new WrapPanel(); body.Children.Add(responses);
        foreach (var (label, outcome) in new[] { ("Hâlâ var", UserOutcome.STILL_PRESENT), ("Yok, açıldı", UserOutcome.RESOLVED),
            ("Test edemedim", UserOutcome.NOT_TESTED), ("Bir kısmı çalışıyor", UserOutcome.PARTIAL) })
            _feedback.Add(Action(responses, label, () => _runtime.Coordinator.FeedbackAsync(_runtime.Coordinator.CreateFeedback(outcome))));
        Action(body, "Böyle kullan (kısmî sonuç)", () => _runtime.Coordinator.AcceptPartialAsync());
        Action(body, "Bende hâlâ sorun var", () => _runtime.Coordinator.CorrectAutoPassAsync());
        Action(body, "Kalanları dene", () => _runtime.Coordinator.ResumeRemainingAsync());
        Action(body, "Durdur / geri al", () => _runtime.Coordinator.StopAsync());
        body.Children.Add(_status); body.Children.Add(new Expander { Header = "Tanılama ve kapsam", Content = _detail });
        Action(body, "Tanılama özetini dışa aktar", ExportAsync);
        Action(body, "Geçmişi sil", () => { _runtime.History.Clear(); _detail.Text = "Yerel geçmiş silindi; etkin kurtarma bilgisi korundu."; return Task.CompletedTask; });
        _enabled.IsChecked = service.Settings.SocialAccess.Enabled;
        _mode.SelectedIndex = service.Settings.SocialAccess.TestMode == "AUTO_BETA" ? 1 : 0;
        _network.IsChecked = service.Settings.SocialAccess.AllowNetworkChanges;
        _data.IsChecked = false; // one-session data preference is never replayed on startup
        _enabled.Click += async (_, _) => await RunAsync(async () => { await _runtime.EnableAsync(_enabled.IsChecked == true); service.SaveSettings(); });
        _mode.SelectionChanged += async (_, _) =>
        {
            service.Settings.SocialAccess.TestMode = _mode.SelectedIndex == 1 ? "AUTO_BETA" : "MANUAL"; service.SaveSettings();
            if (_runtime.Coordinator.Context is not null) await RunAsync(() => _runtime.Coordinator.SwitchModeAsync(_mode.SelectedIndex == 1 ? TestMode.AUTO_BETA : TestMode.MANUAL));
        };
        _runtime.Coordinator.Changed += OnChanged;
        _problem.SelectionChanged += (_, _) => { if (_problem.SelectedIndex == 3) _operation.SelectedIndex = 1; };
        Loaded += async (_, _) => { if (_enabled.IsChecked == true) await RunAsync(() => _runtime.EnableAsync(true)); Refresh(); };
        Closing += async (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            await RunAsync(() => _runtime.Coordinator.StopAsync());
            if (_runtime.Coordinator.State.Phase == ExperimentPhase.RECOVERY_REQUIRED) return;
            _runtime.Coordinator.Changed -= OnChanged; _closing = true; Close();
        };
    }
    private async Task StartAsync()
    {
        if (_enabled.IsChecked != true) { _status.Text = "Önce Alpha özelliğini etkinleştirin."; return; }
        var platform = _platform.SelectedIndex == 0 ? "youtube" : "instagram";
        if (!SocialTarget.TryCreate(platform, _url.Text.Trim(), out var target)) { _status.Text = "Desteklenen resmî HTTPS içerik URL'sini seçin."; return; }
        var original = _surface.SelectedIndex == 0 ? "OFFICIAL_NATIVE_APP" : "OFFICIAL_WEB";
        var surfaces = _allowWeb.IsChecked == true ? new[] { original, "OFFICIAL_WEB" }.Distinct().ToArray() : [original];
        var problem = _problem.SelectedIndex switch { 0 => ProblemClass.NETWORK_UNREACHABLE, 1 => ProblemClass.AGE_OR_ID_VERIFICATION_REQUIRED,
            2 => ProblemClass.BOT_CHALLENGE, 3 => ProblemClass.MEDIA_PATH_FAILURE, 4 => ProblemClass.POSSIBLE_THROTTLING, _ => ProblemClass.UNKNOWN };
        var goal = new SocialGoal { PlatformId = platform, ProblemClass = problem, OriginalSurface = original, IdentityContext = _goal.SelectedIndex == 0 ? "SAME_ACCOUNT" : "ANONYMOUS",
            AllowedSurfaces = surfaces, PublicFallbackAllowed = _goal.SelectedIndex == 1,
            RequiredCapabilities = [problem == ProblemClass.MEDIA_PATH_FAILURE ? "PLAY_MEDIA" : _operation.SelectedIndex switch { 1 => "PLAY_MEDIA", 2 => "VIEW_ACCOUNT_FEED", _ => "VIEW_SELECTED_CONTENT" }] };
        _service.Settings.SocialAccess.AllowNetworkChanges = _network.IsChecked == true;
        _service.Settings.SocialAccess.AllowMeteredRenderer = _data.IsChecked == true;
        _service.SaveSettings();
        await _runtime.Coordinator.StartAsync(goal, target!, _mode.SelectedIndex == 1 ? TestMode.AUTO_BETA : TestMode.MANUAL,
            _baseline.SelectedIndex switch { 0 => true, 1 => false, _ => (bool?)null });
    }
    private void OnChanged() => Dispatcher.BeginInvoke(Refresh);
    private void Refresh()
    {
        var state = _runtime.Coordinator.State;
        _status.Text = state.Message;
        var result = _runtime.Coordinator.Results.LastOrDefault();
        _detail.Text = $"Kapsam: {_runtime.Coordinator.Context?.Candidate.ScopeNote}\nBağlantı: {_runtime.LastNetworkProbe?.Path ?? "ölçülmedi"} · {_runtime.LastNetworkProbe?.Outcome ?? "UNKNOWN"}\n" +
            $"İçerik / Hesap / Medya: {result?.AutoVerdict.ToString() ?? "ölçülmedi"} · {result?.ReasonCode ?? ""}\n" +
            $"Tur: {_runtime.Coordinator.Budget.Attempts}/12 yöntem · {_runtime.Coordinator.Budget.Requests}/24 tanılama isteği · {_runtime.Coordinator.Budget.Bytes}/5242880 bayt\n" +
            "Tek hedefte gözlenen sonuç diğer hesap/uygulama özelliklerini doğrulamaz. Saha etkisi: FIELD_UNVERIFIED.";
        _start.IsEnabled = !_runtime.Coordinator.HasResources && state.Phase is ExperimentPhase.IDLE or ExperimentPhase.DISABLED or ExperimentPhase.FINISHED or ExperimentPhase.PAUSED;
        var canResolve = state.Phase is ExperimentPhase.PROBING or ExperimentPhase.AUTO_EVALUATING || state.Phase == ExperimentPhase.PAUSED && state.PauseReason == "USER_ACTION_REQUIRED";
        foreach (var b in _feedback) b.IsEnabled = state.Phase == ExperimentPhase.WAITING_FOR_USER || (b.Content is string s && s == "Yok, açıldı" && canResolve);
    }
    private Task ExportAsync()
    {
        if (MessageBox.Show(this, JsonSerializer.Serialize(_runtime.Coordinator.Results, SocialJson.Options), "Tanılama önizlemesi",
            MessageBoxButton.OKCancel) != MessageBoxResult.OK) return Task.CompletedTask;
        var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "social-alpha-diagnostics.json" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_runtime.Coordinator.Results, SocialJson.Options));
        return Task.CompletedTask;
    }
    private async Task RunAsync(Func<Task> action)
    { try { await action(); } catch (Exception) { _status.Text = "İşlem tamamlanamadı. Etkin kaynak varsa önce Durdur / geri al işlemini kullanın."; } finally { Refresh(); } }
    private Button Action(Panel parent, string label, Func<Task> action)
    { var b = new Button { Content = label, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(12, 7, 12, 7) }; b.Click += async (_, _) => await RunAsync(action); parent.Children.Add(b); return b; }
    private static void Field(Panel parent, string label, Control field)
    { parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 3) }); parent.Children.Add(field); }
    private static void Note(Panel parent, string text) => parent.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
}
