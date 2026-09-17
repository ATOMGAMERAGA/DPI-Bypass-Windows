namespace DpiBypass.Core.Config;

/// <summary>What the user asked the window to look like.</summary>
/// <remarks>
/// A preference, not an outcome: what actually gets drawn depends on the machine, and
/// <see cref="BackdropPolicy"/> is what turns one into the other.
/// </remarks>
public enum AppearanceMode
{
    /// <summary>Let Windows decide: Mica where it is supported, a flat surface otherwise.</summary>
    System = 0,

    /// <summary>Mica. The desktop wallpaper's colour, sampled and blurred by DWM into the window.</summary>
    Mica = 1,

    /// <summary>
    /// Desktop Acrylic: real translucency, so whatever is behind the window shows through.
    /// </summary>
    Acrylic = 2,

    /// <summary>A flat, opaque surface painted by the app. Always available, always readable.</summary>
    Plain = 3,
}

/// <summary>The material actually handed to DWM.</summary>
public enum BackdropMaterial
{
    /// <summary>Nothing; the app paints its own background.</summary>
    None = 0,

    /// <summary>DWMSBT_MAINWINDOW.</summary>
    Mica = 1,

    /// <summary>DWMSBT_TABBEDWINDOW - "Mica Alt", a darker base for a tabbed shell.</summary>
    MicaAlt = 2,

    /// <summary>DWMSBT_TRANSIENTWINDOW - Desktop Acrylic.</summary>
    Acrylic = 3,
}

/// <summary>Everything about the machine that decides whether a material will be drawn.</summary>
/// <param name="OsBuild">
/// The Windows build number. DWMWA_SYSTEMBACKDROP_TYPE arrived in 22621 (Windows 11 22H2);
/// older builds accept the call and draw nothing, which is the failure that makes a window
/// with a handed-over client area invisible.
/// </param>
/// <param name="CompositionEnabled">Whether DWM is compositing at all.</param>
/// <param name="RemoteSession">
/// Over Remote Desktop the material is not composited at the far end.
/// </param>
/// <param name="HighContrast">
/// A high contrast theme is a legibility setting; a material that tints text backgrounds
/// with a wallpaper is the opposite of what it asked for.
/// </param>
/// <param name="HardwareAccelerated">
/// False when WPF is rendering in software (render tier 0): a virtual machine, a failed
/// driver, a remoted session. A compositor effect behind a CPU-painted client area is
/// nothing but the desktop showing through.
/// </param>
/// <param name="TransparencyEffects">
/// The "Transparency effects" switch in Windows Settings. Acrylic is exactly that effect
/// and is not drawn without it; Mica is not, and keeps working.
/// </param>
/// <param name="BatterySaver">
/// Windows suppresses transparency while the energy saver is on, for the same reason it
/// suppresses other compositor work.
/// </param>
public readonly record struct BackdropEnvironment(
    int OsBuild,
    bool CompositionEnabled,
    bool RemoteSession,
    bool HighContrast,
    bool HardwareAccelerated,
    bool TransparencyEffects,
    bool BatterySaver)
{
    /// <summary>The build that introduced DWMWA_SYSTEMBACKDROP_TYPE.</summary>
    public const int SystemBackdropMinimumBuild = 22621;

    public bool SupportsSystemBackdrop => OsBuild >= SystemBackdropMinimumBuild;
}

/// <summary>What the policy decided, and why.</summary>
/// <param name="Material">The material to ask DWM for. <see cref="BackdropMaterial.None"/> means paint it ourselves.</param>
/// <param name="Reason">
/// One short sentence, in the app's language, for the log and the diagnostics page. Never
/// null: a material that is applied says so too, because "why is it Mica here and flat on
/// my other machine" is the question this text exists to answer.
/// </param>
/// <param name="Downgraded">
/// True when the user asked for something this machine will not draw, so the interface can
/// say so rather than silently showing them something else.
/// </param>
public readonly record struct BackdropDecision(BackdropMaterial Material, string Reason, bool Downgraded)
{
    public bool UsesCompositor => Material != BackdropMaterial.None;
}

/// <summary>
/// Turns "what the user asked for" plus "what this machine can do" into one material.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately free of any Windows call so the decision can be tested. Every
/// input is a fact somebody else measured; nothing here reads the registry, and nothing
/// here talks to DWM.
/// </para>
/// <para>
/// The bias throughout is that an unreadable window is a far worse outcome than a flat
/// one. Handing the client area to the compositor is what makes a material visible, and
/// a compositor that then declines to paint leaves a transparent hole where the app was.
/// So every doubt resolves to <see cref="BackdropMaterial.None"/>.
/// </para>
/// </remarks>
public static class BackdropPolicy
{
    public static BackdropDecision Decide(AppearanceMode requested, BackdropEnvironment environment)
    {
        if (requested == AppearanceMode.Plain)
        {
            return new BackdropDecision(BackdropMaterial.None, "Düz yüzey seçildi.", Downgraded: false);
        }

        // The conditions below are about whether Windows will paint anything at all, so
        // they apply to every material and are checked before the choice between them.
        if (!environment.CompositionEnabled)
        {
            return Blocked(requested, "masaüstü birleştirme kapalı");
        }

        if (environment.HighContrast)
        {
            return Blocked(requested, "yüksek karşıtlık teması açık");
        }

        if (environment.RemoteSession)
        {
            return Blocked(requested, "uzak masaüstü oturumu");
        }

        if (!environment.HardwareAccelerated)
        {
            return Blocked(requested, "donanım hızlandırma kapalı");
        }

        if (!environment.SupportsSystemBackdrop)
        {
            return Blocked(requested, $"Windows {environment.OsBuild} bu arka planı desteklemiyor");
        }

        return requested switch
        {
            AppearanceMode.Acrylic => DecideAcrylic(environment),
            AppearanceMode.Mica => new BackdropDecision(BackdropMaterial.Mica, "Mica uygulandı.", Downgraded: false),

            // "System" means the performant default Microsoft recommends for a main
            // window, which is Mica - not the glassiest thing the machine will draw.
            _ => new BackdropDecision(BackdropMaterial.Mica, "Sistem: Mica uygulandı.", Downgraded: false),
        };
    }

    private static BackdropDecision DecideAcrylic(BackdropEnvironment environment)
    {
        // Acrylic is the transparency effect, so the switch that turns transparency off
        // turns it off. Mica is a wallpaper tint rather than a live blur and survives
        // both, which is why the fallback is Mica rather than a flat surface.
        if (!environment.TransparencyEffects)
        {
            return new BackdropDecision(
                BackdropMaterial.Mica,
                "Saydamlık efektleri kapalı; Acrylic yerine Mica uygulandı.",
                Downgraded: true);
        }

        if (environment.BatterySaver)
        {
            return new BackdropDecision(
                BackdropMaterial.Mica,
                "Pil tasarrufu açık; Acrylic yerine Mica uygulandı.",
                Downgraded: true);
        }

        return new BackdropDecision(BackdropMaterial.Acrylic, "Acrylic cam uygulandı.", Downgraded: false);
    }

    private static BackdropDecision Blocked(AppearanceMode requested, string why)
        => new(
            BackdropMaterial.None,
            $"Düz yüzey kullanılıyor: {why}.",
            Downgraded: requested != AppearanceMode.System);

    /// <summary>The label the interface shows for a mode.</summary>
    public static string Describe(AppearanceMode mode) => mode switch
    {
        AppearanceMode.Mica => "Mica",
        AppearanceMode.Acrylic => "Acrylic cam",
        AppearanceMode.Plain => "Düz",
        _ => "Sistem",
    };
}
