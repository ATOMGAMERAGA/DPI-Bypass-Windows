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
/// Network configuration goes through PowerShell cmdlets rather than
/// <c>netsh</c>: netsh prints localised text that would have to be parsed, and on
/// Turkish Windows that parsing breaks. The cmdlets return objects, so JSON out is
/// stable regardless of the display language.
/// </remarks>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
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

        if (!process.Start())
        {
            return new ProcessResult(-1, string.Empty, $"could not start {fileName}");
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
