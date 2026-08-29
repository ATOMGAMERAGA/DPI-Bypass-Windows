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
public sealed class AutoStartManager
{
    public const string TaskName = "DpiBypass-Autostart";

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
    {
        ProcessResult? query = null;

        try
        {
            query = await ProcessRunner
                .RunAsync("schtasks.exe", ["/Query", "/TN", TaskName], TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The Run entry below still gives us a conservative answer. Throwing would
            // not: this is awaited from a fire and forget started at construction time,
            // where the failure disappears and the autostart checkbox never finishes.
            _log?.Invoke($"Autostart state could not be read: {ex.Message}");
        }

        var runCommand = ReadEnabledRunCommand();
        if (runCommand is null)
        {
            return false;
        }

        // The current entry only asks Task Scheduler to run the elevated task, so a
        // definitively missing task means it cannot start anything. A direct Run-key
        // fallback (and the entry written by older versions) remains self-sufficient.
        if (IsTaskBridge(runCommand))
        {
            // A query exception is uncertainty rather than proof that the task is
            // missing. Preserve the visible Windows switch in that case and try again
            // on the next launch.
            return query is null || query.Value.Success;
        }

        return true;
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
        // The same constant the launch path parses, so the two can never drift apart
        // and leave a logon task whose switch nothing recognises.
        var arguments = startMinimised ? StartupPlan.MinimisedSwitch : string.Empty;
        // Prefer the SID: it survives the account being renamed.
        var principalIdentity = string.IsNullOrEmpty(sid)
            ? $"      <UserId>{Escape(userName ?? string.Empty)}</UserId>"
            : $"      <UserId>{Escape(sid)}</UserId>";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>{Escape(AppPaths.Author)}</Author>
                <Description>{Escape(AppPaths.ProductName)} - DPI engellerini aşan koruma servisini oturum açıldığında başlatır.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers />
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

    private static string? ReadEnabledRunCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key is null)
            {
                return null;
            }

            foreach (var name in new[] { RunValueName, PreviousRunValueName, LegacyRunValueName })
            {
                if (key.GetValue(name) is string command
                    && !string.IsNullOrWhiteSpace(command)
                    && IsStartupApproved(name))
                {
                    return command;
                }
            }
        }
        catch (Exception)
        {
            // An unreadable entry is not enabled as far as the current user is
            // concerned; the settings checkbox will stay conservative.
        }

        return null;
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
