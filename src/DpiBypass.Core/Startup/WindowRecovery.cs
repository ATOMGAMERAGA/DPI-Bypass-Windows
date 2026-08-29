namespace DpiBypass.Core.Startup;

/// <summary>What to try next for a window that is meant to be on screen and is not.</summary>
public enum WindowRecoveryStep
{
    /// <summary>Nothing to do; the window is fine or hidden on purpose.</summary>
    None = 0,

    /// <summary>Unminimise, restore the taskbar button, show, raise and activate.</summary>
    Reveal = 1,

    /// <summary>Reveal, and move the window back onto a monitor that exists.</summary>
    Reposition = 2,

    /// <summary>Reveal, drop the compositor backdrop, paint an opaque background, redraw.</summary>
    OpaqueFallback = 3,

    /// <summary>Build a replacement window. Once, ever, and only for a handle that never drew.</summary>
    Recreate = 4,

    /// <summary>Out of attempts. Tell the user, in whatever way still works.</summary>
    GiveUp = 5,
}

/// <summary>
/// The escalation for a window that will not appear, and the bound on it.
/// </summary>
/// <remarks>
/// <para>
/// Bounded on purpose, in both directions. An app that gives up after one
/// <c>Show()</c> leaves the user with a process they cannot reach; an app that keeps
/// calling <c>Show()</c> and <c>Activate()</c> on a timer burns a core, steals focus
/// from whatever the user moved on to, and still does not fix anything. So each
/// attempt does something different from the last, the cheapest and least intrusive
/// first, and the sequence ends - either with a window or with a message that says
/// where to look.
/// </para>
/// <para>
/// Recreation is last and is used once. It is the only step that can lose state, and
/// it only makes sense for the one failure it addresses: a handle that has never
/// produced a frame. A window that drew and then became unreachable has a live
/// composition; showing and moving it is the fix, and destroying it is not.
/// </para>
/// </remarks>
public static class WindowRecovery
{
    /// <summary>How many recovery attempts a startup gets before the user is told.</summary>
    public const int MaxAttempts = 3;

    /// <param name="observation">What the window looks like right now.</param>
    /// <param name="attemptsMade">How many recovery attempts this startup has already spent.</param>
    /// <param name="recreationSpent">Whether the one window rebuild has been used.</param>
    public static WindowRecoveryStep Next(WindowObservation observation, int attemptsMade, bool recreationSpent)
    {
        if (!WindowHealthEvaluator.Evaluate(observation).IsBroken)
        {
            return WindowRecoveryStep.None;
        }

        if (attemptsMade >= MaxAttempts)
        {
            return WindowRecoveryStep.GiveUp;
        }

        // No window at all - it threw on the way up. There is nothing to show, move or
        // repaint, so building one is the only step that means anything here, and it is
        // still capped at one attempt like every other rebuild.
        if (!observation.WindowExists)
        {
            return recreationSpent ? WindowRecoveryStep.GiveUp : WindowRecoveryStep.Recreate;
        }

        return attemptsMade switch
        {
            // Cheapest first, and it is also the common case: a window that was shown
            // while something else held the foreground, or one the shell minimised.
            0 => observation.Minimised || observation.OnScreen
                ? WindowRecoveryStep.Reveal
                : WindowRecoveryStep.Reposition,

            // Still nothing. The next most likely reason a visible window shows nothing
            // is that its client area was handed to a compositor that is not painting it.
            1 => WindowRecoveryStep.OpaqueFallback,

            _ => CanRecreate(observation, recreationSpent)
                ? WindowRecoveryStep.Recreate
                : WindowRecoveryStep.GiveUp,
        };
    }

    /// <summary>
    /// Whether rebuilding the window could plausibly help, which is only true for a
    /// window that has never drawn.
    /// </summary>
    private static bool CanRecreate(WindowObservation observation, bool recreationSpent)
        => !recreationSpent && !observation.EverRendered;
}
