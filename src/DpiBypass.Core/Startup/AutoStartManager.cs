using System.Text;
using DpiBypass.Core.Interop;
using Microsoft.Win32;

namespace DpiBypass.Core.Startup;

/// <summary>
/// Makes the app come up with Windows.
/// </summary>
/// <remarks>
/// A scheduled task owns the elevated launch, because the engine needs administrator
/// rights and starting the app itself from the Run key would put a UAC prompt in front
/// of the user on every boot. The Run key starts that task instead. This extra hop is
/// intentional: Windows lists it under Settings &gt; Apps &gt; Startup and its switch can
/// genuinely disable the launch, while Task Scheduler still supplies elevation
/// without a prompt. If task registration is refused we fall back to launching the app
/// from the Run key, which still works - it just prompts.
/// </remarks>
/// <summary>
/// What the two halves of the autostart registration currently say.
/// </summary>
/// <param name="TaskRegistered">The elevated logon task exists.</param>
/// <param name="TaskEnabled">Task Scheduler is willing to run it.</param>
/// <param name="TaskStartsAtLogon">It carries a logon trigger of its own.</param>
/// <param name="RunEntryPresent">There is an entry under Startup Apps.</param>
/// <param name="RunEntryIsTaskBridge">That entry starts the task rather than the app.</param>
/// <param name="TurnedOffInWindows">Windows records the entry as switched off.</param>
/// <param name="Uncertain">Task Scheduler could not be asked at all.</param>
public readonly record struct AutoStartStatus(
    bool TaskRegistered,
    bool TaskEnabled,
    bool TaskStartsAtLogon,
    bool RunEntryPresent,
    bool RunEntryIsTaskBridge,
    bool TurnedOffInWindows,
    bool Uncertain)
{
    /// <summary>The task fires at logon by itself, with nothing else involved.</summary>
    public bool TaskStartsByItself => TaskRegistered && TaskEnabled && TaskStartsAtLogon;

    /// <summary>
    /// Whether the Startup Apps entry alone would start something.
    /// </summary>
    /// <remarks>
    /// A bridge entry only starts the app through the task, so it is worth nothing when
    /// the task is definitely gone. An entry written by an older build points straight
    /// at the executable and stands on its own.
    /// </remarks>
    public bool RunEntryStartsSomething => RunEntryPresent
        && !TurnedOffInWindows
        && (!RunEntryIsTaskBridge || Uncertain || (TaskRegistered && TaskEnabled));

    /// <summary>Whether a logon actually brings the application up.</summary>
    public bool IsEnabled => !TurnedOffInWindows && (TaskStartsByItself || RunEntryStartsSomething);

    /// <summary>
    /// Whether the registration is incomplete in a way this build can put back.
    /// </summary>
    /// <remarks>
    /// Repair is deliberately not attempted when Windows has switched the entry off -
    /// that is the user's decision and re-registering would overrule it - nor when Task
    /// Scheduler could not be asked, because silence is not evidence of damage.
    /// </remarks>
    public bool NeedsRepair => !TurnedOffInWindows
        && !Uncertain
        && (!TaskStartsByItself || !RunEntryPresent);
}

public sealed class AutoStartManager
{
    public const string TaskName = "DpiBypass-Autostart";

    /// <summary>
    /// Marks a launch that Windows started at logon rather than a person did.
    /// </summary>
    /// <remarks>
    /// The task's own trigger is what makes autostart survive a missing Run entry, and
    /// this switch is what keeps the Windows Startup Apps switch meaningful in spite of
    /// it: a launch carrying it stands down when Windows records the entry as disabled.
    /// Without the pair, the two halves of the registration disagree - either the app
    /// never starts because one registry value went missing, or it keeps starting after
    /// the user has switched it off in Settings.
    /// </remarks>
    public const string AutoStartSwitch = StartupPlan.AutoStartSwitch;

    /// <summary>Names used before the app was renamed, cleaned up whenever we touch autostart.</summary>
    private const string LegacyTaskName = "AtomDpiBypass-Autostart";
    private const string LegacyRunValueName = "AtomDpiBypass";
    private const string PreviousRunValueName = "DpiBypass";

    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string RunValueName = "DPI Bypass";

