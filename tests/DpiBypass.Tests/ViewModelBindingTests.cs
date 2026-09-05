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
        // Anything inside an item template belongs to the bound item, not to the view
        // model, and is checked against the item's own type by the test below.
        var text = WithoutItemTemplates(File.ReadAllText(xamlPath));

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
    /// The markup with item templates removed; deferred pages still use the view model.
    /// </summary>
    /// <remarks>
    /// A binding inside a template resolves against the item, so checking it against the
    /// view model would either fail on a perfectly good template or - worse - pass
    /// because the item's property happens to share a name with one of the view model's,
    /// which is how "Title" and "Detail" slipped through. They are checked against the
    /// item type instead.
    /// </remarks>
    private static string WithoutItemTemplates(string text)
    {
        var document = XDocument.Parse(text);
        document.Descendants()
            .Where(element => element.Name.LocalName.EndsWith(".ItemTemplate", StringComparison.Ordinal))
            .Remove();
        return document.ToString();
    }

    /// <summary>
    /// Every binding inside an item template names a member of the item type it is bound to.
    /// </summary>
    /// <remarks>
    /// The item type is worked out from the collection the template's own items control is
    /// bound to, so a template that names a property the item does not have is caught -
    /// including one whose name happens to exist on the view model.
    /// </remarks>
    [Fact]
    public void EveryBindingInsideAnItemTemplateNamesAMemberOfTheBoundItemType()
    {
        var document = XDocument.Load(RepoFiles.MainWindowXaml);
        var checkedTemplates = 0;
        var unresolved = new List<string>();

        foreach (var template in document.Descendants().Where(element => element.Name.LocalName == "DataTemplate"))
        {
            // <ListBox ItemsSource="{Binding X}"><ListBox.ItemTemplate><DataTemplate>…
            var holder = template.Parent;
            var control = holder?.Name.LocalName.EndsWith(".ItemTemplate", StringComparison.Ordinal) == true
                ? holder.Parent
                : holder;

            if (control?.Attribute("ItemsSource")?.Value is not { } itemsSource
                || !itemsSource.Contains("Binding", StringComparison.Ordinal))
            {
                continue;
            }

            var collection = itemsSource
                .Trim('{', '}')
                .Replace("Binding", string.Empty, StringComparison.Ordinal)
                .Split(',')[0]
                .Replace("Path=", string.Empty, StringComparison.Ordinal)
                .Trim();

            if (ItemTypeOf(collection) is not { } itemType || MembersOf(itemType) is not { Count: > 0 } members)
            {
                continue;
            }

            checkedTemplates++;

            foreach (Match match in BindingExpression().Matches(template.ToString()))
            {
                var body = match.Groups[1].Value;
                if (body.Contains("RelativeSource", StringComparison.Ordinal)
                    || body.Contains("ElementName", StringComparison.Ordinal)
                    || body.Contains("Source=", StringComparison.Ordinal))
                {
                    continue;
                }

                var root = body.Split(',')[0].Trim();
                if (root.StartsWith("Path=", StringComparison.Ordinal))
                {
                    root = root["Path=".Length..].Trim();
                }

                root = root.Split('.')[0].Trim();
                if (root.Length > 0 && !members.Contains(root))
                {
                    unresolved.Add($"{collection} ({itemType}): {root}");
                }
            }
        }

        Assert.True(checkedTemplates > 0, "no item template was resolved, so this test proves nothing");
        Assert.Empty(unresolved);
    }

    /// <summary>Proves the item-template scan has teeth rather than matching everything.</summary>
    [Fact]
    public void AMisspeltBindingInsideAnItemTemplateWouldBeCaught()
        => Assert.DoesNotContain("OrdinalTypo", MembersOf("LatencyFlowStep"));

    /// <summary>The element type of an <c>ObservableCollection&lt;T&gt;</c> the view model exposes.</summary>
    private static string? ItemTypeOf(string collectionName)
    {
        var text = File.ReadAllText(RepoFiles.MainViewModel);
        var match = Regex.Match(
            text,
            @"public\s+(?:ObservableCollection|IReadOnlyList|List)<([\w\.]+)>\s+" + Regex.Escape(collectionName) + @"\b");

        return match.Success ? match.Groups[1].Value.Split('.')[^1] : null;
    }

    /// <summary>
    /// The public members of one type, wherever in the two projects it is declared.
    /// </summary>
    private static HashSet<string> MembersOf(string typeName)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);

            // Positional records: the parameters are public properties.
            foreach (Match match in Regex.Matches(
                text,
                @"record\s+(?:struct\s+)?" + Regex.Escape(typeName) + @"\s*\(([^)]*)\)"))
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

            // Members declared in the body, which for a positional record is where the
            // computed properties like StateLabel and Severity live.
            var declaration = Regex.Match(
                text,
                @"(?:record|class)\s+(?:struct\s+)?" + Regex.Escape(typeName) + @"\b[^{;]*\{");

            if (!declaration.Success)
            {
                continue;
            }

            var body = Body(text, declaration.Index + declaration.Length - 1);
            foreach (Match match in PublicMember().Matches(body))
            {
                members.Add(match.Groups[1].Value);
            }
        }

        return members;
    }

    /// <summary>The text between a type's opening brace and its matching close.</summary>
    private static string Body(string text, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return text[openBrace..i];
            }
        }

        return text[openBrace..];
    }

    private static IEnumerable<string> SourceFiles()
    {
        yield return RepoFiles.MainViewModel;

        foreach (var file in Directory.EnumerateFiles(RepoFiles.CoreProjectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                yield return file;
            }
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
