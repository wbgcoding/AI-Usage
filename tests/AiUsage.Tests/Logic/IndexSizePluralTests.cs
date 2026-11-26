using System.Globalization;
using AiUsage.Services;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The statistics index line of the settings names its entry count in the form the count needs.</summary>
[Collection(SharedStateTestsCollection.Name)]
[Trait("Layer", "Logic")]
public class IndexSizePluralTests
{
    [Theory]
    [InlineData("pl", 2, "Indeks: 2 wpisy, ")]
    [InlineData("pl", 5, "Indeks: 5 wpisów, ")]
    [InlineData("pl", 12, "Indeks: 12 wpisów, ")]
    [InlineData("pl", 22, "Indeks: 22 wpisy, ")]
    [InlineData("ro", 2, "Index: 2 intrări, ")]
    [InlineData("ro", 20, "Index: 20 de intrări, ")]
    [InlineData("en", 20, "Index: 20 entries, ")]
    public void The_index_line_keeps_the_formatted_count_and_picks_the_noun_form(string language, long entries, string expectedStart)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        LocalizationService.Instance.SetLanguage(language);
        try
        {
            var text = SettingsViewModel.FormatStatsIndexSize((entries, 5 * 1024 * 1024));
            Assert.StartsWith(expectedStart, text);
            Assert.True(text.Length > expectedStart.Length, "the size must follow the count");
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
            CultureInfo.CurrentCulture = previous;
        }
    }
}
