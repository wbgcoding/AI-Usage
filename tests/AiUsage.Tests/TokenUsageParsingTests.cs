using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.Parsing;
using AiUsage.Stats;
using AiUsage.Storage;

namespace AiUsage.Tests;

/// <summary>
/// A real token count, never a placeholder, never an estimate. Codex is the
/// only provider with a measured local source for it - Gemini has none and Claude's local
/// transcripts have no reliable per-window anchor to attach a sum to, so both are covered by the
/// "no line at all" assertions below.
/// </summary>
public class TokenUsageParsingTests : IDisposable
{
    private static readonly string FixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public void CodexRateLimitParser_reads_the_total_token_count_when_present()
    {
        var line = """
            {"timestamp":"2026-09-01T10:00:00Z","type":"event_msg","payload":{"type":"token_count",
            "info":{"total_token_usage":{"total_tokens":51505},"model_context_window":258400},
            "rate_limits":{"primary":{"used_percent":2.0,"window_minutes":300},"secondary":{"used_percent":88.0,"window_minutes":10080}}}}
            """.ReplaceLineEndings("");

        var ok = CodexRateLimitParser.TryParse(line, out var result);

        Assert.True(ok);
        Assert.Equal(51505, result!.TotalTokens);
    }

    [Theory]
    [InlineData("""{"timestamp":"2026-09-01T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":1.0,"window_minutes":300},"secondary":{"used_percent":1.0,"window_minutes":10080}}}}""")]
    [InlineData("""{"timestamp":"2026-09-01T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{},"rate_limits":{"primary":{"used_percent":1.0,"window_minutes":300},"secondary":{"used_percent":1.0,"window_minutes":10080}}}}""")]
    [InlineData("""{"timestamp":"2026-09-01T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{}},"rate_limits":{"primary":{"used_percent":1.0,"window_minutes":300},"secondary":{"used_percent":1.0,"window_minutes":10080}}}}""")]
    public void CodexRateLimitParser_reports_no_token_count_rather_than_zero_when_unmeasured(string line)
    {
        var ok = CodexRateLimitParser.TryParse(line, out var result);

        Assert.True(ok);
        Assert.Null(result!.TotalTokens);
    }

    [Fact]
    public async Task CodexProvider_attaches_the_token_count_to_the_five_hour_window_only()
    {
        var directory = Track(TestPaths.CreateDirectory("ai-usage-codex-tokens"));
        File.Copy(Path.Combine(FixturesDirectory, "codex-with-tokens.jsonl"), Path.Combine(directory, "rollout-test.jsonl"));
        var provider = new CodexProvider(directory, () => DateTimeOffset.Parse("2026-09-01T10:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        var fiveHour = snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour);
        var weekly = snapshot.Windows.Single(w => w.Kind == WindowKind.Weekly);
        Assert.Equal(51505, fiveHour.Tokens?.TotalTokens);
        Assert.Null(weekly.Tokens);
    }

    [Fact]
    public async Task CodexProvider_leaves_tokens_null_when_the_fixture_has_none_measured_yet()
    {
        var directory = Track(TestPaths.CreateDirectory("ai-usage-codex-tokens"));
        File.Copy(Path.Combine(FixturesDirectory, "codex-normal.jsonl"), Path.Combine(directory, "rollout-test.jsonl"));
        var provider = new CodexProvider(directory, () => DateTimeOffset.Parse("2026-09-01T10:01:00Z"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Null(snapshot.Windows.Single(w => w.Kind == WindowKind.FiveHour).Tokens);
    }

    [Fact]
    public void CodexUsageLogParser_counts_a_dropping_counter_as_its_own_reset_not_a_zero()
    {
        // Codex resets a running total mid file after a context compaction, not only across files -
        // each of the four counters must be checked against its own previous value, never against
        // the others' sum, so the input counter dropping here does not swallow the tokens it now
        // reports, and does not disturb the output counter's own, unrelated rise.
        var parser = new CodexUsageLogParser();
        var first = CodexTokenCountLine("2026-09-01T10:00:00Z", input: 100, output: 10, cacheCreation: 0, cacheRead: 0);
        var second = CodexTokenCountLine("2026-09-01T10:05:00Z", input: 300, output: 30, cacheCreation: 0, cacheRead: 0);
        var third = CodexTokenCountLine("2026-09-01T10:10:00Z", input: 50, output: 40, cacheCreation: 0, cacheRead: 0);

        long totalInput = 0;
        long totalOutput = 0;
        foreach (var line in new[] { first, second, third })
        {
            Assert.True(parser.TryParseLine(line, out var result));
            totalInput += result.InputTokens;
            totalOutput += result.OutputTokens;
        }

        Assert.Equal(350, totalInput); // 100 + 200 + 50 - the third event's drop counts as its own reset
        Assert.Equal(40, totalOutput); // 10 + 20 + 10 - never disturbed by the input counter's reset
    }

    [Fact]
    public void CodexUsageLogParser_resets_each_counter_independently_of_the_others()
    {
        // The reverse of the case above: this time the output counter is the one that drops while
        // input keeps rising, proving the reset rule is applied per counter rather than coupled to
        // whichever field happens to be checked first.
        var parser = new CodexUsageLogParser();
        var first = CodexTokenCountLine("2026-09-01T10:00:00Z", input: 100, output: 50, cacheCreation: 0, cacheRead: 0);
        var second = CodexTokenCountLine("2026-09-01T10:05:00Z", input: 150, output: 20, cacheCreation: 0, cacheRead: 0);

        long totalInput = 0;
        long totalOutput = 0;
        foreach (var line in new[] { first, second })
        {
            Assert.True(parser.TryParseLine(line, out var result));
            totalInput += result.InputTokens;
            totalOutput += result.OutputTokens;
        }

        Assert.Equal(150, totalInput); // 100 + 50
        Assert.Equal(70, totalOutput); // 50 + 20 - the drop to 20 counts as its own reset, not a zero
    }

    private static string CodexTokenCountLine(string timestamp, long input, long output, long cacheCreation, long cacheRead) =>
        "{\"type\":\"event_msg\",\"timestamp\":\"" + timestamp + "\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":" +
        input + ",\"cached_input_tokens\":" + cacheRead + ",\"cache_write_input_tokens\":" + cacheCreation + ",\"output_tokens\":" + output + "}}}}";

    [Fact]
    public void HistoryStore_round_trips_a_point_with_a_token_count()
    {
        var directory = Track(TestPaths.GetPath("ai-usage-history-tokens"));
        var store = new HistoryStore(directory, () => DateTimeOffset.Parse("2026-09-05T12:00:00Z"));

        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null, tokens: 51505);

        var point = Assert.Single(store.Load("codex", TimeSpan.FromDays(1)));
        Assert.Equal(51505, point.Tokens);
    }

    [Fact]
    public void HistoryStore_leaves_tokens_null_when_none_was_given()
    {
        var directory = Track(TestPaths.GetPath("ai-usage-history-tokens"));
        var store = new HistoryStore(directory, () => DateTimeOffset.Parse("2026-09-05T12:00:00Z"));

        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);

        var point = Assert.Single(store.Load("codex", TimeSpan.FromDays(1)));
        Assert.Null(point.Tokens);
    }

    private readonly List<string> _cleanupPaths = [];

    private string Track(string path)
    {
        _cleanupPaths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _cleanupPaths)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
