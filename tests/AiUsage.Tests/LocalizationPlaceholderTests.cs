using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using AiUsage.Services;
using Xunit;
using System.Collections;

namespace AiUsage.Tests;

/// <summary>
/// A translation that drops or duplicates a placeholder throws at runtime, or worse, prints the
/// wrong argument in one language only - the parity test proves both files carry the same keys, it
/// says nothing about what is inside the values.
/// </summary>
public class LocalizationPlaceholderTests
{
    // Matches {0}, {1:0.#}, {2,-8} and so on, but not the escaped literal {{.
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    [Fact]
    public void EveryLanguageUsesTheSamePlaceholdersAsEnglishForEveryKey()
    {
        var concreteLanguages = LocalizationService.Languages.Where(l => l.Resources is not null).ToList();
        var english = concreteLanguages.Single(l => l.Code == "en");
        var englishValues = LoadValues(english.Resources!);
        Assert.NotEmpty(englishValues);

        foreach (var language in concreteLanguages.Where(l => l.Code != "en"))
        {
            var values = LoadValues(language.Resources!);
            var mismatches = new List<string>();

            foreach (var (key, englishValue) in englishValues)
            {
                if (!values.TryGetValue(key, out var translated))
                    continue; // a missing key is the parity test's finding, not this one's

                var expected = IndexCounts(englishValue);
                var actual = IndexCounts(translated);
                if (!SameCounts(expected, actual))
                    mismatches.Add($"{key}: en {Describe(expected)} vs {language.Code} {Describe(actual)}");
            }

            Assert.True(mismatches.Count == 0,
                $"Placeholder mismatch between Strings.resx and the '{language.Code}' resx:{Environment.NewLine}"
                + string.Join(Environment.NewLine, mismatches));
        }
    }

    private static Dictionary<string, string> LoadValues(ResourceManager manager)
    {
        var set = manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        return set.Cast<DictionaryEntry>()
            .Where(e => e.Value is string)
            .ToDictionary(e => (string)e.Key!, e => (string)e.Value!);
    }

    private static Dictionary<int, int> IndexCounts(string value)
    {
        var counts = new Dictionary<int, int>();
        foreach (Match match in Placeholder.Matches(value))
        {
            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            counts[index] = counts.GetValueOrDefault(index) + 1;
        }
        return counts;
    }

    private static bool SameCounts(Dictionary<int, int> a, Dictionary<int, int> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var count) && count == pair.Value);

    private static string Describe(Dictionary<int, int> counts) =>
        counts.Count == 0
            ? "none"
            : string.Join(", ", counts.OrderBy(p => p.Key).Select(p => $"{{{p.Key}}}x{p.Value}"));
}
