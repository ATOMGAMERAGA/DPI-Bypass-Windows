using System.Xml.Linq;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The window's scrolling behaviour, checked without running WPF.
/// </summary>
/// <remarks>
/// "Scrolling is very buggy" is one report with several causes, and every one of them is
/// invisible to a compiler and to the window self-test: a page that stops responding to
/// the wheel over a list, a combo box that answers the wheel by changing a setting, a
/// navigation rail that clips its own items, and a page entrance animation that threw on
/// every load. They are pinned here because nothing else can catch them.
/// </remarks>
public sealed class ScrollingTests
{
    private static XDocument Window() => XDocument.Load(RepoFiles.MainWindowXaml);

    private static XDocument Theme() => XDocument.Load(RepoFiles.SharedThemeXaml);

    private const string Infrastructure = "clr-namespace:DpiBypass.App.Infrastructure";

    /// <summary>
    /// Every list inside a page hands the wheel back once it cannot use it.
    /// </summary>
    /// <remarks>
    /// A WPF list handles the wheel unconditionally - it scrolls if it can and marks the
    /// event handled either way - so a page with a list on it stops scrolling the moment
    /// the pointer crosses that list, with nothing on screen to explain why.
    /// </remarks>
    [Fact]
    public void ListsInsideAPageDoNotSwallowTheWheel()
    {
        var theme = Theme();
        var ns = theme.Root!.Name.Namespace;

        var listStyles = theme.Descendants(ns + "Style")
            .Where(style => (string?)style.Attribute("TargetType") == "ListBox")
            .ToArray();

        Assert.NotEmpty(listStyles);

        // Both list styles the pages use, directly or through BasedOn.
        var bubbling = listStyles
            .Where(style => style.Elements(ns + "Setter")
                .Any(setter => ((string?)setter.Attribute("Property"))?.EndsWith(
                    "NestedScrolling.BubbleWheel", StringComparison.Ordinal) == true))
            .Select(style => (string?)style.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Key"))
            .ToArray();

        Assert.Contains("PlainListStyle", bubbling);
        Assert.Contains("ChoiceListStyle", bubbling);
    }

    /// <summary>
    /// A combo box must never answer a scroll gesture by changing what it holds.
    /// </summary>
    /// <remarks>
    /// These pickers choose the operator profile, the bypass strategy, the DNS mode and
    /// the latency target. Scrolling past one of them silently reconfigured the engine,
    /// and the only evidence was the setting being different afterwards.
    /// </remarks>
    [Fact]
    public void NoComboBoxChangesItsValueOnAScrollGesture()
    {
        var window = Window();
        var ns = window.Root!.Name.Namespace;
        var infra = window.Root.GetNamespaceOfPrefix("infra");

        Assert.Equal(Infrastructure, infra?.NamespaceName);

        var comboBoxes = window.Descendants(ns + "ComboBox").ToArray();

        Assert.NotEmpty(comboBoxes);
        Assert.All(
            comboBoxes,
            box => Assert.Equal("True", (string?)box.Attribute(infra! + "NestedScrolling.IgnoreWheel")));
    }

    /// <summary>Each page is one scroll surface, and it is the one that moves.</summary>
    [Fact]
    public void EveryPageScrollsAsAWhole()
    {
        var window = Window();
        var ns = window.Root!.Name.Namespace;

        var pages = window.Descendants(ns + "TabItem")
            .Select(tab => tab.Elements().FirstOrDefault(child => child.Name != ns + "TabItem.Header"))
            .Select(content => content?.Name.LocalName == "DeferredTabContent.Template"
                ? content.Element(ns + "DataTemplate")?.Elements().Single()
                : content)
            .Where(content => content is not null)
            .ToArray();

        Assert.NotEmpty(pages);

        foreach (var page in pages)
        {
            // A page is either a scroll surface itself or a grid that manages its own
            // rows - the domains page pins its search box above a list that scrolls.
            Assert.True(
                page!.Name == ns + "ScrollViewer" || page.Name == ns + "Grid",
                $"Unexpected page root <{page.Name.LocalName}>.");
        }
    }

    /// <summary>
    /// The rail scrolls, so a short window cannot make a page unreachable.
    /// </summary>
    [Fact]
    public void TheNavigationRailCanBeScrolledWhenItDoesNotFit()
    {
        var theme = Theme();
        var ns = theme.Root!.Name.Namespace;

        var host = Assert.Single(
            theme.Descendants(ns + "StackPanel"),
            panel => (string?)panel.Attribute("IsItemsHost") == "True");

        var viewer = host.Ancestors(ns + "ScrollViewer").FirstOrDefault();

        Assert.NotNull(viewer);
        Assert.Equal("Auto", (string?)viewer!.Attribute("VerticalScrollBarVisibility"));
    }

    /// <summary>
    /// The page entrance animates only the element's own properties.
    /// </summary>
    /// <remarks>
    /// It used to animate a <c>TranslateTransform</c> declared in a Style setter. WPF
    /// freezes a Freezable in a setter and shares that one instance across every element
    /// the style touches, so the animation threw "cannot animate on an immutable object
    /// instance" every time a page loaded. The dispatcher's handler swallowed it, which
    /// meant a log full of interface errors for a flourish that never once ran.
    /// </remarks>
    [Fact]
    public void ThePageEntranceDoesNotAnimateASharedTransform()
    {
        var theme = Theme();
        var ns = theme.Root!.Name.Namespace;
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        var surfaces = theme.Descendants(ns + "Style")
            .Where(style => ((string?)style.Attribute(x + "Key"))?.StartsWith(
                "PageSurface", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(2, surfaces.Length);

        foreach (var setter in surfaces.SelectMany(style => style.Elements(ns + "Setter")))
        {
            Assert.NotEqual("RenderTransform", (string?)setter.Attribute("Property"));
        }

        foreach (var animation in surfaces.SelectMany(style => style.Descendants(ns + "DoubleAnimation")))
        {
            Assert.Equal("Opacity", (string?)animation.Attribute(ns + "Storyboard.TargetProperty")
                ?? (string?)animation.Attribute("Storyboard.TargetProperty"));
        }
    }

    /// <summary>
    /// Turning system animations off swaps the style rather than editing a sealed one.
    /// </summary>
    /// <remarks>
    /// The window used to call <c>Triggers.Clear()</c> on the shared style after its own
    /// XAML had already resolved it. A Style is sealed the first time it is applied, so
    /// that call could only ever throw - and it was reached on exactly the machines that
    /// had asked for no animation.
    /// </remarks>
    [Fact]
    public void TheReducedMotionPathReplacesTheStyleInsteadOfMutatingIt()
    {
        var window = File.ReadAllText(RepoFiles.Find("src", "DpiBypass.App", "MainWindow.xaml.cs"));
        var app = File.ReadAllText(RepoFiles.Find("src", "DpiBypass.App", "App.xaml.cs"));

        Assert.DoesNotContain("Triggers.Clear()", window, StringComparison.Ordinal);
        Assert.Contains("PageSurfaceStaticStyle", app, StringComparison.Ordinal);
        Assert.Contains("ClientAreaAnimation", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// The primary button says what protection is doing, not only whether it is on.
    /// </summary>
    /// <remarks>
    /// A start takes seconds, and for all of them the button used to read "Korumayı
    /// başlat" and do nothing when it was pressed, because the service refuses a second
    /// start. The one control on every page looked broken exactly while the app was
    /// working.
    /// </remarks>
    [Fact]
    public void ThePrimaryButtonReflectsAStartThatIsStillInProgress()
    {
        var viewModel = File.ReadAllText(RepoFiles.MainViewModel);

        Assert.Contains("ProtectionState.Starting => \"Başlatılıyor…\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("ProtectionState.Stopping => \"Durduruluyor…\"", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "new AsyncRelayCommand(ToggleAsync, () => !_isBusy && !IsTransitioning)",
            viewModel,
            StringComparison.Ordinal);
    }
}
