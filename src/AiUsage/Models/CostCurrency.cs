using System.Text.Json.Serialization;

namespace AiUsage.Models;

/// <summary>The currency of the cost estimate in the token statistics. Prices are in US dollars;
/// <see cref="Eur"/> converts with the daily reference rate, <see cref="Auto"/> picks euro when the
/// system region uses it.</summary>
[JsonConverter(typeof(CostCurrencyConverter))]
public enum CostCurrency
{
    Usd,
    Eur,
    Auto,
}
