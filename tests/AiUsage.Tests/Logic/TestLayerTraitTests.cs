using System.Reflection;
using System.Text.RegularExpressions;

namespace AiUsage.Tests;

/// <summary>Keeps the three test groups (Logic, UI, Integration) complete: every test class carries
/// exactly one <c>[Trait("Layer", ...)]</c> and sits in the folder of that name, so
/// <c>dotnet test --filter Layer=UI</c> and the folder pattern in the project notes always agree
/// and the groups add up to the whole suite.</summary>
[Trait("Layer", "Logic")]
public class TestLayerTraitTests
{
    private static readonly string[] Layers = ["Logic", "UI", "Integration"];

    private static readonly Regex TraitLine = new(@"\[Trait\(""Layer"",\s*""(?<value>[^""]*)""\)\]", RegexOptions.Compiled);

    /// <summary>Test classes xunit would discover (public, concrete, with at least one test method)
    /// that lack a single valid layer trait.</summary>
    private static List<string> Offenders(IEnumerable<Type> types, bool publicOnly = true)
    {
        var offenders = new List<string>();
        foreach (var type in types)
        {
            if (!type.IsClass || type.IsAbstract || publicOnly && !(type.IsPublic || type.IsNestedPublic))
                continue;
            var hasTests = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(method => method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0);
            if (!hasTests)
                continue;

            var values = type.GetCustomAttributesData()
                .Where(data => data.AttributeType == typeof(TraitAttribute)
                               && data.ConstructorArguments.Count == 2
                               && data.ConstructorArguments[0].Value as string == "Layer")
                .Select(data => data.ConstructorArguments[1].Value as string)
                .ToList();
            if (values.Count != 1 || !Layers.Contains(values[0]))
                offenders.Add(type.FullName ?? type.Name);
        }
        return offenders;
    }

    [Fact]
    public void Every_test_class_carries_exactly_one_valid_layer_trait()
    {
        var offenders = Offenders(typeof(TestLayerTraitTests).Assembly.GetTypes());

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_check_flags_a_class_without_a_layer_trait_or_with_an_unknown_one()
    {
        var offenders = Offenders([typeof(PlantedUntagged), typeof(PlantedUnknownLayer), typeof(PlantedTagged), typeof(PlantedTwice)], publicOnly: false);

        Assert.Equal(
            [
                "AiUsage.Tests.TestLayerTraitTests+PlantedUntagged",
                "AiUsage.Tests.TestLayerTraitTests+PlantedUnknownLayer",
                "AiUsage.Tests.TestLayerTraitTests+PlantedTwice",
            ],
            offenders);
    }

    [Fact]
    public void Every_test_file_sits_in_the_folder_named_by_its_layer_trait()
    {
        var root = Path.Combine(RepoRoot.Find(), "tests", "AiUsage.Tests");
        var misplaced = new List<string>();
        foreach (var layer in Layers)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, layer), "*.cs", SearchOption.AllDirectories))
            {
                // This file plants nested classes with wrong tags on purpose.
                if (Path.GetFileName(file) == nameof(TestLayerTraitTests) + ".cs")
                    continue;

                foreach (Match match in TraitLine.Matches(File.ReadAllText(file)))
                {
                    if (match.Groups["value"].Value != layer)
                        misplaced.Add($"{Path.GetRelativePath(root, file)}: {match.Groups["value"].Value}");
                }
            }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
        {
            if (TraitLine.IsMatch(File.ReadAllText(file)))
                misplaced.Add($"{Path.GetFileName(file)}: tagged but outside a layer folder");
        }

        Assert.Empty(misplaced);
    }

    // Never discovered by xunit (private), only handed to the check above.
#pragma warning disable xUnit1000
    private sealed class PlantedUntagged
    {
        [Fact]
        public void Case() { }
    }

    [Trait("Layer", "Bogus")]
    private sealed class PlantedUnknownLayer
    {
        [Fact]
        public void Case() { }
    }

    [Trait("Layer", "UI")]
    private sealed class PlantedTagged
    {
        [Fact]
        public void Case() { }
    }

    [Trait("Layer", "UI")]
    [Trait("Layer", "Logic")]
    private sealed class PlantedTwice
    {
        [Fact]
        public void Case() { }
    }
#pragma warning restore xUnit1000
}
