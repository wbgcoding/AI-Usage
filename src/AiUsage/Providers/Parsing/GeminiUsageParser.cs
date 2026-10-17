using System.Text.Json;
using AiUsage.Models;
using AiUsage.Services;

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
                    || !(modelEl.GetString() ?? "").StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Same proto3 rule as the summary: a missing fraction is an exhausted quota, which always
                // names its reset. A bucket with neither says nothing; an unused quota may come without a
                // reset and still counts.
                double remaining = 0;
                if (bucket.TryGetProperty("remainingFraction", out var fractionEl))
                {
                    if (fractionEl.ValueKind != JsonValueKind.Number || !fractionEl.TryGetDouble(out remaining))
                        continue;
                }
                else if (!bucket.TryGetProperty("resetTime", out _))
                {
                    continue;
                }
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

    // The model ids the per-model answer listed when this read was last adapted. A Gemini id outside
    // it is still counted, but named in the log so the read can be checked against it.
    private static readonly HashSet<string> KnownGeminiModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "gemini-2.5-flash", "gemini-2.5-flash-lite", "gemini-2.5-flash-thinking", "gemini-2.5-pro",
        "gemini-3-flash", "gemini-3-flash-agent", "gemini-3.1-flash-image", "gemini-3.1-flash-lite",
        "gemini-3.5-flash-extra-low", "gemini-3.5-flash-lite", "gemini-3.5-flash-low", "gemini-3.8-flash-tiered",
    };

    // Model families the per-model answer is known to hold besides Gemini: other makers' models and
    // the editor's own completion models. An id from none of them is a family this read ignores.
    private static readonly string[] KnownModelPrefixes = ["gemini-", "claude-", "gpt-", "chat_", "tab_"];

    /// <summary>
    /// What in the two answers no longer matches what this parser was written for: a new Gemini
    /// model, a model family it does not know, Gemini models that stopped sharing one quota, an
    /// unknown window name, or no Gemini quota in either answer. Each note is one log line telling
    /// that the read needs adapting; empty while everything looks as expected. Ids and group names
    /// only, never a figure or an account detail.
    /// </summary>
    public static IReadOnlyList<string> ChangeNotes(string summaryJson, string modelQuotaJson)
    {
        var notes = new List<string>();
        var summaryWindows = Parse(summaryJson);

        notes.AddRange(WebChangeNotes(summaryJson));

        if (summaryWindows.Count > 0)
            return notes;

        if (ParseModelQuota(modelQuotaJson).Count == 0)
        {
            notes.Add($"neither the quota summary (groups: {string.Join(", ", GroupNames(summaryJson).Select(ReadChangeLog.SafeName))}) nor the per-model answer holds a Gemini quota; the tile shows no figures until the read is adapted.");
            return notes;
        }

        double? lowestFraction = null, highestFraction = null;
        foreach (var (model, fraction) in ModelBuckets(modelQuotaJson))
        {
            if (model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
            {
                if (!KnownGeminiModels.Contains(model))
                    notes.Add($"new Gemini model '{ReadChangeLog.SafeName(model)}' found; it is counted in the shared Gemini quota, check that this still holds.");
                if (fraction is { } value)
                {
                    lowestFraction = Math.Min(lowestFraction ?? value, value);
                    highestFraction = Math.Max(highestFraction ?? value, value);
                }
            }
            else if (!KnownModelPrefixes.Any(prefix => model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"unknown model '{ReadChangeLog.SafeName(model)}' found; it is not counted on the Gemini tile, check whether it belongs there.");
            }
        }
        if (highestFraction - lowestFraction > 1e-6)
            notes.Add("the Gemini models no longer share one quota; the tile shows the most used one, check whether separate rows are needed.");

        return notes;
    }

    /// <summary>
    /// The notes that need nothing but one quota summary: a bucket of the Gemini group with a window
    /// name the read does not know, with no window name, or with a remainingFraction that is no number
    /// between 0 and 1 (each is skipped). Only an answer whose Gemini group is recognised is looked at,
    /// so the other usage addresses of the web read, which hold other shapes, say nothing here.
    /// Empty for a summary that looks as expected. Names only, never a figure.
    /// </summary>
    public static IReadOnlyList<string> WebChangeNotes(string summaryJson)
    {
        var notes = new List<string>();
        foreach (var window in UnknownWindowNames(summaryJson))
            notes.Add($"the quota summary names an unknown window '{ReadChangeLog.SafeName(window)}', which is not shown.");
        notes.AddRange(UnusableBucketNotes(summaryJson));
        return notes.Distinct().ToList();
    }

    private static List<string> UnusableBucketNotes(string summaryJson)
    {
        var notes = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(summaryJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array
                || PickGeminiGroup(groups) is not { } group
                || !group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                return notes;

            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object)
                    continue;
                if (!bucket.TryGetProperty("window", out var windowEl) || windowEl.ValueKind != JsonValueKind.String)
                {
                    notes.Add("a bucket of the Gemini group names no window, so it is not shown.");
                    continue;
                }

                if (bucket.TryGetProperty("remainingFraction", out var fractionEl)
                    && (fractionEl.ValueKind != JsonValueKind.Number || !fractionEl.TryGetDouble(out var fraction) || fraction is < 0 or > 1))
                    notes.Add($"the '{ReadChangeLog.SafeName(windowEl.GetString())}' bucket has a remainingFraction that is no number between 0 and 1, so it is not shown.");
            }
        }
        catch (JsonException)
        {
        }
        return notes;
    }

    private static List<string> GroupNames(string summaryJson)
    {
        var names = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(summaryJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.ValueKind == JsonValueKind.Object
                        && group.TryGetProperty("displayName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        names.Add(nameEl.GetString() ?? "");
                }
            }
        }
        catch (JsonException)
        {
        }
        return names.Count > 0 ? names : ["none"];
    }

    private static List<string> UnknownWindowNames(string summaryJson)
    {
        var names = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(summaryJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array
                || PickGeminiGroup(groups) is not { } group
                || !group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                return names;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind == JsonValueKind.Object
                    && bucket.TryGetProperty("window", out var windowEl) && windowEl.ValueKind == JsonValueKind.String
                    && windowEl.GetString() is { } window && window is not ("5h" or "weekly"))
                    names.Add(window);
            }
        }
        catch (JsonException)
        {
        }
        return names;
    }

    private static List<(string Model, double? Fraction)> ModelBuckets(string modelQuotaJson)
    {
        var models = new List<(string, double?)>();
        try
        {
            using var document = JsonDocument.Parse(modelQuotaJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                return models;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object
                    || !bucket.TryGetProperty("modelId", out var modelEl) || modelEl.ValueKind != JsonValueKind.String)
                    continue;
                double? fraction = bucket.TryGetProperty("remainingFraction", out var fractionEl) && fractionEl.ValueKind == JsonValueKind.Number && fractionEl.TryGetDouble(out var value)
                    ? value
                    : bucket.TryGetProperty("resetTime", out _) ? 0 : null;
                models.Add((modelEl.GetString() ?? "", fraction));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
        }
        return models;
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
