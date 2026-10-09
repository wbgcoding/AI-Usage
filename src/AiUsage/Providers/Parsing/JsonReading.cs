using System.Text.Json;

namespace AiUsage.Providers.Parsing;

/// <summary>The "this property, if it is there and of this kind" reads every foreign-JSON parser
/// needs. Each answers null for a missing property, one of another kind (a string holding a number
/// is not a number) or a number that does not fit, and never throws.</summary>
internal static class JsonReading
{
    /// <summary>A missing property and an explicit JSON null both mean "no block here".</summary>
    internal static JsonElement? TryGetObject(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    internal static double? TryGetDouble(JsonElement? element, string propertyName) =>
        element is { } parent && parent.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value)
            ? value
            : null;

    internal static int? TryGetInt32(JsonElement? element, string propertyName) =>
        element is { } parent && parent.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    internal static long? TryGetInt64(JsonElement? element, string propertyName) =>
        element is { } parent && parent.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value
            : null;
}
