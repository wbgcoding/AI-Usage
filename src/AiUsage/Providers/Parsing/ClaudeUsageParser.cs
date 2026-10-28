using System.Globalization;
using System.Linq;
using System.Text.Json;
using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Providers.Parsing;

public enum ClaudeUsageOutcome { Ok, Blocked, Failed }

/// <summary>Outcome of one Claude web-usage fetch: either windows worth showing, a challenge page
/// (<see cref="ClaudeUsageOutcome.Blocked"/>), or a shape this parser does not recognise.</summary>
public sealed record ClaudeUsageResult(ClaudeUsageOutcome Outcome, IReadOnlyList<UsageWindow> Windows, ClaudeExtraUsage? Extra = null)
{
    public static readonly ClaudeUsageResult Failed = new(ClaudeUsageOutcome.Failed, []);
    public static readonly ClaudeUsageResult Blocked = new(ClaudeUsageOutcome.Blocked, []);
}

/// <summary>
/// Pure function over the usage endpoint's response text - no network, no WebView2, so it is
/// testable against inline fixtures alone. The exact field names it looks for are an open question
/// until seen once on a real, signed-in response: an unrecognised shape yields
/// <see cref="ClaudeUsageOutcome.Failed"/>, never a guessed percentage.
/// </summary>
public static class ClaudeUsageParser
{
    private static readonly string[] PercentKeys = ["utilization", "used_percent", "usedPercent", "percentUsed"];
    private static readonly string[] ResetKeys = ["resets_at", "resetsAt"];
    private static readonly string[] TokenKeys = ["used_tokens", "token_usage", "total_tokens"];

    // A future API returning many scoped limits must never grow a tile without bound.
    private const int MaxScopedWindows = 4;

    public static ClaudeUsageResult Parse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return ClaudeUsageResult.Failed;