    private readonly string _executablePath;
    private readonly Action<string>? _log;

    public AutoStartManager(string? executablePath = null, Action<string>? log = null)
    {
        _executablePath = executablePath ?? Environment.ProcessPath ?? string.Empty;
        _log = log;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
        => (await InspectAsync(cancellationToken).ConfigureAwait(false)).IsEnabled;

    /// <summary>
    /// Reads both halves of the registration and says what a logon would actually do.
    /// </summary>
    /// <remarks>
    /// One call rather than a boolean, because "is autostart on" and "is autostart
    /// intact" are different questions and answering only the first is what left users
    /// with no launch at logon. A registration missing its task, or carrying a task an
    /// older build wrote without a trigger, reported itself as simply "off" - so the
    /// settings checkbox turned itself off to match, and the app never started with
    /// Windows again until somebody noticed and ticked it by hand.
    /// </remarks>
    public async Task<AutoStartStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        ProcessResult? query = null;

        try
        {
            query = await ProcessRunner
                .RunAsync(
                    "schtasks.exe",
                    ["/Query", "/TN", TaskName, "/XML", "ONE"],
                    TimeSpan.FromSeconds(20),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The Run entry below still gives us a conservative answer. Throwing would
            // not: this is awaited from a fire and forget started at construction time,
            // where the failure disappears and the autostart checkbox never finishes.
            _log?.Invoke($"Autostart state could not be read: {ex.Message}");
        }

        var (runCommand, approved) = ReadRunEntry();

        return DescribeStatus(
            taskXml: query is { Success: true } success ? success.StandardOutput : null,
            taskQueryRan: query is not null,
            taskQuerySucceeded: query is { Success: true },
            runCommand: runCommand,
            runEntryApproved: approved);
    }

    /// <summary>
    /// Turns what the two registrations say into one verdict, with no I/O of its own.
    /// </summary>
    internal static AutoStartStatus DescribeStatus(
        string? taskXml,
        bool taskQueryRan,
        bool taskQuerySucceeded,
        string? runCommand,
        bool runEntryApproved)
    {
        var registered = taskQuerySucceeded && !string.IsNullOrWhiteSpace(taskXml);

        return new AutoStartStatus(
            TaskRegistered: registered,
            TaskEnabled: registered && TaskXmlSaysEnabled(taskXml!),
            TaskStartsAtLogon: registered && TaskXmlHasLogonTrigger(taskXml!),
            RunEntryPresent: !string.IsNullOrWhiteSpace(runCommand),
            RunEntryIsTaskBridge: runCommand is not null && IsTaskBridge(runCommand),
            TurnedOffInWindows: !string.IsNullOrWhiteSpace(runCommand) && !runEntryApproved,

            // A query that would not run at all is not evidence that anything is
            // missing, and repairing on that basis would rewrite a healthy
            // registration every launch.
            Uncertain: !taskQueryRan);
    }

    /// <summary>
    /// Whether Windows has switched this app off under Settings &gt; Apps &gt; Startup.
    /// </summary>
    /// <remarks>
    /// Registry only, so a launch can ask it before it has done anything else. The task
    /// fires at logon on its own now, and this is what still lets the Windows switch
    /// stop it: a launch that carries <see cref="AutoStartSwitch"/> and finds a disabled
    /// entry here has been told not to run.
    /// </remarks>
    public static bool IsTurnedOffInWindows()
    {
        var (command, approved) = ReadRunEntry();
        return command is not null && !approved;
    }

    private static bool TaskXmlSaysEnabled(string xml)
    {
        // Only the settings flag matters here; the element name is not localised, so
        // this reads the same on every Windows display language.
        var disabled = xml.Replace(" ", string.Empty).Replace("\t", string.Empty);
        return !disabled.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TaskXmlHasLogonTrigger(string xml)
        => xml.Contains("<LogonTrigger", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts back whatever is missing from a registration the user has asked for.
    /// </summary>
    /// <remarks>
    /// Called on every launch that has "start with Windows" switched on, because the
    /// two registrations are outside this application's control between runs: a task
    /// can be deleted, a Run value can be cleaned away, and a task written by an older
    /// build has no trigger of its own. Doing nothing about that is what turned a
    /// missing registry value into an app that never came up with Windows again and
    /// then quietly unticked its own checkbox. It is a no-op when nothing is missing,
    /// and it never overrides a switch the user turned off in Windows.
    /// </remarks>
    public async Task<AutoStartStatus> EnsureRegisteredAsync(
        bool startMinimised,
        CancellationToken cancellationToken = default)
    {
        var status = await InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!status.NeedsRepair)
        {
            return status;
        }

        _log?.Invoke(status.TaskRegistered
            ? "Autostart task is missing its logon trigger; registering it again."
            : "Autostart registration is incomplete; registering it again.");

        if (!await EnableAsync(startMinimised, cancellationToken).ConfigureAwait(false))
        {
            return status;
        }

        return await InspectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> EnableAsync(bool startMinimised, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_executablePath) || !File.Exists(_executablePath))
        {
            _log?.Invoke("Cannot enable autostart: executable path is unknown.");
            return false;
        }

        var xmlPath = Path.Combine(Path.GetTempPath(), $"dpibypass-task-{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks only accepts UTF-16 task definitions.
            await File.WriteAllTextAsync(xmlPath, BuildTaskXml(startMinimised), Encoding.Unicode, cancellationToken)
                .ConfigureAwait(false);

            var create = await ProcessRunner.RunAsync(
                "schtasks.exe",
                ["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"],
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            if (create.Success)
            {
                await RemoveLegacyAutoStartAsync(cancellationToken).ConfigureAwait(false);

                if (WriteTaskRunKey())
                {
                    _log?.Invoke("Autostart registered in Startup Apps through an elevated task.");
                    return true;
                }

                _log?.Invoke("Startup Apps entry could not be written; falling back to a direct Run key.");
                return WriteDirectRunKey(startMinimised);
            }

            _log?.Invoke($"Scheduled task registration failed ({create.ExitCode}); falling back to the Run key.");
            return WriteDirectRunKey(startMinimised);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Autostart could not be enabled: {ex.Message}");
            return WriteDirectRunKey(startMinimised);
        }
        finally
        {
            TryDelete(xmlPath);
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ProcessRunner
                .RunAsync("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"], TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The Run key below is the half that a non-elevated launch can still fix,
            // so it is always attempted rather than skipped because the task tool failed.
            _log?.Invoke($"Scheduled task could not be removed: {ex.Message}");
        }

        RemoveRunKey();
        await RemoveLegacyAutoStartAsync(cancellationToken).ConfigureAwait(false);
        _log?.Invoke("Autostart removed.");
    }

    /// <summary>
    /// Drops the entries the pre-rename build left behind. Without this the old task
    /// keeps firing at every logon and launching an executable that is no longer there.
    /// </summary>
    private async Task RemoveLegacyAutoStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ProcessRunner
                .RunAsync("schtasks.exe", ["/Delete", "/TN", LegacyTaskName, "/F"], TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(LegacyRunValueName) is not null)
            {
                key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Nothing left over, or nothing we can do about it.
        }
    }

    private string BuildTaskXml(bool startMinimised)
        => BuildTaskXml(
            _executablePath,
            startMinimised,
            Elevation.CurrentUserSid,
            Elevation.CurrentUserName);

    internal static string BuildTaskXml(
        string executablePath,
        bool startMinimised,
        string? sid,
        string? userName)
    {
        // The same constants the launch path parses, so the two can never drift apart
        // and leave a logon task whose switches nothing recognises.
        var arguments = startMinimised
            ? $"{AutoStartSwitch} {StartupPlan.MinimisedSwitch}"
            : AutoStartSwitch;
        // Prefer the SID: it survives the account being renamed.
        var identity = Escape(string.IsNullOrEmpty(sid) ? userName ?? string.Empty : sid);
        var principalIdentity = $"      <UserId>{identity}</UserId>";

        // Scoping the trigger to the same account keeps the task off other users'
        // logons, where its window would appear on a desktop nobody asked it to.
        var triggerIdentity = identity.Length == 0
            ? string.Empty
            : $"      <UserId>{identity}</UserId>";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>{Escape(AppPaths.Author)}</Author>
                <Description>{Escape(AppPaths.ProductName)} - DPI engellerini aşan koruma servisini oturum açıldığında başlatır.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <!--
                  The task starts itself at logon rather than waiting to be started.
                  It used to have no trigger at all, so the whole of autostart hung on
                  one HKCU Run value calling schtasks: lose that value - a cleanup tool,
                  a profile rebuilt, a registration written under a different elevated
                  account - and the app simply never came up with Windows again, with
                  nothing on screen to say so. The Run entry is still written, because
                  it is what puts the app in Settings > Apps > Startup and gives the
                  user a switch; a launch from this trigger checks that switch and
                  stands down when it is off. The short delay lets the network stack
                  and the shell settle first, and IgnoreNew below means the two paths
                  can never produce two copies.
                -->
                <LogonTrigger>
                  <Enabled>true</Enabled>
            {triggerIdentity}
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
            {principalIdentity}
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(executablePath)}</Command>
                  <Arguments>{Escape(arguments)}</Arguments>
                  <WorkingDirectory>{Escape(Path.GetDirectoryName(executablePath) ?? string.Empty)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private bool WriteTaskRunKey()
        => WriteRunValue(BuildTaskRunCommand(Environment.SystemDirectory));

    private bool WriteDirectRunKey(bool startMinimised)
        => WriteRunValue(
            $"\"{_executablePath}\"{(startMinimised ? " " + StartupPlan.MinimisedSwitch : string.Empty)}");

    private bool WriteRunValue(string command)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(RunValueName, command);
            key.DeleteValue(PreviousRunValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);

            // A removed-and-recreated Run value can retain the disabled verdict
            // Windows stored under StartupApproved. Enabling it inside this app is an
            // explicit user action, so remove that stale verdict and let Windows
            // recreate the normal enabled state.
            ClearStartupApproval(RunValueName, PreviousRunValueName, LegacyRunValueName);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Run key fallback failed: {ex.Message}");
            return false;
        }
    }

