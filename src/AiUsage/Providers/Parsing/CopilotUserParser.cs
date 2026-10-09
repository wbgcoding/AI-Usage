using System.Text.Json;
using AiUsage.Models;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Pure function over the GitHub CLI's <c>copilot_internal/user</c> response - no process, so it is
/// tested against inline fixtures alone. The response carries a <c>quota_snapshots</c> object with a
/// bucket per quota id (chat, completions, premium interactions), each with a
/// <c>percent_remaining</c>, and a shared monthly <c>quota_reset_date_utc</c>. Each bucket that has a
/// real quota becomes one "Other" window; an unlimited or absent bucket is skipped rather than shown
/// as an invented number.
/// </summary>
public static class CopilotUserParser
{
    // Label keys (resolved by StatusTextMap) for the known quota ids; an unknown id falls back to its
    // own text so a future bucket still shows rather than being dropped.
    private static readonly Dictionary<string, string> KnownLabels = new(StringComparer.Ordinal)
    {
        ["chat"] = "Window_CopilotChat",
        ["completions"] = "Window_CopilotCompletions",
        ["premium_interactions"] = "Window_CopilotPremium",
    };

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
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("quota_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Object)
                return [];

            var resetsAt = ReadResetDate(root);

            var windows = new List<UsageWindow>();
            foreach (var snapshot in snapshots.EnumerateObject())
            {
                if (snapshot.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var bucket = snapshot.Value;

                // A bucket the account does not actually have, or an unlimited one, carries no
                // meaningful percentage to show.
                if (bucket.TryGetProperty("has_quota", out var hasQuota) && hasQuota.ValueKind == JsonValueKind.False)
                    continue;
                if (bucket.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True)
                    continue;
                if (!bucket.TryGetProperty("percent_remaining", out var remainingEl)
                    || remainingEl.ValueKind != JsonValueKind.Number || !remainingEl.TryGetDouble(out var remaining))
                    continue;
                // A remaining percentage outside 0..100 cannot describe a real quota - the bucket is
                // dropped rather than clamped into a false number.
                if (remaining is < 0 or > 100)
                    continue;

                var label = KnownLabels.TryGetValue(snapshot.Name, out var known) ? known : snapshot.Name;
                windows.Add(new UsageWindow(label, WindowKind.Other, 100 - remaining, resetsAt, windowMinutes: null));
            }

            return windows;
        }
    }

    /// <summary>The signed-in GitHub handle, when the response names one - the same document
    /// <see cref="Parse"/> already reads, never a separate call and never an email address.</summary>
    public static string? ParseLogin(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseText);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("login", out var loginEl) && loginEl.ValueKind == JsonValueKind.String
                && loginEl.GetString() is { Length: > 0 } login
                ? login
                : null;
        }
    }

    /// <summary>The account's tier as the same response words it (<c>copilot_plan</c>, e.g.
    /// "individual_pro" or "business"), or null - never a separate call.</summary>
    public static string? ParsePlan(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("copilot_plan", out var plan) && plan.ValueKind == JsonValueKind.String
                ? plan.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadResetDate(JsonElement root)
    {
        if (!root.TryGetProperty("quota_reset_date_utc", out var resetEl) || resetEl.ValueKind != JsonValueKind.String)
            return null;
        return SessionLineAge.TryParse(resetEl.GetString(), out var parsed)
            ? UnixTimeConversion.PlausibleOrNull(parsed.ToUniversalTime())
            : null;
    }
}
