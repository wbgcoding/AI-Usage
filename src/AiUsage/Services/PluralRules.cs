using System.Globalization;

namespace AiUsage.Services;

/// <summary>
/// Picks the noun form a count needs in the languages whose grammar has more than singular and
/// plural. Polish uses a separate form for 2-4 (but not 12-14): "2 lata", "5 lat". Romanian adds
/// "de" from 20 on: "19 zile", "20 de zile"; fractions and 0 go without it. Every other language
/// of the app gets by with the singular and one plural.
/// </summary>
internal static class PluralRules
{
    /// <summary>Polish puts a fraction in the genitive singular: "1,5 roku", not "1,5 lat". Only
    /// a language with such a rule keeps a "&lt;key&gt;.Fraction" text.</summary>
    public static bool UsesFractionForm(CultureInfo language, double count) =>
        language.TwoLetterISOLanguageName == "pl" && Math.Abs(count) % 1 != 0;

    public static bool UsesFewForm(CultureInfo language, double count)
    {
        var magnitude = Math.Abs(count);
        var isWhole = magnitude % 1 == 0;
        var lastTwo = (long)magnitude % 100;

        return language.TwoLetterISOLanguageName switch
        {
            "pl" => isWhole && (lastTwo % 10 is >= 2 and <= 4) && (lastTwo is < 12 or > 14),
            "ro" => !isWhole || magnitude == 0 || (magnitude != 1 && lastTwo is >= 1 and <= 19),
            _ => false,
        };
    }
}
