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

    // Values that read the same in that language as in English: product names, and words the language
    // borrowed unchanged.
    private static readonly HashSet<string> SameAsEnglishOnPurpose =
    [
        "fr:Settings.Section.Notifications", "fr:Settings.Via.ClaudeCode", "nl:Settings.Via.ClaudeCode",
        "it:Stats.Project.Tip.InputOutputCache", "pl:Settings.Section.System",
    ];

    private static readonly Regex Placeholder = new(@"\{\d+[^}]*\}", RegexOptions.Compiled);

    [Fact]
    public void EveryTranslationIsFilledAndLongTextsAreActuallyTranslated()
    {
        var english = Values(LocalizationService.Languages.Single(l => l.Code == "en").Resources!);
        var offenders = new List<string>();
        foreach (var language in LocalizationService.Languages.Where(l => l.Resources is not null && l.Code is not "en"))
        {
            foreach (var (key, value) in Values(language.Resources!))
            {
                // A text that is empty in English on purpose (the closing word of a sentence that only
                // some languages have) may be empty in the others too.
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (!english.TryGetValue(key, out var englishValue) || !string.IsNullOrWhiteSpace(englishValue))
                        offenders.Add($"{language.Code}:{key} is empty");
                }
                // Formats like "{0} min {1} s" stay as they are; only real text counts.
                else if (!key.StartsWith("Language.", StringComparison.Ordinal)
                    && english.TryGetValue(key, out var source) && source == value
                    && Placeholder.Replace(value, "").Count(char.IsLetter) >= 12
                    && !SameAsEnglishOnPurpose.Contains($"{language.Code}:{key}"))
                    offenders.Add($"{language.Code}:{key} is still English");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void ATextTranslatedUnderOneKeyIsNotLeftEnglishUnderAnother()
    {
        // The same English wording is often reused under several keys ("{0} min" in a tooltip and in
        // a list). When a language translates it under one key, the key still in English is a miss
        // that the length threshold above lets through for short texts.
        var english = Values(LocalizationService.Languages.Single(l => l.Code == "en").Resources!);
        var offenders = new List<string>();
        foreach (var language in LocalizationService.Languages.Where(l => l.Resources is not null && l.Code is not "en"))
        {
            var values = Values(language.Resources!);
            var groups = english
                .Where(e => !e.Key.StartsWith("Language.", StringComparison.Ordinal) && ContainsLetters(Placeholder.Replace(e.Value, "")))
                .GroupBy(e => e.Value, e => e.Key);
            foreach (var group in groups)
            {
                var keys = group.Where(values.ContainsKey).ToList();
                var translated = keys.Any(k => values[k] != group.Key);
                if (!translated)
                    continue;
                foreach (var key in keys.Where(k => values[k] == group.Key && !SameAsEnglishOnPurpose.Contains($"{language.Code}:{k}")))
                    offenders.Add($"{language.Code}:{key} ('{group.Key}')");
            }
        }

        Assert.True(offenders.Count == 0, "Left English although translated elsewhere:\n" + string.Join("\n", offenders));
    }

    private static Dictionary<string, string> Values(ResourceManager manager) =>
        manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key!, e => e.Value as string ?? "");

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
        @"(?<![A-Za-z])(Content|Text|Header|ToolTip|Title|AutomationProperties\.Name|AutomationProperties\.HelpText)\s*=\s*""([^""]*)""",
        RegexOptions.Compiled);

    // Text between two tags: <TextBlock>Hello</TextBlock>.
    private static readonly Regex XamlElementContent = new(@">([^<>]*)<", RegexOptions.Compiled);

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
        // No comma in the value: a CSV header row ("section,label,day") is data, not shown text.
        new(@"\b(?:ToolTip|Header|Content|Title)\s*=\s*""([A-Za-zÄÖÜäöüß][^"",]*)""", RegexOptions.Compiled),
        new(@"AutomationProperties\.SetName\([^,]+,\s*""([^""]+)""", RegexOptions.Compiled),
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

    [Theory]
    [InlineData("<Button AutomationProperties.Name=\"Close window\" />")]
    [InlineData("<Button AutomationProperties.HelpText=\"Closes it\" />")]
    [InlineData("<TextBlock>Hello there</TextBlock>")]
    public void TheXamlScanSeesAccessibleNamesAndElementText(string xaml)
    {
        Assert.True(XamlAttribute.IsMatch(xaml) || XamlElementContent.Matches(xaml).Any(m => ContainsLetters(m.Groups[1].Value)));
    }

    [Theory]
    [InlineData("button.ToolTip = \"Close it\";")]
    [InlineData("new MenuItem { Header = \"Open\" }")]
    [InlineData("AutomationProperties.SetName(button, \"Close\");")]
    public void TheCodeScanSeesToolTipHeaderAndAutomationNames(string code)
    {
        Assert.Contains(CodeSinks, sink => sink.IsMatch(code));
    }

    // Where a text is read by key: the indexer (loc["Key"], LocalizationService.Instance["Key"]),
    // Format("Key", ...) and a XAML binding path ({Binding ..., Path=[Key]}).
    private static readonly Regex[] CodeKeyReads =
    [
        new(@"\b(?:loc|Loc|_loc|localization|Instance)\[\s*""([^""]+)""\s*\]", RegexOptions.Compiled),
        new(@"\.Format\(\s*""([A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)""", RegexOptions.Compiled),
    ];

    private static readonly Regex XamlKeyRead = new(@"Path=\[([^\]]+)\]", RegexOptions.Compiled);

    [Fact]
    public void EveryKeyReadInCodeAndXamlExists()
    {
        // The indexer answers "" for an unknown key, so a typo would show an empty label and no test would notice.
        var keys = LoadKeys(LocalizationService.Languages.Single(l => l.Code == "en").Resources!);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SrcDir, "*.cs", SearchOption.AllDirectories))
        {
            var code = StripComments(File.ReadAllText(file));
            foreach (var read in CodeKeyReads)
                foreach (Match match in read.Matches(code))
                    if (!keys.Contains(match.Groups[1].Value))
                        offenders.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
        }
        foreach (var file in Directory.EnumerateFiles(SrcDir, "*.xaml", SearchOption.AllDirectories))
        {
            var text = XamlComment.Replace(File.ReadAllText(file), "");
            foreach (Match match in XamlKeyRead.Matches(text))
                if (!keys.Contains(match.Groups[1].Value))
                    offenders.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
        }

        Assert.True(offenders.Count == 0, "Keys used but missing from Strings.resx:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NoUiCodeJoinsShownTextWithALiteralComma()
    {
        // A list shown to the user goes through TextCompose.List: the separator is a resource (CJK uses its own).
        var offenders = new List<string>();
        var uiFiles = new[] { "Views", "ViewModels", "Stats" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(SrcDir, dir), "*.cs", SearchOption.AllDirectories))
            .Where(file => !Regex.IsMatch(Path.GetFileName(file), "Store|Parser"));
        foreach (var file in uiFiles)
        {
            var code = StripComments(File.ReadAllText(file));
            foreach (Match match in Regex.Matches(code, @"[Ss]tring\.Join\(\s*"", """))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0, "A literal-comma Join in UI code (use TextCompose.List): " + string.Join(", ", offenders));
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

        var files = LocalizationService.Languages.Where(l => l.Resources is not null)
            .Select(l => l.Code == "en" ? "Strings.resx" : $"Strings.{l.Code}.resx");
        foreach (var file in files)
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
