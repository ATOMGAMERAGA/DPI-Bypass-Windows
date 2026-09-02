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

        Assert.DoesNotContain(
            "270",
            document.Descendants().Attributes("Width").Select(attribute => attribute.Value));
    }

    [Fact]
    public void VodafoneFeatureIdentityAndHotspotDiagnosticsAreBothPresent()
    {
        var bindings = UiBindings.PathsIn(FindMainWindow());
        var markup = XDocument.Load(FindMainWindow()).ToString(SaveOptions.DisableFormatting);

        // The name and the switch stay visible; the feature was never up for removal.
        Assert.Contains("Vodafone Sınırsız Modu", markup, StringComparison.Ordinal);

        // Every capability the section has is reachable from the card. Checked as
        // view-model members rather than as exact binding strings, so rearranging the
        // markup does not fail the test while dropping a control still does.
        foreach (var member in new[]
        {
            "VodafoneModeEnabled",
            "VodafoneStatusLine",
            "VodafoneNetworks",
            "ForgetVodafoneNetworkCommand",
            "RememberVodafoneNetworkCommand",
            "HotspotDiagnostics",
            "HotspotDiagnoseCommand",
            "HotspotCleanupCommand",
            "HotspotCards",
            "HotspotDetails",
            "HotspotSuggestion",
            "HotspotCheckedAt",
        })
        {
            Assert.Contains(member, bindings);
        }
    }

    /// <summary>
    /// The Vodafone card renders structured findings, not the report text.
    /// </summary>
    /// <remarks>
    /// <c>ToReport()</c> is written for a person to paste into a support thread. Printing
    /// it into the main area was the whole of the result presentation, and reading it back
    /// would make the interface depend on the exact wording of a diagnostic sentence. It
    /// belongs under "Teknik ayrıntılar" and nowhere else.
    /// </remarks>
    [Fact]
    public void TheVodafoneCardShowsStructuredFindingsRatherThanTheRawReport()
    {
        var viewModel = File.ReadAllText(RepoFiles.MainViewModel);
        var bindings = UiBindings.PathsIn(FindMainWindow());

        // The card is built from HotspotStatusView, and the raw report is only ever
        // copied across as one field - never parsed.
        Assert.Contains("_service.HotspotView", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToReport()", viewModel, StringComparison.Ordinal);

        Assert.Contains("HotspotCards", bindings);
        Assert.Contains("HotspotReport", bindings);
    }

    /// <summary>
    /// The latency card has to offer every control the feature actually has, because a
    /// capability with no way to reach it is the same as one that does not exist.
    /// </summary>
    [Fact]
    public void TheLatencyCardExposesTheTargetPickerTheTestsAndTheGuard()
    {
        var bindings = UiBindings.PathsIn(FindMainWindow());

        foreach (var binding in new[]
        {
            "LowLatencyMode",
            "LatencyTargetOptions",
            "SelectedLatencyTarget",
            "LatencyCustomTarget",
            "LatencyProcesses",
            "SelectedLatencyProcess",
            "RefreshLatencyProcessesCommand",
            "LatencyPrimaryCommand",
            "LatencyTestCommand",
            "LatencyDeepTestCommand",
            "LatencyRetestCommand",
            "LatencyRestoreCommand",
            "LatencyClearProfilesCommand",
            "LatencyCancelCommand",
            "LatencyHeadline",
            "LatencySuggestion",
            "LatencyCards",
            "LatencyLanes",
            "LatencyTargetError",
            "LatencyAppliedChanges",
            "LatencyRejectedChanges",
            "TrafficGuardEnabled",
            "LatencyGuardSummary",
        })
        {
            Assert.Contains(binding, bindings);
        }
    }

    /// <summary>
    /// The card is where a user learns that idle ping, loaded latency and route delay are
    /// different things, so the wording that says so is part of the contract.
    /// </summary>
    [Fact]
    public void TheLatencyCardSeparatesIdleLoadedAndRouteDelayInWords()
    {
        var markup = XDocument.Load(FindMainWindow()).ToString(SaveOptions.DisableFormatting);
        var bindings = UiBindings.PathsIn(FindMainWindow());

        // Idle and loaded are separate cards fed by separate fields, so the screen cannot
        // present one as the other however the measurements arrive. The titles come from
        // the view model, which is where the cards are built.
        var viewModel = File.ReadAllText(RepoFiles.MainViewModel);

        Assert.Contains("LatencyCards", bindings);
        Assert.Contains("\"Boştaki ping\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("\"Yük altında ping\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("Yük altında test et", markup, StringComparison.Ordinal);

        // The route-versus-local distinction stays available; it just lives in the
        // details rather than as a paragraph on the main screen.
        Assert.Contains("LatencyPathSummary", bindings);
    }

}
