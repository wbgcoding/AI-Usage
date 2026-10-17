using System.Text.Json;
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

/// <summary>Reads a name case-insensitively and takes anything unknown as <see cref="CostCurrency.Usd"/>,
/// so a hand-edited or newer settings file never rejects the whole file.</summary>
internal sealed class CostCurrencyConverter : JsonConverter<CostCurrency>
{
    public override CostCurrency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<CostCurrency>(reader.GetString(), ignoreCase: true, out var value)
            && Enum.IsDefined(value))
            return value;

        reader.Skip();
        return CostCurrency.Usd;
    }

    public override void Write(Utf8JsonWriter writer, CostCurrency value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
