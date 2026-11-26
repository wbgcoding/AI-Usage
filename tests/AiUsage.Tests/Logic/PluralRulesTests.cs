using System.Globalization;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
[Trait("Layer", "Logic")]
public class PluralRulesTests
{
    [Theory]
    [InlineData("pl", 1, false)]
    [InlineData("pl", 2, true)]
    [InlineData("pl", 4, true)]
    [InlineData("pl", 5, false)]
    [InlineData("pl", 12, false)]
    [InlineData("pl", 14, false)]
    [InlineData("pl", 22, true)]
    [InlineData("pl", 1.5, false)]
    [InlineData("ro", 0, true)]
    [InlineData("ro", 1, false)]
    [InlineData("ro", 19, true)]
    [InlineData("ro", 20, false)]
    [InlineData("ro", 101, true)]
    [InlineData("ro", 120, false)]
    [InlineData("ro", 1.5, true)]
    [InlineData("de", 3, false)]
    [InlineData("en", 25, false)]
    public void UsesFewForm_follows_the_language_rule(string language, double count, bool expected)
    {
        Assert.Equal(expected, PluralRules.UsesFewForm(CultureInfo.GetCultureInfo(language), count));
    }

    [Theory]
    [InlineData("ro", 3, "3 zile")]
    [InlineData("ro", 30, "30 de zile")]
    [InlineData("ro", 730, "2 ani")]
    [InlineData("ro", 7300, "20 de ani")]
    [InlineData("pl", 730, "2 lata")]
    [InlineData("pl", 1825, "5 lat")]
    [InlineData("de", 30, "30 Tage")]
    public void A_duration_takes_the_noun_form_its_count_needs(string language, int days, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        LocalizationService.Instance.SetLanguage(language);
        try
        {
            Assert.Equal(expected, DurationFormatter.Describe(days));
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
            CultureInfo.CurrentCulture = previous;
        }
    }
}
