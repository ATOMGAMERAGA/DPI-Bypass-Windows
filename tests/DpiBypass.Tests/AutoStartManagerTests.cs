using System.Xml.Linq;
using DpiBypass.Core.Startup;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Pins the two-part startup registration: an elevated task that fires at logon, and a
/// visible Windows Startup Apps entry the user can switch off.
/// </summary>
public sealed class AutoStartManagerTests
{
    /// <summary>
    /// The task starts itself, rather than waiting for a registry value to start it.
    /// </summary>
    /// <remarks>
    /// It used to have no trigger at all: the whole of autostart hung on one HKCU Run
    /// value invoking <c>schtasks /Run</c>, so anything that removed that value - a
    /// cleanup tool, a rebuilt profile, an installation elevated under a different
    /// account - left the app never starting with Windows again, silently. The Run entry
    /// is still written, because it is what Settings &gt; Apps &gt; Startup shows and what
    /// gives the user a switch; the launch checks that switch for itself.
    /// </remarks>
    [Fact]
    public void TheTaskFiresAtLogonForTheAccountThatRegisteredIt()
    {
        var xml = AutoStartManager.BuildTaskXml(
            "/opt/DPI Bypass/DpiBypass.exe",
            startMinimised: true,
            sid: "S-1-5-21-1000",
            userName: "ignored");

        var document = XDocument.Parse(xml);
        var ns = document.Root!.Name.Namespace;
        var trigger = Assert.Single(document.Descendants(ns + "LogonTrigger"));

        Assert.Equal("true", trigger.Element(ns + "Enabled")!.Value);
        Assert.Equal("S-1-5-21-1000", trigger.Element(ns + "UserId")!.Value);

        // Element order is the schema's, not ours: Task Scheduler rejects a definition
        // whose children are out of sequence, and the fallback for a rejected task is a
        // Run key entry that prompts for elevation on every single logon.
        Assert.Equal(
            new[] { "Enabled", "UserId", "Delay" },
            trigger.Elements().Select(element => element.Name.LocalName));

        // Two starts can never become two copies: the Run entry and the trigger both go
        // through this one task, and a second instance of it is dropped.
        Assert.Equal("IgnoreNew", document.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("HighestAvailable", document.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("/opt/DPI Bypass/DpiBypass.exe", document.Descendants(ns + "Command").Single().Value);
    }

    /// <summary>
    /// The launch carries both switches, and the launch path recognises each of them.
    /// </summary>
    [Fact]
    public void TheLaunchIsMarkedAsAutomaticAndCarriesTheTrayPreferenceSeparately()
    {
        // Split the way the shell hands them over, which is how the launch path sees them.
        var minimised = Arguments(startMinimised: true).Split(' ');
        var visible = Arguments(startMinimised: false).Split(' ');

        Assert.True(StartupPlan.StartedByWindows(minimised));
        Assert.True(StartupPlan.StartedByWindows(visible));

        // "Windows started this" and "start in the tray" are different facts. The second
        // is only sent when the user has actually asked for it.
        Assert.True(StartupPlan.WantsMinimised(minimised));
        Assert.False(StartupPlan.WantsMinimised(visible));
    }

    [Fact]
    public void RunEntryInvokesTheElevatedTaskInsteadOfTheAdminApplication()
    {
        var command = AutoStartManager.BuildTaskRunCommand("/windows/system32");

        Assert.True(AutoStartManager.IsTaskBridge(command));
        Assert.True(command.Contains("schtasks.exe", StringComparison.OrdinalIgnoreCase));
        Assert.True(command.Contains(AutoStartManager.TaskName, StringComparison.OrdinalIgnoreCase));
        Assert.False(command.Contains("DpiBypass.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A complete registration is left alone.</summary>
    [Fact]
    public void AnIntactRegistrationIsEnabledAndNeedsNoRepair()
    {
        var status = AutoStartManager.DescribeStatus(
            taskXml: TaskXml(logonTrigger: true, enabled: true),
            taskQueryRan: true,
            taskQuerySucceeded: true,
            runCommand: AutoStartManager.BuildTaskRunCommand("/windows/system32"),
            runEntryApproved: true);

        Assert.True(status.IsEnabled);
        Assert.True(status.TaskStartsByItself);
        Assert.False(status.NeedsRepair);
    }

    /// <summary>
    /// The failure the user reported: nothing starts at logon, and the app used to
    /// respond by turning its own checkbox off.
    /// </summary>
    [Fact]
    public void AMissingRegistrationIsRepairedRatherThanReadAsTheUsersChoice()
    {
        var status = AutoStartManager.DescribeStatus(
            taskXml: null,
            taskQueryRan: true,
            taskQuerySucceeded: false,
            runCommand: null,
            runEntryApproved: true);

        Assert.False(status.IsEnabled);
        Assert.True(status.NeedsRepair);
    }

    /// <summary>
    /// A task an older build wrote has no trigger, so the Run entry is the only thing
    /// starting it - which is exactly the single point of failure being removed.
    /// </summary>
    [Fact]
    public void ATaskWithoutItsOwnTriggerIsRegisteredAgain()
    {
        var status = AutoStartManager.DescribeStatus(
            taskXml: TaskXml(logonTrigger: false, enabled: true),
            taskQueryRan: true,
            taskQuerySucceeded: true,
            runCommand: AutoStartManager.BuildTaskRunCommand("/windows/system32"),
            runEntryApproved: true);

        // The bridge still works, so nothing is broken for the user today...
        Assert.True(status.IsEnabled);
        Assert.False(status.TaskStartsByItself);

        // ...and it is still put right, because one deleted registry value is all that
        // stands between this machine and no autostart at all.
        Assert.True(status.NeedsRepair);
    }

    /// <summary>Windows Settings still owns the switch, and outranks the repair.</summary>
    [Fact]
    public void AnEntrySwitchedOffInWindowsIsNeitherEnabledNorRepaired()
    {
        var status = AutoStartManager.DescribeStatus(
            taskXml: TaskXml(logonTrigger: true, enabled: true),
            taskQueryRan: true,
            taskQuerySucceeded: true,
            runCommand: AutoStartManager.BuildTaskRunCommand("/windows/system32"),
            runEntryApproved: false);

        Assert.True(status.TurnedOffInWindows);
        Assert.False(status.IsEnabled);
        Assert.False(status.NeedsRepair);
    }

    /// <summary>
    /// A task the tool could not be asked about is not evidence of anything.
    /// </summary>
    /// <remarks>
    /// Repairing on silence would rewrite a healthy registration on every launch, and
    /// reporting "off" would untick a checkbox the user never touched.
    /// </remarks>
    [Fact]
    public void SilenceFromTaskSchedulerChangesNothing()
    {
        var status = AutoStartManager.DescribeStatus(
            taskXml: null,
            taskQueryRan: false,
            taskQuerySucceeded: false,
            runCommand: AutoStartManager.BuildTaskRunCommand("/windows/system32"),
            runEntryApproved: true);

        Assert.True(status.Uncertain);
        Assert.True(status.IsEnabled);
        Assert.False(status.NeedsRepair);
    }

    /// <summary>A disabled task cannot start anything, whatever the bridge says.</summary>
    [Fact]
    public void ABridgeIntoADisabledTaskIsNotAWorkingAutostart()
    {
        var status = AutoStartManager.DescribeStatus(
            taskXml: TaskXml(logonTrigger: true, enabled: false),
            taskQueryRan: true,
            taskQuerySucceeded: true,
            runCommand: AutoStartManager.BuildTaskRunCommand("/windows/system32"),
            runEntryApproved: true);

        Assert.False(status.IsEnabled);
        Assert.True(status.NeedsRepair);
    }

    /// <summary>
    /// The pre-rename entry points straight at the executable, so it stands on its own.
    /// </summary>
    [Fact]
    public void AnOlderDirectRunEntryStartsTheAppWithoutTheTask()
    {
        var status = AutoStartManager.DescribeStatus(
            taskXml: null,
            taskQueryRan: true,
            taskQuerySucceeded: false,
            runCommand: "\"C:\\Program Files\\DPI Bypass\\DpiBypass.exe\" --minimized",
            runEntryApproved: true);

        Assert.False(status.RunEntryIsTaskBridge);
        Assert.True(status.IsEnabled);
    }

    private static string Arguments(bool startMinimised)
    {
        var xml = AutoStartManager.BuildTaskXml(
            "/opt/DPI Bypass/DpiBypass.exe",
            startMinimised,
            sid: "S-1-5-21-1000",
            userName: "ignored");

        var document = XDocument.Parse(xml);
        return document.Descendants(document.Root!.Name.Namespace + "Arguments").Single().Value;
    }

    private static string TaskXml(bool logonTrigger, bool enabled) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers>{(logonTrigger ? "<LogonTrigger><Enabled>true</Enabled></LogonTrigger>" : string.Empty)}</Triggers>
          <Settings>
            <Enabled>{(enabled ? "true" : "false")}</Enabled>
          </Settings>
        </Task>
        """;
}
