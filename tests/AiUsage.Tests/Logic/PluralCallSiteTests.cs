using System.Globalization;
using AiUsage.Services;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The counted phrases of the stats and settings texts pick the noun form their count
/// needs in Polish and Romanian.</summary>
[Collection(SharedStateTestsCollection.Name)]
[Trait("Layer", "Logic")]
public class PluralCallSiteTests
{
    private static void InLanguage(string language, Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        LocalizationService.Instance.SetLanguage(language);
        try
        {
            body();
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static List<StatsGroupedRow> Projects(int count) =>
        Enumerable.Range(0, count).Select(i => new StatsGroupedRow("p" + i, [1], 1)).ToList();

    [Theory]
    [InlineData("pl", 2, "2 projekty")]
    [InlineData("pl", 5, "5 projektów")]
    [InlineData("pl", 12, "12 projektów")]
    [InlineData("pl", 22, "22 projekty")]
    [InlineData("ro", 2, "2 proiecte")]
    [InlineData("ro", 19, "19 proiecte")]
    [InlineData("ro", 20, "20 de proiecte")]
    [InlineData("en", 7, "7 projects")]
    public void The_other_projects_tooltip_names_its_count_in_the_right_form(string language, int count, string expected)
    {
        InLanguage(language, () =>
        {
            var firstLine = StatsViewModel.BuildOtherProjectsTooltip(Projects(count)).Split('\n')[0];
            Assert.Equal(expected, firstLine);
        });
    }
}
