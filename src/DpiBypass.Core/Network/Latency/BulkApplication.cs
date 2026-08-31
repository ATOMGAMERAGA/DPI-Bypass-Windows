using System.Diagnostics;

namespace DpiBypass.Core.Network;

/// <summary>
/// The one executable whose bulk sending may be paced, resolved rather than typed.
/// </summary>
/// <remarks>
/// <para>
/// A QoS policy matches on <c>AppPathNameMatchCondition</c>, which Microsoft documents as
/// "the name by which an application is run, such as <c>application.exe</c> or
/// <c>%ProgramFiles%\application.exe</c>". Free text in that field is a footgun: a typo
/// produces a policy that matches nothing, and the machine looks exactly as it does when
/// the policy works and the throttle does not help.
/// </para>
/// <para>
/// So the value comes from a running process the user picked out of a list, and the three
/// things that are actually different - the process id, the image name and the full path -
/// are kept apart rather than collapsed into one string. The path is used when it can be
/// read, because it cannot match the wrong <c>updater.exe</c>; the image name is the
/// fallback, and the difference is shown rather than hidden.
/// </para>
/// </remarks>
public sealed record BulkApplicationSelection
{
    /// <summary>The image name as Windows runs it, always with the extension.</summary>
    public required string ExecutableName { get; init; }

    /// <summary>The full path, when this process let us read it.</summary>
    public string? VerifiedPath { get; init; }

    /// <summary>The process ids that name currently maps to, for the flow check.</summary>
    public IReadOnlyList<uint> ProcessIds { get; init; } = [];

    /// <summary>Why the path could not be read, when it could not.</summary>
    public string? PathProblem { get; init; }

    public bool IsRunning => ProcessIds.Count > 0;

    /// <summary>Exactly what goes into the policy's match condition.</summary>
    public string MatchCondition => VerifiedPath ?? ExecutableName;

    /// <summary>How precise that match is, in words for the card.</summary>
    public string Describe() => VerifiedPath is { Length: > 0 }
        ? $"{ExecutableName} ({VerifiedPath})"
        : $"{ExecutableName} (tam yol okunamadı{(PathProblem is null ? string.Empty : $": {PathProblem}")})";
}

/// <summary>Turns a name the user chose into a selection with real processes behind it.</summary>
public interface IBulkApplicationResolver
{
    BulkApplicationSelection? Resolve(string executableName);
}

/// <summary>Reads the live process list; never starts, stops or changes anything.</summary>
public sealed class WindowsBulkApplicationResolver : IBulkApplicationResolver
{
    private readonly Action<string>? _log;

    public WindowsBulkApplicationResolver(Action<string>? log = null) => _log = log;

    public BulkApplicationSelection? Resolve(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        var trimmed = executableName.Trim();
        var bare = trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
        var pids = new List<uint>();
        string? path = null;
        string? problem = null;

        try
        {
            foreach (var process in Process.GetProcessesByName(bare))
            {
                using (process)
                {
                    pids.Add((uint)process.Id);

                    if (path is not null)
                    {
                        continue;
                    }

                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // A 32-bit process read from a 64-bit one, or a protected one.
                        // The image name still matches; the path just cannot be confirmed.
                        problem ??= "erişim reddedildi";
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            _log?.Invoke($"latency.guard: '{trimmed}' süreç listesi okunamadı ({ex.Message}).");
            return null;
        }

        if (pids.Count == 0)
        {
            return null;
        }

        return new BulkApplicationSelection
        {
            ExecutableName = $"{bare}.exe",
            VerifiedPath = path,
            ProcessIds = pids,
            PathProblem = path is null ? problem ?? "süreç yolu bildirmedi" : null,
        };
    }
}
