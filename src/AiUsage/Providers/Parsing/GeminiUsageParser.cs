using System.Text.Json;
using AiUsage.Models;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Pure function over Antigravity's <c>retrieveUserQuotaSummary</c> response - no network, so it is
/// tested against inline fixtures alone. The response groups models (a "Gemini Models" group, a
/// "Claude and GPT models" group) and each group carries a five-hour and a weekly bucket with a
/// <c>remainingFraction</c>. This maps the Gemini group's two buckets onto the tile's five-hour and
/// weekly windows; an unrecognised shape yields no windows rather than a guessed number. Since the
/// summary stopped listing a Gemini group, <see cref="ParseModelQuota"/> reads the Gemini models'
/// shared quota from the per-model answer instead.
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

    /// <summary>The group whose name mentions Gemini, or the first group when no group is named at
    /// all. A group named for other models (the "Claude and GPT models" one) never stands in: its
    /// numbers on the Gemini tile would be another quota's.</summary>
    private static JsonElement? PickGeminiGroup(JsonElement groups)
    {
        JsonElement? first = null;
        var anyNamed = false;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object)
                continue;
            first ??= group;
            if (group.TryGetProperty("displayName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                anyNamed = true;
                if ((nameEl.GetString() ?? "").Contains("Gemini", StringComparison.OrdinalIgnoreCase))
                    return group;
            }
        }
        return anyNamed ? null : first;
    }

    /// <summary>
    /// Reads Antigravity's <c>retrieveUserQuota</c> answer: one bucket per model, each with a
    /// <c>remainingFraction</c> and the reset of its five-hour window. The Gemini models share one
    /// quota, so the most used of them is the tile's five-hour window; the answer has no weekly
    /// figure. Models of other makers and the editor's own completion models (no reset) are left out.
    /// An answer without a Gemini model yields no window.
    /// </summary>
    public static IReadOnlyList<UsageWindow> ParseModelQuota(string responseText)
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
                || !document.RootElement.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                return [];

            double? lowest = null;
            DateTimeOffset? resetsAt = null;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object
                    || !bucket.TryGetProperty("modelId", out var modelEl) || modelEl.ValueKind != JsonValueKind.String
                    || !(modelEl.GetString() ?? "").StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
                    || !bucket.TryGetProperty("resetTime", out _))
                    continue;
                // Same proto3 rule as the summary: a missing fraction is an exhausted quota.
                double remaining = 0;
                if (bucket.TryGetProperty("remainingFraction", out var fractionEl)
                    && (fractionEl.ValueKind != JsonValueKind.Number || !fractionEl.TryGetDouble(out remaining)))
                    continue;
                if (remaining is < 0 or > 1)
                    continue;
                if (lowest is { } known && remaining >= known)
                    continue;

                lowest = remaining;
                resetsAt = ReadResetTime(bucket);
            }

            return lowest is { } fraction
                ? [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, (1 - fraction) * 100, resetsAt, windowMinutes: null)]
                : [];
        }
    }

    private static DateTimeOffset? ReadResetTime(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("resetTime", out var resetEl) || resetEl.ValueKind != JsonValueKind.String)
            return null;
        return SessionLineAge.TryParse(resetEl.GetString(), out var parsed)
            ? UnixTimeConversion.PlausibleOrNull(parsed.ToUniversalTime())
            : null;
    }
}