    internal static string BuildTaskRunCommand(string? systemDirectory)
    {
        var executable = string.IsNullOrWhiteSpace(systemDirectory)
            ? "schtasks.exe"
            : Path.Combine(systemDirectory, "schtasks.exe");

        return $"\"{executable}\" /Run /TN \"{TaskName}\"";
    }

    internal static bool IsTaskBridge(string command)
        => command.Contains("schtasks.exe", StringComparison.OrdinalIgnoreCase)
           && command.Contains("/Run", StringComparison.OrdinalIgnoreCase)
           && command.Contains(TaskName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Startup Apps entry this app owns, and whether Windows still allows it.
    /// </summary>
    /// <remarks>
    /// Presence and approval are returned separately because they mean different
    /// things. A missing entry is a registration to repair; a present entry that
    /// Windows has switched off is a decision to respect, and repairing it would
    /// silently overrule the user in Settings.
    /// </remarks>
    private static (string? Command, bool Approved) ReadRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key is null)
            {
                return (null, true);
            }

            foreach (var name in new[] { RunValueName, PreviousRunValueName, LegacyRunValueName })
            {
                if (key.GetValue(name) is string command && !string.IsNullOrWhiteSpace(command))
                {
                    return (command, IsStartupApproved(name));
                }
            }
        }
        catch (Exception)
        {
            // An unreadable entry is not enabled as far as the current user is
            // concerned; the settings checkbox will stay conservative.
        }

        return (null, true);
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                key.DeleteValue(PreviousRunValueName, throwOnMissingValue: false);
                key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
            }

            ClearStartupApproval(RunValueName, PreviousRunValueName, LegacyRunValueName);
        }
        catch (Exception)
        {
            // Nothing to clean up.
        }
    }

    private static bool IsStartupApproved(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath);
            var state = key?.GetValue(valueName) as byte[];

            // Windows uses 0x03 for a Run entry disabled in Startup Apps. A missing
            // record and the normal 0x02 record both mean enabled.
            return state is not { Length: > 0 } || state[0] != 0x03;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static void ClearStartupApproval(params string[] valueNames)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            foreach (var name in valueNames)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Windows will recreate the state when it next enumerates Startup Apps.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
