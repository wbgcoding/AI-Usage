using System.Globalization;
using System.Text.Json;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Age check for one line of a session transcript, used while reading a file newest-first: the
/// first line older than the bound means every line before it is older still. Costs one substring
/// search for ordinary lines; the JSON parse only runs for a line that already looks old.
/// </summary>
internal static class SessionLineAge
{
    /// <summary>The oldest a transcript line may be and still describe a live quota window: the
    /// longest window is weekly.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private const string Marker = "\"timestamp\":\"";

    /// <summary>True only when the line's own top-level <c>timestamp</c> is earlier than
    /// <paramref name="cutoff"/>. A line without a readable timestamp is never reported old.</summary>
    internal static bool IsOlderThan(string line, DateTimeOffset cutoff)
    {
        var start = line.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
            return false;

        // The first match can belong to a nested object; it only decides whether the exact check
        // below is worth running.
        start += Marker.Length;
        var end = line.IndexOf('"', start);
        if (end < 0 || end - start > 40 || !TryParse(line.AsSpan(start, end - start), out var candidate) || candidate >= cutoff)
            return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("timestamp", out var element)
                && element.ValueKind == JsonValueKind.String
                && TryParse(element.GetString(), out var exact)
                && exact < cutoff;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParse(ReadOnlySpan<char> text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out value);
}
