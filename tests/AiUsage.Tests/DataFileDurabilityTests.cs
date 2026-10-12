using System.Globalization;
using AiUsage.Io;
using AiUsage.Models;
using AiUsage.Storage;

namespace AiUsage.Tests;

public class DataFileDurabilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T12:00:00Z");

    private static string Line(DateTimeOffset timestamp, double percent, int? compactionLevel = null) =>
        $$"""{"v":1,"t":"{{timestamp:O}}","w":"FiveHour","p":{{percent.ToString(CultureInfo.InvariantCulture)}},"r":null,"c":{{(compactionLevel is null ? "null" : compactionLevel.Value.ToString(CultureInfo.InvariantCulture))}}}""";

    [Fact]
    public void AtomicFile_replaces_the_content_and_leaves_no_temp_file()
    {
        var directory = TestPaths.CreateDirectory("atomic");
        var path = Path.Combine(directory, "data.json");
        File.WriteAllText(path, "old content that is longer than the new one");

        AtomicFile.WriteAllBytes(path, [.. "new"u8]);

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void AtomicFile_commits_the_temp_file_to_the_device_before_the_move()
    {
        var source = SourceScan.Read("src", "AiUsage", "Io", "AtomicFile.cs");

        var flush = source.IndexOf("Flush(flushToDisk: true)", StringComparison.Ordinal);
        var move = source.IndexOf("File.Move(", StringComparison.Ordinal);
        Assert.True(flush >= 0 && move > flush, "the flush to the device must come before the move");
    }

    [Fact]
    public void A_half_written_statistics_backup_copy_is_cleaned_up_once_old()
    {
        var directory = TestPaths.CreateDirectory("stats-bak");
        var old = Path.Combine(directory, "stats.v6.bak.tmp");
        var fresh = Path.Combine(directory, "stats.v7.bak.tmp");
        var finished = Path.Combine(directory, "stats.v6.bak");
        File.WriteAllText(old, "partial");
        File.WriteAllText(fresh, "partial");
        File.WriteAllText(finished, "complete");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(finished, DateTime.UtcNow - TimeSpan.FromHours(2));

        AppPaths.CleanUpLeftoverTempFiles(directory);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(finished));
    }

    [Fact]
    public void Only_the_newest_two_quarantined_settings_copies_stay()
    {
        var directory = TestPaths.CreateDirectory("corrupt-copies");
        var settings = Path.Combine(directory, "settings.json");
        string[] stamps = ["20260101-100000", "20260301-100000", "20260201-100000", "20260401-100000"];
        foreach (var stamp in stamps)
            File.WriteAllText($"{settings}.corrupt-{stamp}", "bad");
        File.WriteAllText(Path.Combine(directory, "settings.json.bak"), "backup");

        SettingsStore.DeleteOlderCorruptCopies(settings);

        Assert.Equal(
            ["settings.json.corrupt-20260301-100000", "settings.json.corrupt-20260401-100000"],
            Directory.GetFiles(directory, "settings.json.corrupt-*").Select(Path.GetFileName).Order());
        Assert.True(File.Exists(Path.Combine(directory, "settings.json.bak")));
    }

    [Fact]
    public void A_corrupt_settings_file_is_quarantined_and_the_oldest_copies_go()
    {
        var directory = TestPaths.CreateDirectory("corrupt-load");
        File.WriteAllText(Path.Combine(directory, "settings.json.corrupt-20200101-000000"), "bad");
        File.WriteAllText(Path.Combine(directory, "settings.json.corrupt-20200102-000000"), "bad");
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{not valid json");
        using var store = new SettingsStore(directory);

        store.Load();

        var left = Directory.GetFiles(directory, "settings.json.corrupt-*").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain("settings.json.corrupt-20200101-000000", left);
    }

    [Fact]
    public void Maintain_rewrites_the_file_once_and_gives_the_result_of_compacting_then_pruning()
    {
        var oneStep = TestPaths.CreateDirectory("maintain-one");
        var twoSteps = TestPaths.CreateDirectory("maintain-two");
        var start = Now.AddDays(-60);
        var lines = Enumerable.Range(0, 40).Select(i => Line(start.AddMinutes(i), i == 17 ? 99.0 : i))
            .Append(Line(Now.AddDays(-400), 5))
            .Append(Line(Now.AddMinutes(-3), 42))
            .ToArray();
        File.WriteAllLines(Path.Combine(oneStep, "history-codex.jsonl"), lines);
        File.WriteAllLines(Path.Combine(twoSteps, "history-codex.jsonl"), lines);
        var rewrites = 0;
        var combined = new HistoryStore(oneStep, () => Now) { InsideRewriteLockForTest = _ => rewrites++ };
        var separate = new HistoryStore(twoSteps, () => Now);

        combined.Maintain("codex", retentionDays: 90);
        separate.Compact("codex");
        separate.Prune("codex", 90);

        Assert.Equal(1, rewrites);
        Assert.Equal(
            File.ReadAllText(Path.Combine(twoSteps, "history-codex.jsonl")),
            File.ReadAllText(Path.Combine(oneStep, "history-codex.jsonl")));
    }

    [Fact]
    public void A_chart_load_is_served_from_the_file_while_a_rewrite_holds_the_lock()
    {
        var directory = TestPaths.CreateDirectory("load-during-rewrite");
        File.WriteAllLines(Path.Combine(directory, "history-claude.jsonl"),
            [Line(Now.AddHours(-3), 10), Line(Now.AddHours(-2), 20), Line(Now.AddHours(-1), 30)]);
        var inside = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var store = new HistoryStore(directory, () => Now)
        {
            InsideRewriteLockForTest = _ =>
            {
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            },
        };

        var rewrite = Task.Run(() => store.Maintain("claude", retentionDays: 90));
        try
        {
            Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));

            var load = Task.Run(() => store.Load("claude", TimeSpan.FromDays(1)));

            Assert.True(load.Wait(TimeSpan.FromSeconds(5)), "a load must not wait for a rewrite in progress");
            Assert.Equal([10.0, 20.0, 30.0], load.Result.Select(point => point.Percent));
        }
        finally
        {
            release.Set();
            rewrite.Wait(TimeSpan.FromSeconds(10));
        }
    }
}
