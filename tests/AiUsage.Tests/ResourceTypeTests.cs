using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace AiUsage.Tests;

/// <summary>
/// WPF converts a literal attribute value ("8" to a Thickness) but never converts a resource
/// value: a Double token behind Padding="{StaticResource Space.S}" compiles, ships, and then throws
/// a XamlParseException while the first window loads. Nothing else in this suite can see that - the
/// windows are not instantiated here (<see cref="ConfirmWindowTests"/>) - so every token use in the
/// shipped XAML is matched against the type Tokens.xaml declares for it.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class ResourceTypeTests
{
    /// <summary>
    /// The property types the shipped XAML assigns design tokens to. An unlisted property name
    /// fails on purpose: a new kind of token use has to be named here to stay covered.
    /// </summary>
    private static readonly Dictionary<string, Type> PropertyTypes = new(StringComparer.Ordinal)
    {
        ["Margin"] = typeof(Thickness),
        ["Padding"] = typeof(Thickness),
        ["BorderThickness"] = typeof(Thickness),
        ["CornerRadius"] = typeof(CornerRadius),
        ["FontSize"] = typeof(double),
        ["FontFamily"] = typeof(FontFamily),
        ["Width"] = typeof(double),
        ["Height"] = typeof(double),
        ["MinWidth"] = typeof(double),
        ["MinHeight"] = typeof(double),
        ["MaxWidth"] = typeof(double),
        ["MaxHeight"] = typeof(double),
        ["StrokeThickness"] = typeof(double),
    };

    private static readonly Regex ResourceReference =
        new(@"^\{(?:Static|Dynamic)Resource\s+([^}]+)\}$", RegexOptions.Compiled);

    private static ResourceDictionary Tokens()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ThemeFixtures", "Tokens.xaml");
        using var stream = File.OpenRead(path);
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static IEnumerable<string> AppXamlFiles()
    {
        // A worktree's own root has a ".git" FILE (pointing at the real repo's .git/worktrees/<name>),
        // not a ".git" directory - checking only Directory.Exists (as elsewhere in this test project)
        // walks straight past a worktree root and finds the main checkout's .git directory instead,
        // silently scanning the wrong copy of the source. Checking either keeps this correct in both.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not find the repository root (.git) above " + AppContext.BaseDirectory);

        var source = Path.Combine(dir.FullName, "src", "AiUsage");
        return Directory.EnumerateFiles(source, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_design_token_is_used_on_a_property_of_its_own_type()
    {
        var tokens = Tokens();
        var problems = new List<string>();

        foreach (var file in AppXamlFiles())
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);

            foreach (var element in document.Descendants())
            foreach (var (property, value) in TokenUses(element))
            {
                var match = ResourceReference.Match(value.Trim());
                if (!match.Success)
                    continue;

                var key = match.Groups[1].Value.Trim();
                if (!tokens.Contains(key))
                    continue;

                var line = ((IXmlLineInfo)element).LineNumber;
                if (!PropertyTypes.TryGetValue(property, out var expected))
                {
                    problems.Add($"{Path.GetFileName(file)}:{line} - token '{key}' is set on '{property}', "
                        + "which ResourceTypeTests.PropertyTypes does not list yet");
                    continue;
                }

                var actual = tokens[key]!.GetType();
                if (!expected.IsAssignableFrom(actual))
                    problems.Add($"{Path.GetFileName(file)}:{line} - '{property}' needs a {expected.Name}, "
                        + $"but token '{key}' is a {actual.Name}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Attribute assignments plus Style setters, whose target property is named in Property= and
    /// may carry its owner type ("TextBlock.Padding").
    /// </summary>
    private static IEnumerable<(string Property, string Value)> TokenUses(XElement element)
    {
        if (element.Name.LocalName == "Setter")
        {
            var property = element.Attribute("Property")?.Value;
            var value = element.Attribute("Value")?.Value;
            if (property is not null && value is not null)
            {
                var name = property[(property.LastIndexOf('.') + 1)..];
                yield return (name, value);
            }
            yield break;
        }

        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
                continue;

            var name = attribute.Name.LocalName;
            // Attached properties ("Grid.Row") carry their owner; tokens are never set on one.
            if (name.Contains('.', StringComparison.Ordinal))
                continue;

            yield return (name, attribute.Value);
        }
    }
}
