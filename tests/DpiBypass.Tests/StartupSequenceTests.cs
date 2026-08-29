using DpiBypass.Core.Startup;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The whole startup decision chain, walked the way a real launch walks it.
/// </summary>
/// <remarks>
/// The individual rules are pinned next door; this is here because the bug was never
/// in one of them. It was in the joins: a plan that said "show the window", a window
/// that produced an HWND and never a frame, and three separate places that each read
/// that HWND as success - the settings file, the answer given to the next launch, and
/// the watchdog that was supposed to notice. Each was defensible on its own and
/// together they produced a live application nobody could reach. So the sequences are
/// walked end to end, in the states a real machine is in.
/// </remarks>
public sealed class StartupSequenceTests
{
    /// <summary>The states a window passes through on the way to the screen.</summary>
    private static readonly WindowReadiness[] BeforeTheFirstFrame =
    [
        WindowReadiness.Created,
        WindowReadiness.SourceInitialized,
        WindowReadiness.Loaded,
    ];

    private static WindowObservation Window(WindowReadiness readiness, bool wanted = true, bool tray = true) => new(
        WindowExists: true,
        WantsToBeVisible: wanted,
        Readiness: readiness,
        WpfVisible: wanted,
        HasHandle: readiness >= WindowReadiness.SourceInitialized,
        NativeVisible: wanted && readiness >= WindowReadiness.SourceInitialized,
        Minimised: false,
        Cloak: WindowCloak.None,
        OnScreen: true,
        TrayAvailable: tray);

    /// <summary>
    /// A brand new installation, launched by hand: the window has to appear, and
    /// nothing may be written down until it has.
    /// </summary>
    [Fact]
    public void AFirstRunRecordsNothingUntilTheWindowHasActuallyDrawn()
    {
        var plan = StartupPlan.Decide([], startMinimisedSetting: true, hasShownWindowBefore: false, trayIconAvailable: true);
        Assert.True(plan.ShowsWindow);

        foreach (var readiness in BeforeTheFirstFrame)
        {
            var observation = Window(readiness);

            Assert.False(WindowHealthEvaluator.MayRecordWindowShown(observation));
            Assert.True(WindowHealthEvaluator.Evaluate(observation).IsBroken);
        }

        var rendered = Window(WindowReadiness.Rendered);

        Assert.True(WindowHealthEvaluator.Evaluate(rendered).IsReachable);
        Assert.True(WindowHealthEvaluator.MayRecordWindowShown(rendered));
    }

    /// <summary>
    /// The poisoning this whole change exists to stop: a first run that never drew
    /// must not let the next logon start in the notification area.
    /// </summary>
    [Fact]
    public void AFirstRunThatNeverDrewLeavesTheNextLogonVisible()
    {
        // What a failed first launch may write down.
        var hasShownWindow = WindowHealthEvaluator.MayRecordWindowShown(Window(WindowReadiness.SourceInitialized));
        Assert.False(hasShownWindow);

        // ... and therefore what the logon task does next time.
        var next = StartupPlan.Decide(
            [StartupPlan.MinimisedSwitch],
            startMinimisedSetting: true,
            hasShownWindowBefore: hasShownWindow,
            trayIconAvailable: true);

        Assert.True(next.ShowsWindow);
        Assert.Equal("ilk çalıştırma", next.Reason);
    }

    [Fact]
    public void AFirstRunThatDrewLetsTheNextLogonStartInTheTray()
    {
        var hasShownWindow = WindowHealthEvaluator.MayRecordWindowShown(Window(WindowReadiness.Rendered));
        Assert.True(hasShownWindow);

        var next = StartupPlan.Decide(
            [StartupPlan.MinimisedSwitch],
            startMinimisedSetting: true,
            hasShownWindowBefore: hasShownWindow,
            trayIconAvailable: true);

        Assert.False(next.ShowsWindow);
    }

    /// <summary>
    /// A second launch while the first copy is still getting its window up: it waits,
    /// and then exits without showing anything of its own.
    /// </summary>
    [Fact]
    public void ASecondLaunchWaitsThroughStartupAndThenStandsDown()
    {
        var actions = new List<LaunchAction>();

        foreach (var readiness in BeforeTheFirstFrame)
        {
            var reply = InstanceHandover.ReplyFor(Window(readiness), startupComplete: true, recoveryExhausted: false);
            actions.Add(InstanceHandover.Decide(reply, startupBudgetSpent: false));
        }

        Assert.All(actions, action => Assert.Equal(LaunchAction.WaitForStartup, action));

        var final = InstanceHandover.ReplyFor(
            Window(WindowReadiness.Rendered),
            startupComplete: true,
            recoveryExhausted: false);

        Assert.Equal(HandoverReply.WindowShown, final);
        Assert.Equal(LaunchAction.Exit, InstanceHandover.Decide(final, startupBudgetSpent: false));
    }

