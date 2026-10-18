using System.Text.Json;
using AiUsage.Models;
using AiUsage.Services;

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
    // own words so a future bucket still shows rather than being dropped.
    private static readonly Dictionary<string, string> KnownLabels = new(StringComparer.Ordinal)
    {
        ["chat"] = "Window_CopilotChat",
        ["completions"] = "Window_CopilotCompletions",
        ["premium_interactions"] = "Window_CopilotPremium",
    };

    /// <summary>A bucket id such as <c>code_review</c> as words: it would otherwise match the shape of an
    /// internal key and resolve to nothing on screen.</summary>
    private static string Readable(string id)
    {
        var words = id.Replace('_', ' ').Trim();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>Everything one response holds, read from a single parse of it.</summary>
    public sealed record Reading(
        IReadOnlyList<UsageWindow> Windows,
        string? Plan,
        string? Login,
        IReadOnlyList<string> ChangeNotes,
        bool HasQuotaSnapshots)
    {
        public static readonly Reading Empty = new([], null, null, [], false);
    }

    /// <summary>
    /// Parses the response once: its windows, plan, login, the change notes and whether the
    /// <c>quota_snapshots</c> object is there. A response that is empty or not JSON reads as
    /// <see cref="Reading.Empty"/>. The change notes name what no longer matches what this parser
    /// was written for: a quota id it has no label for (shown under its own name), a quota that is
    /// neither unlimited nor absent yet has no numeric <c>percent_remaining</c>, or one whose
    /// percentage lies outside 0..100 (both skipped). Ids only, never a figure or an account detail.
    /// </summary>
    public static Reading Read(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return Reading.Empty;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseText);
        }
        catch (JsonException)
        {
            return Reading.Empty;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Reading.Empty;

            var plan = root.TryGetProperty("copilot_plan", out var planEl) && planEl.ValueKind == JsonValueKind.String
                ? planEl.GetString()
                : null;
            var login = root.TryGetProperty("login", out var loginEl) && loginEl.ValueKind == JsonValueKind.String
                && loginEl.GetString() is { Length: > 0 } handle
                ? handle
                : null;
            if (!root.TryGetProperty("quota_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Object)
                return new Reading([], plan, login, [], false);

            var resetsAt = ReadResetDate(root);
            var windows = new List<UsageWindow>();
            var notes = new List<string>();
            foreach (var snapshot in snapshots.EnumerateObject())
            {
                if (snapshot.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var bucket = snapshot.Value;

                var id = ReadChangeLog.SafeName(snapshot.Name);
                var knownLabel = KnownLabels.TryGetValue(snapshot.Name, out var known);
                if (!knownLabel)
                    notes.Add($"new quota id '{id}' found; it is shown under its own name, check that this is right.");

                // A bucket the account does not actually have, or an unlimited one, carries no
                // meaningful percentage to show.
                if (bucket.TryGetProperty("has_quota", out var hasQuota) && hasQuota.ValueKind == JsonValueKind.False)
                    continue;
                if (bucket.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True)
                    continue;
                if (!bucket.TryGetProperty("percent_remaining", out var remainingEl)
                    || remainingEl.ValueKind != JsonValueKind.Number || !remainingEl.TryGetDouble(out var remaining))
                {
                    notes.Add($"quota '{id}' has no numeric percent_remaining, so it is not shown.");
                    continue;
                }

                // A remaining percentage outside 0..100 cannot describe a real quota - the bucket is
                // dropped rather than clamped into a false number.
                if (remaining is < 0 or > 100)
                {
                    notes.Add($"quota '{id}' reports a percentage outside 0 to 100, so it is not shown.");
                    continue;
                }

                var label = knownLabel ? known! : Readable(snapshot.Name);
                windows.Add(new UsageWindow(label, WindowKind.Other, 100 - remaining, resetsAt, windowMinutes: null));
            }

            return new Reading(windows, plan, login, notes, true);
        }
    }

    public static IReadOnlyList<string> ChangeNotes(string responseText) => Read(responseText).ChangeNotes;

    /// <summary>True when the response holds the <c>quota_snapshots</c> object this parser reads, even
    /// if every bucket in it is unlimited or absent.</summary>
    public static bool HasQuotaSnapshots(string responseText) => Read(responseText).HasQuotaSnapshots;

    public static IReadOnlyList<UsageWindow> Parse(string responseText) => Read(responseText).Windows;

    /// <summary>The signed-in GitHub handle, when the response names one, never an email address.</summary>
    public static string? ParseLogin(string responseText) => Read(responseText).Login;

    /// <summary>The account's tier as the response words it (<c>copilot_plan</c>, e.g.
    /// "individual_pro" or "business"), or null.</summary>
    public static string? ParsePlan(string responseText) => Read(responseText).Plan;

    private static DateTimeOffset? ReadResetDate(JsonElement root)
    {
        if (!root.TryGetProperty("quota_reset_date_utc", out var resetEl) || resetEl.ValueKind != JsonValueKind.String)
            return null;
        return SessionLineAge.TryParse(resetEl.GetString(), out var parsed)
            ? UnixTimeConversion.PlausibleOrNull(parsed.ToUniversalTime())
            : null;
    }
}
