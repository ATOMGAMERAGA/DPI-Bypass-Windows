using DpiBypass.Core.Startup;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The rules that decide whether the user is actually looking at the application.
/// </summary>
/// <remarks>
/// <para>
/// This is the bug that produced a process with a taskbar button and no window. Every
/// decision that mattered - whether to record that the app had been seen, what to tell
/// a second launch, and when to stop watching - was made from <c>Window.IsVisible</c>,
/// which is true from the instant an HWND exists. A window that never draws a frame, a
/// window DWM is cloaking and a window sitting on the coordinates of a monitor that was
/// unplugged all answer yes to that question, and all three are invisible.
/// </para>
/// <para>
/// So the policy lives here, in one place, with no WPF in it, and the direction of the
/// defaults is pinned as hard as the cases: anything short of a confirmed frame on a
/// reachable window is broken, because treating "probably fine" as success is exactly
/// how a failed first run poisoned every launch after it.
/// </para>
/// </remarks>
public sealed class WindowVisibilityTests
{
    /// <summary>A window that is up, drawn, uncloaked and on a monitor.</summary>
    private static WindowObservation Healthy() => new(
        WindowExists: true,
        WantsToBeVisible: true,
        Readiness: WindowReadiness.Rendered,
        WpfVisible: true,
        HasHandle: true,
        NativeVisible: true,
        Minimised: false,
        Cloak: WindowCloak.None,
        OnScreen: true,
        TrayAvailable: true);

    [Fact]
    public void ADrawnWindowOnAMonitorIsReachable()
    {
        Assert.Equal(WindowHealth.Reachable, WindowHealthEvaluator.Evaluate(Healthy()).Health);
    }

    /// <summary>
    /// The exact reported failure: an HWND that everything agrees is visible and that
    /// has never produced a frame.
    /// </summary>
    [Theory]
    [InlineData(WindowReadiness.Created)]
    [InlineData(WindowReadiness.SourceInitialized)]
    [InlineData(WindowReadiness.Loaded)]
    public void AVisibleWindowThatNeverDrewAFrameIsBroken(WindowReadiness readiness)
    {
        var observation = Healthy() with { Readiness = readiness };

        var report = WindowHealthEvaluator.Evaluate(observation);

        Assert.Equal(WindowHealth.Broken, report.Health);
        Assert.Contains("ContentRendered", report.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WindowCloak.App)]
    [InlineData(WindowCloak.Shell)]
    [InlineData(WindowCloak.Inherited)]
    [InlineData(WindowCloak.Shell | WindowCloak.Inherited)]
    public void ACloakedWindowIsNeverConsideredVisible(WindowCloak cloak)
    {
        // IsWindowVisible keeps saying yes for a cloaked window, which is why the
        // cloak has to be asked about separately.
        var observation = Healthy() with { Cloak = cloak };

        Assert.Equal(WindowHealth.Broken, WindowHealthEvaluator.Evaluate(observation).Health);
    }

    [Fact]
    public void AWindowOutsideEveryMonitorIsBroken()
    {
        var observation = Healthy() with { OnScreen = false };

        Assert.Equal(WindowHealth.Broken, WindowHealthEvaluator.Evaluate(observation).Health);
    }

    [Fact]
    public void AMinimisedWindowIsStillReachable()
    {
        // Windows parks a minimised window far off the desktop, so its rectangle says
        // nothing - and a user who minimised the window has not lost the application.
        var observation = Healthy() with { Minimised = true, OnScreen = false };

        Assert.Equal(WindowHealth.Reachable, WindowHealthEvaluator.Evaluate(observation).Health);
    }

    [Fact]
    public void AWindowThatWasNeverBuiltIsBroken()
    {
        var observation = WindowObservation.Missing(wantsToBeVisible: true, trayAvailable: true);

        Assert.Equal(WindowHealth.Broken, WindowHealthEvaluator.Evaluate(observation).Health);
    }

    [Fact]
    public void AWindowTheUserClosedToTheTrayIsNotAFault()
    {
        var observation = Healthy() with { WantsToBeVisible = false, WpfVisible = false, NativeVisible = false };

        Assert.Equal(WindowHealth.HiddenOnPurpose, WindowHealthEvaluator.Evaluate(observation).Health);
    }

    [Fact]
    public void BeingHiddenWithNoWayBackInIsAFault()
    {
        // No window and no notification area icon is the invisible process this whole
        // policy exists to prevent, however deliberate the hiding was.
        var observation = Healthy() with
        {
            WantsToBeVisible = false,
            WpfVisible = false,
            NativeVisible = false,
            TrayAvailable = false,
        };

        Assert.Equal(WindowHealth.Broken, WindowHealthEvaluator.Evaluate(observation).Health);
    }

