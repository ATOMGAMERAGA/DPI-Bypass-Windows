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
        var document = XDocument.Load(FindMainWindow());
        var markup = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("Vodafone Sınırsız Modu", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding VodafoneModeEnabled, Mode=TwoWay}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding VodafoneStatusLine}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding VodafoneNetworks}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding ForgetVodafoneNetworkCommand}", markup, StringComparison.Ordinal);

        Assert.Contains("{Binding HotspotDiagnostics, Mode=TwoWay}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding HotspotDiagnoseCommand}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding HotspotCleanupCommand}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding HotspotStatusLine}", markup, StringComparison.Ordinal);

        Assert.DoesNotContain("Vodafone sınırsız modu&quot; (hotspot TTL yeniden yazımı) kaldırıldı", markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The latency card has to offer every control the feature actually has, because a
    /// capability with no way to reach it is the same as one that does not exist.
    /// </summary>
    [Fact]
    public void TheLatencyCardExposesTheTargetPickerTheTestsAndTheGuard()
    {
        var markup = XDocument.Load(FindMainWindow()).ToString(SaveOptions.DisableFormatting);

        foreach (var binding in new[]
        {
            "{Binding LowLatencyMode, Mode=TwoWay}",
            "{Binding LatencyTargetOptions}",
            "{Binding SelectedLatencyTarget, Mode=TwoWay}",
            "{Binding LatencyCustomTarget, Mode=TwoWay, UpdateSourceTrigger=LostFocus}",
            "{Binding LatencyProcesses}",
            "{Binding SelectedLatencyProcess, Mode=TwoWay}",
            "{Binding RefreshLatencyProcessesCommand}",
            "{Binding LatencyTestCommand}",
            "{Binding LatencyDeepTestCommand}",
            "{Binding LatencyRetestCommand}",
            "{Binding LatencyRestoreCommand}",
            "{Binding LatencyClearProfilesCommand}",
            "{Binding LatencyHeadline}",
            "{Binding LatencyTargetSummary}",
            "{Binding LatencyIdleSummary}",
            "{Binding LatencyUploadSummary}",
            "{Binding LatencyDownloadSummary}",
            "{Binding LatencyPathSummary}",
            "{Binding LatencyAppliedChanges}",
            "{Binding LatencyRejectedChanges}",
            "{Binding TrafficGuardEnabled, Mode=TwoWay}",
            "{Binding LatencyGuardSummary}",
        })
        {
            Assert.Contains(binding, markup, StringComparison.Ordinal);
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

        Assert.Contains("Boştaki ping, yük altındaki gecikme, jitter, paket kaybı", markup, StringComparison.Ordinal);
        Assert.Contains("ISP/WAN rota gecikmesi ayrı şeylerdir", markup, StringComparison.Ordinal);
        Assert.Contains("Yük altında derin test", markup, StringComparison.Ordinal);

        // The QoS namespace is named where the user can see it, because the promise that
        // nothing else is touched is only worth anything if it is checkable.
        Assert.Contains("DPIBypass.Latency.", markup, StringComparison.Ordinal);
    }
}
