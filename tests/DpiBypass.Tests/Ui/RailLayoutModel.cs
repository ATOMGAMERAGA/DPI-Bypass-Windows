namespace DpiBypass.Tests.Ui;

/// <summary>Which panel hosts the navigation items.</summary>
internal enum RailItemsHost
{
    /// <summary>
    /// WPF's own <c>TabPanel</c>. For <c>TabStripPlacement</c> Left or Right its measure
    /// and arrange passes both use <c>DesiredSize - Margin</c>: the child is arranged
    /// into a rect of exactly that height and the panel advances by the same amount.
    /// <c>FrameworkElement.ArrangeCore</c> then subtracts the margin from that rect a
    /// second time, so any child with a vertical margin lands in a slot shorter than it
    /// measured - and WPF renders it at full size behind a layout clip of the slot.
    /// </summary>
    TabPanel,

    /// <summary>A vertical <c>StackPanel</c>, which arranges each child into its full
    /// <c>DesiredSize</c> and so subtracts the margin exactly once.</summary>
    StackPanel,
}

internal readonly record struct Edges(double Top, double Bottom)
{
    public double Vertical => Top + Bottom;

    public static readonly Edges Zero = new(0, 0);
}

/// <summary>The measurements one navigation item is built from.</summary>
internal sealed record RailItemMetrics
{
    /// <summary>Vertical margin on the <c>TabItem</c> itself - the one a panel can drop.</summary>
    public required Edges ItemMargin { get; init; }

    /// <summary>Vertical margin on the coloured chrome inside the item template.</summary>
    public required Edges ChromeMargin { get; init; }

    /// <summary>The chrome's height before its margin: its MinHeight, or its content.</summary>
    public required double ChromeHeight { get; init; }
}

/// <summary>One arranged navigation item.</summary>
internal readonly record struct RailItemLayout(
    double SlotTopDip,
    double SlotHeightDip,
    double ChromeHeightDip,
    double VisibleChromeHeightDip,
    double DpiScale)
{
    /// <summary>How much of the chrome the item's layout clip cuts off the bottom edge.</summary>
    public double ClippedDip => ChromeHeightDip - VisibleChromeHeightDip;

    public double ClippedDevicePixels => Math.Round(ClippedDip * DpiScale, 6);

    public bool IsClipped => ClippedDip > 1e-9;
}

/// <summary>
/// A deliberately small model of the WPF layout rules that decide whether a vertical
/// tab strip clips its selected item.
/// </summary>
/// <remarks>
/// It exists so the navigation markup can be judged without a display: the production
/// values are parsed out of <c>Theme/Shared.xaml</c> and pushed through the same
/// measure, arrange and clip arithmetic WPF performs, at every DPI scale the app is
/// expected to render at. <c>RailLayoutModelTests</c> pins the model against the
/// configuration that shipped the bug, so a model that stopped reporting clipping would
/// fail there before it could wave the production markup through by mistake.
/// </remarks>
internal static class RailLayoutModel
{
    /// <summary>The scales Windows offers, including the fractional ones.</summary>
    public static readonly double[] DpiScales = [1.0, 1.25, 1.5, 1.75, 2.0];

    public static IReadOnlyList<RailItemLayout> Arrange(
        RailItemsHost host,
        RailItemMetrics metrics,
        int itemCount,
        double dpiScale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(itemCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dpiScale, 0);

        // Measure. A FrameworkElement's DesiredSize includes its own margin, and layout
        // rounding snaps it to the device pixel grid (WPF's RoundLayoutValue).
        var chromeHeight = Round(metrics.ChromeHeight, dpiScale);
        var chromeDesired = Round(chromeHeight + metrics.ChromeMargin.Vertical, dpiScale);

        // Control.MeasureOverride hands back the single visual child's DesiredSize, so
        // this is the item's size before its own margin - the size a clip is judged by.
        var itemUnclipped = chromeDesired;
        var itemDesired = Round(itemUnclipped + metrics.ItemMargin.Vertical, dpiScale);

        var results = new List<RailItemLayout>(itemCount);
        var offset = 0d;

        for (var index = 0; index < itemCount; index++)
        {
            // The height the panel puts in the arrange rect, and the step it advances by.
            var arrangeHeight = host == RailItemsHost.TabPanel
                ? itemDesired - metrics.ItemMargin.Vertical
                : itemDesired;

            // ArrangeCore: the slot is the rect minus the margin. A slot shorter than the
            // unclipped desired size renders at full size behind a layout clip of the slot.
            var slotHeight = arrangeHeight - metrics.ItemMargin.Vertical;
            var renderedItemHeight = Math.Max(slotHeight, itemUnclipped);

            // Inside the item the chrome is arranged the same way, into the rendered height.
            var renderedChromeHeight = Math.Max(renderedItemHeight - metrics.ChromeMargin.Vertical, chromeHeight);

            // What survives the item's layout clip, measured from the chrome's own top.
            var visible = Math.Clamp(
                Math.Max(slotHeight, 0) - metrics.ChromeMargin.Top,
                0,
                renderedChromeHeight);

            results.Add(new RailItemLayout(
                offset + metrics.ItemMargin.Top,
                slotHeight,
                renderedChromeHeight,
                visible,
                dpiScale));

            offset += arrangeHeight;
        }

        return results;
    }

    /// <summary>WPF's <c>RoundLayoutValue</c>: snap a size to the device pixel grid.</summary>
    private static double Round(double value, double dpiScale) => Math.Abs(dpiScale - 1.0) < 1e-9
        ? Math.Round(value)
        : Math.Round(value * dpiScale) / dpiScale;
}
