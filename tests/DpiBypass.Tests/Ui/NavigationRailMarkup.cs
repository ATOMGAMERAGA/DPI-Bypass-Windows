using System.Globalization;
using System.Xml.Linq;

namespace DpiBypass.Tests.Ui;

internal readonly record struct XamlThickness(double Left, double Top, double Right, double Bottom)
{
    public static readonly XamlThickness Zero = new(0, 0, 0, 0);

    public Edges Vertical => new(Top, Bottom);

    public bool HasNegativeEdge => Left < 0 || Top < 0 || Right < 0 || Bottom < 0;

    public static XamlThickness Parse(string? text) => TryParse(text, out var thickness)
        ? thickness
        : throw new FormatException($"'{text}' is not a WPF Thickness.");

    /// <summary>
    /// Returns false for a value the markup does not spell out, such as
    /// <c>{TemplateBinding Padding}</c>: there is no literal edge to judge there.
    /// </summary>
    public static bool TryParse(string? text, out XamlThickness thickness)
    {
        thickness = Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (text.TrimStart().StartsWith('{'))
        {
            return false;
        }

        var parts = new List<double>(4);
        foreach (var part in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            parts.Add(value);
        }

        switch (parts.Count)
        {
            case 1:
                thickness = new XamlThickness(parts[0], parts[0], parts[0], parts[0]);
                return true;
            case 2:
                thickness = new XamlThickness(parts[0], parts[1], parts[0], parts[1]);
                return true;
            case 4:
                thickness = new XamlThickness(parts[0], parts[1], parts[2], parts[3]);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>The navigation rail as it is actually written in <c>Theme/Shared.xaml</c>.</summary>
internal sealed class NavigationRailMarkup
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private NavigationRailMarkup(XElement controlStyle, XElement itemStyle)
    {
        ControlStyle = controlStyle;
        ItemStyle = itemStyle;

        ItemsHostElement = controlStyle.Descendants()
            .Single(element => string.Equals((string?)element.Attribute("IsItemsHost"), "True", StringComparison.OrdinalIgnoreCase));

        ItemTemplate = itemStyle.Descendants(Presentation + "ControlTemplate").Single();
        Chrome = NamedPart(ItemTemplate, "Chrome");
        Pill = NamedPart(ItemTemplate, "Pill");
    }

    public XElement ControlStyle { get; }

    public XElement ItemStyle { get; }

    public XElement ItemsHostElement { get; }

    public XElement ItemTemplate { get; }

    /// <summary>The coloured background that carries the selected state.</summary>
    public XElement Chrome { get; }

    /// <summary>The vertical accent bar shown beside the selected item.</summary>
    public XElement Pill { get; }

    public RailItemsHost ItemsHost => ItemsHostElement.Name.LocalName switch
    {
        "TabPanel" => RailItemsHost.TabPanel,
        "StackPanel" => RailItemsHost.StackPanel,
        var other => throw new Xunit.Sdk.XunitException($"Unmodelled navigation items host '{other}'."),
    };

    public XamlThickness ItemMargin => XamlThickness.Parse(SetterValue(ItemStyle, "Margin"));

    public XamlThickness ChromeMargin => XamlThickness.Parse((string?)Chrome.Attribute("Margin"));

    public XamlThickness ChromePadding => XamlThickness.Parse((string?)Chrome.Attribute("Padding"));

    public double ChromeMinHeight => double.Parse(
        (string?)Chrome.Attribute("MinHeight") ?? "0",
        CultureInfo.InvariantCulture);

    public RailItemMetrics Metrics => new()
    {
        ItemMargin = ItemMargin.Vertical,
        ChromeMargin = ChromeMargin.Vertical,
        ChromeHeight = ChromeMinHeight,
    };

    /// <summary>Every trigger in the item template, keyed by the property it watches.</summary>
    public IEnumerable<XElement> TriggersOn(string property) => ItemTemplate
        .Descendants(Presentation + "Trigger")
        .Where(trigger => (string?)trigger.Attribute("Property") == property);

    /// <summary>Template-part/property pairs one trigger sets.</summary>
    public static IReadOnlyList<(string Part, string Property)> SettersOf(XElement trigger) =>
    [
        .. trigger.Elements(Presentation + "Setter")
            .Select(setter => (
                Part: (string?)setter.Attribute("TargetName") ?? "(self)",
                Property: (string?)setter.Attribute("Property") ?? string.Empty)),
    ];

    public static NavigationRailMarkup Load()
    {
        var document = XDocument.Load(RepoFiles.SharedThemeXaml);

        return new NavigationRailMarkup(
            StyleWithKey(document, "NavTabControlStyle"),
            StyleWithKey(document, "NavTabItemStyle"));
    }

    private static XElement StyleWithKey(XDocument document, string key) => document
        .Descendants(Presentation + "Style")
        .Single(style => (string?)style.Attribute(Xaml + "Key") == key);

    private static XElement NamedPart(XElement template, string name) => template
        .Descendants()
        .Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string? SetterValue(XElement style, string property) => style
        .Elements(Presentation + "Setter")
        .Where(setter => (string?)setter.Attribute("Property") == property)
        .Select(setter => (string?)setter.Attribute("Value"))
        .FirstOrDefault();
}
