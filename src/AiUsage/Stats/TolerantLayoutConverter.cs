using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsage.Stats;

/// <summary>Reads the stored section arrangement and turns a wrong JSON shape (a string where the
/// array belongs, a number inside a column) into null, so a hand-edited arrangement falls back to the
/// default layout instead of making the whole settings file unreadable.</summary>
public sealed class TolerantLayoutConverter : JsonConverter<List<StatsLayoutRow>?>
{
    public override List<StatsLayoutRow>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        try
        {
            return document.RootElement.Deserialize<List<StatsLayoutRow>>(options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<StatsLayoutRow>? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
