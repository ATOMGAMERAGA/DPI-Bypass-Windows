using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DpiBypass.Core.Config;
using Microsoft.Win32;

namespace DpiBypass.App.Infrastructure;

/// <summary>
/// Puts a Windows 11 system backdrop behind the window - but only where Windows is
/// going to draw it, and never without recording what it did.
/// </summary>
/// <remarks>
/// <para>
/// A backdrop is switched on by handing the client area over to the compositor: WPF
/// stops painting a background and DWM paints the material behind it. When DWM does
/// paint, that is what makes an app look like it belongs on Windows 11. When it does
/// not, the window is a hole - title bar present, controls present, body showing
/// whatever is behind it - which to somebody who just double-clicked a shortcut is
/// indistinguishable from the app never opening. So the material is requested only when
/// every precondition holds, the transparency is applied strictly after DWM has accepted
/// the attribute, and anything unexpected rolls the window back to an opaque surface.
/// </para>
/// <para>
/// Three things about the mechanism are not obvious and each of them has been a bug here.
/// </para>
/// <para>
/// <b>The frame has to be extended.</b> <c>DWMWA_SYSTEMBACKDROP_TYPE</c> tells DWM which
/// material to use; it does not by itself extend the material under the client area.
/// <c>DwmExtendFrameIntoClientArea</c> with -1 margins is what does that, and without it
/// the attribute is accepted, the app dutifully stops painting, and the material appears
/// nowhere. WPF's own <c>WindowBackdropManager</c> calls it; this used not to.
/// </para>
/// <para>
/// <b>WPF is already doing this.</b> Setting <c>ThemeMode</c> on the application or the
/// window puts WPF's Fluent theming in charge, and its <c>ThemeManager.ApplyStyleOnWindow</c>
/// calls <c>WindowBackdropManager.SetBackdrop(window, MainWindow)</c> from
/// <c>Window.CreateSourceWindow</c> - so on a 22H2 machine Mica is already on before the
/// first frame, with the composition target already transparent. Two managers on one
/// window means whichever ran last wins, and a user who picked Acrylic would silently get
/// Mica back on the next theme change. The app therefore opts WPF's manager out through
/// <c>Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop</c> (see the
/// runtimeconfig options in DpiBypass.App.csproj) and owns the window alone. Fluent's
/// control chrome is unaffected; only the backdrop moves.
/// </para>
/// <para>
/// <b>An opaque Window.Background hides it.</b> Whatever DWM paints is behind the client
/// area, so a window whose own Background brush is opaque covers it completely. Clearing
/// that brush is the last step of applying a material and the first step of removing one.
/// </para>
/// </remarks>
public static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;

    // DWM_SYSTEMBACKDROP_TYPE
    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;
    private const int DwmsbtTransientWindow = 3;
    private const int DwmsbtTabbedWindow = 4;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int SmRemoteSession = 0x1000;

    /// <summary>Battery saver is on. SYSTEM_POWER_STATUS.SystemStatusFlag.</summary>
    private const byte PowerSavingOn = 1;

    /// <summary>What the last apply did, in one line, for the log and the status bar.</summary>
    public static string Availability { get; private set; } = "denenmedi";

    /// <summary>The last full result, including the evidence behind it.</summary>
    public static BackdropOutcome Last { get; private set; } = BackdropOutcome.NotAttempted;

    /// <summary>
    /// Reads everything about this machine that decides whether a material will be drawn.
    /// </summary>
    /// <remarks>
    /// Every probe is individually guarded. A machine that will not answer one question is
    /// treated as one that cannot draw the effect that question was about, which is the
    /// safe direction: the cost of being wrong here is a flat window, and the cost of
    /// being wrong the other way is an invisible one.
    /// </remarks>
    public static BackdropEnvironment ReadEnvironment() => new(
        OsBuild: Environment.OSVersion.Version.Build,
        CompositionEnabled: Probe(() => DwmIsCompositionEnabled(out var on) == 0 && on, fallback: false),
        RemoteSession: Probe(() => GetSystemMetrics(SmRemoteSession) != 0, fallback: true),
        HighContrast: Probe(() => SystemParameters.HighContrast, fallback: true),
        HardwareAccelerated: Probe(() => (RenderCapability.Tier >> 16) > 0, fallback: false),
        TransparencyEffects: Probe(TransparencyEffectsEnabled, fallback: false),
        BatterySaver: Probe(BatterySaverEngaged, fallback: false));

    /// <summary>
    /// Applies the material the user asked for, as far as this machine will take it.
    /// </summary>
    /// <returns>
    /// What happened, with the evidence. The caller keeps painting its own background
    /// unless <see cref="BackdropOutcome.Applied"/> is true.
    /// </returns>
    public static BackdropOutcome Apply(Window window, AppearanceMode requested, bool darkMode)
    {
        // The title bar follows the app theme whether or not a material is used.
        UpdateTitleBarTheme(window, darkMode);

        var environment = ReadEnvironment();
        var decision = BackdropPolicy.Decide(requested, environment);

        if (!decision.UsesCompositor)
        {
            RemoveCore(window);
            return Record(new BackdropOutcome(
                requested, decision.Material, Applied: false, decision.Reason, decision.Downgraded, environment, HResult: 0));
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return Record(new BackdropOutcome(
                requested, decision.Material, Applied: false, "Pencere tanıtıcısı henüz yok.", decision.Downgraded, environment, HResult: 0));
        }

        HwndSource? source;
        try
        {
            source = HwndSource.FromHwnd(handle);
        }
        catch (Exception)
        {
            source = null;
        }

        if (source?.CompositionTarget is null)
        {
            // Without the render target there is no way to make the client area
            // transparent, and a window that keeps painting over the material just looks
            // like a normal window - so leave it as one.
            return Record(new BackdropOutcome(
                requested, decision.Material, Applied: false, "Pencerenin işleme hedefi yok.", decision.Downgraded, environment, HResult: 0));
        }

        try
        {
            // Order matters and this is the order. Extend the frame first so the material
            // has somewhere to be drawn, then name the material, and only once DWM has
            // accepted it stop painting over the top.
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            var extended = DwmExtendFrameIntoClientArea(handle, ref margins);

            var value = ToDwmValue(decision.Material);
            var result = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref value, sizeof(int));
            if (result != 0)
            {
                ResetFrame(handle);
                return Record(new BackdropOutcome(
                    requested,
                    decision.Material,
                    Applied: false,
                    $"DWM isteği reddetti (0x{result:X8}).",
                    decision.Downgraded,
                    environment,
                    result));
            }

            if (extended != 0)
            {
                // Accepted the material but refused to extend the frame under the client
                // area. Carrying on would hand the client area to a compositor that has
                // nowhere to paint it: the exact shape of the invisible-window failure.
                var none = DwmsbtNone;
                DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref none, sizeof(int));
                return Record(new BackdropOutcome(
                    requested,
                    decision.Material,
                    Applied: false,
                    $"Çerçeve istemci alanına genişletilemedi (0x{extended:X8}).",
                    decision.Downgraded,
                    environment,
                    extended));
            }

            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;

            return Record(new BackdropOutcome(
                requested, decision.Material, Applied: true, decision.Reason, decision.Downgraded, environment, HResult: 0));
        }
        catch (DllNotFoundException)
        {
            return Record(Failed(requested, decision, environment, "dwmapi.dll bulunamadı."));
        }
        catch (EntryPointNotFoundException)
        {
            return Record(Failed(requested, decision, environment, "dwmapi bu çağrıyı tanımıyor."));
        }
        catch (Exception ex)
        {
            return Record(Failed(requested, decision, environment, ex.Message));
        }
    }

    /// <summary>
    /// Puts an opaque window back: used when the material stops being drawn while the app
    /// is running, and whenever the user picks the flat surface.
    /// </summary>
    public static void Remove(Window window)
    {
        RemoveCore(window);
        Availability = "düz";
        Last = Last with { Applied = false, Reason = "Düz yüzeye alındı." };
    }

    private static void RemoveCore(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;

        try
        {
            if (handle != nint.Zero)
            {
                var none = DwmsbtNone;
                DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref none, sizeof(int));
                ResetFrame(handle);

                var source = HwndSource.FromHwnd(handle);
                if (source?.CompositionTarget is not null)
                {
                    // The window's own colour, not black. The composition target is what
                    // shows during a resize before WPF has painted the new area, and black
                    // there is the flicker people describe as the window "tearing".
                    source.CompositionTarget.BackgroundColor = SurfaceColour(window);
                }
            }
        }
        catch (Exception)
        {
            // The background reference below is what actually makes it readable.
        }

        window.SetResourceReference(Window.BackgroundProperty, "AppWindowBackgroundBrush");
    }

    /// <summary>Keeps the title bar's light/dark rendering in step with the app theme.</summary>
    public static void UpdateTitleBarTheme(Window window, bool darkMode)
    {
        if (Environment.OSVersion.Version < new Version(10, 0, 18985))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        try
        {
            var dark = darkMode ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }

    /// <summary>
    /// Whether the machine would still draw the material that is currently applied.
    /// </summary>
    /// <remarks>
    /// Used by the watchdog. Returns the reason it would not, or null when it would - the
    /// answer is a sentence rather than a bool because it ends up in the log, and "the
    /// window went flat" with no reason attached is the report nobody can act on.
    /// </remarks>
    public static string? DescribeLoss(AppearanceMode requested)
    {
        var decision = BackdropPolicy.Decide(requested, ReadEnvironment());
        return decision.UsesCompositor ? null : decision.Reason;
    }

    /// <summary>Everything the diagnostics page and the log need, as lines.</summary>
    public static string Explain()
    {
        var outcome = Last;
        var environment = outcome.Environment;

        var text = new StringBuilder();
        text.Append("istenen=").Append(BackdropPolicy.Describe(outcome.Requested));
        text.Append(" · uygulanan=").Append(outcome.Applied ? outcome.Material.ToString() : "yok");
        text.Append(" · neden=").Append(outcome.Reason);
        text.Append(" · yapı=").Append(environment.OsBuild);
        text.Append(" · birleştirme=").Append(environment.CompositionEnabled ? "açık" : "kapalı");
        text.Append(" · hızlandırma=").Append(environment.HardwareAccelerated ? "açık" : "kapalı");
        text.Append(" · saydamlık=").Append(environment.TransparencyEffects ? "açık" : "kapalı");
        text.Append(" · yüksek karşıtlık=").Append(environment.HighContrast ? "açık" : "kapalı");
        text.Append(" · uzak oturum=").Append(environment.RemoteSession ? "evet" : "hayır");
        text.Append(" · pil tasarrufu=").Append(environment.BatterySaver ? "açık" : "kapalı");

        if (outcome.HResult != 0)
        {
            text.Append(" · HRESULT=0x").Append(outcome.HResult.ToString("X8"));
        }

        return text.ToString();
    }

    private static BackdropOutcome Failed(
        AppearanceMode requested,
        BackdropDecision decision,
        BackdropEnvironment environment,
        string reason)
        => new(requested, decision.Material, Applied: false, reason, decision.Downgraded, environment, HResult: 0);

    private static BackdropOutcome Record(BackdropOutcome outcome)
    {
        Last = outcome;
        Availability = outcome.Applied
            ? outcome.Material switch
            {
                BackdropMaterial.Acrylic => "Acrylic",
                BackdropMaterial.MicaAlt => "Mica Alt",
                _ => "Mica",
            }
            : outcome.Reason;

        return outcome;
    }

    private static int ToDwmValue(BackdropMaterial material) => material switch
    {
        BackdropMaterial.Acrylic => DwmsbtTransientWindow,
        BackdropMaterial.MicaAlt => DwmsbtTabbedWindow,
        BackdropMaterial.Mica => DwmsbtMainWindow,
        _ => DwmsbtNone,
    };

    /// <summary>Undoes the extended frame, so a window that goes flat has an ordinary one.</summary>
    private static void ResetFrame(nint handle)
    {
        try
        {
            var margins = default(Margins);
            DwmExtendFrameIntoClientArea(handle, ref margins);
        }
        catch (Exception)
        {
            // Leaving the frame extended costs a hairline at the window edge, nothing more.
        }
    }

    private static System.Windows.Media.Color SurfaceColour(Window window)
    {
        try
        {
            return window.TryFindResource("AppWindowBackgroundBrush") is SolidColorBrush brush
                ? brush.Color
                : System.Windows.SystemColors.WindowColor;
        }
        catch (Exception)
        {
            return System.Windows.SystemColors.WindowColor;
        }
    }

    private static bool TransparencyEffectsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("EnableTransparency") is not int value || value != 0;
    }

    private static bool BatterySaverEngaged()
        => GetSystemPowerStatus(out var status) && status.SystemStatusFlag == PowerSavingOn;

    /// <summary>Runs one environment probe, answering <paramref name="fallback"/> if it throws.</summary>
    private static bool Probe(Func<bool> read, bool fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static T Probe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>One attempt at a backdrop, with the evidence behind the answer.</summary>
/// <param name="Requested">What the user asked for.</param>
/// <param name="Material">What the policy chose.</param>
/// <param name="Applied">Whether the client area is now the compositor's.</param>
/// <param name="Reason">The sentence shown to the user and written to the log.</param>
/// <param name="Downgraded">Whether the user got something other than what they picked.</param>
/// <param name="Environment">What the machine said when it was asked.</param>
/// <param name="HResult">The DWM return code, when one failed.</param>
public readonly record struct BackdropOutcome(
    AppearanceMode Requested,
    BackdropMaterial Material,
    bool Applied,
    string Reason,
    bool Downgraded,
    BackdropEnvironment Environment,
    int HResult)
{
    public static readonly BackdropOutcome NotAttempted = new(
        AppearanceMode.System,
        BackdropMaterial.None,
        Applied: false,
        "Henüz denenmedi.",
        Downgraded: false,
        default,
        HResult: 0);
}
