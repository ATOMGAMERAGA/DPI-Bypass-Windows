using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Every <c>{Binding}</c> in the window has to name something the view model actually has.
/// </summary>
/// <remarks>
/// WPF does not fail a binding it cannot resolve: it writes a trace line nobody reads and
/// leaves the control empty. So renaming a view-model member and missing one of its
/// bindings produces a blank panel or a dead button on a build that compiles, passes and
/// renders - which is exactly the failure the window self-test cannot catch either. The
/// view model cannot be loaded for reflection here (it drags in WPF, which does not load
/// off Windows), so the names are read out of the source instead.
/// </remarks>
public sealed partial class ViewModelBindingTests
{
    [Fact]
    public void EveryBindingInTheWindowNamesAViewModelMember()
    {
        var declared = DeclaredMembers();
        var unresolved = BindingRoots(RepoFiles.MainWindowXaml)
            .Where(path => !declared.Contains(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unresolved);
    }

    [Fact]
    public void EveryBindingInTheThemeNamesAViewModelMember()
    {
        var declared = DeclaredMembers();
        var unresolved = BindingRoots(RepoFiles.SharedThemeXaml)
            .Where(path => !declared.Contains(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unresolved);
    }

    /// <summary>Proves the scan has teeth rather than matching everything.</summary>
    [Fact]
    public void AMisspeltBindingWouldBeCaught()
        => Assert.DoesNotContain("HotspotStatusLineTypo", DeclaredMembers());

    [Fact]
    public void EveryCommandBindingNamesACommandMember()
    {
        var declared = DeclaredMembers();
        var commands = BindingRoots(RepoFiles.MainWindowXaml)
            .Where(path => path.EndsWith("Command", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.Contains(command, declared));
    }

    /// <summary>
    /// The binding paths a data context has to satisfy, with the ones that name their own
    /// source (a template parent, an ancestor, an element) left out.
    /// </summary>
    private static IEnumerable<string> BindingRoots(string xamlPath)
    {
        var text = File.ReadAllText(xamlPath);

        foreach (Match match in BindingExpression().Matches(text))
        {
            var body = match.Groups[1].Value;

            // Bindings that carry their own source are resolved against that, not the
            // view model, so the property may legitimately live anywhere.
            if (body.Contains("RelativeSource", StringComparison.Ordinal)
                || body.Contains("ElementName", StringComparison.Ordinal)
                || body.Contains("Source=", StringComparison.Ordinal))
            {
                continue;
            }

            var path = body.Split(',')[0].Trim();
            if (path.StartsWith("Path=", StringComparison.Ordinal))
            {
                path = path["Path=".Length..].Trim();
            }

            // An empty path binds the data context itself; a dotted path only needs its
            // first segment to exist here.
            var root = path.Split('.')[0].Trim();
            if (root.Length > 0)
            {
                yield return root;
            }
        }

        // DisplayMemberPath names a property on the bound item type the same way.
        var document = XDocument.Load(xamlPath);
        foreach (var value in document.Descendants().Attributes("DisplayMemberPath").Select(attribute => attribute.Value))
        {
            yield return value.Split('.')[0].Trim();
        }
    }

    /// <summary>
    /// Public members declared in the view model file: the view model itself plus the
    /// small records it exposes as list items.
    /// </summary>
    private static HashSet<string> DeclaredMembers()
    {
        var text = File.ReadAllText(RepoFiles.Find("src", "DpiBypass.App", "ViewModels", "MainViewModel.cs"));
        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in PublicMember().Matches(text))
        {
            members.Add(match.Groups[1].Value);
        }

        // Positional record parameters are public properties too.
        foreach (Match match in PositionalRecord().Matches(text))
        {
            foreach (var parameter in match.Groups[1].Value.Split(','))
            {
                var name = parameter.Trim().Split([' ', '='], StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
                if (name is { Length: > 0 })
                {
                    members.Add(name);
                }
            }
        }

        Assert.NotEmpty(members);
        return members;
    }

    /// <summary>Property and command declarations: <c>public SomeType Name { ... }</c>.</summary>
    [GeneratedRegex(@"public\s+(?:static\s+)?[\w<>?\[\],\s\.]+?\s+(\w+)\s*(?:\{|=>)", RegexOptions.Compiled)]
    private static partial Regex PublicMember();

    [GeneratedRegex(@"public\s+sealed\s+record\s+\w+\s*\(([^)]*)\)", RegexOptions.Compiled)]
    private static partial Regex PositionalRecord();

    [GeneratedRegex(@"\{Binding\s+([^}]*)\}", RegexOptions.Compiled)]
    private static partial Regex BindingExpression();
}
