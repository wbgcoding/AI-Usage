using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

public class StatsStoreWindowRecordsTests
{
    [Fact]
    public void The_window_records_sum_the_token_kinds_per_model_from_the_start_hour_on()
    {
        using var dir = TestPaths.CreateDisposableDirectory("window-records");
        var store = new StatsStore(dir);
        var day = new DateOnly(2026, 1, 5);
        store.AddDelta([
            new StatsRecord("claude", day, "claude-sonnet-4-5", "a", 100, 10, 5, 1, Hour: 8),
            new StatsRecord("claude", day, "claude-sonnet-4-5", "b", 200, 20, 0, 0, Hour: 10),
            new StatsRecord("claude", day, "claude-opus-4-8", "a", 50, 5, 0, 0, Hour: 11),
            new StatsRecord("claude", day.AddDays(1), "claude-sonnet-4-5", "a", 1, 1, 1, 1, Hour: 0),
            new StatsRecord("codex", day, "gpt-5", "a", 999, 0, 0, 0, Hour: 10),
        ]);

        var records = store.WindowRecordsSince("claude", new DateTime(2026, 1, 5, 9, 30, 0));

        Assert.NotNull(records);
        var sonnet = Assert.Single(records, record => record.Model == "claude-sonnet-4-5");
        Assert.Equal((201, 21, 1, 1), (sonnet.InputTokens, sonnet.OutputTokens, sonnet.CacheCreationTokens, sonnet.CacheReadTokens));
        var opus = Assert.Single(records, record => record.Model == "claude-opus-4-8");
        Assert.Equal(55, opus.TotalTokens);
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal("claude", record.Provider));
    }

    [Fact]
    public void An_empty_slice_answers_an_empty_list_not_null()
    {
        using var dir = TestPaths.CreateDisposableDirectory("window-records-empty");
        var store = new StatsStore(dir);

        var records = store.WindowRecordsSince("claude", new DateTime(2026, 1, 5, 9, 0, 0));

        Assert.NotNull(records);
        Assert.Empty(records);
    }
}
