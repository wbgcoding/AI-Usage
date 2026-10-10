using System.Text.Json;
using AiUsage.Models;
using static AiUsage.Providers.Parsing.JsonReading;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Turns one Codex usage response into the same two windows the session files already produce
/// (see <see cref="CodexRateLimitParser"/>, which reads the identical rate-limit block out of a
/// local line). Pure function over the response text, no clock or file access beyond the "now" the
/// caller passes for the relative reset values. Never throws: an unknown shape yields no windows,
/// never a guessed number.
/// </summary>
public static class CodexWebUsageParser
{
    public static IReadOnlyList<UsageWindow> Parse(string json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return [];

            var windows = new List<UsageWindow>(capacity: 2);

            // The web app's own shape: "rate_limit" holding "primary_window"/"secondary_window",
            // each with the window length and the reset in seconds.
            if (TryGetObject(root, "rate_limit") is { } rateLimit)
            {
                AddWindow(windows, TryGetObject(rateLimit, "primary_window"), "Window_FiveHour", WindowKind.FiveHour, now);
                AddWindow(windows, TryGetObject(rateLimit, "secondary_window"), "Window_Weekly", WindowKind.Weekly, now);
                if (windows.Count > 0)
                    return windows;
            }

            // The session files' shape: the block nested under "rate_limits", or the same block at
            // the root.
            var limits = TryGetObject(root, "rate_limits") ?? root;
            AddWindow(windows, TryGetObject(limits, "primary"), "Window_FiveHour", WindowKind.FiveHour, now);
            AddWindow(windows, TryGetObject(limits, "secondary"), "Window_Weekly", WindowKind.Weekly, now);
            return windows;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The account's tier as the usage answer words it (<c>plan_type</c> at the root, e.g.
    /// "plus"), or null - the same document <see cref="Parse"/> already reads.</summary>
    public static string? ParsePlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("plan_type", out var plan) && plan.ValueKind == JsonValueKind.String
                ? plan.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddWindow(
        List<UsageWindow> windows, JsonElement? block, string label, WindowKind kind, DateTimeOffset now)
    {
        if (block is not { } window || TryGetDouble(window, "used_percent") is not { } usedPercent)
            return;

        // A length that is not a positive, int-sized minute count reads as unknown; the percentage
        // stays valid on its own.
        long? minutes = TryGetInt32(window, "window_minutes");
        minutes ??= TryGetInt64(window, "limit_window_seconds") is { } seconds ? seconds / 60 : null;
        var windowMinutes = minutes is > 0 and <= int.MaxValue ? (int?)minutes : null;
        windows.Add(new UsageWindow(label, kind, usedPercent, ResetsAt(window, now), windowMinutes));
    }

    // No usage window runs longer than a year; anything beyond reads as no reset at all.
    private const long MaxCountdownSeconds = 400L * 24 * 60 * 60;

    /// <summary>Every spelling the rate-limit block is known to use: an absolute unix instant
    /// (<c>resets_at</c> in the session files, <c>reset_at</c> on the web) and a relative countdown
    /// (<c>resets_in_seconds</c>, <c>reset_after_seconds</c>). A countdown that has already run out
    /// describes a window that is over, not one resetting right now, so it is dropped rather than
    /// shown as a past instant.</summary>
    private static DateTimeOffset? ResetsAt(JsonElement window, DateTimeOffset now)
    {
        if ((TryGetInt64(window, "resets_at") ?? TryGetInt64(window, "reset_at")) is { } unixTime)
        {
            // An out-of-range number reads as no reset, never a throw.
            var instant = UnixTimeConversion.FromUnixSecondsOrMillisecondsOrNull(unixTime);
            return instant is { } value && value > now ? UnixTimeConversion.PlausibleOrNull(value) : null;
        }

        // Bounded before the addition: an absurd countdown would otherwise overflow DateTimeOffset.
        if ((TryGetInt64(window, "resets_in_seconds") ?? TryGetInt64(window, "reset_after_seconds")) is { } seconds
            && seconds > 0 && seconds <= MaxCountdownSeconds)
            return now.AddSeconds(seconds);

        return null;
    }
}
