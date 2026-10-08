using System.Globalization;
using System.Linq;
using System.Text.Json;
using AiUsage.Models;

namespace AiUsage.Providers.Parsing;

public enum ClaudeUsageOutcome { Ok, Blocked, Failed }

/// <summary>Outcome of one Claude web-usage fetch: either windows worth showing, a challenge page
/// (<see cref="ClaudeUsageOutcome.Blocked"/>), or a shape this parser does not recognise.</summary>
public sealed record ClaudeUsageResult(ClaudeUsageOutcome Outcome, IReadOnlyList<UsageWindow> Windows)
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
            if (TryReadWindow(root, "seven_day", "Window_Weekly", WindowKind.Weekly, out var weekly))
                windows.Add(weekly);
            windows.AddRange(ReadScopedWindows(root));

            return windows.Count > 0
                ? new ClaudeUsageResult(ClaudeUsageOutcome.Ok, windows)
                : ClaudeUsageResult.Failed;
        }
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
                // A mixed API-version fleet may hand back milliseconds instead of seconds; a value
                // this large can never be a plausible seconds count, so treat it as milliseconds.
                var seconds = rawNumber >= 100_000_000_000 ? rawNumber / 1000 : rawNumber;
                var converted = UnixTimeConversion.FromUnixSecondsOrNull(seconds);
                return converted is { } instant ? UnixTimeConversion.PlausibleOrNull(instant) : null;
            }
        }
        return null;
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
