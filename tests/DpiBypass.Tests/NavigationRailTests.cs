using System.Xml.Linq;
using DpiBypass.Tests.Ui;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The model itself, checked against the configuration that produced the reported bug.
/// </summary>
/// <remarks>
/// Without these, <see cref="NavigationRailTests"/> could pass because the model stopped
/// detecting clipping rather than because the markup stopped clipping.
/// </remarks>
public sealed class RailLayoutModelTests
{
    /// <summary>The rail exactly as it shipped: a 2px margin on the TabItem, in a TabPanel.</summary>
    private static readonly RailItemMetrics Legacy = new()
    {
        ItemMargin = new Edges(0, 2),
        ChromeMargin = Edges.Zero,
        ChromeHeight = 38,
    };

    /// <summary>The same gap, moved inside the item template where the panel cannot drop it.</summary>
    private static readonly RailItemMetrics Fixed = new()
    {
        ItemMargin = Edges.Zero,
        ChromeMargin = new Edges(0, 2),
        ChromeHeight = 38,
    };

    [Fact]
    public void TheShippedBugIsReproduced()
    {
        var items = RailLayoutModel.Arrange(RailItemsHost.TabPanel, Legacy, itemCount: 6, dpiScale: 1.0);

        Assert.All(items, item => Assert.True(item.IsClipped));

        // Exactly the margin TabPanel dropped: two device pixels off the bottom edge,
        // which is where the lower rounded corners of the blue chrome live.
        Assert.All(items, item => Assert.Equal(2, item.ClippedDevicePixels));
        Assert.All(items, item => Assert.Equal(36, item.VisibleChromeHeightDip));
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void TheShippedBugGetsWorseOnFractionalScaling(double scale)
    {
        var items = RailLayoutModel.Arrange(RailItemsHost.TabPanel, Legacy, itemCount: 6, dpiScale: scale);

        Assert.All(items, item => Assert.True(item.ClippedDevicePixels >= 2));
    }

    [Fact]
    public void MovingTheGapInsideTheTemplateRemovesTheClipEvenUnderTabPanel()
    {
        foreach (var host in new[] { RailItemsHost.TabPanel, RailItemsHost.StackPanel })
        {
            foreach (var scale in RailLayoutModel.DpiScales)
            {
                var items = RailLayoutModel.Arrange(host, Fixed, itemCount: 6, scale);
                Assert.All(items, item => Assert.False(item.IsClipped));
            }
        }
    }

    [Fact]
    public void AStackPanelHonoursAnItemMarginThatTabPanelDrops()
    {
        var stacked = RailLayoutModel.Arrange(RailItemsHost.StackPanel, Legacy, itemCount: 3, dpiScale: 1.0);

        Assert.All(stacked, item => Assert.False(item.IsClipped));

        // Real layout space: 38 of chrome then the 2px gap, so the next item starts at 40.
        Assert.Equal(0, stacked[0].SlotTopDip);
        Assert.Equal(40, stacked[1].SlotTopDip);
        Assert.Equal(80, stacked[2].SlotTopDip);
    }
}

/// <summary>
/// The navigation rail as shipped. Everything here reads the real
/// <c>Theme/Shared.xaml</c>, so it fails on the markup rather than on a copy of it.
/// </summary>
public sealed class NavigationRailTests
{
    /// <summary>Status plus the five pages under it.</summary>
    private const int NavigationItemCount = 6;

    private static readonly NavigationRailMarkup Rail = NavigationRailMarkup.Load();

    [Fact]
    public void TheSelectedItemIsNeverClippedAtAnyDpiScale()
    {
        foreach (var scale in RailLayoutModel.DpiScales)
        {
            var items = RailLayoutModel.Arrange(Rail.ItemsHost, Rail.Metrics, NavigationItemCount, scale);

            Assert.All(items, item => Assert.Equal(0, item.ClippedDevicePixels));
            Assert.All(items, item => Assert.True(
                item.VisibleChromeHeightDip >= Rail.ChromeMinHeight - 1e-9,
                $"chrome shows {item.VisibleChromeHeightDip} of {Rail.ChromeMinHeight} DIP at scale {scale}"));
        }
    }

    /// <summary>
    /// The fix has to survive the items host being swapped back, because the panel is not
    /// the only thing that can drop a margin - it is only the one that did.
    /// </summary>
    [Fact]
    public void TheItemCarriesNoVerticalMarginForAPanelToDrop()
    {
        Assert.Equal(0, Rail.ItemMargin.Top);
        Assert.Equal(0, Rail.ItemMargin.Bottom);

        foreach (var scale in RailLayoutModel.DpiScales)
        {
            var items = RailLayoutModel.Arrange(RailItemsHost.TabPanel, Rail.Metrics, NavigationItemCount, scale);
            Assert.All(items, item => Assert.Equal(0, item.ClippedDevicePixels));
        }
    }

    [Fact]
    public void TheGapBetweenItemsIsRealLayoutSpaceInsideTheTemplate()
    {
        Assert.True(
            Rail.ChromeMargin.Bottom > 0,
            "the selected chrome needs its own bottom margin, or the items sit flush against each other");

        var items = RailLayoutModel.Arrange(Rail.ItemsHost, Rail.Metrics, NavigationItemCount, dpiScale: 1.0);
        var step = items[1].SlotTopDip - items[0].SlotTopDip;

        Assert.Equal(Rail.ChromeMinHeight + Rail.ChromeMargin.Vertical.Vertical, step);
        Assert.All(
            Enumerable.Range(1, items.Count - 1),
            index => Assert.Equal(step, items[index].SlotTopDip - items[index - 1].SlotTopDip));
    }

