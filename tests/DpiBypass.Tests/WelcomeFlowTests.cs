using System.Xml.Linq;
using DpiBypass.Core.Config;
using DpiBypass.Core.Onboarding;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// When the window greets somebody, and what it promises them.
/// </summary>
/// <remarks>
/// A greeting answers an arrival. Getting that wrong is not cosmetic: one that appears
/// every time the window comes back from the notification area turns the app's own front
/// door into an obstacle, and one that never appears leaves a first-time user in front of
/// a network tool with no idea what pressing the button will do.
/// </remarks>
public sealed class WelcomeFlowTests
{
    private static WelcomeContext Launch(
        bool byUser = true,
        bool minimised = false,
        bool fromTray = false,
        bool seenTour = false,
        bool showOnStartup = true)
        => new(byUser, minimised, fromTray, seenTour, showOnStartup);

    [Fact]
    public void TheFirstManualLaunchGetsTheIntroduction()
        => Assert.Equal(WelcomeKind.Tour, WelcomeFlow.Decide(Launch()));

    /// <summary>
    /// Even with the startup preference off: somebody who has never seen the app cannot
    /// have meaningfully turned off a greeting they have never been shown.
    /// </summary>
    [Fact]
    public void TheFirstLaunchIgnoresTheStartupPreference()
        => Assert.Equal(WelcomeKind.Tour, WelcomeFlow.Decide(Launch(showOnStartup: false)));

    [Fact]
    public void ALaterManualLaunchGetsTheShortGreeting()
        => Assert.Equal(WelcomeKind.Brief, WelcomeFlow.Decide(Launch(seenTour: true)));

    [Fact]
    public void TurningTheStartupPreferenceOffSkipsTheGreetingAltogether()
        => Assert.Equal(WelcomeKind.None, WelcomeFlow.Decide(Launch(seenTour: true, showOnStartup: false)));

    /// <summary>The logon task is not an arrival, whatever else is set.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ALaunchWindowsMadeGreetsNobody(bool seenTour, bool showOnStartup)
        => Assert.Equal(
            WelcomeKind.None,
            WelcomeFlow.Decide(Launch(byUser: false, seenTour: seenTour, showOnStartup: showOnStartup)));

    /// <summary>Neither is a launch that goes straight to the notification area.</summary>
    [Fact]
    public void ALaunchThatStartsInTheTrayGreetsNobody()
        => Assert.Equal(WelcomeKind.None, WelcomeFlow.Decide(Launch(minimised: true)));

    /// <summary>
    /// And neither is the window reappearing in a session that has been running all day.
    /// </summary>
    [Fact]
    public void ComingBackFromTheTrayIsNotAnArrival()
    {
        Assert.Equal(WelcomeKind.None, WelcomeFlow.Decide(Launch(fromTray: true)));
        Assert.Equal(WelcomeKind.None, WelcomeFlow.Decide(Launch(fromTray: true, seenTour: false)));
    }

    [Fact]
    public void TheIntroductionIsFourShortCards()
    {
        Assert.Equal(4, WelcomeFlow.Cards.Count);

        foreach (var card in WelcomeFlow.Cards)
        {
            Assert.False(string.IsNullOrWhiteSpace(card.Title));
            Assert.False(string.IsNullOrWhiteSpace(card.Body));
            Assert.False(string.IsNullOrWhiteSpace(card.Symbol));

            // One sentence, not a manual page. The whole point of the introduction is that
            // it is shorter than the thing it introduces.
            Assert.True(card.Body.Length <= 120, $"'{card.Title}' is {card.Body.Length} characters");
            Assert.EndsWith(".", card.Body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The copy promises only what the app does.
    /// </summary>
    /// <remarks>
    /// This app finds settings that work on a network. It is not a VPN, it does not make
    /// anybody anonymous, and it does not encrypt the user's traffic - saying otherwise in
    /// the first four sentences somebody reads would be the most damaging place to say it.
    /// </remarks>
    [Fact]
    public void TheIntroductionPromisesNothingTheAppDoesNotDo()
    {
        var copy = string.Join(' ', WelcomeFlow.Cards.SelectMany(card => new[] { card.Title, card.Body }));

        foreach (var claim in new[] { "VPN", "anonim", "şifrele", "gizli kalır", "takip edilemez", "güvenli tünel" })
        {
            Assert.DoesNotContain(claim, copy, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The wording the brief says the app actually does.</summary>
    [Fact]
    public void TheFirstCardSaysWhatTheAppIsFor()
    {
        var first = WelcomeFlow.Cards[0];

        Assert.Contains("hoş geldin", first.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ağında çalışan ayarları", first.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheStartupPreferenceDefaultsToOnAndTheTourToUnseen()
    {
        var settings = new AppSettings();

        Assert.True(settings.ShowWelcomeOnStartup);
        Assert.False(settings.WelcomeCompleted);
    }

    /// <summary>
    /// The greeting lives inside the window: nothing in its markup changes the window's
    /// state, style or size.
    /// </summary>
    /// <remarks>
    /// "Covers the content area" and "goes full screen" look the same in a description and
    /// nothing alike to somebody who had the window where they wanted it. The overlay is
    /// a Grid in the client area; a WindowState or WindowStyle setter anywhere near it
    /// would mean something else entirely.
    /// </remarks>
    [Fact]
    public void TheGreetingNeverResizesOrMaximisesTheWindow()
    {
        var document = XDocument.Load(RepoFiles.MainWindowXaml);
        var ns = document.Root!.Name.Namespace;
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        var overlay = document
            .Descendants(ns + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "WelcomeOverlay");

        foreach (var forbidden in new[] { "WindowState", "WindowStyle", "Topmost", "ResizeMode", "Width", "Height" })
        {
            Assert.Null(overlay.Attribute(forbidden));
        }

        // And the window itself is never told to change state anywhere in the markup.
        Assert.DoesNotContain(
            document.Descendants().SelectMany(element => element.Attributes()),
            attribute => attribute.Name.LocalName is "WindowState" or "WindowStyle");

        // The shell is collapsed rather than merely painted over, so Tab cannot reach a
        // control behind the greeting.
        var shell = document
            .Descendants(ns + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "AppShell");

        Assert.Equal("{StaticResource AppShellStyle}", (string?)shell.Attribute("Style"));

        var theme = XDocument.Load(RepoFiles.SharedThemeXaml);
        var shellStyle = theme.Descendants(theme.Root!.Name.Namespace + "Style")
            .Single(style => (string?)style.Attribute(x + "Key") == "AppShellStyle");

        Assert.Contains(
            shellStyle.Descendants(theme.Root!.Name.Namespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Visibility"
                && (string?)setter.Attribute("Value") == "Collapsed");
    }

    /// <summary>Back, Next and Skip are all reachable, and so is re-opening the tour later.</summary>
    [Fact]
    public void TheIntroductionOffersBackNextSkipAndCanBeReopenedFromSettings()
    {
        var bindings = UiBindings.PathsIn(RepoFiles.MainWindowXaml);

        foreach (var member in new[]
        {
            "WelcomeBackCommand",
            "WelcomeNextCommand",
            "WelcomeSkipCommand",
            "ShowWelcomeTourCommand",
            "ShowWelcomeOnStartup",
            "WelcomePrimaryAction",
            "WelcomeSteps",
            "IsWelcomeTour",
        })
        {
            Assert.Contains(member, bindings);
        }
    }
}
