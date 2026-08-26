using DpiBypass.Core.Apps;
using DpiBypass.Core.Interop;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The lookups the app does against the machine it is running on, and the rules that
/// keep them from taking the window down with them.
/// </summary>
/// <remarks>
/// None of this is the app's job - it is context for the status page and the
/// autostart entry - but all of it runs during start-up, and every one of these
/// checks exists because a failure here used to be indistinguishable from the
/// application being broken.
/// </remarks>
public class HostProbeTests
{
    /// <summary>
    /// The bug this pins: <c>Directory.EnumerateDirectories</c> is lazy, so a try
    /// block around the call catches nothing. The access check happens where the
    /// caller walks the sequence - after the handler has gone - and one of the roots
    /// walked is <c>%ProgramFiles%\WindowsApps</c>, which denies enumeration to
    /// administrators on a stock Windows install.
    /// </summary>
    [Fact]
    public void ADirectoryListingIsReadBeforeItIsHandedBack()
    {
        var root = Directory.CreateTempSubdirectory("dpibypass-listing-").FullName;

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "app-1.0.0"));
            Directory.CreateDirectory(Path.Combine(root, "app-1.0.1"));
            Directory.CreateDirectory(Path.Combine(root, "packages"));

            var listed = DiscordDetector.SafeEnumerateDirectories(root, "app-*");

            // Reading it only after the directory is gone is what proves the listing
            // was materialised rather than deferred to the caller.
            Directory.Delete(root, recursive: true);

            Assert.Equal(2, listed.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void AMissingDirectoryListsAsEmptyRatherThanThrowing()
        => Assert.Empty(DiscordDetector.SafeEnumerateDirectories(
            Path.Combine(Path.GetTempPath(), $"dpibypass-absent-{Guid.NewGuid():N}"),
            "*"));

    [Fact]
    public void LookingForDiscordNeverThrows()
    {
        // The exception this would have raised on a real Windows desktop is an
        // UnauthorizedAccessException from the store folder, and it used to take the
        // browser lookup queued behind it down as well.
        var found = DiscordDetector.FindDiscord();

        Assert.NotNull(found);
    }

    [Fact]
    public void LookingForBrowsersNeverThrows() => Assert.NotNull(DiscordDetector.FindBrowsers());

    /// <summary>
    /// A tool named without a path is resolved against the current directory first by
    /// <c>CreateProcess</c>, which is both a planting risk in an elevated process and
    /// the reason a deleted working directory turns every helper launch into "the
    /// system cannot find the path specified".
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\schtasks.exe")]
    [InlineData(@"..\tools\schtasks.exe")]
    [InlineData("tools/schtasks.exe")]
    public void APathTheCallerChoseIsLeftAlone(string fileName)
        => Assert.Equal(fileName, ProcessRunner.ResolveExecutable(fileName));

    [Fact]
    public void ANameThatCannotBeResolvedIsLeftForTheNormalSearch()
    {
        // Nothing by this name is in the system directory on any host, so the caller
        // gets the name back and Windows searches for it the usual way.
        const string name = "dpibypass-no-such-tool.exe";

        Assert.Equal(name, ProcessRunner.ResolveExecutable(name));
    }

    [Fact]
    public async Task ALaunchThatCannotStartIsReportedRatherThanThrown()
    {
        // Autostart, DNS and the log page all run helpers like this from paths that
        // discard the result. A throw there is an unobserved fault that leaves the UI
        // waiting for an answer that never comes.
        var result = await ProcessRunner.RunAsync(
            Path.Combine(Path.GetTempPath(), $"dpibypass-missing-{Guid.NewGuid():N}.exe"),
            [],
            TimeSpan.FromSeconds(5));

        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
    }
}
