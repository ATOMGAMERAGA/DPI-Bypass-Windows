using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The rules the greeting's motion has to keep, read off the markup that declares it.
/// </summary>
/// <remarks>
/// Motion is easy to add and hard to take back once it is costing frames on somebody's
/// laptop, so the constraints live here rather than in a review comment: nothing repeats,
/// nothing blurs, the whole entrance is short enough not to be a wait, and there is a
/// complete design underneath it for a machine that asked for no animation at all.
/// </remarks>
public sealed class WelcomeMotionTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Fluent 2 puts a change of this size in the 250-350 ms band.</summary>
    private const double EntranceBudgetMs = 400;

    [Fact]
    public void NothingInTheGreetingRepeats()
    {
        // A storyboard that repeats forever is a surface being redrawn for as long as the
        // greeting is on screen. Everything here runs once and stops.
        foreach (var element in WelcomeMarkup())
        {
            foreach (var animation in element.Descendants().Where(IsAnimation))
            {
                var repeat = (string?)animation.Attribute("RepeatBehavior");
                Assert.True(
                    repeat is null or "1x",
                    $"{Name(element)} repeats ({repeat}); the greeting's motion must run once.");

                Assert.Null(animation.Attribute("AutoReverse"));
            }
        }
    }

    [Fact]
    public void TheGreetingDoesNotBlurAnything()
    {
        // A BlurEffect is a per-frame GPU pass over everything beneath it, which is the one
        // thing a large soft background must not be. The light is a radial gradient - one
        // brush, drawn once - and the rest of the app avoids DropShadowEffect for the same
        // reason.
        foreach (var element in WelcomeMarkup())
        {
            foreach (var effect in element.Descendants())
            {
                Assert.False(
                    effect.Name.LocalName is "BlurEffect" or "DropShadowEffect",
                    $"{Name(element)} uses {effect.Name.LocalName}.");
            }
        }

        Assert.Contains(
            WelcomeMarkup(),
            element => element.Descendants().Any(child => child.Name.LocalName == "RadialGradientBrush"));
    }

    [Fact]
    public void TheWholeEntranceIsShorterThanAWait()
    {
        // Every animation's start plus its own length, across the card, the light, the
        // brand and the action bar. The longest of those is what somebody actually waits
        // through before the greeting has finished arriving.
        var longest = WelcomeMarkup()
            .SelectMany(element => element.Descendants().Where(IsAnimation))
            .Select(animation => Milliseconds(animation, "BeginTime") + DurationOf(animation))
            .DefaultIfEmpty(0)
            .Max();

        Assert.True(longest > 0, "The greeting declares no motion at all.");
        Assert.True(
            longest <= EntranceBudgetMs,
            $"The greeting's entrance runs for {longest:0} ms, past the {EntranceBudgetMs:0} ms budget.");
    }

    [Fact]
    public void ThePiecesArriveInTheOrderTheyAreRead()
    {
        // Mark, then heading, then the sentence under it - each a beat behind the last.
        // Staggered rather than simultaneous is the whole difference between "it appeared"
        // and "it arrived", and the order has to match how the card is read.
        var card = Resource("WelcomeCardAnimatedTemplate");

        var begins = new[] { "WelcomeCardIcon", "WelcomeCardTitle", "WelcomeCardBody" }
            .Select(target => card.Descendants()
                .Where(IsAnimation)
                .Where(animation => (string?)animation.Attribute("Storyboard.TargetName") == target)
                .Min(animation => Milliseconds(animation, "BeginTime")))
            .ToArray();

        Assert.Equal(0, begins[0]);
        Assert.True(begins[1] > begins[0], "The heading does not follow the mark.");
        Assert.True(begins[2] > begins[1], "The sentence does not follow the heading.");

        // Close enough together to read as one movement rather than three.
        Assert.True(begins[2] - begins[0] <= 200, "The stagger is long enough to read as a delay.");
    }

    [Fact]
    public void TheActionBarDoesNotMakeAnybodyWaitForIt()
    {
        // The buttons are hit-testable from the first frame whatever their opacity, but
        // somebody who cannot see them will not press them. So the bar's own entrance is
        // the shortest thing in the greeting, and it never starts late.
        var bar = Resource("WelcomeActionsStyle");

        foreach (var animation in bar.Descendants().Where(IsAnimation))
        {
            Assert.Equal(0, Milliseconds(animation, "BeginTime"));
            Assert.True(
                DurationOf(animation) <= 160,
                $"The action bar takes {DurationOf(animation):0} ms to appear.");
        }
    }

    [Fact]
    public void ThereIsACompleteDesignWithTheMotionTakenOut()
    {
        // Not a fallback that looks like a greeting which failed to start: the same sizes,
        // the same spacing and the same hierarchy, with the movement removed.
        var animated = Resource("WelcomeCardAnimatedTemplate");
        var stat1c = Resource("WelcomeCardStaticTemplate");

        Assert.DoesNotContain(stat1c.Descendants(), IsAnimation);
        Assert.DoesNotContain(
            stat1c.Descendants(),
            element => element.Name.LocalName.EndsWith("Transform", StringComparison.Ordinal));

        // The mark, the heading and the sentence all still there, at the same sizes.
        foreach (var attribute in new[] { "Width", "Height" })
        {
            Assert.Equal(
                Values(animated, "Border", attribute),
                Values(stat1c, "Border", attribute));
        }

        Assert.Equal(
            Values(animated, "TextBlock", "FontSize"),
            Values(stat1c, "TextBlock", "FontSize"));

        Assert.Equal(
            Values(animated, "TextBlock", "Margin"),
            Values(stat1c, "TextBlock", "Margin"));

        // And the other two pieces have static twins as well, so a reduced-motion machine
        // cannot end up with a still card behind a light that is still scaling in.
        foreach (var key in new[] { "WelcomeGlowStaticStyle", "WelcomeBrandStaticStyle" })
        {
            Assert.DoesNotContain(Resource(key).Descendants(), IsAnimation);
        }
    }

    [Fact]
    public void TheGreetingCannotResizeOrMoveTheWindow()
    {
        // It fills the window's content area because it is a child of the window's layout
        // root. Nothing in it may reach for the window itself.
        var window = XDocument.Load(RepoFiles.MainWindowXaml);
        var overlay = window.Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "WelcomeOverlay");

        Assert.Equal("Window", overlay.Parent!.Parent!.Name.LocalName);

        foreach (var forbidden in new[] { "WindowState", "WindowStyle", "Topmost", "Left", "Top", "Width", "Height" })
        {
            Assert.DoesNotContain(
                overlay.DescendantsAndSelf().SelectMany(element => element.Attributes()),
                attribute => attribute.Name.LocalName == forbidden
                    && attribute.Value.Contains("Window", StringComparison.Ordinal));
        }
    }

    /// <summary>Every resource the greeting's look and motion is declared in.</summary>
    private static IReadOnlyList<XElement> WelcomeMarkup() =>
    [
        Resource("WelcomeCardAnimatedTemplate"),
        Resource("WelcomeCardStaticTemplate"),
        Resource("WelcomeGlowStyle"),
        Resource("WelcomeBrandStyle"),
        Resource("WelcomeActionsStyle"),
        Resource("WelcomeOverlayStyle"),
    ];

    private static XElement Resource(string key)
        => XDocument.Load(RepoFiles.SharedThemeXaml).Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Key") == key);

    private static string Name(XElement element) => (string?)element.Attribute(Xaml + "Key") ?? element.Name.LocalName;

    private static bool IsAnimation(XElement element)
        => element.Name.LocalName.EndsWith("Animation", StringComparison.Ordinal)
        || element.Name.LocalName.EndsWith("AnimationUsingKeyFrames", StringComparison.Ordinal);

    /// <summary>The animation's own length, following a Duration through the token it names.</summary>
    private static double DurationOf(XElement animation)
    {
        var duration = (string?)animation.Attribute("Duration");
        if (duration is null)
        {
            return 0;
        }

        if (duration.StartsWith('{'))
        {
            var key = duration.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last().TrimEnd('}');
            var token = XDocument.Load(RepoFiles.Find("src", "DpiBypass.App", "Theme", "Tokens.xaml"))
                .Descendants()
                .Single(element => (string?)element.Attribute(Xaml + "Key") == key);

            return TimeSpan.Parse(token.Value, CultureInfo.InvariantCulture).TotalMilliseconds;
        }

        return TimeSpan.Parse(duration, CultureInfo.InvariantCulture).TotalMilliseconds;
    }

    private static double Milliseconds(XElement animation, string attribute)
        => (string?)animation.Attribute(attribute) is { } value
            ? TimeSpan.Parse(value, CultureInfo.InvariantCulture).TotalMilliseconds
            : 0;

    private static IReadOnlyList<string> Values(XElement root, string tag, string attribute)
        => [.. root.Descendants()
            .Where(element => element.Name.LocalName == tag)
            .Select(element => (string?)element.Attribute(attribute))
            .Where(value => value is not null)
            .Select(value => value!)];
}
