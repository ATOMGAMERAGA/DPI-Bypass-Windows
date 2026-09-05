using System.Xml.Linq;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The parts of the window that fail at run time rather than at compile time.
/// </summary>
/// <remarks>
/// A <c>Click</c> handler the code-behind does not have, and an <c>ElementName</c>
/// nothing declares, both compile perfectly and then either throw while the window is
/// being built or silently do nothing. Neither is caught by the build, and neither is
/// caught by a window self-test that only opens the first page.
/// </remarks>
public sealed class UiSurfaceTests
{
    private static XDocument Window() => XDocument.Load(RepoFiles.MainWindowXaml);

    private static string CodeBehind() => File.ReadAllText(
        RepoFiles.Find("src", "DpiBypass.App", "MainWindow.xaml.cs"));

    /// <summary>Every event handler named in the markup exists in the code-behind.</summary>
    [Fact]
    public void EveryEventHandlerInTheMarkupExists()
    {
        var code = CodeBehind();
        var handlers = Window()
            .Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "Click" or "SelectionChanged" or "Loaded" or "Checked" or "TextChanged")
            .Select(attribute => attribute.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, handler => Assert.Contains($"void {handler}(", code, StringComparison.Ordinal));
    }

    /// <summary>Every <c>ElementName</c> binding names something the window declares.</summary>
    [Fact]
    public void EveryElementNameBindingNamesADeclaredElement()
    {
        var document = Window();
        var xaml = document.Root!.GetNamespaceOfPrefix("x")!;

        var declared = document
            .Descendants()
            .Select(element => element.Attribute(xaml + "Name")?.Value)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        var referenced = document
            .Descendants()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .Where(value => value.Contains("ElementName=", StringComparison.Ordinal))
            .Select(value => value
                .Split("ElementName=", StringSplitOptions.None)[1]
                .Split([',', '}'])[0]
                .Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(referenced);
        Assert.All(referenced, name => Assert.Contains(name, declared));
    }

    /// <summary>
    /// The long settings page offers a way to reach the three sections below its fold.
    /// </summary>
    /// <remarks>
    /// Ping, the send-rate cap and Vodafone mode are all off screen on a 1366x768 display
    /// at 125%, which is a very ordinary laptop. Buttons rather than hyperlinks so they
    /// are in the tab order like everything else on the page.
    /// </remarks>
    [Fact]
    public void TheLongSettingsPageCanJumpToItsThreeMainSections()
    {
        var document = Window();
        var xaml = document.Root!.GetNamespaceOfPrefix("x")!;
        var names = document
            .Descendants()
            .Select(element => element.Attribute(xaml + "Name")?.Value)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("LatencySection", names);
        Assert.Contains("TrafficGuardSection", names);
        Assert.Contains("VodafoneSection", names);

        var jumps = document
            .Descendants()
            .Where(element => element.Attribute("Click")?.Value == "OnJumpToSection")
            .ToArray();

        Assert.Equal(3, jumps.Length);
        Assert.All(jumps, button => Assert.Contains("ElementName=", button.Attribute("Tag")!.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// The log page can be narrowed and says what it is showing.
    /// </summary>
    [Fact]
    public void TheLogPageOffersALevelFilterAndASearch()
    {
        var document = Window();
        var ns = document.Root!.Name.Namespace;

        var levelFilter = document.Descendants(ns + "ComboBox")
            .Single(box => box.Attribute("ItemsSource")?.Value.Contains("LogLevelOptions", StringComparison.Ordinal) == true);
        var search = document.Descendants(ns + "TextBox")
            .Single(box => box.Attribute("Text")?.Value.Contains("LogSearch", StringComparison.Ordinal) == true);

        // Both are typed into or scrolled past, so both need a name a screen reader can use.
        Assert.NotNull(levelFilter.Attributes().SingleOrDefault(a => a.Name.LocalName == "Name"));
        Assert.NotNull(search.Attributes().SingleOrDefault(a => a.Name.LocalName == "Name"));

        // The list shows the filtered view, so copy and the list agree with each other.
        var list = document.Descendants(ns + "ListBox")
            .Single(box => box.Attribute("ItemsSource")?.Value.Contains("VisibleLogLines", StringComparison.Ordinal) == true);

        Assert.NotNull(list);
    }

    /// <summary>
    /// The report action is on the log page, and says what it does before it is pressed.
    /// </summary>
    [Fact]
    public void TheDiagnosticReportButtonExplainsItselfBeforeItIsPressed()
    {
        var document = Window();
        var ns = document.Root!.Name.Namespace;

        var button = document.Descendants(ns + "Button")
            .Single(b => b.Attribute("Command")?.Value.Contains("SaveDiagnosticReportCommand", StringComparison.Ordinal) == true);

        var tooltip = button.Attribute("ToolTip")?.Value ?? string.Empty;

        // Sending a file that carries your network's name is not something to discover
        // afterwards, so both facts are on the button itself.
        Assert.Contains("takma ad", tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bu bilgisayarda kalır", tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(button.Attributes().SingleOrDefault(a => a.Name.LocalName == "AutomationProperties.Name"));
    }

    /// <summary>
    /// The engine's state and the target site's reachability are two separate lines.
    /// </summary>
    /// <remarks>
    /// A running engine says a driver handle is open. Whether discord.com answers is a
    /// different question, and one green headline covering both is how somebody ends up
    /// looking at a reassuring window while nothing loads.
    /// </remarks>
    [Fact]
    public void TheStatusCardSeparatesTheEngineFromTheVerification()
    {
        var text = File.ReadAllText(RepoFiles.MainWindowXaml);

        Assert.Contains("{Binding StatusHeadline}", text, StringComparison.Ordinal);
        Assert.Contains("VerificationSummary", text, StringComparison.Ordinal);
        Assert.Contains("VerificationSeverity", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing on the new surfaces relies on colour alone.
    /// </summary>
    /// <remarks>
    /// Each severity-coloured line is bound to a Tag that only picks a brush; the wording
    /// beside it carries the meaning, and the flow rows additionally print a word for
    /// their state.
    /// </remarks>
    [Fact]
    public void SeverityColourIsAlwaysAccompaniedByWords()
    {
        var text = File.ReadAllText(RepoFiles.MainWindowXaml);
        var document = Window();

        var coloured = document
            .Descendants()
            .Where(element => element.Attribute("Style")?.Value.Contains("SeverityStatusLineStyle", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(coloured);

        // Every one of them renders text: a bare coloured shape would carry the state in
        // colour alone.
        Assert.All(coloured, element => Assert.NotNull(element.Attribute("Text")));

        // And the flow rows spell the state out as well as colouring it.
        Assert.Contains("{Binding StateLabel}", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The measurement steps are shown, with the elapsed time rather than a percentage.
    /// </summary>
    [Fact]
    public void TheLatencyCardShowsItsStepsAndAnElapsedTimeRatherThanAPercentage()
    {
        var text = File.ReadAllText(RepoFiles.MainWindowXaml);

        Assert.Contains("{Binding LatencyFlow}", text, StringComparison.Ordinal);
        Assert.Contains("{Binding LatencyElapsed}", text, StringComparison.Ordinal);

        // The bar stays indeterminate: a run has no predictable total, so a determinate
        // value would be a number with nothing behind it.
        Assert.DoesNotContain("LatencyProgressPercent", text, StringComparison.Ordinal);
    }
}
