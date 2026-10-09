using System.Text.Json;
using AiUsage.Models;
using static AiUsage.Providers.Parsing.JsonReading;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Turns one Cursor usage response into its windows - Cursor's own dashboard pages report against a
/// monthly cycle, not the five-hour/weekly split the other providers use, so <see
/// cref="WindowKind.Other"/> is the right bucket rather than a new window kind, which already has
/// its own threshold setting. A response that splits its plan usage by model yields one window per
/// bar (Cursor's own models, everything else) instead of the single combined one; a merged-in weekly
/// Grok Bot usage rides along as a further window when present. Pure function over the response
/// text, no network access. Never throws: an unrecognised shape yields an empty list, never a
/// guessed percentage.
/// </summary>
public static class CursorUsageParser
{
    /// <summary>The account's tier as the usage summary words it (<c>membershipType</c> at the
    /// root, e.g. "pro"), or null when the answer has none - the same document <see cref="Parse"/>
    /// reads, never a separate call.</summary>
    public static string? ParsePlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("membershipType", out var plan) && plan.ValueKind == JsonValueKind.String
                ? plan.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<UsageWindow> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return [];

            if (ReadUsageSummary(root) is { } summaryWindows)
                return summaryWindows;

            if (SumAcrossModels(root) is { } summed)
                return [new UsageWindow(
                    "Window_Month", WindowKind.Other, summed.Percent, ReadResetsAt(root), windowMinutes: null,
                    summed.Tokens, summed.Allowance)];

            var requestUsageObject = FindRequestUsageObject(root);
            var percent = requestUsageObject is { } usage
                ? PercentOf(usage, "numRequests", "maxRequestUsage")
                : PercentOf(root, "usedIncludedUsageCents", "includedUsageCents")
                    ?? PercentOf(root, "usedIncludedUsage", "includedUsage");

            if (percent is null)
                return [];

            var tokenSource = requestUsageObject ?? root;
            var tokens = ReadTokens(tokenSource) ?? (requestUsageObject is null ? null : ReadTokens(root));

