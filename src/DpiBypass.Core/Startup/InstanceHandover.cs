namespace DpiBypass.Core.Startup;

/// <summary>What the copy holding the instance lock said when it was asked for a window.</summary>
public enum HandoverReply
{
    /// <summary>Nobody answered. The holder is gone, wedged, or half torn down.</summary>
    NoAnswer = 0,

    /// <summary>
    /// It is alive but has not built its window yet, and is asking to be waited for.
    /// </summary>
    Starting = 1,

    /// <summary>The window is genuinely on screen. This launch is finished.</summary>
    WindowShown = 2,
}

/// <summary>What a launch should do with the answer it got.</summary>
public enum LaunchAction
{
    /// <summary>Ask again shortly: the running copy is on its way.</summary>
    WaitForStartup = 0,

    /// <summary>The window is up. Exit quietly; the user is looking at the app.</summary>
    Exit = 1,

    /// <summary>
    /// Nobody is serving. Establish whether the holder is alive at all before
    /// considering ending it.
    /// </summary>
    ProbeLiveness = 2,
}

/// <summary>
/// Turns the running copy's answer into what this launch does next.
/// </summary>
/// <remarks>
/// <para>
/// This is a three-line decision that has been wrong twice, in both directions, and
/// each time it cost the user the application - so it lives here, on its own, where
/// it can be tested.
/// </para>
/// <para>
/// The expensive mistake is treating <see cref="HandoverReply.Starting"/> as
/// silence. The engine is a machine-wide packet filter: one copy owns the driver
/// handles and has pointed the machine's resolvers at its own DNS proxy, so ending
/// it takes the connection down with it and the replacement has to rebuild all of
/// it. A copy that is two seconds into loading a self-contained runtime has no
/// window and no control channel, and used to be indistinguishable from a dead one -
/// which is why an installation that launched the app, followed by anything else
/// that launched it again, ended with the second copy killing the first. The window
/// appearing and vanishing again seconds after an install is exactly that, and it is
/// the case <see cref="LaunchAction.WaitForStartup"/> exists for.
/// </para>
/// <para>
/// The opposite mistake is waiting forever. A copy that never finishes starting
/// still holds the lock, and a launch that will not act on that leaves the user with
/// no window and no way to get one, on every attempt, until the machine is rebooted.
/// So the wait is generous but bounded, and once the budget is spent a copy that is
/// still only claiming to be starting is treated like any other that cannot produce
/// a window.
/// </para>
/// </remarks>
public static class InstanceHandover
{
    /// <param name="reply">What the running copy answered.</param>
    /// <param name="startupBudgetSpent">
    /// Whether this launch has already spent its whole "it is still starting" budget.
    /// </param>
    public static LaunchAction Decide(HandoverReply reply, bool startupBudgetSpent) => reply switch
    {
        HandoverReply.WindowShown => LaunchAction.Exit,
        HandoverReply.Starting when !startupBudgetSpent => LaunchAction.WaitForStartup,
        _ => LaunchAction.ProbeLiveness,
    };
}
