using System.Globalization;
using System.Text.Json;
using AiUsage.Models;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Pure function over Antigravity's <c>retrieveUserQuotaSummary</c> response - no network, so it is
/// tested against inline fixtures alone. The response groups models (a "Gemini Models" group, a
/// "Claude and GPT models" group) and each group carries a five-hour and a weekly bucket with a
/// <c>remainingFraction</c>. This maps the Gemini group's two buckets onto the tile's five-hour and
/// weekly windows; an unrecognised shape yields no windows rather than a guessed number.
/// </summary>
public static class GeminiUsageParser
{
    public static IReadOnlyList<UsageWindow> Parse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseText);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                return [];

            var group = PickGeminiGroup(groups);
            if (group is not { } geminiGroup || !geminiGroup.TryGetProperty("buckets", out var buckets)
                || buckets.ValueKind != JsonValueKind.Array)
                return [];

            var windows = new List<UsageWindow>(capacity: 2);
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object)
                    continue;
                if (!bucket.TryGetProperty("window", out var windowEl) || windowEl.ValueKind != JsonValueKind.String)
                    continue;
                // Proto3 JSON leaves out a field that holds its default value, so an exhausted bucket
                // arrives with no remainingFraction at all - that is 0 left, not an unknown bucket.
                double remaining = 0;
                if (bucket.TryGetProperty("remainingFraction", out var fractionEl)
                    && (fractionEl.ValueKind != JsonValueKind.Number || !fractionEl.TryGetDouble(out remaining)))
                    continue;
                // A fraction outside 0..1 cannot describe "part of a quota left" - the bucket is
                // dropped rather than clamped into a false 0% or 100%.
                if (remaining is < 0 or > 1)
                    continue;

                var (label, kind) = windowEl.GetString() switch
                {
                    "5h" => ("Window_FiveHour", WindowKind.FiveHour),
                    "weekly" => ("Window_Weekly", WindowKind.Weekly),
                    _ => (null, WindowKind.Other),
                };
                if (label is null)
                    continue;

                var usedPercent = (1 - remaining) * 100;
                windows.Add(new UsageWindow(label, kind, usedPercent, ReadResetTime(bucket), windowMinutes: null));
            }

            return windows;
        }
    }

    /// <summary>The group whose name mentions Gemini, or the first group when none does - the buckets
    /// carry the same window shape either way, this only picks whose five-hour/weekly the tile shows.</summary>
    private static JsonElement? PickGeminiGroup(JsonElement groups)
    {
        JsonElement? first = null;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object)
                continue;
            first ??= group;
            if (group.TryGetProperty("displayName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                && (nameEl.GetString() ?? "").Contains("Gemini", StringComparison.OrdinalIgnoreCase))
                return group;
        }
        return first;
    }

    private static DateTimeOffset? ReadResetTime(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("resetTime", out var resetEl) || resetEl.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(
            resetEl.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }
}
