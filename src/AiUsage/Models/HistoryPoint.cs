using System.Text.Json.Serialization;

namespace AiUsage.Models;

/// <summary>
/// One line of a provider's history file. Short JSON keys on purpose - a
/// default retention of a year runs to roughly 100k lines per provider.
/// </summary>
public sealed record HistoryPoint(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("w")] WindowKind Window,
    [property: JsonPropertyName("p")] double Percent,
    [property: JsonPropertyName("r")] DateTimeOffset? ResetsAt,
    // 1 = compacted to one value per hour, 2 = one value per day, null = raw.
    [property: JsonPropertyName("c")] int? CompactionLevel = null,
    // Real token count measured alongside this point; null when the provider
    // had no local source for it at the time.
    [property: JsonPropertyName("tok")] long? Tokens = null,
    // The window's own resource key (see UsageWindow.Label), e.g. "Window_CursorModels" - lets more
    // than one WindowKind.Other series for the same provider be told apart (Cursor's per-model bars,
    // its Grok Bot bar); null for a line written before this field existed, or for a provider that
    // never had more than one window of that kind. Pre-release, no migration: an old line without it
    // still reads, it simply carries no label.
    [property: JsonPropertyName("l")] string? Label = null);