    [Fact]
    public void TheItemsHostIsNotWpfsVerticalTabPanel()
    {
        Assert.Equal(RailItemsHost.StackPanel, Rail.ItemsHost);
        Assert.Equal("Vertical", (string?)Rail.ItemsHostElement.Attribute("Orientation"));
    }

    /// <summary>
    /// A negative margin or a transform hides a clip by drawing outside the measured
    /// bounds, which puts the pixels back but leaves the layout wrong - and breaks again
    /// the moment a parent does clip to its bounds.
    /// </summary>
    [Fact]
    public void NothingInTheRailPaintsOutsideItsMeasuredBounds()
    {
        foreach (var element in Rail.ControlStyle.DescendantsAndSelf().Concat(Rail.ItemStyle.DescendantsAndSelf()))
        {
            foreach (var name in new[] { "Margin", "Padding" })
            {
                if ((string?)element.Attribute(name) is { } value
                    && XamlThickness.TryParse(value, out var thickness))
                {
                    Assert.False(
                        thickness.HasNegativeEdge,
                        $"{element.Name.LocalName}.{name}='{value}' pulls the rail outside its layout slot");
                }
            }
        }

        Assert.DoesNotContain(Rail.ItemTemplate.Descendants(), IsLayoutEscapeHatch);
        Assert.DoesNotContain(
            Rail.ItemTemplate.Descendants().Attributes(),
            attribute => attribute.Name.LocalName is "Clip" or "ClipToBounds" or "OpacityMask" or "RenderTransform");
    }

    [Fact]
    public void SelectionAndKeyboardFocusStayDifferentVisualStates()
    {
        var selected = Assert.Single(Rail.TriggersOn("IsSelected"));
        var focused = Assert.Single(Rail.TriggersOn("IsKeyboardFocused"));

        var selectedSetters = NavigationRailMarkup.SettersOf(selected);
        var focusedSetters = NavigationRailMarkup.SettersOf(focused);

        // Selection paints the chrome and lights the accent pill.
        Assert.Contains(("Chrome", "Background"), selectedSetters);
        Assert.Contains(("Pill", "Background"), selectedSetters);

        // Focus draws a stroke, and only a stroke - so tabbing to the selected item does
        // not repaint it, and tabbing to an unselected one does not look selected.
        Assert.Equal([("Chrome", "BorderBrush")], focusedSetters);
        Assert.Empty(selectedSetters.Intersect(focusedSetters));
    }

    /// <summary>
    /// The focus stroke is drawn on a border thickness that is always reserved, so gaining
    /// or losing focus can never change how tall an item measures.
    /// </summary>
    [Fact]
    public void TheFocusStrokeDoesNotResizeTheItem()
    {
        Assert.Equal("1", (string?)Rail.Chrome.Attribute("BorderThickness"));
        Assert.Equal("Transparent", (string?)Rail.Chrome.Attribute("BorderBrush"));
    }

    [Fact]
    public void TheRailSnapsToDevicePixels()
    {
        Assert.Equal("True", (string?)Rail.Chrome.Attribute("SnapsToDevicePixels"));
        Assert.Equal("True", (string?)Rail.Chrome.Attribute("UseLayoutRounding"));
        Assert.Equal("True", (string?)Rail.ItemsHostElement.Attribute("UseLayoutRounding"));
    }

    [Fact]
    public void TheAccentPillKeepsItsWidthWhetherOrNotItIsLit()
    {
        // A pill that only takes space when selected would shift every label sideways as
        // the selection moves, which reads as the rail jumping.
        Assert.Equal("3", (string?)Rail.Pill.Attribute("Width"));
        Assert.Equal("Transparent", (string?)Rail.Pill.Attribute("Background"));
        Assert.Equal("Center", (string?)Rail.Pill.Attribute("VerticalAlignment"));
    }

    [Fact]
    public void EveryNavigationItemUsesTheOneFixedStyle()
    {
        var document = XDocument.Load(RepoFiles.MainWindowXaml);
        var ns = document.Root!.Name.Namespace;

        var items = document.Descendants(ns + "TabItem")
            .Where(item => item.Attribute("Style") is not null)
            .ToArray();

        Assert.Equal(NavigationItemCount, items.Length);
        Assert.All(items, item => Assert.Equal(
            "{StaticResource NavTabItemStyle}",
            (string?)item.Attribute("Style")));

        // An inline margin would go straight back through the panel that drops it.
        Assert.All(items, item => Assert.Null(item.Attribute("Margin")));
        Assert.All(items, item => Assert.Null(item.Attribute("Height")));
    }

    /// <summary>
    /// Headers are localised Turkish with access keys; a longer one must grow the item
    /// rather than being cut off, so nothing in the rail pins a height.
    /// </summary>
    [Fact]
    public void LongHeaderTextIsAllowedToGrowTheItem()
    {
        Assert.Null(Rail.Chrome.Attribute("Height"));
        Assert.Null(Rail.Chrome.Attribute("MaxHeight"));
        Assert.Null(Rail.ItemsHostElement.Attribute("Height"));

        var metrics = Rail.Metrics with { ChromeHeight = Rail.ChromeMinHeight + 14 };
        var items = RailLayoutModel.Arrange(Rail.ItemsHost, metrics, NavigationItemCount, dpiScale: 1.5);

        Assert.All(items, item => Assert.Equal(0, item.ClippedDevicePixels));
    }

    private static bool IsLayoutEscapeHatch(XElement element) => element.Name.LocalName
        is "TranslateTransform"
        or "ScaleTransform"
        or "TransformGroup"
        or "LayoutTransform";
}
