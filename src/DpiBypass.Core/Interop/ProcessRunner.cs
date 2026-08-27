using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DpiBypass.Core.Interop;

public readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs short lived console helpers.
/// </summary>
/// <remarks>
/// <para>
/// Network configuration goes through PowerShell cmdlets rather than
/// <c>netsh</c>: netsh prints localised text that would have to be parsed, and on
/// Turkish Windows that parsing breaks. The cmdlets return objects, so JSON out is
/// stable regardless of the display language.
/// </para>
/// <para>
/// Everything here is launched by absolute path, from a working directory this
/// process picked. Both matter more than they look. With
/// <see cref="ProcessStartInfo.UseShellExecute"/> off, Windows resolves a bare file
/// name against the current directory before anything else - so a copy of
/// <c>powershell.exe</c> sitting in whatever folder the app happened to be started
/// from would be run in its place, elevated. And when that folder has since been
/// deleted - the installer's temp directory is the usual culprit, because the app is
/// launched from it and it is removed the moment setup exits - the launch fails
/// outright with "the system cannot find the path specified", which is how a
/// perfectly good installation ends up reporting a path error it cannot explain.
/// </para>
/// </remarks>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => await RunCoreAsync(fileName, arguments, environment: null, timeout, cancellationToken).ConfigureAwait(false);

    private static async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable(fileName),
            WorkingDirectory = SafeWorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                // Callers use fixed DPI_BYPASS_* names. Values go through the process
                // environment block, never through PowerShell source code, so an
                // adapter name containing quotes or metacharacters stays data.
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"could not start {fileName}");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // A helper that will not launch is a degraded feature, never a reason to
            // take the caller down: autostart falls back to the Run key, DNS falls
            // back to leaving the system alone, and the app keeps running.
            return new ProcessResult(-1, string.Empty, $"{fileName}: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ProcessResult(-2, stdout.ToString(), "timed out");
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Windows PowerShell encodes redirected stdout with <c>[Console]::OutputEncoding</c>,
    /// which for a windowless child is the system OEM code page - CP857 on Turkish
    /// Windows - so adapter names such as "Kablosuz Ağ Bağlantısı" would arrive as
    /// replacement characters. This pins the child to BOM-less UTF-8 to match how the
    /// output is decoded on this side.
    /// </summary>
    private const string Utf8OutputPrelude =
        "$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false; ";

    /// <summary>Runs a PowerShell snippet with no profile and no interactive prompts.</summary>
    public static Task<ProcessResult> PowerShellAsync(
        string script,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", Utf8OutputPrelude + script],
            timeout,
            cancellationToken);

    /// <summary>
    /// Runs fixed PowerShell source with untrusted values supplied out of band.
    /// </summary>
    public static Task<ProcessResult> PowerShellWithEnvironmentAsync(
        string script,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", Utf8OutputPrelude + script],
            environment,
            timeout,
            cancellationToken);

    /// <summary>
    /// A directory that is certain to exist for the lifetime of this process.
    /// </summary>
    /// <remarks>
    /// The app's own folder, because it cannot be removed while the executable inside
    /// it is running. <see cref="Environment.CurrentDirectory"/> deliberately is not
    /// used: whoever launched this process chose it, and the launcher this app is
    /// most often started by - the installer - deletes it on the way out.
    /// </remarks>
    private static readonly string SafeWorkingDirectory = ResolveSafeWorkingDirectory();

    private static string ResolveSafeWorkingDirectory()
    {
        foreach (var candidate in new[] { AppContext.BaseDirectory, Environment.SystemDirectory })
        {
            try
            {
                if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // Try the next one.
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Turns a Windows tool name into the full path of the copy shipped with Windows.
    /// </summary>
    /// <remarks>
    /// Anything that is already a path is handed back untouched, so callers keep
    /// their say. A bare name only resolves when the file really is in the system
    /// directory; otherwise the name is returned and the normal search runs, which
    /// keeps this from breaking on a machine that puts its tools somewhere else.
    /// </remarks>
    internal static string ResolveExecutable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.AsSpan().IndexOfAny('\\', '/', ':') >= 0)
        {
            return fileName;
        }

        try
        {
            var system = Environment.SystemDirectory;
            if (string.IsNullOrEmpty(system))
            {
                return fileName;
            }

            var candidate = Path.Combine(system, fileName);
            return File.Exists(candidate) ? candidate : fileName;
        }
        catch (Exception)
        {
            return fileName;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
    }
}