        // A challenge or interstitial page must never read as this app's own failure: it can come
        // back as HTML, or as a JSON error body wearing a JSON content-type.
        if (LooksLikeChallenge(responseText))
            return ClaudeUsageResult.Blocked;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseText);
        }
        catch (JsonException)
        {
            return ClaudeUsageResult.Failed;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ClaudeUsageResult.Failed;

            var windows = new List<UsageWindow>();

            if (TryReadWindow(root, "five_hour", "Window_FiveHour", WindowKind.FiveHour, out var fiveHour))
                windows.Add(fiveHour);
            if (TryReadWindow(root, "seven_day", "Window_Weekly", WindowKind.Weekly, out var weekly)
                || TryReadAllModelsWeeklyLimit(root, out weekly))
                windows.Add(weekly);
            windows.AddRange(ReadScopedWindows(root));

            return windows.Count > 0
                ? new ClaudeUsageResult(ClaudeUsageOutcome.Ok, windows, ReadExtraUsage(root))
                : ClaudeUsageResult.Failed;
        }
    }

    /// <summary>The "extra_usage" block: <c>is_enabled</c>, <c>monthly_limit</c> and <c>used_credits</c> in
    /// minor units (cents), <c>utilization</c> in percent, an optional <c>currency</c> code (USD when
    /// absent). Null when the answer has no such object, so a reader can tell "no block" from "switched off".</summary>
    private static ClaudeExtraUsage? ReadExtraUsage(JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out var node) || node.ValueKind != JsonValueKind.Object)
            return null;

        var enabled = node.TryGetProperty("is_enabled", out var enabledEl) && enabledEl.ValueKind == JsonValueKind.True;
        var currency = node.TryGetProperty("currency", out var currencyEl) && currencyEl.ValueKind == JsonValueKind.String
            && currencyEl.GetString() is { Length: > 0 } code ? code.ToUpperInvariant() : "USD";
        return new ClaudeExtraUsage(
            enabled, ReadNumber(node, "utilization"), MinorToMajor(ReadNumber(node, "used_credits")),
            MinorToMajor(ReadNumber(node, "monthly_limit")), currency);
    }

    private static double? ReadNumber(JsonElement node, string key) =>
        node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static decimal? MinorToMajor(double? minor) => minor is { } cents ? (decimal)cents / 100m : null;

    // The window names and limit kinds this read was written for. Anything else the answer carries
    // is named in ChangeNotes instead of being skipped without a trace.
    private static readonly string[] KnownWindowKeys = ["five_hour", "seven_day"];

    // Windows the answer is known to carry beside the two shown ones: the per-model and per-app weekly
    // windows and the extra-usage block. They are read elsewhere or left out on purpose, so a number
    // in them is no change.
    private static readonly string[] KnownUnshownWindowKeys =
        ["seven_day_opus", "seven_day_sonnet", "seven_day_haiku", "seven_day_oauth_apps", "extra_usage", "iguana_necktie"];

    // "weekly_all" is the all-models weekly limit, the same number as the "seven_day" window: it is
    // read only when that window is missing, so the weekly row never shows twice.
    private static readonly string[] KnownLimitKinds = ["weekly_scoped", "weekly_all", "session"];

    /// <summary>
    /// What in the answer no longer matches what this parser was written for: a window object under a
    /// name it does not know, a known window without any of the percent fields it reads, a "limits"
    /// entry of an unknown kind, a scoped weekly limit without a percent or a model name. Each note is
    /// one log line telling that the read needs adapting; empty for an answer that looks as expected
    /// (and for a challenge page or text that is no JSON object, which are not a change of shape).
    /// Names only, never a figure or an account detail.
    /// </summary>
    public static IReadOnlyList<string> ChangeNotes(string responseText)
    {
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(responseText) || LooksLikeChallenge(responseText))
            return notes;

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return notes;

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var name = ReadChangeLog.SafeName(property.Name);
                var hasPercentField = PercentKeys.Any(key => property.Value.TryGetProperty(key, out _));
                if (KnownWindowKeys.Contains(property.Name))
                {
                    if (!hasPercentField)
                        notes.Add($"the window '{name}' holds none of the known percent fields (keys: {ReadChangeLog.SafeNames(property.Value.EnumerateObject().Select(p => p.Name))}).");
                }
                else if (!KnownUnshownWindowKeys.Contains(property.Name)
                    && PercentKeys.Any(key => property.Value.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number))
                {
                    notes.Add($"the answer holds an unknown window '{name}', which is not shown.");
                }
            }

            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in limits.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var kind = entry.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String ? kindEl.GetString() : null;
                    if (kind is null)
                        notes.Add("a limits entry names no kind, so it is not shown.");
                    else if (!KnownLimitKinds.Contains(kind))
                        notes.Add($"a limits entry of unknown kind '{ReadChangeLog.SafeName(kind)}' is not shown.");
                    else if (kind == "weekly_scoped")
                        AddScopedLimitNotes(entry, notes);
                }
            }
        }
        catch (JsonException)
        {
        }

        return notes.Distinct().ToList();
    }

    private static void AddScopedLimitNotes(JsonElement entry, List<string> notes)
    {
        if (!entry.TryGetProperty("percent", out var percentEl) || percentEl.ValueKind != JsonValueKind.Number)
            notes.Add("a weekly_scoped limit carries no numeric percent, so it is not shown.");

        var hasModelName = entry.TryGetProperty("scope", out var scopeEl) && scopeEl.ValueKind == JsonValueKind.Object
            && scopeEl.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.Object
            && modelEl.TryGetProperty("display_name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(nameEl.GetString());
        if (!hasModelName)
            notes.Add("a weekly_scoped limit names no model, so it is not shown.");
    }

    /// <summary>True for an HTML page (leading '<' after stripping a byte-order mark and
    /// whitespace) or a known challenge/interstitial phrase found in the first 4096 characters -
    /// covers a Cloudflare or similar page even when it answers with a JSON content-type.</summary>
    private static bool LooksLikeChallenge(string text)
    {
        var trimmed = text.TrimStart('﻿').TrimStart();
        if (trimmed.StartsWith('<'))
            return true;

        var sample = trimmed.Length > 4096 ? trimmed[..4096] : trimmed;
        return sample.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("Attention Required! | Cloudflare", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadWindow(JsonElement root, string key, string label, WindowKind kind, out UsageWindow window)
    {
        window = null!;
        if (!root.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object)
            return false;

        double? percent = null;
        foreach (var percentKey in PercentKeys)
        {
            if (node.TryGetProperty(percentKey, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var parsedPercent))
            {
                percent = parsedPercent;
                break;
            }
        }
        if (percent is null)
            return false;

        window = new UsageWindow(
            label, kind, percent.Value, ReadResetsAt(node), windowMinutes: null,
            tokens: ReadTokens(node), allowance: ReadAllowance(node));
        return true;
    }

    /// <summary>Reads a real token count alongside the window's percentage, when the response
    /// carries one - the first of <see cref="TokenKeys"/> present as a number wins; null when none
    /// of them is, which leaves the tile's token segment simply omitted rather than guessed.</summary>
    private static TokenUsage? ReadTokens(JsonElement node)
    {
        foreach (var tokenKey in TokenKeys)
        {
            if (node.TryGetProperty(tokenKey, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var total))
                return new TokenUsage(total);
        }
        return null;
    }

    // Checked in this order; a real response carries at most one of these pairs.
    private static readonly (string Used, string Total, string UnitKey)[] AllowancePairs =
    [
        ("used", "limit", "Unit.Requests"),
        ("used_requests", "request_limit", "Unit.Requests"),
        ("used_messages", "message_limit", "Unit.Messages"),
    ];

    /// <summary>Reads the window's used/total denominator, when the response carries one - null
    /// when it carries only the raw percentage, or when the matching pair's total is not positive.</summary>
    private static UsageAllowance? ReadAllowance(JsonElement node)
    {
        foreach (var (usedKey, totalKey, unitKey) in AllowancePairs)
        {
            if (!node.TryGetProperty(usedKey, out var usedEl) || !node.TryGetProperty(totalKey, out var totalEl))
                continue;

            if (usedEl.ValueKind != JsonValueKind.Number || !usedEl.TryGetInt64(out var used))
                return null;
            if (totalEl.ValueKind != JsonValueKind.Number || !totalEl.TryGetInt64(out var total) || total <= 0)
                return null;

            return new UsageAllowance(used, total, unitKey);
        }
        return null;
    }

    private static DateTimeOffset? ReadResetsAt(JsonElement node)
    {
        foreach (var resetKey in ResetKeys)
        {
            if (!node.TryGetProperty(resetKey, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (text is null || !DateTimeOffset.TryParse(
                        text, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var parsed))
                    continue;

                return UnixTimeConversion.PlausibleOrNull(parsed.ToUniversalTime());
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var rawNumber))
            {
                // A mixed API-version fleet may hand back milliseconds instead of seconds.
                var converted = UnixTimeConversion.FromUnixSecondsOrMillisecondsOrNull(rawNumber);
                return converted is { } instant ? UnixTimeConversion.PlausibleOrNull(instant) : null;
            }
        }
        return null;
    }

    /// <summary>The all-models weekly limit from the "limits" array, as the weekly window, for an
    /// answer that carries it there instead of under "seven_day". False when there is none or it has
    /// no numeric percent.</summary>
    private static bool TryReadAllModelsWeeklyLimit(JsonElement root, out UsageWindow window)
    {
        window = null!;
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String
                || kindEl.GetString() != "weekly_all"
                || !entry.TryGetProperty("percent", out var percentEl) || percentEl.ValueKind != JsonValueKind.Number
                || !percentEl.TryGetDouble(out var percent))
                continue;

            window = new UsageWindow("Window_Weekly", WindowKind.Weekly, percent, ReadResetsAt(entry), windowMinutes: null);
            return true;
        }
        return false;
    }

    /// <summary>Reads the response's per-model weekly caps from its "limits" array - one <see
    /// cref="WindowKind.Other"/> window per model-scoped entry, capped and sorted so a future API
    /// returning many models cannot grow a tile unbounded. An entry that is not weekly-scoped, or
    /// carries no percent or display name, is skipped rather than shown with an invented label.</summary>
    private static IEnumerable<UsageWindow> ReadScopedWindows(JsonElement root)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            yield break;

        var scoped = new List<(double Percent, UsageWindow Window)>();
        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            if (!entry.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String
                || kindEl.GetString() != "weekly_scoped")
                continue;
            if (!entry.TryGetProperty("percent", out var percentEl) || percentEl.ValueKind != JsonValueKind.Number
                || !percentEl.TryGetDouble(out var percent))
                continue;

            string? displayName = null;
            if (entry.TryGetProperty("scope", out var scopeEl) && scopeEl.ValueKind == JsonValueKind.Object
                && scopeEl.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.Object
                && modelEl.TryGetProperty("display_name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                displayName = nameEl.GetString();
            }
            if (string.IsNullOrEmpty(displayName))
                continue;

            scoped.Add((percent, new UsageWindow(displayName, WindowKind.Other, percent, ReadResetsAt(entry), windowMinutes: null)));
        }

        foreach (var (_, window) in scoped.OrderByDescending(s => s.Percent).Take(MaxScopedWindows))
            yield return window;
    }
}
