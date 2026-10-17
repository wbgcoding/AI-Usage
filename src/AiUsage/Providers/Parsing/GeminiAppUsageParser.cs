using System.Text.Json;
using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Pure function over the payload of the Gemini app's usage answer (the <c>jSf9Qc</c> call of
/// gemini.google.com/usage, already cut out of its batch envelope by <see cref="GeminiDiscoveryScript"/>).
/// The payload is positional JSON: <c>[2, entries, false]</c>, each entry
/// <c>[unknownNumber, usedFraction, kind, [[resetEpochSeconds, nanos]]]</c> with kind 1 the five-hour
/// window and kind 2 the weekly one, in no fixed order. A used fraction left out (a default value in
/// the answer's encoding) is 0. An unrecognised shape yields no windows rather than a guessed number.
/// </summary>
public static class GeminiAppUsageParser
{
    private const int FiveHourKind = 1;
    private const int WeeklyKind = 2;

    public static IReadOnlyList<UsageWindow> Parse(string payloadJson)
    {
        var windows = new List<UsageWindow>(capacity: 2);
        using var document = TryParse(payloadJson);
        if (document is null || EntriesOf(document.RootElement) is not { } entries)
            return windows;

        var seen = new HashSet<int>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array)
                continue;
            if (ReadKind(entry) is not { } kind || kind is not (FiveHourKind or WeeklyKind))
                continue;
            if (ReadUsedFraction(entry) is not { } used)
                continue;
            // The first entry of a kind wins; a repeat is reported by ChangeNotes, never shown twice.
            if (!seen.Add(kind))
                continue;

            var (label, windowKind) = kind == FiveHourKind
                ? ("Window_FiveHour", WindowKind.FiveHour)
                : ("Window_Weekly", WindowKind.Weekly);
            windows.Add(new UsageWindow(label, windowKind, used * 100, ReadResetTime(entry), windowMinutes: null));
        }

        return windows;
    }

    /// <summary>
    /// What in the answer no longer matches what this parser was written for: an unknown shape, a
    /// window kind other than the five-hour and weekly ones, a used fraction that is no number between
    /// 0 and 1, a kind named twice, or an answer that holds neither window. Each note is one log line
    /// telling that the read needs adapting; empty while everything looks as expected. Kinds and
    /// names only, never a figure.
    /// </summary>
    public static IReadOnlyList<string> ChangeNotes(string payloadJson)
    {
        var notes = new List<string>();
        using var document = TryParse(payloadJson);
        if (document is null || EntriesOf(document.RootElement) is not { } entries)
        {
            notes.Add("the usage answer has an unknown shape, so no usage window is shown.");
            return notes;
        }

        var seen = new HashSet<int>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array)
                continue;
            if (ReadKind(entry) is not { } kind)
            {
                notes.Add($"the usage answer names an unknown window kind '{ReadChangeLog.SafeName(KindName(entry))}', which is not shown.");
                continue;
            }
            if (kind is not (FiveHourKind or WeeklyKind))
            {
                notes.Add($"the usage answer names an unknown window kind '{kind}', which is not shown.");
                continue;
            }
            if (ReadUsedFraction(entry) is null)
            {
                notes.Add($"the window of kind '{kind}' has a used fraction that is no number between 0 and 1, so it is not shown.");
                continue;
            }
            if (!seen.Add(kind))
                notes.Add($"the usage answer names the window kind '{kind}' more than once; only the first is shown.");
        }

        if (Parse(payloadJson).Count == 0)
            notes.Add("the usage answer holds no five-hour or weekly window, so no usage window is shown.");

        return notes.Distinct().ToList();
    }

    private static JsonDocument? TryParse(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;
        try
        {
            return JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The entries array of <c>[2, entries, false]</c>, or null for any other shape.</summary>
    private static JsonElement? EntriesOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2 && root[1].ValueKind == JsonValueKind.Array
            ? root[1]
            : null;

    /// <summary>The integer kind of an entry, or null when it is missing or no whole number.</summary>
    private static int? ReadKind(JsonElement entry) =>
        entry.GetArrayLength() > 2 && entry[2].ValueKind == JsonValueKind.Number && entry[2].TryGetInt32(out var kind)
            ? kind
            : null;

    /// <summary>The kind as text for a note about a kind that is no whole number; names only.</summary>
    private static string? KindName(JsonElement entry) =>
        entry.GetArrayLength() > 2 && entry[2].ValueKind == JsonValueKind.String ? entry[2].GetString() : null;

    /// <summary>The used fraction, 0 when the answer leaves it out (null or absent), and null when it is
    /// no number between 0 and 1 - the entry is dropped rather than clamped into a false 0% or 100%.</summary>
    private static double? ReadUsedFraction(JsonElement entry)
    {
        if (entry.GetArrayLength() < 2 || entry[1].ValueKind == JsonValueKind.Null)
            return 0;
        if (entry[1].ValueKind != JsonValueKind.Number || !entry[1].TryGetDouble(out var used) || used is < 0 or > 1)
            return null;
        return used;
    }

    private static DateTimeOffset? ReadResetTime(JsonElement entry)
    {
        if (entry.GetArrayLength() <= 3 || entry[3].ValueKind != JsonValueKind.Array || entry[3].GetArrayLength() == 0)
            return null;
        var reset = entry[3][0];
        if (reset.ValueKind != JsonValueKind.Array || reset.GetArrayLength() == 0
            || reset[0].ValueKind != JsonValueKind.Number || !reset[0].TryGetInt64(out var seconds))
            return null;
        return UnixTimeConversion.FromUnixSecondsOrNull(seconds) is { } instant
            ? UnixTimeConversion.PlausibleOrNull(instant)
            : null;
    }
}
