namespace DpiBypass.Core.Startup;

/// <summary>
/// How far a window has actually got, which is not the same question as whether
/// <c>Show()</c> returned.
/// </summary>
/// <remarks>
/// These are separate states because the app used to treat them as one, and that is
/// the bug this file exists for. A WPF window reaches <see cref="Created"/> in its
/// constructor, <see cref="SourceInitialized"/> the moment it owns an HWND - which is
/// already enough for a taskbar button and for <c>IsWindowVisible</c> to answer yes -
/// and <see cref="Loaded"/> once its tree is up. None of that means a single pixel
/// reached the screen. Only <see cref="Rendered"/> does, and only
/// <c>ContentRendered</c> reports it.
/// </remarks>
public enum WindowReadiness
{
    /// <summary>No window object at all.</summary>
    None = 0,

    /// <summary>Constructed. No handle, nothing on screen.</summary>
    Created = 1,

    /// <summary>An HWND exists: taskbar button, window manager, and still no frame.</summary>
    SourceInitialized = 2,

    /// <summary>The visual tree is up and laid out. Still no confirmed frame.</summary>
    Loaded = 3,

    /// <summary>A frame has been presented. The only state the user can see.</summary>
    Rendered = 4,
}

/// <summary>
/// Why DWM is not showing a window it otherwise considers visible.
/// </summary>
/// <remarks>
/// Cloaking is how Windows hides a window without telling anyone: a window on another
/// virtual desktop, a suspended app, a shell animation that did not finish.
/// <c>IsWindowVisible</c> keeps saying yes throughout, which is exactly why it is not
/// enough on its own. Values match DWM_CLOAKED_*.
/// </remarks>
[Flags]
public enum WindowCloak
{
    None = 0,

    /// <summary>DWM_CLOAKED_APP: the application asked for it.</summary>
    App = 1,

    /// <summary>DWM_CLOAKED_SHELL: another virtual desktop, or a suspended app.</summary>
    Shell = 2,

    /// <summary>DWM_CLOAKED_INHERITED: an owner window is cloaked.</summary>
    Inherited = 4,
}

/// <summary>What the window is doing, as far as the user is concerned.</summary>
public enum WindowHealth
{
    /// <summary>A real frame, on a real monitor, that the user can reach.</summary>
    Reachable = 0,

    /// <summary>Not on screen, and that is what was asked for. The tray is the way back.</summary>
    HiddenOnPurpose = 1,

    /// <summary>Meant to be on screen and is not, whatever the window manager says.</summary>
    Broken = 2,
}

/// <summary>The verdict plus the one line that explains it in the log.</summary>
public readonly record struct WindowHealthReport(WindowHealth Health, string Reason)
{
    public bool IsReachable => Health == WindowHealth.Reachable;

    public bool IsBroken => Health == WindowHealth.Broken;

    public override string ToString() => $"{Health}: {Reason}";
}

/// <summary>
/// Everything known about the window at one moment, gathered from WPF and from the
/// window manager, in a shape with no UI dependency so the rules can be tested.
/// </summary>
/// <param name="WindowExists">A window object was constructed.</param>
/// <param name="WantsToBeVisible">The app intends the user to be looking at it now.</param>
/// <param name="Readiness">How far the window actually got.</param>
/// <param name="WpfVisible"><c>Window.IsVisible</c>.</param>
/// <param name="HasHandle">An HWND exists.</param>
/// <param name="NativeVisible"><c>IsWindowVisible</c> for that HWND.</param>
/// <param name="Minimised">Iconic, which is a valid place for a window the user put there.</param>
/// <param name="Cloak">What DWM says it is hiding, if anything.</param>
/// <param name="OnScreen">Enough of the window lands on an active work area.</param>
/// <param name="TrayAvailable">There is a notification area icon to come back through.</param>
public readonly record struct WindowObservation(
    bool WindowExists,
    bool WantsToBeVisible,
    WindowReadiness Readiness,
    bool WpfVisible,
    bool HasHandle,
    bool NativeVisible,
    bool Minimised,
    WindowCloak Cloak,
    bool OnScreen,
    bool TrayAvailable)
{
    /// <summary>A frame has been presented at least once in this window's life.</summary>
    public bool EverRendered => Readiness >= WindowReadiness.Rendered;

    /// <summary>The observation for a launch that never managed to build a window.</summary>
    public static WindowObservation Missing(bool wantsToBeVisible, bool trayAvailable) => new(
        WindowExists: false,
        WantsToBeVisible: wantsToBeVisible,
        Readiness: WindowReadiness.None,
        WpfVisible: false,
        HasHandle: false,
        NativeVisible: false,
        Minimised: false,
        Cloak: WindowCloak.None,
        OnScreen: false,
        TrayAvailable: trayAvailable);

    public override string ToString() =>
        $"hazırlık={Readiness} · WPF görünür={WpfVisible} · tanıtıcı={(HasHandle ? "var" : "yok")} · "
        + $"Win32 görünür={NativeVisible} · simge durumunda={Minimised} · DWM gizleme={Cloak} · "
        + $"ekranda={OnScreen} · tepsi={(TrayAvailable ? "var" : "yok")}";
}

