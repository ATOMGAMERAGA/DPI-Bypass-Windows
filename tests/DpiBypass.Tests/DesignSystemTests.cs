using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The rules the design system is only worth having if nothing quietly breaks them.
/// </summary>
/// <remarks>
/// None of this needs WPF to run, which is the point: an icon that resolves to nothing,
/// a palette key that exists in one theme and not the other, or a colour written into a
/// style are all mistakes that would otherwise only show up as a blank square or a
/// invisible label on somebody's screen.
/// </remarks>
public sealed class DesignSystemTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string IconsXaml => RepoFiles.Find("src", "DpiBypass.App", "Theme", "Icons.xaml");

    private static string TokensXaml => RepoFiles.Find("src", "DpiBypass.App", "Theme", "Tokens.xaml");

    private static string LightXaml => RepoFiles.Find("src", "DpiBypass.App", "Theme", "Light.xaml");

    private static string DarkXaml => RepoFiles.Find("src", "DpiBypass.App", "Theme", "Dark.xaml");

    private static IReadOnlySet<string> KeysIn(string path)
        => XDocument.Load(path)
            .Descendants()
            .Select(element => (string?)element.Attribute(X + "Key"))
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every symbol the markup asks for is a glyph the icon set actually has.</summary>
    /// <remarks>
    /// FluentIcon returns null for a symbol it cannot find rather than throwing, because
    /// a page that fails to build is worse than a page with one glyph missing. That makes
    /// a typo invisible at runtime, so it has to be caught here.
    /// </remarks>
    [Fact]
    public void EverySymbolTheMarkupNamesExistsInTheIconSet()
    {
        var keys = KeysIn(IconsXaml);
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in new[] { RepoFiles.MainWindowXaml, RepoFiles.SharedThemeXaml })
        {
            var markup = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(markup, @"(?:ActionButton\.)?Symbol\s*=\s*""(?<symbol>[A-Za-z]+)"""))
            {
                symbols.Add(match.Groups["symbol"].Value);
            }
        }

        Assert.NotEmpty(symbols);

        foreach (var symbol in symbols)
        {
            // Both grids, because FluentIcon picks by size and a symbol that only exists
            // on one of them silently changes size when a caller asks for the other.
            Assert.Contains($"Icon.{symbol}.20.Regular", keys);
            Assert.Contains($"Icon.{symbol}.24.Regular", keys);
        }
    }

    /// <summary>
    /// The icon geometries are upstream path data, not something drawn here.
    /// </summary>
    /// <remarks>
    /// The giveaway is the fill rule. Every generated geometry carries the <c>F1</c>
    /// prefix, which is what makes WPF use SVG's nonzero winding rule; a geometry typed
    /// in by hand would not have it, and would render with holes in it wherever two
    /// subpaths wind the same way.
    /// </remarks>
    [Fact]
    public void TheIconGeometriesAreUpstreamSvgPathDataUnderTheNonZeroFillRule()
    {
        var document = XDocument.Load(IconsXaml);
        var geometries = document.Descendants(Xaml + "Geometry").ToArray();

        Assert.True(geometries.Length >= 100, $"expected a full icon set, found {geometries.Length}");

        foreach (var geometry in geometries)
        {
            var key = (string?)geometry.Attribute(X + "Key");
            Assert.NotNull(key);
            Assert.Matches(@"^Icon\.[A-Za-z]+\.(20|24)\.(Regular|Filled)$", key!);
            Assert.StartsWith("F1 M", geometry.Value, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// No hand-drawn icon geometry survives in the design system or the window.
    /// </summary>
    [Fact]
    public void NothingDrawsItsOwnIconAnyMore()
    {
        foreach (var file in new[] { RepoFiles.MainWindowXaml, RepoFiles.SharedThemeXaml })
        {
            var document = XDocument.Load(file);

            // A stroked Path is how the old icons were drawn. Genuine drawing - the
            // connection ring, a divider - uses Ellipse, Border or Line, never Path.
            var strokedPaths = document
                .Descendants(Xaml + "Path")
                .Where(path => path.Attribute("Stroke") is not null)
                .ToArray();

            Assert.Empty(strokedPaths);

            // And nothing declares a geometry of its own outside the generated file.
            Assert.Empty(document.Descendants(Xaml + "Geometry"));
        }
    }

    /// <summary>
    /// Emoji are not an icon family: they render differently per Windows build, ignore
    /// the app's colour, and have no size variants. The interface uses none.
    /// </summary>
    [Fact]
    public void TheInterfaceUsesNoEmojiAsIcons()
    {
        foreach (var file in new[] { RepoFiles.MainWindowXaml, RepoFiles.SharedThemeXaml })
        {
            var markup = File.ReadAllText(file);

            var emoji = markup.EnumerateRunes()
                .Where(rune => rune.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF)
                .Select(rune => rune.ToString())
                .Distinct()
                .ToArray();

            Assert.Empty(emoji);
        }
    }

    /// <summary>
    /// The two palettes are interchangeable: the same key set, so switching theme can
    /// never leave a DynamicResource unresolved and an element painted with nothing.
    /// </summary>
    [Fact]
    public void TheLightAndDarkPalettesDeclareTheSameKeys()
    {
        var light = KeysIn(LightXaml);
        var dark = KeysIn(DarkXaml);

        Assert.Equal(light.OrderBy(key => key, StringComparer.Ordinal), dark.OrderBy(key => key, StringComparer.Ordinal));
        Assert.NotEmpty(light);
    }

    /// <summary>
    /// Colour lives in the palettes and nowhere else.
    /// </summary>
    /// <remarks>
    /// A literal <c>#RRGGBB</c> in a style is a colour that cannot follow the theme: it
    /// looks right in whichever mode it was written in and wrong in the other one, and
    /// no amount of palette swapping reaches it.
    /// </remarks>
    [Fact]
    public void NoStyleOrPageWritesAColourOfItsOwn()
    {
        foreach (var file in new[] { RepoFiles.MainWindowXaml, RepoFiles.SharedThemeXaml, TokensXaml })
        {
            var markup = File.ReadAllText(file);
            var literals = Regex.Matches(markup, @"""#[0-9A-Fa-f]{3,8}""")
                .Select(match => match.Value)
                .Distinct()
                .ToArray();

            Assert.Empty(literals);
        }
    }

    /// <summary>
    /// The tokens are the single source for spacing, radius, type and motion.
    /// </summary>
    [Fact]
    public void TheTokenFileCarriesEveryAxisTheStylesReadFromIt()
    {
        var keys = KeysIn(TokensXaml);

        foreach (var required in new[]
        {
            "Space.4", "Space.8", "Space.12", "Space.16", "Space.24",
            "Radius.Small", "Radius.Control", "Radius.Tile", "Radius.Card", "Radius.Pill",
            "IconSize.Small", "IconSize.Body", "IconSize.Large",
            "Type.Family", "Type.FamilyMono", "Type.Caption", "Type.Body", "Type.Title",
            "Motion.Fast", "Motion.Micro", "Motion.Content", "Motion.State",
            "Motion.EaseOut", "Motion.EaseInOut",
        })
        {
            Assert.Contains(required, keys);
        }
    }

    /// <summary>
    /// The motion durations stay inside the bands this app was timed to.
    /// </summary>
    /// <remarks>
    /// Micro-interactions 100-160 ms, content transitions 180-240 ms, a significant
    /// state change 250-350 ms. These are this application's starting points rather than
    /// a universal standard, which is exactly why they are written down: a number that
    /// drifts out of its band should have to be argued for, not typed.
    /// </remarks>
    [Theory]
    [InlineData("Motion.Fast", 100, 160)]
    [InlineData("Motion.Micro", 100, 160)]
    [InlineData("Motion.Content", 180, 240)]
    [InlineData("Motion.ContentSlow", 180, 240)]
    [InlineData("Motion.State", 250, 350)]
    [InlineData("Motion.StateSlow", 250, 350)]
    public void EachMotionDurationStaysInItsBand(string key, int lowerMs, int upperMs)
    {
        var value = XDocument.Load(TokensXaml)
            .Descendants(Xaml + "Duration")
            .Single(element => (string?)element.Attribute(X + "Key") == key)
            .Value;

        var duration = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.InRange(duration.TotalMilliseconds, lowerMs, upperMs);
    }
}
