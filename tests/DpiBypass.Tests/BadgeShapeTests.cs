using System.Globalization;
using System.Xml.Linq;
using DpiBypass.Tests.Ui;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The shape of the app's capsule badges, as geometry rather than as markup.
/// </summary>
/// <remarks>
/// These cover the bug where every badge asking for a capsule drew as an oval. The
/// geometry half resolves the same rounded rectangles WPF would (see
/// <see cref="BorderRendering"/>); the markup half checks that no style asks for the shape
/// the wrong way again. Neither is a substitute for looking at a rendered window on
/// Windows.
/// </remarks>
public sealed class BadgeShapeTests
{
    /// <summary>The BETA badge beside the section title, at its measured size.</summary>
    private const double BadgeWidth = 46;
    private const double BadgeHeight = 19;
    private const double Hairline = 1;

    [Fact]
    public void ALargeRadiusDrawsAnEllipseRatherThanACapsule()
    {
        // What Radius.Pill (999) did to every badge that carried it.
        var badge = BorderRendering.Generate(BadgeWidth, BadgeHeight, cornerRadius: 999, Hairline);

        Assert.True(badge.IsEllipse);
        Assert.False(badge.IsCapsule);

        // Not one straight pixel of top edge survives: the caps meet at the midpoint, so
        // the badge's "sides" are the flanks of an oval.
        Assert.Equal(0, badge.Stroke.StraightTopEdge, 3);
        Assert.Equal(0, badge.Stroke.StraightSideEdge, 3);

        // Each cap is as wide as half the badge and as tall as half its height.
        Assert.Equal((BadgeWidth - Hairline) / 2, badge.Stroke.Radii.X, 3);
        Assert.Equal((BadgeHeight - Hairline) / 2, badge.Stroke.Radii.Y, 3);
        Assert.False(badge.Stroke.Radii.IsCircular);
    }

    [Fact]
    public void TheBehavioursRadiusDrawsARealCapsule()
    {
        var badge = BorderRendering.Generate(BadgeWidth, BadgeHeight, PillRadiusFor(BadgeHeight, Hairline), Hairline);

        Assert.True(badge.IsCapsule);
        Assert.False(badge.IsEllipse);
        Assert.True(badge.Stroke.Radii.IsCircular);
        Assert.True(badge.Fill.Radii.IsCircular);

        // Straight top and bottom edges between two semicircular end caps: the stroke's
        // width less its own height, which is what makes it a capsule and not an oval.
        Assert.Equal((BadgeWidth - Hairline) - (BadgeHeight - Hairline), badge.Stroke.StraightTopEdge, 3);
    }

    [Fact]
    public void TakingTheStrokeOffIsWhatMakesTheCapsCircular()
    {
        // Half the height is the obvious rule and it is not quite right on a bordered
        // badge: the stroke runs down a rect one hairline shorter than the badge, so a
        // radius of 9.5 clamps to 9 vertically and stays 9.5 horizontally. Both rings come
        // out slightly egg-shaped. Subtracting the stroke first is what squares that up.
        var corrected = BorderRendering.Generate(BadgeWidth, BadgeHeight, PillRadiusFor(BadgeHeight, Hairline), Hairline);
        Assert.True(corrected.HasCircularCaps);
        Assert.True(corrected.IsCapsule);

        var naive = BorderRendering.Generate(BadgeWidth, BadgeHeight, BadgeHeight / 2, Hairline);
        Assert.False(naive.HasCircularCaps);
        Assert.Equal(9.5, naive.Stroke.Radii.X, 3);
        Assert.Equal(9.0, naive.Stroke.Radii.Y, 3);

        // Both are still centred on the same point either way - the caps are the problem,
        // not their placement.
        Assert.Equal(naive.Stroke.CapCentre, naive.Fill.CapCentre);
    }

    [Theory]
    // Short label, the BETA badge, a long label, and the same badge at 125%, 150% and 200%
    // text scaling - the sizes a fixed radius token could never have covered.
    [InlineData(28, 17)]
    [InlineData(46, 19)]
    [InlineData(132, 19)]
    [InlineData(58, 24)]
    [InlineData(70, 29)]
    [InlineData(92, 38)]
    public void EveryBadgeSizeStaysACapsule(double width, double height)
    {
        var badge = BorderRendering.Generate(width, height, PillRadiusFor(height, Hairline), Hairline);

        Assert.True(badge.IsCapsule, $"{width}x{height} is not a capsule");
        Assert.True(BorderRendering.Generate(width, height, cornerRadius: 999, Hairline).IsEllipse);
    }