    [Fact]
    public void EveryVerdictExplainsItself()
    {
        // The reason is the only thing in the log that tells a user with no window why
        // there is no window.
        WindowObservation[] observations =
        [
            Healthy(),
            Healthy() with { Readiness = WindowReadiness.Loaded },
            Healthy() with { Cloak = WindowCloak.Shell },
            Healthy() with { OnScreen = false },
            Healthy() with { HasHandle = false },
            Healthy() with { NativeVisible = false },
            Healthy() with { WpfVisible = false },
            Healthy() with { WantsToBeVisible = false },
            WindowObservation.Missing(true, false),
        ];

        Assert.All(
            observations,
            o => Assert.False(string.IsNullOrWhiteSpace(WindowHealthEvaluator.Evaluate(o).Reason)));
    }

    // --- what may be written to disk ------------------------------------------

    /// <summary>
    /// The regression that turns one failed launch into a permanently invisible
    /// installation.
    /// </summary>
    /// <remarks>
    /// "HasShownWindow" is what allows the logon task to start the app in the
    /// notification area. Saved on a launch whose window never drew, it tells every
    /// later logon that the user has already met this application - so the app spends
    /// the rest of its life behind the Windows 11 overflow chevron, where somebody who
    /// has never seen it has no reason to look.
    /// </remarks>
    [Theory]
    [InlineData(WindowReadiness.Created)]
    [InlineData(WindowReadiness.SourceInitialized)]
    [InlineData(WindowReadiness.Loaded)]
    public void AWindowThatNeverRenderedIsNeverRecordedAsShown(WindowReadiness readiness)
    {
        var observation = Healthy() with { Readiness = readiness };

        Assert.False(WindowHealthEvaluator.MayRecordWindowShown(observation));
    }

    [Fact]
    public void ARenderedButUnreachableWindowIsNeverRecordedAsShown()
    {
        Assert.False(WindowHealthEvaluator.MayRecordWindowShown(Healthy() with { Cloak = WindowCloak.App }));
        Assert.False(WindowHealthEvaluator.MayRecordWindowShown(Healthy() with { OnScreen = false }));
        Assert.False(WindowHealthEvaluator.MayRecordWindowShown(Healthy() with { NativeVisible = false }));
    }

    [Fact]
    public void AConfirmedFrameOnAReachableWindowIsRecorded()
    {
        Assert.True(WindowHealthEvaluator.MayRecordWindowShown(Healthy()));
    }

    [Fact]
    public void StartingInTheTrayIsNotTheSameAsHavingBeenSeen()
    {
        var observation = Healthy() with { WantsToBeVisible = false };

        Assert.False(WindowHealthEvaluator.MayRecordWindowShown(observation));
    }

    // --- what a second launch is told -----------------------------------------

    [Fact]
    public void ASecondLaunchIsNeverToldAWindowIsUpBeforeItHasDrawn()
    {
        // The failure from the other side: a copy with an invisible window used to
        // answer "the window is on screen" to every launch for the rest of the session,
        // so the shortcut did nothing, for ever, and nothing could take over.
        var observation = Healthy() with { Readiness = WindowReadiness.SourceInitialized };

        var reply = InstanceHandover.ReplyFor(observation, startupComplete: true, recoveryExhausted: false);

        Assert.NotEqual(HandoverReply.WindowShown, reply);
    }

    [Fact]
    public void ACopyStillGettingItsWindowUpAsksToBeWaitedFor()
    {
        var observation = Healthy() with { Readiness = WindowReadiness.Loaded };

        Assert.Equal(
            HandoverReply.Starting,
            InstanceHandover.ReplyFor(observation, startupComplete: true, recoveryExhausted: false));
    }

    [Fact]
    public void ACopyThatGaveUpOnItsWindowSaysSoSoTheLaunchCanTakeOver()
    {
        // Without this the wedged copy holds the instance lock and answers "wait" for
        // ever, and the user never gets an application at all.
        var observation = Healthy() with { Readiness = WindowReadiness.Loaded };

        Assert.Equal(
            HandoverReply.NoAnswer,
            InstanceHandover.ReplyFor(observation, startupComplete: true, recoveryExhausted: true));
    }

    [Fact]
    public void ACopyWhoseLoopIsNotRunningYetSaysItIsStarting()
    {
        Assert.Equal(
            HandoverReply.Starting,
            InstanceHandover.ReplyFor(Healthy(), startupComplete: false, recoveryExhausted: false));
    }

    [Fact]
    public void AReachableWindowEndsTheOtherLaunch()
    {
        Assert.Equal(
            HandoverReply.WindowShown,
            InstanceHandover.ReplyFor(Healthy(), startupComplete: true, recoveryExhausted: false));

        // And the launch on the other side acts on it.
        Assert.Equal(LaunchAction.Exit, InstanceHandover.Decide(HandoverReply.WindowShown, startupBudgetSpent: false));
    }

