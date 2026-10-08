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
    public void The_language_table_has_exactly_the_three_prepared_entries_and_each_resolves_a_known_key()
    {
        Assert.Equal(["System", "en", "de"], LocalizationService.Languages.Select(l => l.Code));

        foreach (var language in LocalizationService.Languages)
        {
            var resolved = Resolve(language.Code).GetString("Settings.Section.Display", CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrEmpty(resolved));
        }
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

    private static ResourceManager Resolve(string language) => LocalizationService.ResourceManagerFor(language);
}
