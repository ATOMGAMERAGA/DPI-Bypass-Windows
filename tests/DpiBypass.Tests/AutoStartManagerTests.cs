using System.Xml.Linq;
using DpiBypass.Core.Startup;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Pins the two-part startup registration: a visible Windows Startup Apps entry
/// triggers a separate task that supplies elevation without a UAC prompt.
/// </summary>
public sealed class AutoStartManagerTests
{
    [Fact]
    public void TaskIsManualSoTheWindowsStartupSwitchOwnsTheLaunch()
    {
        var xml = AutoStartManager.BuildTaskXml(
            "/opt/DPI Bypass/DpiBypass.exe",
            startMinimised: true,
            sid: "S-1-5-21-1000",
            userName: "ignored");

        var document = XDocument.Parse(xml);
        var ns = document.Root!.Name.Namespace;

        Assert.Empty(document.Descendants(ns + "LogonTrigger"));
        Assert.Equal("HighestAvailable", document.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal(StartupPlan.MinimisedSwitch, document.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("/opt/DPI Bypass/DpiBypass.exe", document.Descendants(ns + "Command").Single().Value);
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
}
