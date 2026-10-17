using System.Text.Json;
using static AiUsage.Providers.Parsing.JsonReading;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// One Codex "token_count" event line, reduced to what the tile needs. Either block can be
/// missing: newer session files write a JSON null for a window they have no number for, so a
/// line may carry the five-hour window alone, the weekly window alone, or both.
/// </summary>
public sealed record CodexRateLimits(
    double? PrimaryUsedPercent,
    int? PrimaryWindowMinutes,
    DateTimeOffset? PrimaryResetsAt,
    double? SecondaryUsedPercent,
    int? SecondaryWindowMinutes,
    DateTimeOffset? SecondaryResetsAt,
    string? PlanType,
    DateTimeOffset Timestamp,
    // Cumulative for the current rollout file, not for the resetting rate-limit window - the two
    // are different concepts that happen to share a line. Null when the
    // event carries no usage info yet (e.g. the very first line of a fresh session).
    long? TotalTokens = null);

/// <summary>
/// Pure line parser, no file or clock access, so it is testable against inline fixtures alone.
/// Never throws: any shape it does not recognise is simply not a match.
/// </summary>
public static class CodexRateLimitParser
{
    public static bool TryParse(string line, out CodexRateLimits? result) => TryParse(line, out result, notes: null);

    /// <summary>The change notes of one line alone (see the <c>TryParse</c> overload
    /// that collects them): empty for a line that looks as expected and for every line that is no
    /// rate-limit event at all.</summary>
    public static IReadOnlyList<string> ChangeNotes(string line)
    {
        var notes = new List<string>();
        _ = TryParse(line, out _, notes);
        return notes;
    }

    /// <summary>Same parse; <paramref name="notes"/> additionally collects what in a rate-limit block
    /// no longer matches what this parser was written for (an unknown window, a window without
    /// <c>used_percent</c>, an unusual window length - see <see cref="CodexWindowKinds.CollectNotes"/>),
    /// so the caller can report it. Names and lengths only.</summary>
    public static bool TryParse(string line, out CodexRateLimits? result, List<string>? notes)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (TryGetString(root, "type") != "event_msg")
                return false;
            if (!root.TryGetProperty("payload", out var payload))
                return false;
            if (TryGetString(payload, "type") != "token_count")
                return false;
            if (!payload.TryGetProperty("rate_limits", out var rateLimits)
                || rateLimits.ValueKind != JsonValueKind.Object)
                return false;

            if (notes is not null)
                CodexWindowKinds.CollectNotes(rateLimits, "rate_limits", ["primary", "secondary"], notes);

            // A null block is a normal shape, not a broken line: read what is there and let the
            // "at least one percentage" check below decide whether the line is usable at all.
            var primary = TryGetObject(rateLimits, "primary");
            var secondary = TryGetObject(rateLimits, "secondary");
            if (primary is null && secondary is null)
                return false;

            if (TryGetString(root, "timestamp") is not { } timestampText
                || !SessionLineAge.TryParse(timestampText, out var timestamp))
                return false;

            var primaryUsedPercent = TryGetDouble(primary, "used_percent");
            var secondaryUsedPercent = TryGetDouble(secondary, "used_percent");
            if (primaryUsedPercent is null && secondaryUsedPercent is null)
                return false;

            result = new CodexRateLimits(
                PrimaryUsedPercent: primaryUsedPercent,
                PrimaryWindowMinutes: CodexWindowKinds.PlausibleMinutesOrNull(TryGetInt32(primary, "window_minutes")),
                PrimaryResetsAt: TryGetUnixTime(primary, "resets_at"),
                SecondaryUsedPercent: secondaryUsedPercent,
                SecondaryWindowMinutes: CodexWindowKinds.PlausibleMinutesOrNull(TryGetInt32(secondary, "window_minutes")),
                SecondaryResetsAt: TryGetUnixTime(secondary, "resets_at"),
                PlanType: TryGetString(rateLimits, "plan_type"),
                Timestamp: timestamp,
                TotalTokens: TryGetTotalTokens(payload));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    // "info" is null on some lines (e.g. no usage measured yet); "total_token_usage" can be an
    // empty object early in a session. Both are "not measured", not zero - the tile must be able
    // to tell the two apart and show nothing rather than a false "0 tokens".
    private static long? TryGetTotalTokens(JsonElement payload) =>
        payload.TryGetProperty("info", out var info)
        && info.ValueKind == JsonValueKind.Object
        && info.TryGetProperty("total_token_usage", out var totalTokenUsage)
        && totalTokenUsage.ValueKind == JsonValueKind.Object
        && totalTokenUsage.TryGetProperty("total_tokens", out var totalTokens)
        && totalTokens.ValueKind == JsonValueKind.Number
        && totalTokens.TryGetInt64(out var tokens)
            ? tokens
            : null;

    private static DateTimeOffset? TryGetUnixTime(JsonElement? element, string propertyName)
    {
        if (element is not { } parent || !parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds))
            return null;

        return UnixTimeConversion.FromUnixSecondsOrMillisecondsOrNull(seconds) is { } instant
            ? UnixTimeConversion.PlausibleOrNull(instant)
            : null;
    }
}
