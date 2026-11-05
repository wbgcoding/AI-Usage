using AiUsage.Services;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The effort levels in the stats rings and bar labels read in the app's language; a level the
/// app does not know stays as the tool wrote it.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class EffortLabelTests
{
    [Theory]
    [InlineData("de", "low", "Niedrig")]
    [InlineData("de", "xhigh", "Sehr hoch")]
    [InlineData("de", "", "Unbekannt")]
    [InlineData("en", "medium", "Medium")]
    [InlineData("en", "xhigh", "Extra high")]
    [InlineData("fr", "high", "Élevé")]
    [InlineData("ja", "max", "最大")]
    [InlineData("pl", "minimal", "Minimalny")]
    public void A_known_level_reads_in_the_language(string language, string level, string expected)
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(language);
        try
        {
            Assert.Equal(expected, StatsViewModel.ResolveEffortLabel(level));
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void A_level_the_app_does_not_know_stays_as_written()
    {
        Assert.Equal("turbo", StatsViewModel.ResolveEffortLabel("turbo"));
    }

    [Fact]
    public void The_level_match_ignores_case()
    {
        Assert.Equal("Hoch", StatsViewModel.ResolveEffortLabel("High"));
    }
}
