using System.Xml.Linq;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>Regression checks for defects that are visible without running WPF.</summary>
public sealed class UiMarkupTests
{
    private static string FindMainWindow()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DpiBypass.App", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Could not locate src/DpiBypass.App/MainWindow.xaml.");
    }

    [Fact]
    public void NavigationAccessKeysAreNotRenderedAsLiteralUnderscores()
    {
        var document = XDocument.Load(FindMainWindow());
        var ns = document.Root!.Name.Namespace;

        var textBlocksWithAccessMarkers = document
            .Descendants(ns + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text?.Contains('_') == true)
            .ToArray();

        Assert.Empty(textBlocksWithAccessMarkers);

        var accessLabels = document
            .Descendants(ns + "AccessText")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null)
            .ToArray();

        Assert.Equal(6, accessLabels.Length);
        Assert.All(accessLabels, label => Assert.Contains('_', label!));
    }

    [Fact]
    public void StatusTilesAreNotPinnedToTheOldNarrowWidth()
    {
        var document = XDocument.Load(FindMainWindow());

        Assert.Empty(document.Descendants().Attributes("Width").Where(attribute => attribute.Value == "270"));
    }
}
