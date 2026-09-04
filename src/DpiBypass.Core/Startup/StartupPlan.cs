namespace DpiBypass.Core.Startup;

/// <summary>What a launch should put on screen.</summary>
public enum StartupVisibility
{
    /// <summary>Put the window up. The only outcome the user can be sure to notice.</summary>
    ShowWindow = 0,

    /// <summary>Stay in the notification area, because the user has asked for that and can get back.</summary>
    StartHidden = 1,
}

/// <summary>
/// Decides whether a launch shows its window.
/// </summary>
/// <remarks>
/// <para>
/// Getting this wrong is indistinguishable from the app being broken, which is
/// exactly what happened: the logon task starts the app with <c>--minimized</c>, so
/// on a fresh machine the first thing that ever ran was a copy with no window - and
/// on Windows 11 a notification area icon that has never been promoted lives behind
/// the overflow chevron, where nobody looks. The app was running, protecting the
/// connection, and completely invisible.
/// </para>
/// <para>
/// So hiding is now something the app has to earn. Every condition below has to
/// hold, and if any of them is in doubt the window goes up: a window the user did
/// not ask for is a small annoyance, a missing one reads as a broken program.
/// </para>
/// </remarks>
public sealed record StartupPlan(StartupVisibility Visibility, string Reason)
{
    /// <summary>The logon task and the Run key entry both pass this.</summary>
    public const string MinimisedSwitch = "--minimized";

    /// <summary>
    /// Marks a launch Windows made at logon rather than one a person made.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="MinimisedSwitch"/> because the two answer different
    /// questions and the app used to have to guess one from the other. "Windows started
    /// this" decides whether the Startup Apps switch applies to this launch; "start in
    /// the tray" is the user's own preference and is passed only when it is set.
    /// </remarks>
    public const string AutoStartSwitch = "--autostart";

    /// <summary>Forces the window up even for a launch that would otherwise hide.</summary>
    public const string ShowSwitch = "--show";

    public bool ShowsWindow => Visibility == StartupVisibility.ShowWindow;

    public static bool WantsMinimised(IReadOnlyList<string>? arguments)
        => HasSwitch(arguments, MinimisedSwitch);

    public static bool WantsWindow(IReadOnlyList<string>? arguments)
        => HasSwitch(arguments, ShowSwitch);

    /// <summary>Whether this launch came from the logon task rather than from a person.</summary>
    public static bool StartedByWindows(IReadOnlyList<string>? arguments)
        => HasSwitch(arguments, AutoStartSwitch);

    /// <param name="arguments">The command line this launch was given.</param>
    /// <param name="startMinimisedSetting">The user's "start in the tray" preference.</param>
    /// <param name="hasShownWindowBefore">
    /// Whether this installation has ever put its window in front of the user. Until
    /// it has, nobody knows the app exists, where it lives, or that there is an icon
    /// to look for - so the first run is always visible whatever else is set.
    /// </param>
    /// <param name="trayIconAvailable">Whether there is an icon to come back through.</param>
    public static StartupPlan Decide(
        IReadOnlyList<string>? arguments,
        bool startMinimisedSetting,
        bool hasShownWindowBefore,
        bool trayIconAvailable)
    {
        if (WantsWindow(arguments))
        {
            return new StartupPlan(StartupVisibility.ShowWindow, "pencere açıkça istendi");
        }

        if (!WantsMinimised(arguments))
        {
            // Either someone double-clicked something - a request to see the app - or
            // the logon task started it with "start in the tray" switched off, which
            // asks for the same thing.
            return new StartupPlan(
                StartupVisibility.ShowWindow,
                StartedByWindows(arguments) ? "\"tepside başla\" kapalı" : "elle başlatıldı");
        }

        if (!startMinimisedSetting)
        {
            return new StartupPlan(StartupVisibility.ShowWindow, "\"tepside başla\" kapalı");
        }

        if (!trayIconAvailable)
        {
            // Hiding without an icon leaves no way back in at all.
            return new StartupPlan(StartupVisibility.ShowWindow, "bildirim alanı simgesi yok");
        }

        if (!hasShownWindowBefore)
        {
            return new StartupPlan(StartupVisibility.ShowWindow, "ilk çalıştırma");
        }

        return new StartupPlan(StartupVisibility.StartHidden, "tepside başlatıldı");
    }

    /// <summary>
    /// Whether a switch is present, in any of the spellings the shell may hand over.
    /// </summary>
    /// <remarks>
    /// The scheduled task, the Run key, the installer and a hand written shortcut all
    /// spell switches differently - one leading dash, two, or a slash - and a switch
    /// that is not recognised turns a tray start into a window on every logon, or the
    /// other way round.
    /// </remarks>
    public static bool HasSwitch(IReadOnlyList<string>? arguments, string name)
    {
        if (arguments is null)
        {
            return false;
        }

        var bare = name.TrimStart('-', '/');

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (string.Equals(argument.Trim().TrimStart('-', '/'), bare, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
