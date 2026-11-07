using System.Text.RegularExpressions;

namespace AiUsage.Tests;

/// <summary>The app language is one global setting, so a test class that switches it must run in
/// <see cref="SharedStateTestsCollection"/>; outside it, it changes the language under another class's
/// running test.</summary>
public class SharedLanguageCollectionTests
{
    private static readonly Regex ClassHeader = new(
        @"(?<attributes>(?:^[ \t]*\[[^\]\r\n]*\][ \t]*\r?\n)*)^[ \t]*(?:public|internal)\s+(?:(?:sealed|static|partial|abstract)\s+)*class\s+(?<name>\w+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>The classes in <paramref name="source"/> whose body calls <c>SetLanguage(</c> while their
    /// attributes name no collection.</summary>
    internal static List<string> UnguardedLanguageSwitchers(string source)
    {
        var headers = ClassHeader.Matches(source);
        var found = new List<string>();
        for (var i = 0; i < headers.Count; i++)
        {
            var start = headers[i].Index + headers[i].Length;
            var end = i + 1 < headers.Count ? headers[i + 1].Index : source.Length;
            var body = source[start..end];
            if (body.Contains("SetLanguage(", StringComparison.Ordinal)
                && !headers[i].Groups["attributes"].Value.Contains("Collection(", StringComparison.Ordinal))
                found.Add(headers[i].Groups["name"].Value);
        }
        return found;
    }

    [Fact]
    public void Every_class_that_switches_the_language_runs_in_the_shared_collection()
    {
        var directory = Path.Combine(RepoRoot.Find(), "tests", "AiUsage.Tests");
        var offenders = Directory.GetFiles(directory, "*.cs")
            .Where(path => Path.GetFileName(path) != nameof(SharedLanguageCollectionTests) + ".cs")
            .SelectMany(path => UnguardedLanguageSwitchers(File.ReadAllText(path)).Select(name => $"{Path.GetFileName(path)}: {name}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_check_finds_a_class_without_the_collection()
    {
        const string source = """
            [Collection(SharedStateTestsCollection.Name)]
            public class Guarded
            {
                void A() => LocalizationService.Instance.SetLanguage("en");
            }

            public class Unguarded
            {
                void B() => LocalizationService.Instance.SetLanguage("fr");
            }

            public class Unrelated
            {
                void C() { }
            }
            """;

        Assert.Equal(["Unguarded"], UnguardedLanguageSwitchers(source));
    }
}