    /// <summary>
    /// An autostart that stayed in the notification area, then a manual launch. The
    /// running copy has to end up showing a real window rather than reporting the one
    /// it never drew.
    /// </summary>
    [Fact]
    public void AManualLaunchGetsARealWindowOutOfATrayOnlyInstance()
    {
        var plan = StartupPlan.Decide(
            [StartupPlan.MinimisedSwitch],
            startMinimisedSetting: true,
            hasShownWindowBefore: true,
            trayIconAvailable: true);

        Assert.False(plan.ShowsWindow);

        // Sitting in the tray, window built but never shown: not a fault, and not a
        // window either.
        var hidden = Window(WindowReadiness.Created, wanted: false);
        Assert.Equal(WindowHealth.HiddenOnPurpose, WindowHealthEvaluator.Evaluate(hidden).Health);
        Assert.Equal(
            HandoverReply.NoAnswer,
            InstanceHandover.ReplyFor(hidden, startupComplete: true, recoveryExhausted: true));

        // The launch asks for the window: shown, but not drawn yet.
        var showing = Window(WindowReadiness.SourceInitialized);
        Assert.Equal(
            HandoverReply.Starting,
            InstanceHandover.ReplyFor(showing, startupComplete: true, recoveryExhausted: false));

        // ... and then drawn.
        Assert.Equal(
            HandoverReply.WindowShown,
            InstanceHandover.ReplyFor(Window(WindowReadiness.Rendered), startupComplete: true, recoveryExhausted: false));
    }

    /// <summary>
    /// The wedged copy: recovery escalates, runs out, and the answer it gives changes
    /// from "wait for me" to "nobody is serving" - which is what lets a later launch
    /// take the lock instead of being trapped behind it for ever.
    /// </summary>
    [Fact]
    public void AWedgedCopyEscalatesAndThenReleasesTheUser()
    {
        var stuck = Window(WindowReadiness.SourceInitialized);

        var steps = new List<WindowRecoveryStep>();
        var recreationSpent = false;

        for (var attempt = 0; attempt <= WindowRecovery.MaxAttempts; attempt++)
        {
            var step = WindowRecovery.Next(stuck, attempt, recreationSpent);
            steps.Add(step);
            recreationSpent |= step == WindowRecoveryStep.Recreate;
        }

        Assert.Equal(
            [
                WindowRecoveryStep.Reveal,
                WindowRecoveryStep.OpaqueFallback,
                WindowRecoveryStep.Recreate,
                WindowRecoveryStep.GiveUp,
            ],
            steps);

        // While it is still trying, a launch is asked to wait rather than to kill it -
        // ending a copy that owns the packet driver takes the connection with it.
        Assert.Equal(
            LaunchAction.WaitForStartup,
            InstanceHandover.Decide(
                InstanceHandover.ReplyFor(stuck, startupComplete: true, recoveryExhausted: false),
                startupBudgetSpent: false));

        // Once it has given up, the launch is free to take over.
        Assert.Equal(
            LaunchAction.ProbeLiveness,
            InstanceHandover.Decide(
                InstanceHandover.ReplyFor(stuck, startupComplete: true, recoveryExhausted: true),
                startupBudgetSpent: false));
    }

    /// <summary>
    /// The display topology changed under a window that had already drawn: it is moved,
    /// not rebuilt, and it never claims to be reachable while it is out there.
    /// </summary>
    [Fact]
    public void AWindowStrandedByAMonitorChangeIsMovedRatherThanRebuilt()
    {
        var laptopPanel = new WindowRect(0, 0, 1920, 1040);
        var strandedRect = new WindowRect(-1500, 120, 1080, 780);

        Assert.False(WindowPlacement.IsReachable(strandedRect, [laptopPanel]));

        var stranded = Window(WindowReadiness.Rendered) with { OnScreen = false };

        Assert.True(WindowHealthEvaluator.Evaluate(stranded).IsBroken);
        Assert.False(WindowHealthEvaluator.MayRecordWindowShown(stranded));
        Assert.Equal(
            WindowRecoveryStep.Reposition,
            WindowRecovery.Next(stranded, attemptsMade: 0, recreationSpent: false));

        var moved = WindowPlacement.MoveOnScreen(strandedRect, [laptopPanel]);
        Assert.True(WindowPlacement.IsReachable(moved, [laptopPanel]));

        // And with the window back on a monitor, the launch that asked gets its answer.
        Assert.Equal(
            HandoverReply.WindowShown,
            InstanceHandover.ReplyFor(
                stranded with { OnScreen = true },
                startupComplete: true,
                recoveryExhausted: false));
    }

    /// <summary>
    /// A machine with no notification area icon can never be left with nothing: hiding
    /// is refused up front, and being hidden anyway counts as a fault.
    /// </summary>
    [Fact]
    public void WithoutATrayIconThereIsAlwaysAWindow()
    {
        var plan = StartupPlan.Decide(
            [StartupPlan.MinimisedSwitch],
            startMinimisedSetting: true,
            hasShownWindowBefore: true,
            trayIconAvailable: false);

        Assert.True(plan.ShowsWindow);

        var hiddenAnyway = Window(WindowReadiness.Created, wanted: false, tray: false);
        Assert.True(WindowHealthEvaluator.Evaluate(hiddenAnyway).IsBroken);
    }

    /// <summary>
    /// An explicit request for the window is answered with a window, whatever else is
    /// configured - and only a drawn one counts as having answered it.
    /// </summary>
    [Fact]
    public void AnExplicitShowIsOnlySatisfiedByADrawnWindow()
    {
        var plan = StartupPlan.Decide(
            [StartupPlan.MinimisedSwitch, StartupPlan.ShowSwitch],
            startMinimisedSetting: true,
            hasShownWindowBefore: true,
            trayIconAvailable: true);

        Assert.True(plan.ShowsWindow);

        foreach (var readiness in BeforeTheFirstFrame)
        {
            Assert.NotEqual(
                HandoverReply.WindowShown,
                InstanceHandover.ReplyFor(Window(readiness), startupComplete: true, recoveryExhausted: false));
        }
    }
}
