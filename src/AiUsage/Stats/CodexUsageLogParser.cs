using System.Globalization;
using System.Text.Json;
using AiUsage.Providers.Parsing;

namespace AiUsage.Stats;

/// <summary>
/// Stateful line-by-line parser for one Codex rollout file (<c>~/.codex/sessions/**/rollout-*.jsonl</c>).
/// Stateful because a rollout file's own token count is cumulative for the whole session, not a
/// per-turn delta: this parser remembers the running totals it has already turned into
/// deltas, and each new "token_count" line reports only the difference since the last one. The
/// active model is tracked the same way, from the plain "model" field a "turn_context" line
/// carries whenever it changes - a rollout file has no per-line model field of its own.
///
/// One instance is meant to be fed every line of one file, in file order, once. Resuming a
/// partially-read file starts a fresh instance seeded with the previous run's saved totals and
/// model (see <see cref="StatsSourceFileState"/>) instead of the default zero/empty state, so the
/// first delta after a resume is still correct.
/// </summary>
public sealed class CodexUsageLogParser
{
    public readonly record struct UsageEvent(
        DateTimeOffset Timestamp, string Model, long InputTokens, long OutputTokens, long CacheCreationTokens, long CacheReadTokens,
        string Effort = "");

    private string _model;
    private string _effort;
    private long _cumulativeInput;
    private long _cumulativeOutput;
    private long _cumulativeCacheCreation;
    private long _cumulativeCacheRead;

    public CodexUsageLogParser(
        string model = "", long cumulativeInput = 0, long cumulativeOutput = 0, long cumulativeCacheCreation = 0, long cumulativeCacheRead = 0,
        string effort = "")
    {
        _model = model;
        _effort = effort;
        _cumulativeInput = cumulativeInput;
        _cumulativeOutput = cumulativeOutput;
        _cumulativeCacheCreation = cumulativeCacheCreation;
        _cumulativeCacheRead = cumulativeCacheRead;
    }

    /// <summary>The running totals, active model and active effort level as they stand after every
    /// line fed so far - what a caller saves into <see cref="StatsSourceFileState"/> so the next run
    /// can resume from here.</summary>
    public (string Model, long CumulativeInput, long CumulativeOutput, long CumulativeCacheCreation, long CumulativeCacheRead, string Effort) State =>
        (_model, _cumulativeInput, _cumulativeOutput, _cumulativeCacheCreation, _cumulativeCacheRead, _effort);

    /// <summary>Cheap raw-byte test run before a line is decoded. <see cref="TryParseLine"/> only reads
    /// or changes state on a "turn_context" line (model, effort) and on an event_msg "token_count"
    /// line; every other type returns before touching anything, so a line carrying neither quoted
    /// value can be skipped unread without changing any later result.</summary>
    internal static bool MayAffectState(ReadOnlySpan<byte> line) =>
        line.IndexOf("\"turn_context\""u8) >= 0 || line.IndexOf("\"token_count\""u8) >= 0;

