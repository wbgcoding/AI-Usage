using System.Globalization;

namespace AiUsage.Models;

/// <summary>
/// The prepaid API credit balance of the Claude Console account behind a Claude sign-in, in major
/// units (dollars, not cents). Billing numbers only, never anything of the account's content.
/// </summary>
public sealed record ClaudeApiCredit(decimal Amount, string Currency)
{
    /// <summary>The balance as the reader's culture writes money ("$18.40", "18,40 $"). A currency
    /// without a well-known sign is written as its code after the number.</summary>
    public string FormatAmount(CultureInfo culture)
    {
        var sign = Currency.ToUpperInvariant() switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            _ => null,
        };
        if (sign is null)
            return Amount.ToString("N2", culture) + " " + Currency.ToUpperInvariant();

        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencySymbol = sign;
        return Amount.ToString("C2", format);
    }
}
