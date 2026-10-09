using AiUsage.Providers.Parsing;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Every line below is written by hand for this test alone - none of it is copied from a
/// real session file, only the field names and type values the real files are known to use.</summary>
public class SessionTurnReaderTests : IDisposable
{
    [Fact]
    public void ReadNewestClaudeTurn_classifies_a_finished_assistant_text_reply_as_AssistantTurn()
    {
        var path = WriteFile(
            """{"type":"user","timestamp":"2026-01-01T12:00:00.000Z","message":{"role":"user","content":[{"type":"text"}]}}""",
            """{"type":"assistant","timestamp":"2026-01-01T12:00:05.000Z","message":{"role":"assistant","content":[{"type":"text"}]}}""");

        var (kind, timestamp) = SessionTurnReader.ReadNewestClaudeTurn(path);

        Assert.Equal(SessionRecordKind.AssistantTurn, kind);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:05.000Z"), timestamp);
    }

    [Fact]
    public void ReadNewestClaudeTurn_classifies_a_human_message_as_UserTurn()
    {
        var path = WriteFile(
            """{"type":"user","timestamp":"2026-01-01T12:00:10.000Z","message":{"role":"user","content":[{"type":"text"}]}}""");

        var (kind, timestamp) = SessionTurnReader.ReadNewestClaudeTurn(path);

        Assert.Equal(SessionRecordKind.UserTurn, kind);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:10.000Z"), timestamp);
    }

    [Fact]
    public void ReadNewestClaudeTurn_classifies_a_plain_string_prompt_as_UserTurn_after_an_assistant_reply()
    {
        var path = WriteFile(
            """{"type":"assistant","timestamp":"2026-10-08T09:59:00Z","message":{"role":"assistant","content":[{"type":"text"}]}}""",
            """{"type":"user","timestamp":"2026-10-08T10:00:00Z","message":{"role":"user","content":"hi"}}""");

        var (kind, timestamp) = SessionTurnReader.ReadNewestClaudeTurn(path);

        Assert.Equal(SessionRecordKind.UserTurn, kind);
        Assert.Equal(DateTimeOffset.Parse("2026-10-08T10:00:00Z"), timestamp);
        Assert.False(AttentionDetector.IsWaiting(kind!.Value, timestamp!.Value, timestamp.Value.AddSeconds(30), TimeSpan.FromHours(12)));
    }

    [Fact]
    public void ReadNewestClaudeTurn_classifies_a_user_line_with_an_empty_content_array_as_UserTurn()
    {
        var path = WriteFile(
            """{"type":"user","timestamp":"2026-10-08T10:00:00Z","message":{"role":"user","content":[]}}""");

        var (kind, _) = SessionTurnReader.ReadNewestClaudeTurn(path);

        Assert.Equal(SessionRecordKind.UserTurn, kind);
    }

    [Fact]
    public void ReadNewestClaudeTurn_classifies_a_tool_result_line_as_ToolCall_not_UserTurn()
    {
        // A tool result comes back wrapped in a "user"-type line in Claude's own transcript shape -
        // it is the harness feeding the model, never the human actually typing something.
        var path = WriteFile(
            """{"type":"assistant","timestamp":"2026-01-01T12:00:00.000Z","message":{"role":"assistant","content":[{"type":"tool_use"}]}}""",
            """{"type":"user","timestamp":"2026-01-01T12:00:01.000Z","message":{"role":"user","content":[{"type":"tool_result"}]}}""");

        var (kind, _) = SessionTurnReader.ReadNewestClaudeTurn(path);

        Assert.Equal(SessionRecordKind.ToolCall, kind);
    }

    [Fact]
    public void ReadNewestClaudeTurn_skips_a_thinking_only_line_and_reads_the_older_user_turn_beneath_it()
    {
        // Thinking alone is not a finished answer - the reader must not call this "waiting" just
        // because the newest line happens to be an assistant record.
        var path = WriteFile(
            """{"type":"user","timestamp":"2026-01-01T12:00:00.000Z","message":{"role":"user","content":[{"type":"text"}]}}""",
            """{"type":"assistant","timestamp":"2026-01-01T12:00:01.000Z","message":{"role":"assistant","content":[{"type":"thinking"}]}}""");

        var (kind, timestamp) = SessionTurnReader.ReadNewestClaudeTurn(path);

        Assert.Equal(SessionRecordKind.ToolCall, kind);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:01.000Z"), timestamp);
    }