            return [new UsageWindow("Window_Month", WindowKind.Other, percent.Value, ReadResetsAt(root), windowMinutes: null, tokens)];
        }
    }

    /// <summary>The usage summary current plans are billed by: <c>individualUsage.plan</c> carries
    /// either a per-model split (<c>autoPercentUsed</c> for Cursor's own models, <c>apiPercentUsed</c>
    /// for everything billed like an API call - the dashboard's own two bars) or, lacking that split,
    /// its plain <c>totalPercentUsed</c> (0-100) or its spend against its allowance in cents; the
    /// cycle ends at <c>billingCycleEnd</c> either way. An unlimited plan without numbers reads as
    /// nothing used. A separate weekly Grok Bot window rides along when the response carries one (see
    /// <see cref="ReadGrokBotWindow"/>) - unrelated to the plan split above, so it is added either
    /// way. Null for any other shape, so the request counters below still get their turn.</summary>
    private static List<UsageWindow>? ReadUsageSummary(JsonElement root)
    {
        if (!root.TryGetProperty("individualUsage", out var individual) || individual.ValueKind != JsonValueKind.Object
            || !individual.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Object)
            return null;

        var billingCycleEnd = ReadDate(root, "billingCycleEnd");
        var autoPercent = TryGetDouble(plan, "autoPercentUsed");
        var apiPercent = TryGetDouble(plan, "apiPercentUsed");

        List<UsageWindow> windows = [];
        if (autoPercent is not null || apiPercent is not null)
        {
            if (autoPercent is not null)
                windows.Add(new UsageWindow("Window_CursorModels", WindowKind.Other, autoPercent.Value, billingCycleEnd, windowMinutes: null));
            if (apiPercent is not null)
                windows.Add(new UsageWindow("Window_OtherModels", WindowKind.Other, apiPercent.Value, billingCycleEnd, windowMinutes: null));
        }
        else
        {
            var percent = TryGetDouble(plan, "totalPercentUsed") ?? PercentOf(plan, "used", "limit");
            if (percent is null && root.TryGetProperty("isUnlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True)
                percent = 0;
            if (percent is null)
                return null;

            windows.Add(new UsageWindow("Window_Month", WindowKind.Other, percent.Value, billingCycleEnd, windowMinutes: null));
        }

        if (ReadGrokBotWindow(root) is { } grokBot)
            windows.Add(grokBot);

        return windows;
    }

    /// <summary>The merged-in <c>grokBot</c> object (see <see cref="CursorDiscoveryScript"/>'s own
    /// extra request) carrying the weekly Grok Bot bar's <c>usagePercent</c> and
    /// <c>nextResetTimestampUtc</c>. Deliberately <see cref="WindowKind.Other"/>, not <see
    /// cref="WindowKind.Weekly"/>, even though it resets weekly - Weekly already means a specific
    /// threshold setting and chart series that belong to the other providers' own weekly window, and
    /// this one must not be folded into that. Null when the field is missing or carries no usable
    /// percentage.</summary>
    private static UsageWindow? ReadGrokBotWindow(JsonElement root)
    {
        if (!root.TryGetProperty("grokBot", out var grokBot) || grokBot.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetDouble(grokBot, "usagePercent") is not { } percent)
            return null;

        return new UsageWindow(
            "Window_GrokBotWeekly", WindowKind.Other, percent, ReadDate(grokBot, "nextResetTimestampUtc"), windowMinutes: 10080);
    }

    private static DateTimeOffset? ReadDate(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        DateTimeOffset? instant = null;
        if (value.ValueKind == JsonValueKind.String)
        {
            instant = SessionLineAge.TryParse(value.GetString(), out var parsed) ? parsed.ToUniversalTime() : null;
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            // Epoch numbers show up both in seconds and in milliseconds.
            instant = UnixTimeConversion.FromUnixSecondsOrMillisecondsOrNull(number);
        }

        return instant is { } found ? UnixTimeConversion.PlausibleOrNull(found) : null;
    }

    private readonly record struct ModelSum(double Percent, TokenUsage? Tokens, UsageAllowance Allowance);

    /// <summary>Sums <c>numTokens</c> across every direct child object of the root that itself
    /// carries a numeric <c>numRequests</c>, and <c>numRequests</c> plus <c>maxRequestUsage</c>
    /// across those of them that carry a numeric limit - a per-model breakdown with more than one model must be
    /// summed across all of them, never read from just the first. Null unless more than one child
    /// object qualifies: <see cref="FindRequestUsageObject"/> already reads exactly one such child
    /// correctly on its own, and a root with none falls through to the cents-based shape below. Also
    /// null when no model carries a positive limit: without a denominator there is no percentage to
    /// show, so no window follows.</summary>
    private static ModelSum? SumAcrossModels(JsonElement root)
    {
        var modelObjects = root.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.Object && HasNumber(property.Value, "numRequests"))
            .Select(property => property.Value)
            .ToList();

        if (modelObjects.Count <= 1)
            return null;

        double requests = 0;
        double limit = 0;
        var hasLimit = false;
        long? tokens = null;

        foreach (var model in modelObjects)
        {
            // Only a model with its own limit counts toward the percentage: requests to a model
            // without one (maxRequestUsage null, unlimited) have no share of that limit to use up.
            if (TryGetDouble(model, "maxRequestUsage") is { } modelLimit)
            {
                requests += TryGetDouble(model, "numRequests") ?? 0;
                limit += modelLimit;
                hasLimit = true;
            }
            if (TryGetInt64(model, "numTokens") is { } modelTokens)
                tokens = (tokens ?? 0) + modelTokens;
        }

        // No guessed percentage without a real denominator: a limit summed from zero, or from no
        // model carrying one at all, yields no window.
        if (!hasLimit || limit <= 0)
            return null;

        return new ModelSum(
            Percent: 100 * requests / limit,
            Tokens: tokens is { } total ? new TokenUsage(total) : null,
            Allowance: new UsageAllowance((long)requests, (long)limit, "Unit.Requests"));
    }

    /// <summary>The first object, in document order, carrying both request-usage numbers - either
    /// the root itself or one level down under a model key (e.g. "gpt-4-1": {...}). Null when
    /// neither the root nor any direct child object carries the pair, so the caller falls back to
    /// the cents-based shape instead.</summary>
    private static JsonElement? FindRequestUsageObject(JsonElement root)
    {
        if (HasNumber(root, "numRequests") && HasNumber(root, "maxRequestUsage"))
            return root;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && HasNumber(property.Value, "numRequests") && HasNumber(property.Value, "maxRequestUsage"))
                return property.Value;
        }

        return null;
    }

    private static bool HasNumber(JsonElement element, string propertyName) => TryGetDouble(element, propertyName) is not null;

    /// <summary>100 x used / limit, or null when either number is missing or the limit is not
    /// positive - a limit of zero or less describes no usable denominator, never a divide-by-zero
    /// guess.</summary>
    private static double? PercentOf(JsonElement element, string usedKey, string limitKey)
    {
        if (TryGetDouble(element, usedKey) is not { } used || TryGetDouble(element, limitKey) is not { } limit)
            return null;
        return limit > 0 ? 100 * used / limit : null;
    }

    /// <summary>A real token count, when the response carries one alongside the usage numbers -
    /// <c>totalTokens</c> first, else an input/output pair summed. Null leaves the window's token
    /// line simply omitted rather than guessed.</summary>
    private static TokenUsage? ReadTokens(JsonElement node)
    {
        if (TryGetInt64(node, "totalTokens") is { } total)
            return new TokenUsage(total);

        if (TryGetInt64(node, "inputTokens") is { } input && TryGetInt64(node, "outputTokens") is { } output)
            return new TokenUsage(input + output);

        return null;
    }

    /// <summary>The response's own <c>startOfMonth</c> plus one month - never today's date, so a
    /// stale cached read still shows the cycle it was actually measured against. Missing,
    /// unparsable or too close to the end of the calendar to add a month yields null rather than a
    /// guessed date.</summary>
    private static DateTimeOffset? ReadResetsAt(JsonElement root) =>
        ReadDate(root, "startOfMonth") is { } start && start <= DateTimeOffset.MaxValue.AddMonths(-1)
            ? start.AddMonths(1)
            : null;
}
