using System.Text;
using DpiBypass.Core.Interop;
using Microsoft.Win32;

namespace DpiBypass.Core.Startup;

/// <summary>
/// Makes the app come up with Windows.
/// </summary>
/// <remarks>
/// A scheduled task is used rather than the Run key, because the engine needs
/// administrator rights and a Run key entry would put a UAC prompt in front of the
/// user on every single boot. A logon task registered with the highest available
/// privileges starts elevated and silently. If task registration is refused for any
/// reason we fall back to the Run key, which still works - it just prompts.
/// </remarks>
public sealed class AutoStartManager
{
    public const string TaskName = "DpiBypass-Autostart";

    /// <summary>Names used before the app was renamed, cleaned up whenever we touch autostart.</summary>
    private const string LegacyTaskName = "AtomDpiBypass-Autostart";
    private const string LegacyRunValueName = "AtomDpiBypass";

    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DpiBypass";

    private readonly string _executablePath;
    private readonly Action<string>? _log;

    public AutoStartManager(string? executablePath = null, Action<string>? log = null)
    {
        _executablePath = executablePath ?? Environment.ProcessPath ?? string.Empty;
        _log = log;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var query = await ProcessRunner
            .RunAsync("schtasks.exe", ["/Query", "/TN", TaskName], TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

        return query.Success || RunKeyExists();
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
                RemoveRunKey();
                await RemoveLegacyAutoStartAsync(cancellationToken).ConfigureAwait(false);
                _log?.Invoke("Autostart registered as an elevated logon task.");
                return true;
            }

            _log?.Invoke($"Scheduled task registration failed ({create.ExitCode}); falling back to the Run key.");
            return WriteRunKey(startMinimised);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Autostart could not be enabled: {ex.Message}");
            return WriteRunKey(startMinimised);
        }
        finally
        {
            TryDelete(xmlPath);
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        await ProcessRunner
            .RunAsync("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"], TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

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
    {
        // The same constant the launch path parses, so the two can never drift apart
        // and leave a logon task whose switch nothing recognises.
        var arguments = startMinimised ? StartupPlan.MinimisedSwitch : string.Empty;
        var sid = Elevation.CurrentUserSid;
        var userName = Elevation.CurrentUserName;

        // Prefer the SID: it survives the account being renamed.
        var principalIdentity = string.IsNullOrEmpty(sid)
            ? $"      <UserId>{Escape(userName)}</UserId>"
            : $"      <UserId>{Escape(sid)}</UserId>";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>{Escape(AppPaths.Author)}</Author>
                <Description>{Escape(AppPaths.ProductName)} - DPI engellerini aşan koruma servisini oturum açıldığında başlatır.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
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
                  <Command>{Escape(_executablePath)}</Command>
                  <Arguments>{Escape(arguments)}</Arguments>
                  <WorkingDirectory>{Escape(Path.GetDirectoryName(_executablePath) ?? string.Empty)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private bool WriteRunKey(bool startMinimised)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(
                RunValueName,
                $"\"{_executablePath}\"{(startMinimised ? " " + StartupPlan.MinimisedSwitch : string.Empty)}");
            return key is not null;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Run key fallback failed: {ex.Message}");
            return false;
        }
    }

    private static bool RunKeyExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Nothing to clean up.
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
