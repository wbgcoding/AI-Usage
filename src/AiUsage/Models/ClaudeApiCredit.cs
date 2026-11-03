using System.Globalization;

namespace AiUsage.Models;

/// <summary>
/// The prepaid API credit balance of the Claude Console account behind a Claude sign-in, in major
/// units (dollars, not cents), and the free credit granted on top (null when the console lists none).
/// Billing numbers only, never anything of the account's content.
/// </summary>
public sealed record ClaudeApiCredit(decimal Amount, string Currency, ClaudeCreditGrant? Grant = null)
{
    /// <summary>The balance as the reader's culture writes money ("$18.40", "18,40 $"). A currency
    /// without a well-known sign is written as its code after the number.</summary>
    public string FormatAmount(CultureInfo culture) => FormatMoney(Amount, culture);

    /// <summary>Any amount in this balance's currency, written like <see cref="FormatAmount"/>.</summary>
    public string FormatMoney(decimal amount, CultureInfo culture)
    {
        var sign = Currency.ToUpperInvariant() switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            _ => null,
        };
        if (sign is null)
            return amount.ToString("N2", culture) + " " + Currency.ToUpperInvariant();

        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencySymbol = sign;
        return amount.ToString("C2", format);
    }
}

/// <summary>
/// The free credit the console grants on top of the bought balance (for example the monthly credit
/// that comes with a Max plan), summed over every grant that has not expired yet: two grants overlap
/// for a few days around the monthly change, so both count then. <see cref="NextExpiry"/> is the
/// earliest end of those grants.
/// </summary>
public sealed record ClaudeCreditGrant(decimal Granted, decimal Remaining, DateTimeOffset? NextExpiry)
{
    /// <summary>What of the granted credit is used up.</summary>
    public decimal Used => Granted - Remaining;

    /// <summary>The used share, 0..1.</summary>
    public double UsedFraction => Granted > 0 ? (double)(Used / Granted) : 0;
}
