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
            "Radius.Small", "Radius.Control", "Radius.Tile", "Radius.Card", "Radius.Surface",
            "IconSize.Small", "IconSize.Body", "IconSize.Large",
            "Type.Family", "Type.FamilyMono", "Type.Caption", "Type.Body", "Type.Title",
            "Motion.Fast", "Motion.Micro", "Motion.Content", "Motion.State",
            "Motion.EaseOut", "Motion.EaseInOut",
        })
        {
            Assert.Contains(required, keys);
        }

        // Radius.Pill is deliberately not among them. A capsule is not a number in WPF:
        // any radius large enough to "round the ends off" is clamped per axis and draws an
        // ellipse, and the radius that does work depends on the badge's measured height.
        // It lives in Infrastructure/PillShape.cs instead. See BadgeShapeTests.
        Assert.DoesNotContain("Radius.Pill", keys);
        Assert.Contains("Pad.Badge", keys);
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

    /// <summary>
    /// Every StaticResource in the window and the theme names a key that exists.
    /// </summary>
    /// <remarks>
    /// This is the one resource mistake that is fatal rather than cosmetic. A
    /// DynamicResource that resolves to nothing leaves an element unpainted; a
    /// StaticResource that resolves to nothing throws while the window is being built, so
    /// the app starts and then has no window - which is exactly the failure mode the whole
    /// window-recovery path in App.xaml.cs exists to survive. It cannot be caught by
    /// compiling, because BAML defers the lookup to load time, and it cannot be caught by
    /// the existing tests, which read bindings rather than resources.
    /// </remarks>
    [Fact]
    public void EveryStaticResourceReferenceNamesAKeyThatExists()
    {
        // Scope matters, and pooling every key from every file hides the mistake this is
        // for. A StaticResource sees the dictionary it is written in and whatever that
        // dictionary merged before it - nothing else. The theme cannot see the window's
        // own Window.Resources, so a converter declared there and used by a style here
        // compiles cleanly and throws the first time WPF builds a template that uses the
        // style. That is what happened to GainBrushConverter, and only the Windows render
        // caught it.
        var tokens = KeysIn(TokensXaml).Concat(KeysIn(IconsXaml)).ToHashSet(StringComparer.Ordinal);

        // The palette is merged by ThemeManager at application level and swapped live, so
        // the theme reaches it with DynamicResource. Its keys are deliberately not here.
        var themeScope = tokens.Concat(KeysIn(RepoFiles.SharedThemeXaml)).ToHashSet(StringComparer.Ordinal);
        var windowScope = themeScope.Concat(KeysIn(RepoFiles.MainWindowXaml)).ToHashSet(StringComparer.Ordinal);

        var unresolved = new List<string>();

        foreach (var (file, visible) in new[]
        {
            (RepoFiles.SharedThemeXaml, themeScope),
            (RepoFiles.MainWindowXaml, windowScope),
        })
        {
            var markup = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(markup, @"\{StaticResource\s+(?<key>[^}\s]+)\s*\}"))
            {
                var key = match.Groups["key"].Value;

                // "{StaticResource {x:Type infra:FluentIcon}}" names an implicit style,
                // whose key is the type rather than a string.
                if (key.StartsWith("{x:Type", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!visible.Contains(key))
                {
                    unresolved.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        Assert.Empty(unresolved);
    }

    /// <summary>
    /// The theme dictionaries the application loads are all of them, and they are merged
    /// in an order where a StaticResource can see what it needs.
    /// </summary>
    /// <remarks>
    /// StaticResource is resolved as the dictionary is parsed, so a token dictionary
    /// merged after the styles that read from it resolves to nothing and throws. Shared
    /// merges its own dependencies rather than relying on App to do it in the right order.
    /// </remarks>
    [Fact]
    public void TheThemeMergesItsTokensAndIconsBeforeItUsesThem()
    {
        var document = XDocument.Load(RepoFiles.SharedThemeXaml);
        var ns = document.Root!.Name.Namespace;

        var merged = document
            .Descendants(ns + "ResourceDictionary.MergedDictionaries")
            .SelectMany(holder => holder.Elements(ns + "ResourceDictionary"))
            .Select(element => (string?)element.Attribute("Source") ?? string.Empty)
            .ToArray();

        Assert.Equal(["Tokens.xaml", "Icons.xaml"], merged);

        // And the merge block is the first thing in the dictionary, ahead of every style
        // that reads from it.
        var firstElement = document.Root!.Elements().First();
        Assert.Equal("ResourceDictionary.MergedDictionaries", firstElement.Name.LocalName);
    }

    /// <summary>
    /// The icon control draws itself rather than templating a Path.
    /// </summary>
    /// <remarks>
    /// Not a style preference. A Path with Stretch="None" reports its geometry's bounds as
    /// its desired size, and WPF layout-clips any element arranged into less than it asked
    /// for - so a 20-unit glyph inside a 16 DIP box loses its right and bottom fifth,
    /// before the render transform that would have made it fit ever runs. Every icon on a
    /// button in this window is 16 DIP. Putting a Template setter back would bring that
    /// back with it.
    /// </remarks>
    [Fact]
    public void TheIconControlHasNoTemplateToBeLayoutClippedBy()
    {
        var document = XDocument.Load(RepoFiles.SharedThemeXaml);
        var ns = document.Root!.Name.Namespace;

        var style = document
            .Descendants(ns + "Style")
            .Single(element => (string?)element.Attribute("TargetType") == "{x:Type infra:FluentIcon}"
                && element.Attribute(X + "Key") is null);

        Assert.DoesNotContain(
            style.Elements(ns + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Template");

        Assert.DoesNotContain(style.Descendants(), element => element.Name.LocalName is "ControlTemplate" or "Path");
    }

    /// <summary>
    /// A markup extension is never buried inside a longer attribute value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// XAML only treats <c>{…}</c> as markup when it is the <em>whole</em> value. Written
    /// anywhere else - <c>Margin="0,0,0,{StaticResource Space.8}"</c> - it is a literal
    /// string, handed to the property's type converter, which throws while the window is
    /// being built. And nothing catches it first: the compiler defers the conversion into
    /// BAML, and a test that only checks whether each key resolves sees a perfectly good
    /// key name.
    /// </para>
    /// <para>
    /// This shipped once. ThicknessConverter threw on the two Margin setters above,
    /// Shared.xaml stopped loading at that line, and every style declared after it - which
    /// is most of them - was simply absent, so the retry died on a missing
    /// SectionTitleStyle instead. The published app could not build its window at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoAttributeBuriesAMarkupExtensionInsideALongerValue()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            Path.GetDirectoryName(RepoFiles.MainWindowXaml)!,
            "*.xaml",
            SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);

            for (var number = 0; number < lines.Length; number++)
            {
                foreach (Match match in Regex.Matches(lines[number], @"[\w.:]+=""(?<value>[^""]*)"""))
                {
                    var value = match.Groups["value"].Value;

                    // "{}" at the start is XAML's own escape for a literal brace, and a
                    // value that begins with "{" is a real markup extension.
                    if (value.Contains('{', StringComparison.Ordinal)
                        && !value.TrimStart().StartsWith('{'))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{number + 1} {match.Value}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// A Thickness, a CornerRadius or a FontFamily cannot be composed from a token, so the
    /// composed values are tokens of their own.
    /// </summary>
    /// <remarks>
    /// The rule above says what may not be written; this says what to write instead. Every
    /// spacing value the styles use is either a whole-value resource reference or a plain
    /// literal - never one glued to the other.
    /// </remarks>
    [Fact]
    public void TheComposedSpacingValuesExistAsTokensOfTheirOwn()
    {
        var keys = KeysIn(TokensXaml);

        foreach (var required in new[]
        {
            "Pad.Card", "Pad.Tile", "Pad.Control", "Pad.Pill", "Pad.Page",
            "Gap.StackTight", "Gap.Stack", "Gap.StackWide", "Gap.Section",
            "Gap.InlineStart", "Gap.InlineEnd",
            "Border.Hairline", "Border.Focus",
        })
        {
            Assert.Contains(required, keys);
        }

        // Radius.Pill is deliberately not among them. A capsule is not a number in WPF:
        // any radius large enough to "round the ends off" is clamped per axis and draws an
        // ellipse, and the radius that does work depends on the badge's measured height.
        // It lives in Infrastructure/PillShape.cs instead. See BadgeShapeTests.
        Assert.DoesNotContain("Radius.Pill", keys);
        Assert.Contains("Pad.Badge", keys);
    }

    /// <summary>
    /// Text set beside an icon carries the optical correction that centring does not.
    /// </summary>
    /// <remarks>
    /// WPF centres a text run's line box, and that box reserves descender room whether or
    /// not the word has a descender - so centring it against a glyph lands the letters
    /// about a pixel low. Measured on a real frame it was +1.0 to +3.5 px across the
    /// navigation rail and +2.5 px on the primary button. The correction is one token
    /// applied in two places, and it is exactly the kind of thing that gets "tidied away"
    /// by somebody who cannot see why the margin is asymmetric - hence this.
    /// </remarks>
    [Fact]
    public void LabelsBesideAnIconCarryTheOpticalLift()
    {
        var keys = KeysIn(TokensXaml);
        Assert.Contains("Text.OpticalLift", keys);
        Assert.Contains("Nav.LabelOffset", keys);

        var document = XDocument.Load(RepoFiles.MainWindowXaml);
        var ns = document.Root!.Name.Namespace;

        // Every rail label, and no rail label left on a plain symmetric margin.
        var labels = document.Descendants(ns + "AccessText").ToArray();
        Assert.Equal(6, labels.Length);
        Assert.All(labels, label =>
            Assert.Equal("{StaticResource Nav.LabelOffset}", (string?)label.Attribute("Margin")));

        // And the one button template every button in the window is built from.
        var theme = File.ReadAllText(RepoFiles.SharedThemeXaml);
        Assert.Contains("Margin=\"{StaticResource Text.OpticalLift}\"", theme, StringComparison.Ordinal);
    }

    /// <summary>
    /// The top bar speaks one corner language.
    /// </summary>
    /// <remarks>
    /// The status chip briefly carried Radius.Pill, which rounds its ends into
    /// semicircles. Beside the primary action's 8 DIP corners that read as a stray oval
    /// behind the words rather than as part of the same bar. Fully-round is right for a
    /// dot and wrong for a chip sitting next to a button.
    /// </remarks>
    [Fact]
    public void TheStatusChipSharesThePrimaryActionsCornerRadius()
    {
        var document = XDocument.Load(RepoFiles.SharedThemeXaml);
        var ns = document.Root!.Name.Namespace;

        var chip = document
            .Descendants(ns + "Style")
            .Single(style => (string?)style.Attribute(X + "Key") == "StatusPillBorderStyle");

        var radius = chip.Elements(ns + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == "CornerRadius");

        Assert.Equal("{StaticResource Radius.Tile}", (string?)radius.Attribute("Value"));
    }
}
