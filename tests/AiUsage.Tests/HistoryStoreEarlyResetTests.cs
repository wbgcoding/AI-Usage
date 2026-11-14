using AiUsage.Models;
using AiUsage.Storage;

namespace AiUsage.Tests;

public sealed class HistoryStoreEarlyResetTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDisposableDirectory("ai-usage-history");
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-17T12:38:00Z");
    private static readonly DateTimeOffset Reset = DateTimeOffset.Parse("2026-10-21T14:00:00Z");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // a leftover temp folder is not worth failing over
        }
    }

    private void At(HistoryStore store, string time, double percent, DateTimeOffset reset, string? plan = "Max")
    {
        _now = DateTimeOffset.Parse(time);
        store.Append("claude", WindowKind.Weekly, percent, reset, plan: plan);
    }

    [Fact]
    public void A_drop_with_the_same_reset_time_is_stored_with_the_early_reset_flag()
    {
        var store = new HistoryStore(_directory, () => _now);
        At(store, "2026-10-17T12:30:00Z", 80, Reset);
        At(store, "2026-10-17T12:38:00Z", 100, Reset);
        At(store, "2026-10-17T12:42:00Z", 4, Reset);

        var points = store.Load("claude", TimeSpan.FromDays(400));
        Assert.Equal([null, null, true], points.OrderBy(point => point.Timestamp).Select(point => point.EarlyReset));
        var lines = File.ReadAllLines(Path.Combine(_directory, "history-claude.jsonl"));
        Assert.Equal(3, lines.Length);
        Assert.DoesNotContain("\"er\"", lines[0]);
        Assert.DoesNotContain("\"er\"", lines[1]);
        Assert.Contains("\"er\":true", lines[2]);
    }

    [Fact]
    public void A_regular_reset_and_a_plan_change_are_not_flagged()
    {
        var store = new HistoryStore(_directory, () => _now);
        At(store, "2026-10-17T12:30:00Z", 90, Reset);
        At(store, "2026-10-21T14:05:00Z", 2, Reset.AddDays(7));
        At(store, "2026-10-22T10:00:00Z", 83, Reset.AddDays(7));
        At(store, "2026-10-22T10:02:00Z", 0, Reset.AddDays(7), plan: "Ultra");

        Assert.All(store.Load("claude", TimeSpan.FromDays(400)), point => Assert.Null(point.EarlyReset));
    }

    [Fact]
    public void A_line_without_the_key_still_reads()
    {
        File.WriteAllText(
            Path.Combine(_directory, "history-claude.jsonl"),
            "{\"v\":1,\"t\":\"2026-10-17T14:38:00+02:00\",\"w\":\"Weekly\",\"p\":100,\"r\":\"2026-10-21T14:00:00+00:00\",\"c\":null,\"tok\":null,\"l\":\"Window_Weekly\",\"pl\":\"Max\"}\n");
        var store = new HistoryStore(_directory, () => _now);

        var point = Assert.Single(store.Load("claude", TimeSpan.FromDays(400)));
        Assert.Null(point.EarlyReset);
    }

    [Fact]
    public void Compaction_keeps_the_point_that_started_a_cycle_after_a_limit_reset()
    {
        var store = new HistoryStore(_directory, () => _now);
        var hour = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var lines = new[]
        {
            new HistoryPoint(1, hour.AddMinutes(5), WindowKind.Weekly, 100, Reset, Plan: "Max"),
            new HistoryPoint(1, hour.AddMinutes(9), WindowKind.Weekly, 4, Reset, Plan: "Max", EarlyReset: true),
            new HistoryPoint(1, hour.AddMinutes(20), WindowKind.Weekly, 6, Reset, Plan: "Max"),
        }.Select(point => System.Text.Json.JsonSerializer.Serialize(point)).ToArray();
        File.WriteAllLines(Path.Combine(_directory, "history-claude.jsonl"), lines);
        _now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");

        store.Compact("claude");

        var remaining = store.Load("claude", TimeSpan.FromDays(400)).OrderBy(point => point.Timestamp).ToList();
        Assert.Equal([100.0, 4.0], remaining.Select(point => point.Percent));
        Assert.Equal([null, true], remaining.Select(point => point.EarlyReset));
    }
}
