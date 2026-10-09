using System.Globalization;
using System.Text.Json;
using AiUsage.Io;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Reads the newest classifiable turn out of a Claude or Codex session file, for <see
/// cref="AttentionDetector"/>. Pure file access, no decision logic of its own - the two formats keep
/// their own line classifier because their transcripts are built differently, but both walk the same
/// way: newest line first, skipping anything that does not parse or does not name a turn, stopping at
/// the first line either classifier recognises. Never throws: a missing, locked, truncated or
/// malformed file yields <c>(null, null)</c>, exactly like "no session file was found" upstream.
/// </summary>
public static class SessionTurnReader
{
    /// <summary>Claude's own transcript shape: one JSON object per line, a top-level "type" of "user"
    /// or "assistant" naming whose turn it is, a "message.content" array of typed blocks underneath.
    /// A tool result comes back wrapped in a "user" line (the harness feeding the tool's output back
    /// to the model), not a real user turn, so it reads as <see cref="SessionRecordKind.ToolCall"/>
    /// instead - only a line with an actual human block (anything other than "tool_result") counts as
    /// <see cref="SessionRecordKind.UserTurn"/>. An assistant line with no plain "text" block among its
    /// content (thinking only, or a tool call) means the model is still working, not finished.</summary>
    public static (SessionRecordKind? Kind, DateTimeOffset? Timestamp) ReadNewestClaudeTurn(string path) =>
        ReadNewest(path, TryClassifyClaudeLine);

    /// <summary>Codex's own transcript shape: one JSON object per line with a top-level "type"; only
    /// "response_item" lines describe a turn, in "payload.type" - "message" (role "user" or
    /// "assistant"), "reasoning", or one of the tool-call/tool-result payload kinds. Every other
    /// top-level type ("event_msg", "session_meta", "turn_context", "world_state") and a "message" from
    /// any role but user/assistant (e.g. "developer") name nothing this vocabulary covers, so the
    /// search keeps looking further back.</summary>
    public static (SessionRecordKind? Kind, DateTimeOffset? Timestamp) ReadNewestCodexTurn(string path) =>
        ReadNewest(path, TryClassifyCodexLine);

    private static (SessionRecordKind? Kind, DateTimeOffset? Timestamp) ReadNewest(
        string path, Func<string, (SessionRecordKind Kind, DateTimeOffset Timestamp)?> classify)
    {
        try
        {
            foreach (var line in ReverseLineReader.ReadLinesReversed(path))
            {
                if (classify(line) is { } parsed)
                    return (parsed.Kind, parsed.Timestamp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deleted, locked or otherwise unreadable right now - "no record found" covers this
            // exactly as well as an empty or missing file would.
        }

        return (null, null);
    }

    private static (SessionRecordKind, DateTimeOffset)? TryClassifyClaudeLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!TryGetString(root, "type", out var type) || (type != "user" && type != "assistant"))
                return null;
            if (!TryGetTimestamp(root, out var timestamp))
                return null;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return null;
            if (!message.TryGetProperty("content", out var content))
                return null;

            // A typed prompt is written as a plain string, not as a block array.
            if (type == "user" && content.ValueKind == JsonValueKind.String)
                return (SessionRecordKind.UserTurn, timestamp);
            if (content.ValueKind != JsonValueKind.Array)
                return null;

            var blockTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object && TryGetString(block, "type", out var blockType))
                    blockTypes.Add(blockType);
            }

            if (type == "assistant")
                return blockTypes.Contains("tool_use")
                    ? (SessionRecordKind.ToolCall, timestamp)
                    : blockTypes.Contains("text")
                        ? (SessionRecordKind.AssistantTurn, timestamp)
                        // Thinking only, or a shape this vocabulary does not name yet - the model has
                        // not produced its final answer, so this is not "waiting" either way.
                        : (SessionRecordKind.ToolCall, timestamp);

            // type == "user"
            return blockTypes.Contains("tool_result")
                ? (SessionRecordKind.ToolCall, timestamp)
                : (SessionRecordKind.UserTurn, timestamp);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> CodexToolPayloadTypes = new(StringComparer.Ordinal)
    {
        "reasoning", "function_call", "function_call_output",
        "custom_tool_call", "custom_tool_call_output",
        "tool_search_call", "tool_search_output",
    };

    private static (SessionRecordKind, DateTimeOffset)? TryClassifyCodexLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!TryGetString(root, "type", out var type) || type != "response_item")
                return null;
            if (!TryGetTimestamp(root, out var timestamp))
                return null;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return null;
            if (!TryGetString(payload, "type", out var payloadType))
                return null;

            if (payloadType == "message")
            {
                if (!TryGetString(payload, "role", out var role))
                    return null;
                return role switch
                {
                    "user" => (SessionRecordKind.UserTurn, timestamp),
                    "assistant" => (SessionRecordKind.AssistantTurn, timestamp),
                    // A role this vocabulary does not name (e.g. "developer") - keep looking further back.
                    _ => null,
                };
            }

            return CodexToolPayloadTypes.Contains(payloadType) ? (SessionRecordKind.ToolCall, timestamp) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } text)
        {
            value = text;
            return true;
        }

        value = "";
        return false;
    }

    private static bool TryGetTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        if (TryGetString(root, "timestamp", out var text) && DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
            return true;

        timestamp = default;
        return false;
    }
}