    [Fact]
    public void ReadNewestClaudeTurn_returns_null_for_a_missing_file()
    {
        var path = Path.Combine(TempDirectory(), "does-not-exist.jsonl");

        var (kind, timestamp) = SessionTurnReader.ReadNewestClaudeTurn(path);

        Assert.Null(kind);
        Assert.Null(timestamp);
    }

    [Fact]
    public void ReadNewestClaudeTurn_skips_a_truncated_line_and_reads_the_line_beneath_it()
    {
        var path = WriteFile(
            """{"type":"user","timestamp":"2026-01-01T12:00:00.000Z","message":{"role":"user","content":[{"type":"text"}]}}""",
            """{"type":"assistant","timestamp":"2026-01-01T12:00:05.000Z","message":{"role":"assistant","content":[{"type":"te""");

        var (kind, timestamp) = SessionTurnReader.ReadNewestClaudeTurn(path);

        Assert.Equal(SessionRecordKind.UserTurn, kind);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:00.000Z"), timestamp);
    }

    [Fact]
    public void ReadNewestCodexTurn_classifies_the_newest_assistant_message_as_AssistantTurn()
    {
        var path = WriteFile(
            """{"type":"response_item","timestamp":"2026-01-01T12:00:00.000Z","payload":{"type":"message","role":"user"}}""",
            """{"type":"event_msg","timestamp":"2026-01-01T12:00:04.000Z","payload":{"type":"token_count"}}""",
            """{"type":"response_item","timestamp":"2026-01-01T12:00:05.000Z","payload":{"type":"message","role":"assistant"}}""");

        var (kind, timestamp) = SessionTurnReader.ReadNewestCodexTurn(path);

        Assert.Equal(SessionRecordKind.AssistantTurn, kind);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:05.000Z"), timestamp);
    }

    [Fact]
    public void ReadNewestCodexTurn_classifies_the_newest_user_message_as_UserTurn()
    {
        var path = WriteFile(
            """{"type":"response_item","timestamp":"2026-01-01T12:00:00.000Z","payload":{"type":"message","role":"assistant"}}""",
            """{"type":"response_item","timestamp":"2026-01-01T12:00:10.000Z","payload":{"type":"message","role":"user"}}""");

        var (kind, timestamp) = SessionTurnReader.ReadNewestCodexTurn(path);

        Assert.Equal(SessionRecordKind.UserTurn, kind);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:10.000Z"), timestamp);
    }

    [Fact]
    public void ReadNewestCodexTurn_classifies_a_function_call_as_ToolCall()
    {
        var path = WriteFile(
            """{"type":"response_item","timestamp":"2026-01-01T12:00:00.000Z","payload":{"type":"message","role":"user"}}""",
            """{"type":"response_item","timestamp":"2026-01-01T12:00:03.000Z","payload":{"type":"function_call"}}""");

        var (kind, timestamp) = SessionTurnReader.ReadNewestCodexTurn(path);

        Assert.Equal(SessionRecordKind.ToolCall, kind);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:03.000Z"), timestamp);
    }

    [Fact]
    public void ReadNewestCodexTurn_skips_a_developer_role_message_and_reads_the_line_beneath_it()
    {
        var path = WriteFile(
            """{"type":"response_item","timestamp":"2026-01-01T12:00:00.000Z","payload":{"type":"message","role":"assistant"}}""",
            """{"type":"response_item","timestamp":"2026-01-01T12:00:02.000Z","payload":{"type":"message","role":"developer"}}""");

        var (kind, timestamp) = SessionTurnReader.ReadNewestCodexTurn(path);

        Assert.Equal(SessionRecordKind.AssistantTurn, kind);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:00.000Z"), timestamp);
    }

    [Fact]
    public void ReadNewestCodexTurn_returns_null_for_an_unreadable_file()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "missing-rollout.jsonl");

        var (kind, timestamp) = SessionTurnReader.ReadNewestCodexTurn(path);

        Assert.Null(kind);
        Assert.Null(timestamp);
    }

    private string WriteFile(params string[] lines)
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-session-turn");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }
}
