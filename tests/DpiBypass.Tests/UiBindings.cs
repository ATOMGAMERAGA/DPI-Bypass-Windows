using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DpiBypass.Tests;

/// <summary>
/// Reads the binding paths out of a XAML file.
/// </summary>
/// <remarks>
/// Lets a test ask "is this control still reachable from the card" without asserting
/// that a particular sentence or attribute ordering survives, which is what made the
/// previous markup tests fail on every wording change while missing a dropped control.
/// Whether each path resolves to a declared member is <see cref="ViewModelBindingTests"/>'s
/// job, and is not repeated here.
/// </remarks>
internal static class UiBindings
{
    private static readonly Regex BindingPath = new(
        @"\{Binding\s+(?:Path=)?(?<path>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)",
        RegexOptions.Compiled);

    /// <summary>
    /// Every property path bound anywhere in one XAML file, and every root they hang off.
    /// </summary>
    /// <remarks>
    /// Both forms are in the set, so a test can assert either "this view model member is
    /// bound somewhere" or "this exact sub-path is bound". The ping card needs the second:
    /// its figures all come off one composed object, and asserting only the root would not
    /// notice the after value disappearing from the card.
    /// </remarks>
    public static IReadOnlySet<string> PathsIn(string xamlPath)
    {
        var markup = XDocument.Load(xamlPath).ToString(SaveOptions.DisableFormatting);

        return BindingPath.Matches(markup)
            .Select(match => match.Groups["path"].Value)
            .SelectMany(path => new[] { path, path.Split('.')[0] })

            // "Mode", "Source" and friends are binding keywords, not paths.
            .Where(path => path is not ("Mode" or "Path" or "Source" or "RelativeSource"
                or "ElementName" or "Converter" or "StringFormat" or "UpdateSourceTrigger"))
            .ToHashSet(StringComparer.Ordinal);
    }
}
