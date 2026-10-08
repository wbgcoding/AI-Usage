using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// One test compares the EN and DE key sets in both directions
/// (missing AND orphaned keys), a second scans every shipped .xaml/.cs file for visible text that
/// never went through <see cref="LocalizationService"/>.
/// </summary>
public class LocalizationParityTests
{
    // [CallerFilePath] resolves to this file's own absolute path at compile time - independent of
    // the test runner's working directory or output folder, unlike the ThemeFixtures files (which
    // are copied into the test output and read from AppContext.BaseDirectory instead). A full-tree
    // scanner genuinely needs the live source, not a copy that could go stale.
    private static readonly string SrcDir = FindSrcDir();

    private static string FindSrcDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        var repoRoot = Path.GetDirectoryName(testsDir)!;
        return Path.Combine(repoRoot, "src", "AiUsage");
    }

    [Fact]
    public void EveryRegisteredLanguageSharesExactlyTheSameKeysAsEnglish()
    {
        var concreteLanguages = LocalizationService.Languages.Where(l => l.Resources is not null).ToList();
        var english = concreteLanguages.Single(l => l.Code == "en");
        var englishKeys = LoadKeys(english.Resources!);
        Assert.NotEmpty(englishKeys); // a silently-empty resource set would make every Except() below trivially pass

        foreach (var language in concreteLanguages.Where(l => l.Code != "en"))
        {
            var keys = LoadKeys(language.Resources!);
            var missing = englishKeys.Except(keys).OrderBy(k => k).ToList();
            var orphaned = keys.Except(englishKeys).OrderBy(k => k).ToList();

            Assert.True(missing.Count == 0, $"Keys in Strings.resx missing from the '{language.Code}' resx: " + string.Join(", ", missing));
            Assert.True(orphaned.Count == 0, $"Keys in the '{language.Code}' resx with no EN counterpart: " + string.Join(", ", orphaned));
        }
    }

    [Fact]
    public void NoTooltipSaysWhenASettingTakesEffect()
    {
        var timing = new Regex(@"wirkt sofort|sofort beim|takes effect|immediately|applies at once|at once\.", RegexOptions.IgnoreCase);
        var offenders = new List<string>();
        foreach (var language in LocalizationService.Languages.Where(l => l.Resources is not null))
        {
            var set = language.Resources!.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
            foreach (var entry in set.Cast<DictionaryEntry>())
            {
                var key = (string)entry.Key!;
                if ((key.StartsWith("Tip.", StringComparison.Ordinal) || key.EndsWith(".Tip", StringComparison.Ordinal))
                    && entry.Value is string text && timing.IsMatch(text))
                    offenders.Add($"{language.Code}:{key}");
            }
        }

        Assert.True(offenders.Count == 0, "Tooltips naming when a setting applies: " + string.Join(", ", offenders));
    }

    private static HashSet<string> LoadKeys(ResourceManager manager)
    {
        var set = manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        return set.Cast<DictionaryEntry>().Select(e => (string)e.Key!).ToHashSet();
    }

    // Content="AI-Usage" (product name) and the About window's interim "AI" glyph (a later change
    // replaces it with real artwork) are the two deliberate, disclosed exceptions - everything else visible
    // must come from a resource key.
    private static readonly HashSet<string> AllowedXamlLiterals = ["AI-Usage", "AI"];

    private static readonly Regex XamlAttribute = new(
        @"(?<![A-Za-z])(Content|Text|Header|ToolTip|Title)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    private static readonly Regex XamlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void NoXamlFileContainsUnlocalizedVisibleText()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SrcDir, "*.xaml", SearchOption.AllDirectories))
        {
            var text = XamlComment.Replace(File.ReadAllText(file), "");
            foreach (Match match in XamlAttribute.Matches(text))
            {
                var value = match.Groups[2].Value;
                if (value.StartsWith('{')) // a Binding/StaticResource/x:Static markup extension
                    continue;
                if (!ContainsLetters(value) || AllowedXamlLiterals.Contains(value))
                    continue;
                offenders.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}=\"{value}\"");
            }
        }

        Assert.True(offenders.Count == 0, "Unlocalized XAML text found:\n" + string.Join("\n", offenders));
    }

    // Deliberately narrow: only the concrete sinks that actually reach a user (MessageBox, a
    // WinForms control's Text/constructor, a WPF Window's Title) rather than every quoted string in
    // the codebase - a blanket scan would flag doc comments and resource keys themselves.
    private static readonly Regex[] CodeSinks =
    [
        new(@"MessageBox\.Show\(\s*(?:this,\s*)?""([A-Za-zÄÖÜäöüß][^""]*\s[^""]*)""", RegexOptions.Compiled),
        new(@"new ToolStripMenuItem\(\s*""([A-Za-zÄÖÜäöüß][^""]*)""", RegexOptions.Compiled),
        new(@"\.Text\s*=\s*""([A-Za-zÄÖÜäöüß][^""]*\s[^""]*)""", RegexOptions.Compiled),
    ];

    [Fact]
    public void NoCodeFileContainsUnlocalizedVisibleText()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SrcDir, "*.cs", SearchOption.AllDirectories))
        {
            var code = StripComments(File.ReadAllText(file));
            foreach (var sink in CodeSinks)
            {
                foreach (Match match in sink.Matches(code))
                    offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0, "Unlocalized code text found:\n" + string.Join("\n", offenders));
    }

    // A hyphen, en dash (0x2013) or em dash (0x2014) used as a sentence connector, surrounded by
    // spaces on both sides. A hyphen inside a single word ("AI-Usage", "Five-hour") never matches,
    // since it has no surrounding spaces. Built from code points rather than the literal
    // characters so this file itself stays free of them.
    private static readonly Regex DashConnector = new(
        $" - | {(char)0x2013} | {(char)0x2014} ", RegexOptions.Compiled);

    [Fact]
    public void NoResxValueUsesADashAsASentenceConnector()
    {
        var resourcesDir = Path.Combine(SrcDir, "Resources");
        var offenders = new List<string>();

        foreach (var file in new[] { "Strings.resx", "Strings.de.resx" })
        {
            var doc = XDocument.Load(Path.Combine(resourcesDir, file));
            foreach (var data in doc.Root!.Elements("data"))
            {
                var key = (string?)data.Attribute("name") ?? "";
                var value = data.Element("value")?.Value ?? "";
                if (DashConnector.IsMatch(value))
                    offenders.Add($"{file}: {key}");
            }
        }

        Assert.True(offenders.Count == 0, "Dash used as a sentence connector:\n" + string.Join("\n", offenders));
    }

    private static bool ContainsLetters(string value) => value.Any(char.IsLetter);

    private static string StripComments(string code)
    {
        var withoutBlockComments = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var lines = withoutBlockComments.Split('\n').Select(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("//") ? "" : line;
        });
        return string.Join('\n', lines);
    }
}
