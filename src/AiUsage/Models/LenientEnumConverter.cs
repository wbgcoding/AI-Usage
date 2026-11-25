using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsage.Models;

/// <summary>Reads an enum name case-insensitively and takes anything unknown as the fallback, so a
/// hand-edited or newer settings file never rejects the whole file.</summary>
internal abstract class LenientEnumConverter<TEnum>(TEnum fallback) : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var value)
            && Enum.IsDefined(value))
            return value;

        reader.Skip();
        return fallback;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class CostCurrencyConverter() : LenientEnumConverter<CostCurrency>(CostCurrency.Usd);

internal sealed class ClaudeCreditRouteConverter() : LenientEnumConverter<ClaudeCreditRoute>(ClaudeCreditRoute.None);
