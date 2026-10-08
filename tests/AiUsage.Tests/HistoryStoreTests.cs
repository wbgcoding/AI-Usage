using System.Diagnostics;
using System.Globalization;
using AiUsage.Models;
using AiUsage.Storage;

namespace AiUsage.Tests;

public class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T12:00:00Z");

    [Fact]
    public void Append_after_a_torn_last_line_keeps_the_new_point_readable()
    {
        var directory = TempDirectory();
        var store = new HistoryStore(directory, () => Now);
        File.WriteAllText(Path.Combine(directory, "history-codex.jsonl"), "{\"v\":1,\"t\":\"2026-09-05T11:00:00+00:00\",\"w\":");

        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);

        Assert.Single(store.Load("codex", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void DeleteAll_reports_whether_every_file_is_gone()
    {
        var directory = TempDirectory();
        var store = new HistoryStore(directory, () => Now);
        File.WriteAllText(Path.Combine(directory, "history-codex.jsonl"), "x");
        File.WriteAllText(Path.Combine(directory, "history-claude.jsonl"), "x");

        Assert.True(store.DeleteAll(["codex", "missing"]));
        Assert.False(File.Exists(Path.Combine(directory, "history-codex.jsonl")));

        using (new FileStream(Path.Combine(directory, "history-claude.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(store.DeleteAll(["claude"]));
        Assert.True(File.Exists(Path.Combine(directory, "history-claude.jsonl")));
    }

    [Fact]
    public void Load_does_not_stop_at_a_valid_json_line_without_a_timestamp()
    {
        var directory = TempDirectory();
        var store = new HistoryStore(directory, () => Now);
        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);
        File.AppendAllText(Path.Combine(directory, "history-codex.jsonl"), "{}\n");

        Assert.Single(store.Load("codex", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Append_writes_nothing_when_the_value_is_unchanged_within_five_minutes()
    {
        var directory = TempDirectory();
        var clock = Now;
        var store = new HistoryStore(directory, () => clock);

        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);
        clock = clock.AddMinutes(2);
        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);

        Assert.Single(store.Load("codex", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Append_writes_a_separate_file_for_a_second_account_of_the_same_provider()
    {
        var directory = TempDirectory();
        var store = new HistoryStore(directory, () => Now);

        store.Append("claude", WindowKind.FiveHour, 10, resetsAt: null);
        store.Append("claude#2", WindowKind.FiveHour, 20, resetsAt: null);

        Assert.True(File.Exists(Path.Combine(directory, "history-claude.jsonl")));
        Assert.True(File.Exists(Path.Combine(directory, "history-claude-2.jsonl")));
        Assert.Single(store.Load("claude", TimeSpan.FromDays(1)));
        Assert.Single(store.Load("claude#2", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Append_writes_again_once_five_minutes_have_passed()
    {
        var directory = TempDirectory();
        var clock = Now;
        var store = new HistoryStore(directory, () => clock);

        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);
        clock = clock.AddMinutes(6);
        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);

        Assert.Equal(2, store.Load("codex", TimeSpan.FromDays(1)).Count);
    }

    [Fact]
    public void Append_writes_immediately_when_the_value_changes()
    {
        var directory = TempDirectory();
        var clock = Now;
        var store = new HistoryStore(directory, () => clock);

        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);
        clock = clock.AddSeconds(1);
        store.Append("codex", WindowKind.FiveHour, 43.0, resetsAt: null);

        Assert.Equal(2, store.Load("codex", TimeSpan.FromDays(1)).Count);
    }

    [Fact]
    public void Load_skips_a_corrupt_line_in_the_middle_and_returns_the_rest_in_order()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        File.WriteAllLines(path,
        [
            Line(Now.AddMinutes(-10), "FiveHour", 10),
            "{ this is not valid json",
            Line(Now.AddMinutes(-5), "FiveHour", 20),
        ]);
        var store = new HistoryStore(directory, () => Now);

        var points = store.Load("codex", TimeSpan.FromDays(1));

        Assert.Equal(2, points.Count);
        Assert.Equal(10, points[0].Percent);
        Assert.Equal(20, points[1].Percent);
    }

    [Fact]
    public void Prune_removes_points_older_than_the_retention_window_and_keeps_today()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        File.WriteAllLines(path,
        [
            Line(Now.AddDays(-2), "FiveHour", 10),
            Line(Now, "FiveHour", 20),
        ]);
        var store = new HistoryStore(directory, () => Now);

        store.Prune("codex", retentionDays: 1);

        var remaining = store.Load("codex", TimeSpan.FromDays(3650));
        Assert.Single(remaining);
        Assert.Equal(20, remaining[0].Percent);
    }

    [Fact]
    public void Prune_leaves_the_file_untouched_when_it_cannot_be_read()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        File.WriteAllLines(path,
        [
            Line(Now.AddDays(-2), "FiveHour", 10),
            Line(Now, "FiveHour", 20),
        ]);
        var before = File.ReadAllBytes(path);
        var store = new HistoryStore(directory, () => Now);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Prune("codex", retentionDays: 1);
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Compact_leaves_the_file_untouched_when_it_cannot_be_read()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        File.WriteAllLines(path, [Line(Now.AddDays(-60), "FiveHour", 55)]);
        var before = File.ReadAllBytes(path);
        var store = new HistoryStore(directory, () => Now);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Compact("codex");
        }

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Compact_leaves_the_file_untouched_when_it_contains_a_line_from_a_newer_schema_version()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        var futureVersionLine = Line(Now.AddDays(-60), "FiveHour", 55)
            .Replace("\"v\":1", $"\"v\":{HistoryStore.SchemaVersion + 1}");
        File.WriteAllLines(path,
        [
            Line(Now.AddDays(-60), "FiveHour", 42), // old enough that Compact would otherwise touch it
            futureVersionLine,
        ]);
        var before = File.ReadAllBytes(path);
        var store = new HistoryStore(directory, () => Now);

        store.Compact("codex");

        Assert.Equal(before, File.ReadAllBytes(path));

        var loaded = store.Load("codex", TimeSpan.FromDays(3650));
        Assert.Single(loaded); // the future-version line is skipped, never treated as garbage or trusted
        Assert.Equal(42, loaded[0].Percent);
    }

    [Fact]
    public void Compact_reduces_forty_points_from_one_hour_sixty_days_ago_to_the_hours_peak()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        var hourAgo = Now.AddDays(-60);
        var lines = Enumerable.Range(0, 40)
            .Select(i => Line(hourAgo.AddMinutes(i), "FiveHour", i == 17 ? 99.0 : i))
            .ToArray();
        File.WriteAllLines(path, lines);
        var store = new HistoryStore(directory, () => Now);

        store.Compact("codex");

        var remaining = store.Load("codex", TimeSpan.FromDays(3650));
        Assert.Single(remaining);
        Assert.Equal(99.0, remaining[0].Percent);
        Assert.Equal(1, remaining[0].CompactionLevel);
    }

    [Fact]
    public void Compact_reduces_a_days_points_from_two_years_ago_to_one_daily_point()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        var dayStart = Now.AddYears(-2).Date;
        var lines = Enumerable.Range(0, 24)
            .Select(hour => Line(new DateTimeOffset(dayStart, TimeSpan.Zero).AddHours(hour), "FiveHour", hour == 3 ? 77.0 : hour))
            .ToArray();
        File.WriteAllLines(path, lines);
        var store = new HistoryStore(directory, () => Now);

        store.Compact("codex");

        var remaining = store.Load("codex", TimeSpan.FromDays(3650));
        Assert.Single(remaining);
        Assert.Equal(77.0, remaining[0].Percent);
        Assert.Equal(2, remaining[0].CompactionLevel);
    }

    [Fact]
    public void Compact_leaves_points_from_the_last_thirty_days_untouched()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        File.WriteAllLines(path,
        [
            Line(Now.AddDays(-5), "FiveHour", 11),
            Line(Now.AddDays(-6), "FiveHour", 12),
        ]);
        var store = new HistoryStore(directory, () => Now);

        store.Compact("codex");

        var remaining = store.Load("codex", TimeSpan.FromDays(3650));
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, point => Assert.Null(point.CompactionLevel));
    }

    [Fact]
    public void Compact_never_re_compacts_an_already_marked_point()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        File.WriteAllLines(path, [Line(Now.AddDays(-60), "FiveHour", 55, compactionLevel: 1)]);
        var store = new HistoryStore(directory, () => Now);

        store.Compact("codex");

        var remaining = store.Load("codex", TimeSpan.FromDays(3650));
        Assert.Single(remaining);
        Assert.Equal(1, remaining[0].CompactionLevel);
        Assert.Equal(55, remaining[0].Percent);
    }

    [Fact]
    public void Load_with_explicit_bounds_excludes_points_outside_either_end()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        File.WriteAllLines(path,
        [
            Line(Now.AddDays(-10), "FiveHour", 1), // before "from"
            Line(Now.AddDays(-8), "FiveHour", 2),  // inside the window
            Line(Now.AddDays(-7), "FiveHour", 3),  // inside the window
            Line(Now.AddDays(-6), "FiveHour", 4),  // after "to"
        ]);
        var store = new HistoryStore(directory, () => Now);

        var points = store.Load("codex", Now.AddDays(-8), Now.AddDays(-7));

        Assert.Equal(2, points.Count);
        Assert.Equal(2, points[0].Percent);
        Assert.Equal(3, points[1].Percent);
    }

    [Fact]
    public void Load_finds_the_recent_slice_of_thirty_thousand_lines_quickly()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        const int lineCount = 30_000;
        const int intervalMinutes = 17; // spans roughly a year, ending exactly at "Now"
        using (var writer = new StreamWriter(path, append: false))
        {
            var start = Now.AddMinutes(-(lineCount - 1) * intervalMinutes);
            for (var i = 0; i < lineCount; i++)
                writer.WriteLine(Line(start.AddMinutes(i * intervalMinutes), "FiveHour", i % 100));
        }

        var store = new HistoryStore(directory, () => Now);

        var stopwatch = Stopwatch.StartNew();
        var points = store.Load("codex", TimeSpan.FromHours(24));
        stopwatch.Stop();

        Assert.NotEmpty(points);
        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"Expected the 24h slice to load in well under 500 ms, took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void Append_reads_the_newest_point_off_disk_once_and_then_remembers_it()
    {
        var directory = TempDirectory();
        var clock = Now;
        var store = new HistoryStore(directory, () => clock);

        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);
        var readsAfterFirst = store.LastPointReadsForTest;
        clock = clock.AddMinutes(2);
        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);

        Assert.Equal(1, readsAfterFirst);
        Assert.Equal(readsAfterFirst, store.LastPointReadsForTest);
    }

    [Fact]
    public void Append_after_a_prune_answers_from_the_rewritten_file_without_reading_it_again()
    {
        var directory = TempDirectory();
        var clock = Now;
        var store = new HistoryStore(directory, () => clock);
        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);
        store.Prune("codex", retentionDays: 7);
        var readsBefore = store.LastPointReadsForTest;

        clock = clock.AddMinutes(2);
        store.Append("codex", WindowKind.FiveHour, 42.0, resetsAt: null);

        Assert.Equal(readsBefore, store.LastPointReadsForTest);
        Assert.Single(store.Load("codex", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Compact_rolls_an_hourly_point_past_the_year_mark_up_to_a_daily_one()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        File.WriteAllLines(path, [Line(Now.AddDays(-400), "FiveHour", 55, compactionLevel: 1)]);
        var store = new HistoryStore(directory, () => Now);

        store.Compact("codex");

        var remaining = store.Load("codex", TimeSpan.FromDays(3650));
        Assert.Single(remaining);
        Assert.Equal(2, remaining[0].CompactionLevel);
        Assert.Equal(55, remaining[0].Percent);
    }

    [Fact]
    public void Compact_buckets_the_hourly_stage_by_the_utc_hour_not_the_stored_offset()
    {
        // Both points sit in the same UTC hour (22:00) but in different hours of their own offsets
        // (00:xx and 23:xx). Reading the hour off the stored offset would make two buckets of them.
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        File.WriteAllLines(path,
        [
            Line(new DateTimeOffset(2026, 1, 15, 0, 30, 0, TimeSpan.FromHours(2)), "FiveHour", 31),
            Line(new DateTimeOffset(2026, 1, 14, 23, 45, 0, TimeSpan.FromHours(1)), "FiveHour", 64),
        ]);
        var store = new HistoryStore(directory, () => Now);

        store.Compact("codex");

        var remaining = store.Load("codex", TimeSpan.FromDays(3650));
        Assert.Single(remaining);
        Assert.Equal(64, remaining[0].Percent);
        Assert.Equal(1, remaining[0].CompactionLevel);
    }

    [Fact]
    public void TotalHistoryBytes_sums_the_sizes_of_the_named_providers_files_and_zero_for_a_missing_one()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "history-codex.jsonl"), new string('a', 100));
        File.WriteAllText(Path.Combine(directory, "history-claude.jsonl"), new string('b', 50));
        var store = new HistoryStore(directory, () => Now);

        var total = store.TotalHistoryBytes(["codex", "claude", "gemini"]);

        Assert.Equal(150, total);
    }

    private static string Line(DateTimeOffset timestamp, string window, double percent, int? compactionLevel = null) =>
        $$"""{"v":1,"t":"{{timestamp:O}}","w":"{{window}}","p":{{percent.ToString(CultureInfo.InvariantCulture)}},"r":null,"c":{{(compactionLevel is null ? "null" : compactionLevel.Value.ToString(CultureInfo.InvariantCulture))}}}""";

    [Fact]
    public void Old_Copilot_window_labels_read_and_rewrite_under_the_new_keys()
    {
        var directory = TempDirectory();
        var oldLine = LineWithLabel(Now.AddHours(-2), "Other", 20.0, "Chat");
        File.WriteAllText(Path.Combine(directory, "history-copilot.jsonl"), oldLine + Environment.NewLine);
        File.WriteAllText(Path.Combine(directory, "history-cursor.jsonl"), oldLine + Environment.NewLine);
        var store = new HistoryStore(directory, () => Now);

        // The chart keeps its past points, now keyed like the live ones.
        Assert.Equal("Window_CopilotChat", Assert.Single(store.Load("copilot", TimeSpan.FromDays(1))).Label);
        // Another provider's label of the same text is not touched.
        Assert.Equal("Chat", Assert.Single(store.Load("cursor", TimeSpan.FromDays(1))).Label);

        // A rewrite (prune) persists the new key.
        store.Prune("copilot", retentionDays: 30);
        Assert.Contains("Window_CopilotChat", File.ReadAllText(Path.Combine(directory, "history-copilot.jsonl")));
    }

    [Fact]
    public void Two_other_labels_never_suppress_each_others_write()
    {
        var directory = TempDirectory();
        var clock = Now;
        var store = new HistoryStore(directory, () => clock);

        store.Append("cursor", WindowKind.Other, 10, resetsAt: null, label: "Window_CursorModels");
        // A different label, same provider and kind, same instant - must still write: sharing the
        // "last point" cache entry with the other label would have suppressed this as "unchanged".
        store.Append("cursor", WindowKind.Other, 90, resetsAt: null, label: "Window_OtherModels");

        var points = store.Load("cursor", TimeSpan.FromDays(1));
        Assert.Equal(2, points.Count);
        Assert.Contains(points, p => p.Label == "Window_CursorModels" && p.Percent == 10);
        Assert.Contains(points, p => p.Label == "Window_OtherModels" && p.Percent == 90);
    }

    [Fact]
    public void An_old_line_without_a_label_still_reads()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "history-copilot.jsonl"), Line(Now, "Other", 40) + "\n");
        var store = new HistoryStore(directory, () => Now);

        var points = store.Load("copilot", TimeSpan.FromDays(1));

        var point = Assert.Single(points);
        Assert.Null(point.Label);
        Assert.Equal(40, point.Percent);
    }

    [Fact]
    public void Compact_never_mixes_the_peak_of_two_different_other_labels()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-cursor.jsonl");
        var hourAgo = Now.AddDays(-60);
        var lines = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            lines.Add(LineWithLabel(hourAgo.AddMinutes(i), "Other", i == 2 ? 5.0 : 1.0, "Window_CursorModels"));
            lines.Add(LineWithLabel(hourAgo.AddMinutes(i), "Other", i == 2 ? 99.0 : 50.0, "Window_OtherModels"));
        }
        File.WriteAllLines(path, lines);
        var store = new HistoryStore(directory, () => Now);

        store.Compact("cursor");

        var remaining = store.Load("cursor", TimeSpan.FromDays(3650));
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, p => p.Label == "Window_CursorModels" && p.Percent == 5.0);
        Assert.Contains(remaining, p => p.Label == "Window_OtherModels" && p.Percent == 99.0);
    }

    private static string LineWithLabel(DateTimeOffset timestamp, string window, double percent, string label) =>
        $$"""{"v":1,"t":"{{timestamp:O}}","w":"{{window}}","p":{{percent.ToString(CultureInfo.InvariantCulture)}},"r":null,"c":null,"l":"{{label}}"}""";

    [Fact]
    public void An_append_for_another_provider_completes_while_a_compaction_holds_its_lock()
    {
        var directory = TempDirectory();
        var store = new HistoryStore(directory, () => Now);
        store.Append("claude", WindowKind.FiveHour, 10.0, resetsAt: null);
        var inside = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        store.InsideRewriteLockForTest = provider =>
        {
            if (provider != "claude")
                return;
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        var compaction = Task.Run(() => store.Compact("claude"));
        try
        {
            Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));

            var append = Task.Run(() => store.Append("codex", WindowKind.FiveHour, 20.0, resetsAt: null));

            Assert.True(append.Wait(TimeSpan.FromSeconds(5)), "an append for another provider must not wait for the rewrite");
            Assert.Single(store.Load("codex", TimeSpan.FromDays(1)));
        }
        finally
        {
            release.Set();
            compaction.Wait(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void An_append_for_the_provider_being_rewritten_is_queued_and_lands_after_the_rewrite()
    {
        var directory = TempDirectory();
        var store = new HistoryStore(directory, () => Now);
        store.Append("claude", WindowKind.FiveHour, 10.0, resetsAt: null);
        var inside = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        store.InsideRewriteLockForTest = _ =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        var compaction = Task.Run(() => store.Compact("claude"));
        try
        {
            Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));

            var append = Task.Run(() => store.Append("claude", WindowKind.FiveHour, 55.0, resetsAt: null));

            Assert.True(append.Wait(TimeSpan.FromSeconds(5)), "the append must queue instead of waiting for the rewrite");
        }
        finally
        {
            release.Set();
            compaction.Wait(TimeSpan.FromSeconds(10));
        }

        Assert.Equal([10.0, 55.0], store.Load("claude", TimeSpan.FromDays(1)).Select(point => point.Percent));
    }

    [Fact]
    public void Load_of_an_unchanged_file_parses_no_line_the_second_time()
    {
        var directory = TempDirectory();
        File.WriteAllLines(Path.Combine(directory, "history-codex.jsonl"),
            [Line(Now.AddHours(-3), "FiveHour", 10), Line(Now.AddHours(-2), "FiveHour", 20), Line(Now.AddHours(-1), "FiveHour", 30)]);
        var store = new HistoryStore(directory, () => Now);

        var first = store.Load("codex", TimeSpan.FromDays(1));
        var parsedAfterFirst = store.LoadLinesParsedForTest;
        var second = store.Load("codex", TimeSpan.FromDays(1));

        Assert.Equal(3, first.Count);
        Assert.Equal(3, parsedAfterFirst);
        Assert.Equal(parsedAfterFirst, store.LoadLinesParsedForTest);
        Assert.Equal(first.Select(p => p.Percent), second.Select(p => p.Percent));
    }

    [Fact]
    public void Load_after_an_append_parses_only_the_new_line_and_shows_it()
    {
        var directory = TempDirectory();
        File.WriteAllLines(Path.Combine(directory, "history-codex.jsonl"),
            [Line(Now.AddHours(-3), "FiveHour", 10), Line(Now.AddHours(-2), "FiveHour", 20)]);
        var store = new HistoryStore(directory, () => Now);
        Assert.Equal(2, store.Load("codex", TimeSpan.FromDays(1)).Count);
        var parsedBefore = store.LoadLinesParsedForTest;

        store.Append("codex", WindowKind.FiveHour, 55.0, resetsAt: null);
        var loaded = store.Load("codex", TimeSpan.FromDays(1));

        Assert.Equal([10.0, 20.0, 55.0], loaded.Select(p => p.Percent));
        Assert.Equal(parsedBefore + 1, store.LoadLinesParsedForTest);
    }

    [Fact]
    public void Load_after_a_compaction_rewrite_reads_the_rewritten_file()
    {
        var directory = TempDirectory();
        var start = Now.AddDays(-60);
        File.WriteAllLines(Path.Combine(directory, "history-codex.jsonl"),
            Enumerable.Range(0, 40).Select(i => Line(start.AddMinutes(i), "FiveHour", i == 17 ? 99.0 : i)));
        var store = new HistoryStore(directory, () => Now);
        Assert.Equal(40, store.Load("codex", TimeSpan.FromDays(3650)).Count);

        store.Compact("codex");
        var loaded = store.Load("codex", TimeSpan.FromDays(3650));

        Assert.Single(loaded);
        Assert.Equal(99.0, loaded[0].Percent);
    }

    [Fact]
    public void Load_hands_out_a_list_the_caller_may_change_without_touching_the_cache()
    {
        var directory = TempDirectory();
        File.WriteAllLines(Path.Combine(directory, "history-codex.jsonl"), [Line(Now.AddHours(-1), "FiveHour", 10)]);
        var store = new HistoryStore(directory, () => Now);

        ((List<HistoryPoint>)store.Load("codex", TimeSpan.FromDays(1))).Clear();

        Assert.Single(store.Load("codex", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Load_filters_the_cached_points_by_the_requested_range()
    {
        var directory = TempDirectory();
        File.WriteAllLines(Path.Combine(directory, "history-codex.jsonl"),
            [Line(Now.AddDays(-9), "FiveHour", 1), Line(Now.AddDays(-5), "FiveHour", 2), Line(Now.AddHours(-1), "FiveHour", 3)]);
        var store = new HistoryStore(directory, () => Now);
        Assert.Equal(3, store.Load("codex", TimeSpan.FromDays(30)).Count);

        Assert.Equal([3.0], store.Load("codex", TimeSpan.FromDays(1)).Select(p => p.Percent));
        Assert.Equal([2.0], store.Load("codex", Now.AddDays(-6), Now.AddDays(-4)).Select(p => p.Percent));
    }

    [Fact]
    public void Load_reaching_further_back_than_an_earlier_load_still_finds_the_older_points()
    {
        var directory = TempDirectory();
        File.WriteAllLines(Path.Combine(directory, "history-codex.jsonl"),
            [Line(Now.AddDays(-9), "FiveHour", 1), Line(Now.AddDays(-5), "FiveHour", 2), Line(Now.AddHours(-1), "FiveHour", 3)]);
        var store = new HistoryStore(directory, () => Now);

        Assert.Equal([3.0], store.Load("codex", TimeSpan.FromDays(1)).Select(p => p.Percent));
        Assert.Equal([2.0, 3.0], store.Load("codex", TimeSpan.FromDays(7)).Select(p => p.Percent));
        Assert.Equal([1.0, 2.0, 3.0], store.Load("codex", TimeSpan.FromDays(30)).Select(p => p.Percent));
        Assert.Equal([3.0], store.Load("codex", TimeSpan.FromDays(1)).Select(p => p.Percent));
    }

    [Fact]
    public void Load_reads_a_file_that_was_replaced_by_a_shorter_one()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        File.WriteAllLines(path, [Line(Now.AddHours(-3), "FiveHour", 10), Line(Now.AddHours(-2), "FiveHour", 20)]);
        var store = new HistoryStore(directory, () => Now);
        Assert.Equal(2, store.Load("codex", TimeSpan.FromDays(1)).Count);

        File.WriteAllLines(path, [Line(Now.AddHours(-1), "FiveHour", 77)]);

        Assert.Equal([77.0], store.Load("codex", TimeSpan.FromDays(1)).Select(p => p.Percent));
        File.Delete(path);
        Assert.Empty(store.Load("codex", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Load_follows_the_store_to_a_new_data_folder()
    {
        var first = TempDirectory();
        var second = TempDirectory();
        File.WriteAllLines(Path.Combine(first, "history-codex.jsonl"), [Line(Now.AddHours(-1), "FiveHour", 10)]);
        File.WriteAllLines(Path.Combine(second, "history-codex.jsonl"),
            [Line(Now.AddHours(-2), "FiveHour", 5), Line(Now.AddHours(-1), "FiveHour", 6)]);
        var store = new HistoryStore(first, () => Now);
        Assert.Single(store.Load("codex", TimeSpan.FromDays(1)));

        store.Redirect(second);

        Assert.Equal([5.0, 6.0], store.Load("codex", TimeSpan.FromDays(1)).Select(p => p.Percent));
    }

    [Fact]
    public void Load_shows_a_last_line_without_newline_but_does_not_cache_it()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "history-codex.jsonl");
        var line = Line(Now.AddHours(-1), "FiveHour", 42);
        File.WriteAllText(path, Line(Now.AddHours(-2), "FiveHour", 1) + "\n" + line);
        var store = new HistoryStore(directory, () => Now);

        Assert.Equal([1.0, 42.0], store.Load("codex", TimeSpan.FromDays(1)).Select(p => p.Percent));

        File.AppendAllText(path, "\n");
        Assert.Equal([1.0, 42.0], store.Load("codex", TimeSpan.FromDays(1)).Select(p => p.Percent));
    }

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-history");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }
}
