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
    // How a bucket carried its remainingFraction: not at all, as a number, or as something else.
    private enum FractionKind { Missing, Number, Invalid }

    // One bucket of the summary's Gemini group. Window is null when the bucket names no window as text.
    private sealed record SummaryBucket(string? Window, FractionKind Kind, double Fraction, DateTimeOffset? ResetsAt);

    // One summary answer, parsed once: every group name, and the buckets of the Gemini group
    // (null when the answer has no recognisable Gemini group with a bucket list).
    private sealed record SummaryRead(List<string> GroupNames, List<SummaryBucket>? GeminiBuckets);

    // One bucket of the per-model answer.
    private sealed record ModelBucket(string Model, FractionKind Kind, double Fraction, bool HasResetTime, DateTimeOffset? ResetsAt);

    public static IReadOnlyList<UsageWindow> Parse(string responseText) => SummaryWindows(ReadSummary(responseText));

    private static List<UsageWindow> SummaryWindows(SummaryRead summary)
    {
        if (summary.GeminiBuckets is not { } buckets)
            return [];

        var windows = new List<UsageWindow>(capacity: 2);
        foreach (var bucket in buckets)
        {
            if (bucket.Window is null || bucket.Kind == FractionKind.Invalid)
                continue;
            // Proto3 JSON leaves out a field that holds its default value, so an exhausted bucket
            // arrives with no remainingFraction at all - that is 0 left, not an unknown bucket.
            var remaining = bucket.Kind == FractionKind.Number ? bucket.Fraction : 0;
            // A fraction outside 0..1 cannot describe "part of a quota left" - the bucket is
            // dropped rather than clamped into a false 0% or 100%.
            if (remaining is < 0 or > 1)
                continue;

            var (label, kind) = bucket.Window switch
            {
                "5h" => ("Window_FiveHour", WindowKind.FiveHour),
                "weekly" => ("Window_Weekly", WindowKind.Weekly),
                _ => (null, WindowKind.Other),
            };
            if (label is null)
                continue;

            var usedPercent = (1 - remaining) * 100;
            windows.Add(new UsageWindow(label, kind, usedPercent, bucket.ResetsAt, windowMinutes: null));
        }

        return windows;
    }

    private static SummaryRead ReadSummary(string? summaryJson)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(summaryJson))
            return new SummaryRead(names, null);

        try
        {
            using var document = JsonDocument.Parse(summaryJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                return new SummaryRead(names, null);

            foreach (var named in groups.EnumerateArray())
            {
                if (named.ValueKind == JsonValueKind.Object
                    && named.TryGetProperty("displayName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    names.Add(nameEl.GetString() ?? "");
            }

            if (PickGeminiGroup(groups) is not { } group
                || !group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                return new SummaryRead(names, null);

            var read = new List<SummaryBucket>();
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object)
                    continue;

                var window = bucket.TryGetProperty("window", out var windowEl) && windowEl.ValueKind == JsonValueKind.String
                    ? windowEl.GetString()
                    : null;
                var (kind, fraction) = ReadFraction(bucket);
                read.Add(new SummaryBucket(window, kind, fraction, ReadResetTime(bucket)));
            }

            return new SummaryRead(names, read);
        }
        catch (JsonException)
        {
            return new SummaryRead(names, null);
        }
    }

    private static (FractionKind Kind, double Fraction) ReadFraction(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("remainingFraction", out var fractionEl))
            return (FractionKind.Missing, 0);
        return fractionEl.ValueKind == JsonValueKind.Number && fractionEl.TryGetDouble(out var value)
            ? (FractionKind.Number, value)
            : (FractionKind.Invalid, 0);
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
    public static IReadOnlyList<UsageWindow> ParseModelQuota(string responseText) => ModelQuotaWindows(ReadModelBuckets(responseText));

    private static List<UsageWindow> ModelQuotaWindows(List<ModelBucket> models)
    {
        double? lowest = null;
        DateTimeOffset? resetsAt = null;
        foreach (var bucket in models)
        {
            if (!bucket.Model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
                continue;
            // Same proto3 rule as the summary: a missing fraction is an exhausted quota, which always
            // names its reset. A bucket with neither says nothing; an unused quota may come without a
            // reset and still counts.
            double remaining = 0;
            if (bucket.Kind == FractionKind.Invalid)
                continue;
            if (bucket.Kind == FractionKind.Number)
                remaining = bucket.Fraction;
            else if (!bucket.HasResetTime)
                continue;
            if (remaining is < 0 or > 1)
                continue;
            if (lowest is { } known && remaining >= known)
                continue;

            lowest = remaining;
            resetsAt = bucket.ResetsAt;
        }

        return lowest is { } fraction
            ? [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, (1 - fraction) * 100, resetsAt, windowMinutes: null)]
            : [];
    }

    private static List<ModelBucket> ReadModelBuckets(string? modelQuotaJson)
    {
        var models = new List<ModelBucket>();
        if (string.IsNullOrWhiteSpace(modelQuotaJson))
            return models;

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
                var (kind, fraction) = ReadFraction(bucket);
                models.Add(new ModelBucket(
                    modelEl.GetString() ?? "", kind, fraction, bucket.TryGetProperty("resetTime", out _), ReadResetTime(bucket)));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
        }
        return models;
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

    // Summary groups known not to hold the Gemini quota. Any other group without "Gemini" in its name
    // is named in the log: it may be the Gemini quota under a new name.
    private static readonly HashSet<string> KnownOtherGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "Claude and GPT models",
    };

    /// <summary>
    /// What in the two answers no longer matches what this parser was written for: a new Gemini
    /// model, a model family it does not know, Gemini models that stopped sharing one quota, an
    /// unknown window name, an unknown summary group, or no Gemini quota in either answer. Each note is one log line telling
    /// that the read needs adapting; empty while everything looks as expected. Ids and group names
    /// only, never a figure or an account detail.
    /// </summary>
    public static IReadOnlyList<string> ChangeNotes(string summaryJson, string modelQuotaJson)
    {
        var notes = new List<string>();
        var summary = ReadSummary(summaryJson);
        var models = ReadModelBuckets(modelQuotaJson);
        var summaryWindows = SummaryWindows(summary);

        notes.AddRange(SummaryNotes(summary));
        foreach (var name in summary.GroupNames)
        {
            if (!name.Contains("Gemini", StringComparison.OrdinalIgnoreCase) && !KnownOtherGroups.Contains(name))
                notes.Add($"the quota summary names an unknown group '{ReadChangeLog.SafeName(name)}'; it is not shown on the Gemini tile, check whether it holds the Gemini quota.");
        }

        if (summaryWindows.Count > 0)
            return notes;

        if (ModelQuotaWindows(models).Count == 0 && !HoldsNoGeminiQuota(summary, summaryWindows, models))
        {
            notes.Add($"neither the quota summary (groups: {GroupList(summary)}) nor the per-model answer holds a Gemini quota; the tile shows no figures until the read is adapted.");
            return notes;
        }

        double? lowestFraction = null, highestFraction = null;
        foreach (var bucket in models)
        {
            var model = bucket.Model;
            if (model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
            {
                if (!KnownGeminiModels.Contains(model))
                    notes.Add($"new Gemini model '{ReadChangeLog.SafeName(model)}' found; it is counted in the shared Gemini quota, check that this still holds.");
                if (ModelFraction(bucket) is { } value)
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

    // The fraction the change check compares: the number itself, 0 for an exhausted bucket that names
    // its reset, otherwise nothing.
    private static double? ModelFraction(ModelBucket bucket) =>
        bucket.Kind == FractionKind.Number ? bucket.Fraction : bucket.HasResetTime ? 0 : null;

    /// <summary>
    /// True when both answers are intact but Antigravity simply has no Gemini quota for this account:
    /// the summary lists only groups known to hold other models, and the per-model answer lists models
    /// but no Gemini one. The tile then takes its numbers from the web session alone; this is no
    /// changed answer.
    /// </summary>
    public static bool HoldsNoGeminiQuota(string summaryJson, string modelQuotaJson)
    {
        var summary = ReadSummary(summaryJson);
        return HoldsNoGeminiQuota(summary, SummaryWindows(summary), ReadModelBuckets(modelQuotaJson));
    }

    private static bool HoldsNoGeminiQuota(SummaryRead summary, List<UsageWindow> summaryWindows, List<ModelBucket> models)
    {
        if (summaryWindows.Count > 0)
            return false;
        if (summary.GroupNames.Count == 0 || !summary.GroupNames.All(KnownOtherGroups.Contains))
            return false;
        return models.Count > 0 && !models.Any(bucket => bucket.Model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The notes that need nothing but one quota summary: a bucket of the Gemini group with a window
    /// name the read does not know, with no window name, or with a remainingFraction that is no number
    /// between 0 and 1 (each is skipped). Only an answer whose Gemini group is recognised is looked at,
    /// so the other usage addresses of the web read, which hold other shapes, say nothing here.
    /// Empty for a summary that looks as expected. Names only, never a figure.
    /// </summary>
    public static IReadOnlyList<string> WebChangeNotes(string summaryJson) => SummaryNotes(ReadSummary(summaryJson));

    private static List<string> SummaryNotes(SummaryRead summary)
    {
        var notes = new List<string>();
        if (summary.GeminiBuckets is not { } buckets)
            return notes;

        foreach (var bucket in buckets)
        {
            if (bucket.Window is { } window && window is not ("5h" or "weekly"))
                notes.Add($"the quota summary names an unknown window '{ReadChangeLog.SafeName(window)}', which is not shown.");
        }

        foreach (var bucket in buckets)
        {
            if (bucket.Window is null)
            {
                notes.Add("a bucket of the Gemini group names no window, so it is not shown.");
                continue;
            }

            if (bucket.Kind == FractionKind.Invalid || bucket.Kind == FractionKind.Number && bucket.Fraction is < 0 or > 1)
                notes.Add($"the '{ReadChangeLog.SafeName(bucket.Window)}' bucket has a remainingFraction that is no number between 0 and 1, so it is not shown.");
        }
        return notes.Distinct().ToList();
    }

    /// <summary>The group names for a log line; "none" only as wording, never as a group to check.</summary>
    private static string GroupList(SummaryRead summary) =>
        summary.GroupNames is { Count: > 0 } names ? string.Join(", ", names.Select(ReadChangeLog.SafeName)) : "none";

    private static DateTimeOffset? ReadResetTime(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("resetTime", out var resetEl) || resetEl.ValueKind != JsonValueKind.String)
            return null;
        return SessionLineAge.TryParse(resetEl.GetString(), out var parsed)
            ? UnixTimeConversion.PlausibleOrNull(parsed.ToUniversalTime())
            : null;
    }
}
