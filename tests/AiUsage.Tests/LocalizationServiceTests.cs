using System.Globalization;
using System.Resources;
using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>
/// Resolves languages through the internal seam rather than the shared instance: these assertions
/// must not depend on, or disturb, whatever language another test has the one instance set to.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class LocalizationServiceTests
{
    private const string GermanDisplaySection = "Anzeige";
    private const string EnglishDisplaySection = "Display";

    [Fact]
    public void System_follows_the_language_windows_is_displayed_in()
    {
        Assert.Equal(GermanDisplaySection, DisplaySectionUnder("de-AT", language: "System"));
    }

    [Fact]
    public void System_falls_back_to_english_for_a_language_with_no_resources()
    {
        Assert.Equal(EnglishDisplaySection, DisplaySectionUnder("fr-FR", language: "System"));
    }

    [Fact]
    public void A_hand_typed_uppercase_code_selects_that_language_instead_of_falling_through()
    {
        // German picked explicitly, on a machine displaying French - only a case-insensitive
        // comparison of the stored code can produce the German text here.
        Assert.Equal(GermanDisplaySection, DisplaySectionUnder("fr-FR", language: "DE"));
        Assert.Equal(EnglishDisplaySection, DisplaySectionUnder("de-AT", language: "EN"));
    }

    [Fact]
    public void The_language_table_has_the_thirteen_entries_in_order_and_each_resolves_a_known_key()
    {
        Assert.Equal(
            ["System", "en", "de", "es", "fr", "it", "nl", "pl", "pt-BR", "ro", "tr", "ja", "zh-Hans"],
            LocalizationService.Languages.Select(l => l.Code));
        Assert.Equal(
            ["Language.System", "Language.English", "Language.German", "Language.Spanish", "Language.French",
             "Language.Italian", "Language.Dutch", "Language.Polish", "Language.PortugueseBrazil",
             "Language.Romanian", "Language.Turkish", "Language.Japanese", "Language.ChineseSimplified"],
            LocalizationService.Languages.Select(l => l.LabelKey));

        foreach (var language in LocalizationService.Languages)
        {
            var resolved = Resolve(language.Code).GetString("Settings.Section.Display", CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrEmpty(resolved));
        }
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("pt-PT", "en")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("es-MX", "es")]
    [InlineData("ja-JP", "ja")]
    [InlineData("zh-TW", "en")]
    public void System_picks_the_exact_then_parent_then_two_letter_match_else_english(string uiCulture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(uiCulture);
            var manager = Resolve("System");
            Assert.Same(LocalizationService.Languages.Single(l => l.Code == expected).Resources, manager);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("en", "en-US")]
    [InlineData("de", "de-DE")]
    [InlineData("es", "es")]
    [InlineData("pt-BR", "pt-BR")]
    public void The_format_culture_maps_each_code_to_a_specific_culture(string code, string expected)
    {
        Assert.Equal(expected, LocalizationService.FormatCultureFor(code).Name);
    }

    private static string? DisplaySectionUnder(string uiCulture, string language)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(uiCulture);
            return Resolve(language).GetString("Settings.Section.Display", CultureInfo.InvariantCulture);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void The_display_culture_names_days_and_months_in_the_ui_language_and_keeps_the_regional_format()
    {
        var regional = CultureInfo.GetCultureInfo("en-US");
        var display = LocalizationService.DisplayCultureFor(regional, CultureInfo.GetCultureInfo("de"));
        var day = new DateTime(2026, 10, 1, 14, 30, 0);

        Assert.Equal("Donnerstag", display.DateTimeFormat.GetDayName(day.DayOfWeek));
        Assert.Equal("Okt", display.DateTimeFormat.AbbreviatedMonthNames[9]);
        Assert.Equal(day.ToString("t", regional), day.ToString("t", display));
        Assert.Equal(1234.5.ToString("N1", regional), 1234.5.ToString("N1", display));
    }

    [Fact]
    public void The_display_culture_is_the_regional_one_itself_when_the_languages_agree()
    {
        var regional = CultureInfo.GetCultureInfo("de-AT");

        Assert.Same(regional, LocalizationService.DisplayCultureFor(regional, CultureInfo.GetCultureInfo("de")));
    }

    private static ResourceManager Resolve(string language) => LocalizationService.ResourceManagerFor(language);
}
