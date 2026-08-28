using System.Xml.Linq;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Directory.Build.props enables invariant globalization repo-wide, which is fine for
/// the core library but fatal for the WPF app: the font cache resolves font fallback
/// cultures through CultureInfo and crashes at startup ("Cannot find non-neutral
/// culture related to 'en-us'", dotnet/wpf#9097). These tests keep the app's
/// InvariantGlobalization=false override from silently disappearing.
/// </summary>
public static class GlobalizationConfigTests
{
    private static string FindAppProjectFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName,
                "src", "DpiBypass.App", "DpiBypass.App.csproj");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new Xunit.Sdk.XunitException(
            "Could not locate src/DpiBypass.App/DpiBypass.App.csproj above the test output directory.");
    }

    [Fact]
    public static void AppProjectDisablesInvariantGlobalization()
    {
        var project = XDocument.Load(FindAppProjectFile());
        var value = project.Descendants("InvariantGlobalization").SingleOrDefault()?.Value;
        Assert.Equal("false", value);
    }
}
