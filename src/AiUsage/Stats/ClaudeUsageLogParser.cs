using System.Globalization;
using System.Text.Json;

namespace AiUsage.Stats;

/// <summary>
/// Pure line parser for a Claude Code session transcript line (<c>~/.claude/projects/*/*.jsonl</c>).
/// Every assistant turn already carries its own token usage, so unlike Codex there is nothing
/// cumulative to diff against a previous line - each recognised line is its own, complete delta.
/// Never throws: any shape it does not recognise (a user turn, a tool result, a line from a future
/// format) is simply not a match, never an error.
/// </summary>
public static class ClaudeUsageLogParser
{
    public readonly record struct UsageEvent(
        DateTimeOffset Timestamp, string Model, long InputTokens, long OutputTokens, long CacheCreationTokens, long CacheReadTokens,
        string Effort = "", string MessageKey = "");

    /// <summary>Cheap raw-byte test run before a line is decoded: <see cref="TryParse"/> only accepts a
    /// line with a <c>usage</c> object inside <c>message</c>, so a line without the quoted key
    /// <c>"usage"</c> anywhere can be skipped unread. A line that passes may still be rejected by
    /// <see cref="TryParse"/>; one that fails never would have been accepted.</summary>
    internal static bool MayContainUsage(ReadOnlySpan<byte> line) => line.IndexOf("\"usage\""u8) >= 0;

    public static bool TryParse(string line, out UsageEvent result)
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
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return false;
            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return false;
            if (!root.TryGetProperty("timestamp", out var timestampProperty) || timestampProperty.ValueKind != JsonValueKind.String)
                return false;
            if (!DateTimeOffset.TryParse(
                    timestampProperty.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
                return false;

            var model = message.TryGetProperty("model", out var modelProperty) && modelProperty.ValueKind == JsonValueKind.String
                ? modelProperty.GetString() ?? ""
                : "";

            var input = GetLong(usage, "input_tokens");
            var output = GetLong(usage, "output_tokens");
            var cacheCreation = GetLong(usage, "cache_creation_input_tokens");
            var cacheRead = GetLong(usage, "cache_read_input_tokens");

            // A usage object with every field missing (none of the four keys present) is not a real
            // measurement - never worth a zero-valued row that would just clutter the index.
            if (input == 0 && output == 0 && cacheCreation == 0 && cacheRead == 0)
                return false;

            var effort = GetEffort(root, "effort") ?? GetEffort(root, "perTurnEffort")
                ?? GetEffort(message, "effort") ?? GetEffort(message, "perTurnEffort") ?? "";

            // Claude Code writes one response several times (same message id and request id, same
            // numbers); the pair is the key the indexer uses to count it once. Empty when either
            // half is missing, and an empty key is never deduplicated.
            var messageId = message.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString() ?? ""
                : "";
            var requestId = root.TryGetProperty("requestId", out var requestProperty) && requestProperty.ValueKind == JsonValueKind.String
                ? requestProperty.GetString() ?? ""
                : "";
            var messageKey = messageId.Length > 0 && requestId.Length > 0 ? messageId + "|" + requestId : "";

            result = new UsageEvent(timestamp, model, input, output, cacheCreation, cacheRead, effort, messageKey);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The project a Claude session transcript belongs to, as the sanitised folder name
    /// Claude Code itself gives the project's working directory (e.g. <c>C--Projects-AI-Usage</c>),
    /// one level above the transcript file. A fallback only: <see cref="TryExtractProjectFromLine"/>
    /// reads the real path straight from the transcript and is preferred whenever a line carries it,
    /// since a folder name cannot be turned back into a path (a hyphen already in the real project
    /// name, e.g. <c>AI-Usage</c>, looks exactly like a replaced path separator).</summary>
    public static string ExtractProjectFromFilePath(string filePath) =>
        Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(filePath))) ?? "";

    /// <summary>Reads the real project path from one transcript line's own <c>cwd</c> field, verbatim
    /// (never trimmed to a folder name; the Codex parser's <see cref="CodexUsageLogParser.TryExtractProjectFromSessionMetaLine"/>
    /// returns the full path as well).
    /// Returns null when the line is not valid JSON, not an object, or carries no string
    /// <c>cwd</c> field - never an error, the same "just not a match" contract as <see cref="TryParse"/>.</summary>
    public static string? TryExtractProjectFromLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("cwd", out var cwdProperty) || cwdProperty.ValueKind != JsonValueKind.String)
                return null;

            var cwd = cwdProperty.GetString();
            return string.IsNullOrEmpty(cwd) ? null : cwd;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The reasoning effort level ("medium", "high", ...) an assistant line carries under
    /// <paramref name="propertyName"/> - checked at both the line's own root and inside "message"
    /// since which of the two actually carries it (and under which of "effort"/"perTurnEffort") has
    /// shifted across Claude Code releases; null when this particular property is missing or not a
    /// string, never an empty string (an explicit empty value is treated the same as absent).</summary>
    private static string? GetEffort(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
        && property.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static long GetLong(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;
}