/// <summary>
/// Turns an observation into the one answer everything else keys off: is the user
/// looking at a usable window?
/// </summary>
/// <remarks>
/// <para>
/// This is the single place that is allowed to decide a window succeeded, because
/// every caller that decided it for itself got it wrong the same way. Persisting
/// "the user has seen this app", answering a second launch that asked for a window,
/// and stopping the visibility watchdog are all the same question, and all three used
/// to answer it with <c>IsVisible</c> - a property that is true from the instant an
/// HWND exists, seconds before anything is drawn and for ever afterwards if nothing
/// ever is.
/// </para>
/// <para>
/// So a window is reachable only when a frame has been presented <i>and</i> the window
/// manager still agrees the result is somewhere the user can get to. Anything short of
/// that is broken and gets recovered, not recorded as success.
/// </para>
/// </remarks>
public static class WindowHealthEvaluator
{
    public static WindowHealthReport Evaluate(WindowObservation observation)
    {
        if (!observation.WantsToBeVisible)
        {
            // Deliberately not on screen. That is only all right while there is a way
            // back in; without one it is the same invisible process by another route.
            return observation.TrayAvailable
                ? new WindowHealthReport(WindowHealth.HiddenOnPurpose, "kullanıcı tepside bıraktı")
                : new WindowHealthReport(WindowHealth.Broken, "gizli ama bildirim alanı simgesi yok");
        }

        if (!observation.WindowExists)
        {
            return new WindowHealthReport(WindowHealth.Broken, "pencere oluşturulamadı");
        }

        if (!observation.HasHandle)
        {
            return new WindowHealthReport(WindowHealth.Broken, "pencere tanıtıcısı yok");
        }

        if (!observation.WpfVisible)
        {
            return new WindowHealthReport(WindowHealth.Broken, "WPF penceresi gizli");
        }

        if (!observation.NativeVisible)
        {
            return new WindowHealthReport(WindowHealth.Broken, "Windows pencereyi görünür saymıyor");
        }

        if (observation.Cloak != WindowCloak.None)
        {
            return new WindowHealthReport(WindowHealth.Broken, $"pencere DWM tarafından gizlendi ({observation.Cloak})");
        }

        // A minimised window has no meaningful rectangle - Windows parks it far off the
        // desktop - so the coordinates are only worth checking on one that is not.
        if (!observation.Minimised && !observation.OnScreen)
        {
            return new WindowHealthReport(WindowHealth.Broken, "pencere etkin ekranların dışında");
        }

        if (!observation.EverRendered)
        {
            // The failure this whole file exists for: an HWND everything agrees is
            // visible, that has never drawn a frame.
            return new WindowHealthReport(WindowHealth.Broken, "ilk kare çizilmedi (ContentRendered gelmedi)");
        }

        return observation.Minimised
            ? new WindowHealthReport(WindowHealth.Reachable, "pencere simge durumunda ama erişilebilir")
            : new WindowHealthReport(WindowHealth.Reachable, "pencere çizildi ve erişilebilir");
    }

    /// <summary>
    /// Whether "this installation has shown its window" may be written to disk.
    /// </summary>
    /// <remarks>
    /// Only ever after a confirmed frame. Writing it earlier is what turned one failed
    /// first run into a permanently invisible installation: the flag said the user had
    /// seen the app, so every later logon was allowed to start in the notification area
    /// and never show a window again.
    /// </remarks>
    public static bool MayRecordWindowShown(WindowObservation observation)
        => observation.EverRendered && Evaluate(observation).IsReachable;
}