    [Fact]
    public void AnAnswerIsNeverWindowShownForAnythingButAReachableWindow()
    {
        WindowObservation[] unreachable =
        [
            Healthy() with { Readiness = WindowReadiness.Created },
            Healthy() with { Cloak = WindowCloak.Shell },
            Healthy() with { OnScreen = false },
            Healthy() with { WpfVisible = false },
            Healthy() with { NativeVisible = false },
            Healthy() with { HasHandle = false },
            WindowObservation.Missing(true, true),
        ];

        foreach (var observation in unreachable)
        {
            foreach (var exhausted in new[] { false, true })
            {
                Assert.NotEqual(
                    HandoverReply.WindowShown,
                    InstanceHandover.ReplyFor(observation, startupComplete: true, exhausted));
            }
        }
    }

    // --- recovery, and the bound on it ----------------------------------------

    [Fact]
    public void AHealthyWindowIsLeftAlone()
    {
        Assert.Equal(
            WindowRecoveryStep.None,
            WindowRecovery.Next(Healthy(), attemptsMade: 0, recreationSpent: false));

        Assert.Equal(
            WindowRecoveryStep.None,
            WindowRecovery.Next(Healthy() with { WantsToBeVisible = false }, attemptsMade: 0, recreationSpent: false));
    }

    [Fact]
    public void TheFirstAttemptIsTheCheapestOne()
    {
        var observation = Healthy() with { Readiness = WindowReadiness.Loaded };

        Assert.Equal(
            WindowRecoveryStep.Reveal,
            WindowRecovery.Next(observation, attemptsMade: 0, recreationSpent: false));
    }

    [Fact]
    public void AWindowOffEveryMonitorIsMovedRatherThanJustShownAgain()
    {
        var observation = Healthy() with { OnScreen = false };

        Assert.Equal(
            WindowRecoveryStep.Reposition,
            WindowRecovery.Next(observation, attemptsMade: 0, recreationSpent: false));
    }

    [Fact]
    public void TheSecondAttemptTakesTheWindowOffTheCompositor()
    {
        // A window whose client area was handed to DWM and is not being painted is
        // running, focused and see-through. Dropping the material costs a visual effect.
        var observation = Healthy() with { Readiness = WindowReadiness.Loaded };

        Assert.Equal(
            WindowRecoveryStep.OpaqueFallback,
            WindowRecovery.Next(observation, attemptsMade: 1, recreationSpent: false));
    }

    [Fact]
    public void AWindowThatNeverDrewIsRebuiltOnlyAsALastResort()
    {
        var observation = Healthy() with { Readiness = WindowReadiness.SourceInitialized };

        Assert.Equal(
            WindowRecoveryStep.Recreate,
            WindowRecovery.Next(observation, attemptsMade: 2, recreationSpent: false));
    }

    [Fact]
    public void AWindowThatCouldNotBeBuiltAtAllIsBuiltRatherThanRaised()
    {
        // Showing, moving or repainting a window that does not exist is not a step.
        var missing = WindowObservation.Missing(wantsToBeVisible: true, trayAvailable: true);

        Assert.Equal(
            WindowRecoveryStep.Recreate,
            WindowRecovery.Next(missing, attemptsMade: 0, recreationSpent: false));

        Assert.Equal(
            WindowRecoveryStep.GiveUp,
            WindowRecovery.Next(missing, attemptsMade: 0, recreationSpent: true));
    }

    [Fact]
    public void AWindowIsNeverRebuiltTwice()
    {
        // Rebuilding on a timer is an application that flashes on screen for ever.
        var observation = Healthy() with { Readiness = WindowReadiness.SourceInitialized };

        Assert.Equal(
            WindowRecoveryStep.GiveUp,
            WindowRecovery.Next(observation, attemptsMade: 2, recreationSpent: true));
    }

    [Fact]
    public void AWindowThatDrewAndThenBecameUnreachableIsNotRebuilt()
    {
        // It has a live composition; showing and moving it is the fix, and destroying
        // it would throw away the one thing that is working.
        var observation = Healthy() with { OnScreen = false };

        Assert.Equal(
            WindowRecoveryStep.GiveUp,
            WindowRecovery.Next(observation, attemptsMade: 2, recreationSpent: false));
    }

    [Fact]
    public void RecoveryIsBounded()
    {
        var observation = Healthy() with { Readiness = WindowReadiness.Loaded };

        for (var attempt = WindowRecovery.MaxAttempts; attempt < WindowRecovery.MaxAttempts + 5; attempt++)
        {
            Assert.Equal(
                WindowRecoveryStep.GiveUp,
                WindowRecovery.Next(observation, attempt, recreationSpent: false));
        }
    }

    [Fact]
    public void EveryAttemptDoesSomethingDifferentFromTheLast()
    {
        // An escalation that repeats itself is a Show()/Activate() loop by another name.
        var observation = Healthy() with { Readiness = WindowReadiness.SourceInitialized };

        var steps = Enumerable
            .Range(0, WindowRecovery.MaxAttempts)
            .Select(attempt => WindowRecovery.Next(observation, attempt, recreationSpent: false))
            .ToArray();

        Assert.Equal(steps.Length, steps.Distinct().Count());
        Assert.DoesNotContain(WindowRecoveryStep.None, steps);
        Assert.DoesNotContain(WindowRecoveryStep.GiveUp, steps);
    }
}
