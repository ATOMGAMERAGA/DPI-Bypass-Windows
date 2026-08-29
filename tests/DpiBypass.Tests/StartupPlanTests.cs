using DpiBypass.Core.Startup;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The rules that decide whether a launch puts anything on screen.
/// </summary>
/// <remarks>
/// Every bug report this app has had about "it does not open" ended here: a process
/// that started, worked, and showed the user nothing. So the policy is pinned. The
/// direction of the defaults matters as much as the cases - when anything is in
/// doubt the answer has to be "show the window", because an unwanted window is a
/// small annoyance and a missing one is a broken program.
/// </remarks>
public class StartupPlanTests
{
    [Fact]
    public void ALaunchWithNoArgumentsShowsTheWindow()
    {
        var plan = StartupPlan.Decide([], startMinimisedSetting: true, hasShownWindowBefore: true, trayIconAvailable: true);

        Assert.True(plan.ShowsWindow);
    }

    [Fact]
    public void NoArgumentsAtAllShowsTheWindow()
    {
        var plan = StartupPlan.Decide(null, startMinimisedSetting: true, hasShownWindowBefore: true, trayIconAvailable: true);

        Assert.True(plan.ShowsWindow);
    }

    [Fact]
    public void TheLogonTaskStartsInTheTrayOnceTheUserHasSeenTheApp()
    {
        var plan = StartupPlan.Decide(
            ["--minimized"],
            startMinimisedSetting: true,
            hasShownWindowBefore: true,
            trayIconAvailable: true);

        Assert.False(plan.ShowsWindow);
    }

    /// <summary>
    /// The failure the whole setting exists to avoid: a machine where the app has
    /// been installed, starts itself at every logon, and has never once been seen.
    /// </summary>
    [Fact]
    public void TheFirstLogonAfterInstallStillShowsTheWindow()
    {
        var plan = StartupPlan.Decide(
            ["--minimized"],
            startMinimisedSetting: true,
            hasShownWindowBefore: false,
            trayIconAvailable: true);

        Assert.True(plan.ShowsWindow);
    }

    [Fact]
    public void WithoutATrayIconHidingWouldLeaveNoWayBackIn()
    {
        var plan = StartupPlan.Decide(
            ["--minimized"],
            startMinimisedSetting: true,
            hasShownWindowBefore: true,
            trayIconAvailable: false);

        Assert.True(plan.ShowsWindow);
    }

    [Fact]
    public void TheUsersPreferenceIsHonouredWhenItSaysShowTheWindow()
    {
        var plan = StartupPlan.Decide(
            ["--minimized"],
            startMinimisedSetting: false,
            hasShownWindowBefore: true,
            trayIconAvailable: true);

        Assert.True(plan.ShowsWindow);
    }

    [Fact]
    public void AnExplicitShowBeatsEverythingElse()
    {
        var plan = StartupPlan.Decide(
            ["--minimized", "--show"],
            startMinimisedSetting: true,
            hasShownWindowBefore: true,
            trayIconAvailable: true);

        Assert.True(plan.ShowsWindow);
    }

    [Theory]
    [InlineData("--minimized")]
    [InlineData("-minimized")]
    [InlineData("/minimized")]
    [InlineData("--MINIMIZED")]
    [InlineData(" --minimized ")]
    public void EveryFormOfTheSwitchTheShellMayPassIsUnderstood(string argument)
    {
        // The scheduled task, the Run key and a hand written shortcut all spell it
        // differently, and a switch that is not recognised turns a tray start into a
        // second window on every logon.
        Assert.True(StartupPlan.WantsMinimised([argument]));
    }

    [Theory]
    [InlineData("--show")]
    [InlineData("-show")]
    [InlineData("/show")]
    [InlineData("--SHOW")]
    public void EveryFormOfTheShowSwitchIsUnderstood(string argument)
    {
        // The installer's post-install launch and the desktop shortcut both use this,
        // and a spelling that is not recognised turns "open the app" into a tray start.
        Assert.True(StartupPlan.WantsWindow([argument]));
        Assert.True(StartupPlan.Decide([argument], true, true, true).ShowsWindow);
    }

    [Theory]
    [InlineData("--ui-selftest")]
    [InlineData("/ui-selftest")]
    public void AnArbitrarySwitchIsMatchedTheSameWay(string argument)
    {
        // The startup self test is spelled the way every other switch is, and looked up
        // through the same matcher rather than a second one that could drift from it.
        Assert.True(StartupPlan.HasSwitch([argument], "--ui-selftest"));
        Assert.False(StartupPlan.HasSwitch(["--ui-selftest-later"], "--ui-selftest"));
        Assert.False(StartupPlan.HasSwitch(null, "--ui-selftest"));
    }

    [Fact]
    public void AnUnrelatedArgumentIsNotAHideRequest()
    {
        Assert.False(StartupPlan.WantsMinimised(["--minimize-later"]));
        Assert.False(StartupPlan.WantsMinimised([string.Empty]));
        Assert.False(StartupPlan.WantsMinimised([]));
    }

    [Fact]
    public void EveryDecisionExplainsItself()
    {
        // The reason goes in the log, and it is the only thing that tells a user with
        // no window why there is no window.
        var plans = new[]
        {
            StartupPlan.Decide([], true, true, true),
            StartupPlan.Decide(["--minimized"], true, true, true),
            StartupPlan.Decide(["--minimized"], true, false, true),
            StartupPlan.Decide(["--minimized"], true, true, false),
            StartupPlan.Decide(["--minimized"], false, true, true),
            StartupPlan.Decide(["--show"], true, true, true),
        };

        Assert.All(plans, plan => Assert.False(string.IsNullOrWhiteSpace(plan.Reason)));
    }
}