    /// <summary>True only for a "token_count" line that reports at least one genuinely new token
    /// against what this instance has already seen. A repeated line changes nothing and yields no
    /// event; a counter that drops below its own previous value has reset (a context compaction, or a
    /// fresh file) rather than gone backwards, so its new value becomes this event's delta for that
    /// counter instead of a clamped, lost zero. A "turn_context" line updates the tracked model and
    /// returns false: it is state to remember, not a usage event of its own.</summary>
    public bool TryParseLine(string line, out UsageEvent result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            // A line can be syntactically valid JSON without being an object at all (e.g. a bare
            // array) - TryGetProperty throws InvalidOperationException on anything else, which the
            // JsonException catch below does not know about.
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var type = JsonReading.TryGetString(root, "type");

            if (type == "turn_context")
            {
                if (root.TryGetProperty("payload", out var turnPayload) && turnPayload.ValueKind == JsonValueKind.Object)
                {
                    if (turnPayload.TryGetProperty("model", out var modelProperty) && modelProperty.ValueKind == JsonValueKind.String)
                        _model = modelProperty.GetString() ?? _model;

                    // "effort" is preferred; "reasoning_effort" is the older field name for the same
                    // value, checked only when "effort" itself is missing from this particular line.
                    if (JsonReading.TryGetString(turnPayload, "effort") is { Length: > 0 } effort)
                        _effort = effort;
                    else if (JsonReading.TryGetString(turnPayload, "reasoning_effort") is { Length: > 0 } reasoningEffort)
                        _effort = reasoningEffort;
                }
                return false;
            }

            if (type != "event_msg")
                return false;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return false;
            if (JsonReading.TryGetString(payload, "type") != "token_count")
                return false;
            if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
                return false;
            if (!info.TryGetProperty("total_token_usage", out var total) || total.ValueKind != JsonValueKind.Object)
                return false;
            if (!root.TryGetProperty("timestamp", out var timestampProperty) || timestampProperty.ValueKind != JsonValueKind.String)
                return false;
            if (!DateTimeOffset.TryParse(
                    timestampProperty.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
                return false;

            // A line missing any of the three base running totals says nothing reliable: reading the
            // gap as zero would make the next complete line count its whole total as new. Such a line
            // is skipped and the previous totals stay.
            if (JsonReading.TryGetInt64(total, "input_tokens") is not { } input
                || JsonReading.TryGetInt64(total, "output_tokens") is not { } output
                || JsonReading.TryGetInt64(total, "cached_input_tokens") is not { } cacheRead)
                return false;

            // Older Codex versions write no cache-write total at all; requiring it would drop every
            // line of their logs. Where it is missing the previous total stands, so it adds nothing.
            var cacheCreation = JsonReading.TryGetInt64(total, "cache_write_input_tokens") ?? _cumulativeCacheCreation;

            // Codex resets its running totals not only across files but also mid file, after a
            // context compaction - each of the four counters is checked against its own previous
            // value independently, never against the others' sum, so one counter dropping while the
            // rest keep rising is handled by the same rule without a special case: a lower value means
            // that counter itself restarted from zero, so the new value IS this event's delta rather
            // than a clamped-to-zero loss.
            var deltaOutput = CounterDelta(_cumulativeOutput, output);
            var deltaCacheCreation = CounterDelta(_cumulativeCacheCreation, cacheCreation);
            var deltaCacheRead = CounterDelta(_cumulativeCacheRead, cacheRead);

            // Codex's own "input_tokens" total already includes whatever share of it was served
            // from cache (unlike Claude, where the input and cache fields are separate, non
            // overlapping quantities) - the cached share is subtracted out right here, so a record
            // built from this event never counts it a second time when its four fields are summed.
            var deltaInput = Math.Max(0, CounterDelta(_cumulativeInput, input) - deltaCacheRead);

            _cumulativeInput = input;
            _cumulativeOutput = output;
            _cumulativeCacheCreation = cacheCreation;
            _cumulativeCacheRead = cacheRead;

            if (deltaInput == 0 && deltaOutput == 0 && deltaCacheCreation == 0 && deltaCacheRead == 0)
                return false;

            result = new UsageEvent(timestamp, _model, deltaInput, deltaOutput, deltaCacheCreation, deltaCacheRead, _effort);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The project a rollout file belongs to: the full "cwd" path the file's own
    /// "session_meta" line carries once, near the start, without trailing separators - the same form
    /// Claude sessions use, so both land under one project. Null when the line is not a session_meta
    /// line or carries no cwd.</summary>
    public static string? TryExtractProjectFromSessionMetaLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (JsonReading.TryGetString(root, "type") != "session_meta")
                return null;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return null;
            if (!payload.TryGetProperty("cwd", out var cwdProperty) || cwdProperty.ValueKind != JsonValueKind.String)
                return null;

            var cwd = cwdProperty.GetString();
            return cwd?.TrimEnd('\\', '/') is { Length: > 0 } project ? project : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The session id the file's "session_meta" line carries (<c>id</c>, or <c>session_id</c>
    /// in an older shape). Null when the line is not a session_meta line or names none.</summary>
    public static string? TryExtractSessionIdFromSessionMetaLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || JsonReading.TryGetString(root, "type") != "session_meta")
                return null;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return null;

            return JsonReading.TryGetString(payload, "id") is { Length: > 0 } id ? id
                : JsonReading.TryGetString(payload, "session_id") is { Length: > 0 } legacy ? legacy
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One counter's delta against its own previous cumulative value - never against any
    /// other counter's. A value lower than what came before means this specific counter itself reset
    /// back to zero (a context compaction, or a fresh file), so the new value is counted as this
    /// event's whole delta rather than being clamped to zero and lost.</summary>
    private static long CounterDelta(long previous, long current) => current < previous ? current : current - previous;
}