    [Fact]
    public void TheWelcomeDotIsACircleAndItsActiveFormIsACapsule()
    {
        // WelcomeDotStyle: 8x8 at rest, widened to 22x8 for the current step, no stroke.
        // The resting dot was the one size the old radius got right, because a square is
        // the case where an inscribed ellipse is already a circle.
        var resting = BorderRendering.Generate(8, 8, PillRadiusFor(8, 0));
        Assert.True(resting.Stroke.Radii.IsCircular);
        Assert.Equal(4, resting.Stroke.Radii.X, 3);

        var active = BorderRendering.Generate(22, 8, PillRadiusFor(8, 0));
        Assert.True(active.Stroke.IsCapsule);
        Assert.Equal(14, active.Stroke.StraightTopEdge, 3);

        // The active one was not: a 22x8 oval, pointed at both ends.
        Assert.True(BorderRendering.Generate(22, 8, cornerRadius: 999).IsEllipse);
    }

    [Fact]
    public void SidePaddingClearsTheEndCap()
    {
        // A capsule's cap eats into its own box, so text needs to start after it or the
        // first letter sits on the curve.
        var padding = SidePaddingOf("Pad.Badge");
        Assert.True(
            padding >= PillRadiusFor(BadgeHeight, Hairline) - 1,
            $"Pad.Badge is {padding} DIP, which does not clear a {PillRadiusFor(BadgeHeight, Hairline)} DIP cap.");
    }

    [Fact]
    public void ACapsuleIsNeverAskedForWithALiteralRadius()
    {
        // The regression this file exists to prevent: somebody writing a big number into a
        // CornerRadius again because that is what it means in CSS.
        foreach (var file in new[] { "Theme/Shared.xaml", "Theme/Tokens.xaml", "MainWindow.xaml" })
        {
            foreach (var value in RadiusValuesIn(file))
            {
                Assert.True(
                    value <= 32,
                    $"{file} asks for a corner radius of {value}. WPF draws that as an ellipse, "
                        + "not a capsule; use infra:PillShape.IsPill instead.");
            }
        }
    }

    [Fact]
    public void EveryPillStyleLeavesItsRadiusToTheBehaviour()
    {
        // A local CornerRadius beats a style setter, so a style must not set both: the
        // badge would keep whichever the behaviour had not yet overwritten.
        var document = XDocument.Load(MarkupPath("Theme/Shared.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var pillStyles = document.Descendants()
            .Where(element => element.Name.LocalName == "Style" && element.Elements().Any(IsPillSetter))
            .ToArray();

        Assert.Equal(3, pillStyles.Length);

        foreach (var style in pillStyles)
        {
            var key = (string?)style.Attribute(x + "Key") ?? "(unkeyed)";
            Assert.False(
                style.Elements().Any(setter => PropertyOf(setter) == "CornerRadius"),
                $"{key} sets both CornerRadius and PillShape.IsPill.");
        }

        static bool IsPillSetter(XElement setter)
            => PropertyOf(setter) == "infra:PillShape.IsPill"
            && (string?)setter.Attribute("Value") == "True";

        static string? PropertyOf(XElement setter)
            => setter.Name.LocalName == "Setter" ? (string?)setter.Attribute("Property") : null;
    }

    /// <summary>The rule PillShape applies, restated here so the test does not import WPF.</summary>
    private static double PillRadiusFor(double height, double borderThickness)
        => Math.Max(0, height - borderThickness) / 2;

    private static double SidePaddingOf(string key)
    {
        var document = XDocument.Load(MarkupPath("Theme/Tokens.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var thickness = document.Descendants()
            .Single(element => element.Name.LocalName == "Thickness" && (string?)element.Attribute(x + "Key") == key)
            .Value;

        return double.Parse(thickness.Split(',')[0], CultureInfo.InvariantCulture);
    }

    private static IEnumerable<double> RadiusValuesIn(string relativePath)
    {
        var document = XDocument.Load(MarkupPath(relativePath));

        var literals = document.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName == "CornerRadius")
            .Select(attribute => attribute.Value)
            .Concat(document.Descendants()
                .Where(element => element.Name.LocalName == "Setter"
                    && (string?)element.Attribute("Property") == "CornerRadius")
                .Select(element => (string?)element.Attribute("Value") ?? string.Empty))
            .Concat(document.Descendants()
                .Where(element => element.Name.LocalName == "CornerRadius")
                .Select(element => element.Value));

        foreach (var literal in literals)
        {
            foreach (var part in literal.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    yield return value;
                }
            }
        }
    }

    private static string MarkupPath(string relativePath) => Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        "src",
        "DpiBypass.App",
        relativePath.Replace('/', Path.DirectorySeparatorChar));
}
