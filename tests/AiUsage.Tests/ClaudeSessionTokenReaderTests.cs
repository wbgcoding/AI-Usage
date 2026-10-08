using AiUsage.Providers.Parsing;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Every line below is written by hand for this test alone, only the field names and shapes
/// a real Claude Code transcript is known to use.</summary>
public class ClaudeSessionTokenReaderTests : IDisposable
{
    [Fact]
    public void SumTokens_adds_up_every_usage_line_in_the_file()
    {
        var path = WriteFile(
            ClaudeLine("2026-01-01T12:00:00Z", input: 10, output: 20, cacheCreation: 30, cacheRead: 40),
            ClaudeLine("2026-01-01T12:00:05Z", input: 1, output: 2, cacheCreation: 3, cacheRead: 4),
            ClaudeLine("2026-01-01T12:00:10Z", input: 100, output: 200, cacheCreation: 300, cacheRead: 400));

        var sum = new ClaudeSessionTokenReader().SumTokens(path);

        Assert.Equal(10 + 20 + 30 + 40 + 1 + 2 + 3 + 4 + 100 + 200 + 300 + 400, sum);
    }

    [Fact]
    public void SumTokens_only_reads_the_bytes_appended_since_the_last_call()
    {
        var path = WriteFile(
            ClaudeLine("2026-01-01T12:00:00Z", input: 10, output: 20, cacheCreation: 30, cacheRead: 40),
            ClaudeLine("2026-01-01T12:00:05Z", input: 1, output: 2, cacheCreation: 3, cacheRead: 4),
            ClaudeLine("2026-01-01T12:00:10Z", input: 100, output: 200, cacheCreation: 300, cacheRead: 400));
        var reader = new ClaudeSessionTokenReader();

        var firstSum = reader.SumTokens(path);
        var lengthAfterFirstCall = new FileInfo(path).Length;
        Assert.Equal(lengthAfterFirstCall, reader.LastReadOffset);

        File.AppendAllText(path, ClaudeLine("2026-01-01T12:00:15Z", input: 5, output: 6, cacheCreation: 7, cacheRead: 8) + "\n");
        var secondSum = reader.SumTokens(path);

        Assert.Equal(firstSum + 5 + 6 + 7 + 8, secondSum);
        Assert.Equal(new FileInfo(path).Length, reader.LastReadOffset);
        Assert.True(reader.LastReadOffset > lengthAfterFirstCall);
    }

    [Fact]
    public void SumTokens_leaves_a_half_written_last_line_for_the_next_call_and_counts_it_once()
    {
        var first = ClaudeLine("2026-01-01T12:00:00Z", input: 10, output: 20, cacheCreation: 30, cacheRead: 40);
        var second = ClaudeLine("2026-01-01T12:00:05Z", input: 1, output: 2, cacheCreation: 3, cacheRead: 4);
        var cut = second.Length / 2;
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(path, first + "\n" + second[..cut]);
        var reader = new ClaudeSessionTokenReader();

        var firstSum = reader.SumTokens(path);

        Assert.Equal(10 + 20 + 30 + 40, firstSum);
        Assert.Equal(first.Length + 1, reader.LastReadOffset);

        File.AppendAllText(path, second[cut..] + "\n");
        var secondSum = reader.SumTokens(path);

        Assert.Equal(10 + 20 + 30 + 40 + 1 + 2 + 3 + 4, secondSum);
        Assert.Equal(new FileInfo(path).Length, reader.LastReadOffset);

        // Nothing new: the finished line is not counted a second time.
        Assert.Equal(secondSum, reader.SumTokens(path));
    }

    [Fact]
    public void SumTokens_reads_a_file_whose_lines_end_in_crlf()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(path,
            ClaudeLine("2026-01-01T12:00:00Z", input: 10, output: 20, cacheCreation: 0, cacheRead: 0) + "\r\n" +
            ClaudeLine("2026-01-01T12:00:05Z", input: 1, output: 2, cacheCreation: 0, cacheRead: 0) + "\r\n");

        Assert.Equal(10 + 20 + 1 + 2, new ClaudeSessionTokenReader().SumTokens(path));
    }

    [Fact]
    public void SumTokens_starts_over_when_the_file_got_shorter()
    {
        var path = WriteFile(
            ClaudeLine("2026-01-01T12:00:00Z", input: 10, output: 20, cacheCreation: 30, cacheRead: 40),
            ClaudeLine("2026-01-01T12:00:05Z", input: 1, output: 2, cacheCreation: 3, cacheRead: 4));
        var reader = new ClaudeSessionTokenReader();
        reader.SumTokens(path);

        // Deleted and re-written shorter, as a fresh session file with the same path would be.
        File.Delete(path);
        File.WriteAllText(path, ClaudeLine("2026-01-01T13:00:00Z", input: 7, output: 8, cacheCreation: 9, cacheRead: 11) + "\n");

        var sum = reader.SumTokens(path);

        Assert.Equal(7 + 8 + 9 + 11, sum);
    }

    [Fact]
    public void SumTokens_counts_a_response_written_several_times_only_once()
    {
        // Claude Code writes one response more than once: same message id, same request id, same numbers.
        var path = WriteFile(
            KeyedLine("2026-01-01T12:00:00Z", "msg_1", "req_1", input: 10, output: 20),
            KeyedLine("2026-01-01T12:00:01Z", "msg_1", "req_1", input: 10, output: 20),
            KeyedLine("2026-01-01T12:00:05Z", "msg_2", "req_2", input: 1, output: 2));

        var sum = new ClaudeSessionTokenReader().SumTokens(path);

        Assert.Equal(10 + 20 + 1 + 2, sum);
    }

    [Fact]
    public void SumTokens_keeps_the_dedup_across_an_incremental_read()
    {
        var path = WriteFile(KeyedLine("2026-01-01T12:00:00Z", "msg_1", "req_1", input: 10, output: 20));
        var reader = new ClaudeSessionTokenReader();
        reader.SumTokens(path);

        File.AppendAllText(path, KeyedLine("2026-01-01T12:00:02Z", "msg_1", "req_1", input: 10, output: 20) + "\n");
        File.AppendAllText(path, KeyedLine("2026-01-01T12:00:09Z", "msg_3", "req_3", input: 4, output: 5) + "\n");

        Assert.Equal(10 + 20 + 4 + 5, reader.SumTokens(path));
    }

    [Fact]
    public void LastEventAt_is_the_newest_usage_line_timestamp()
    {
        var path = WriteFile(
            ClaudeLine("2026-01-01T12:00:00Z", input: 10, output: 20, cacheCreation: 0, cacheRead: 0),
            ClaudeLine("2026-01-01T12:07:00Z", input: 1, output: 2, cacheCreation: 0, cacheRead: 0));
        var reader = new ClaudeSessionTokenReader();

        reader.SumTokens(path);

        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:07:00Z"), reader.LastEventAt);
    }

    private static string KeyedLine(string timestamp, string messageId, string requestId, long input, long output) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"requestId\":\"" + requestId + "\",\"message\":{\"id\":\"" + messageId +
        "\",\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + "}}}";

    private static string ClaudeLine(string timestamp, long input, long output, long cacheCreation, long cacheRead) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"message\":{\"model\":\"claude-opus-5\"," +
        "\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output +
        ",\"cache_creation_input_tokens\":" + cacheCreation + ",\"cache_read_input_tokens\":" + cacheRead + "}}}";

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
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-claude-session-tokens");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }
}
