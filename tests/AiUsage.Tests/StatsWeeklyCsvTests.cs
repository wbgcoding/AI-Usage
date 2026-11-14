using AiUsage.Services;
using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>
/// The CSV of the previous ISO week the token index saves after a walk: the week and the file name, one
/// file per week, nothing when the switch is off, and a folder that is gone reported once per week.
/// </summary>
public class StatsWeeklyCsvTests
{
    // A Wednesday: the previous ISO week is Monday 2026-11-09 to Sunday 2026-11-15, week 46.
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);

    private static string Line(string timestamp, string messageId, long input, long output) =>
        "{\"type\":\"assistant\",\"sessionId\":\"sess-1\",\"requestId\":\"r-" + messageId + "\",\"cwd\":\"C:\\\\Projects\\\\Sample\",\"timestamp\":\"" +
        timestamp + "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"modelA\",\"usage\":{\"input_tokens\":" + input +
        ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}";

    private static void WriteSession(string root, params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "C--Projects-Sample")).FullName;
        File.WriteAllText(Path.Combine(dir, "sess-1.jsonl"), string.Concat(lines.Select(line => line + "\n")));
    }

    private static void RunWalk(StatsIndexerService service)
    {
        // The run slot is taken before StartInBackground returns, so it is free again exactly when the walk is over.
        service.StartInBackground();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (service.IsRunning && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.False(service.IsRunning);
    }

    [Theory]
    [InlineData("2026-11-18", "2026-11-09", "2026-11-15", "AI-Usage-2026-W46.csv")]
    [InlineData("2026-11-16", "2026-11-09", "2026-11-15", "AI-Usage-2026-W46.csv")]
    [InlineData("2026-11-15", "2026-11-02", "2026-11-08", "AI-Usage-2026-W45.csv")]
    [InlineData("2027-01-04", "2026-12-28", "2027-01-03", "AI-Usage-2026-W53.csv")]
    [InlineData("2026-01-05", "2025-12-29", "2026-01-04", "AI-Usage-2026-W01.csv")]
    public void The_previous_week_is_the_ISO_week_before_the_current_one(string today, string from, string to, string file)
    {
        var week = StatsWeeklyCsv.PreviousWeek(DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), week.From);
        Assert.Equal(DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), week.To);
        Assert.Equal(file, week.FileName);
    }

    [Fact]
    public void A_walk_writes_one_file_for_the_previous_week_and_a_second_walk_writes_none()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("weekly-csv-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("weekly-csv-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("weekly-csv-data");
        using var target = TestPaths.CreateDisposableDirectory("weekly-csv-target");
        WriteSession(claudeRoot,
            Line("2026-11-11T12:00:00Z", "m1", 100, 50),
            Line("2026-11-17T12:00:00Z", "m2", 7000, 7000));
        var store = new StatsStore(dataDir);
        using var service = new StatsIndexerService(store, () => new StatsIndexer(store, claudeRoot, codexRoot))
        {
            WeeklyCsvFolder = target,
            Clock = () => Now,
        };

        RunWalk(service);

        var file = Assert.Single(Directory.GetFiles(target));
        Assert.Equal("AI-Usage-2026-W46.csv", Path.GetFileName(file));
        var text = File.ReadAllText(file);
        Assert.StartsWith(StatsExport.Header, text, StringComparison.Ordinal);
        Assert.Contains("2026-11-11", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-11-17", text, StringComparison.Ordinal);
        Assert.Contains("total,,,,,,,,,100,50,0,0,150,", text, StringComparison.Ordinal);

        File.WriteAllText(file, "kept");
        RunWalk(service);

        Assert.Equal(file, Assert.Single(Directory.GetFiles(target)));
        Assert.Equal("kept", File.ReadAllText(file));
    }

    [Fact]
    public void With_no_folder_chosen_a_walk_writes_nothing()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("weekly-csv-off-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("weekly-csv-off-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("weekly-csv-off-data");
        using var target = TestPaths.CreateDisposableDirectory("weekly-csv-off-target");
        WriteSession(claudeRoot, Line("2026-11-11T12:00:00Z", "m1", 100, 50));
        var store = new StatsStore(dataDir);
        using var service = new StatsIndexerService(store, () => new StatsIndexer(store, claudeRoot, codexRoot)) { Clock = () => Now };

        RunWalk(service);
        service.WeeklyCsvFolder = target;
        service.WeeklyCsvFolder = null;
        RunWalk(service);

        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void A_missing_folder_is_reported_once_per_week_and_never_throws()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("weekly-csv-gone-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("weekly-csv-gone-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("weekly-csv-gone-data");
        using var logDir = TestPaths.CreateDisposableDirectory("weekly-csv-gone-log");
        WriteSession(claudeRoot, Line("2026-11-11T12:00:00Z", "m1", 100, 50));
        var log = new LogService(logDir);
        var store = new StatsStore(dataDir);
        var clock = Now;
        using var service = new StatsIndexerService(store, () => new StatsIndexer(store, claudeRoot, codexRoot) { Log = log }, log)
        {
            WeeklyCsvFolder = Path.Combine(dataDir, "no-such-folder"),
            Clock = () => clock,
        };

        for (var run = 0; run < 3; run++)
            RunWalk(service);
        Assert.Equal(1, ReportedLines(log));

        clock = Now.AddDays(7);
        RunWalk(service);
        Assert.Equal(2, ReportedLines(log));
        Assert.False(Directory.Exists(Path.Combine(dataDir, "no-such-folder")));
    }

    [Fact]
    public void A_finished_file_leaves_no_temporary_file_and_a_file_already_there_is_not_touched()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("weekly-csv-direct-data");
        using var target = TestPaths.CreateDisposableDirectory("weekly-csv-direct-target");
        var store = new StatsStore(dataDir);
        var today = new DateOnly(2026, 11, 18);

        Assert.Equal(StatsWeeklyCsv.Outcome.Written, StatsWeeklyCsv.Write(target, today, store, cost: null));
        Assert.Equal(["AI-Usage-2026-W46.csv"], Directory.GetFiles(target).Select(Path.GetFileName));
        Assert.Equal(StatsWeeklyCsv.Outcome.AlreadyThere, StatsWeeklyCsv.Write(target, today, store, cost: null));
        Assert.Equal(StatsWeeklyCsv.Outcome.FolderMissing, StatsWeeklyCsv.Write(Path.Combine(target, "gone"), today, store, cost: null));
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    // The current week of 2026-11-18 (a Wednesday) runs Monday 2026-11-16 to Sunday 2026-11-22.
    [Theory]
    [InlineData(DayOfWeek.Monday, 0, 2026, 11, 16, 0, 0, true)]
    [InlineData(DayOfWeek.Monday, 5, 2026, 11, 16, 4, 59, false)]
    [InlineData(DayOfWeek.Wednesday, 10, 2026, 11, 18, 9, 59, false)]
    [InlineData(DayOfWeek.Wednesday, 10, 2026, 11, 18, 10, 0, true)]
    [InlineData(DayOfWeek.Wednesday, 10, 2026, 11, 20, 3, 0, true)]
    [InlineData(DayOfWeek.Thursday, 0, 2026, 11, 18, 23, 59, false)]
    [InlineData(DayOfWeek.Sunday, 0, 2026, 11, 16, 8, 0, false)]
    [InlineData(DayOfWeek.Sunday, 0, 2026, 11, 21, 23, 59, false)]
    [InlineData(DayOfWeek.Sunday, 0, 2026, 11, 22, 0, 0, true)]
    [InlineData(DayOfWeek.Sunday, 23, 2026, 11, 22, 22, 0, false)]
    public void The_file_is_due_from_the_chosen_day_and_hour_of_the_current_week(
        DayOfWeek day, int hour, int year, int month, int dayOfMonth, int atHour, int atMinute, bool due)
    {
        var now = new DateTime(year, month, dayOfMonth, atHour, atMinute, 0, DateTimeKind.Local);

        Assert.Equal(due, StatsWeeklyCsv.IsDue(now, day, hour));
    }

    [Fact]
    public void A_walk_before_the_due_time_writes_nothing_and_the_first_walk_after_it_writes_once()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("weekly-csv-due-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("weekly-csv-due-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("weekly-csv-due-data");
        using var target = TestPaths.CreateDisposableDirectory("weekly-csv-due-target");
        WriteSession(claudeRoot, Line("2026-11-11T12:00:00Z", "m1", 100, 50));
        var store = new StatsStore(dataDir);
        var clock = Local(2026, 11, 18, 9, 59);
        using var service = new StatsIndexerService(store, () => new StatsIndexer(store, claudeRoot, codexRoot))
        {
            WeeklyCsvFolder = target,
            WeeklyCsvDay = DayOfWeek.Wednesday,
            WeeklyCsvHour = 10,
            Clock = () => clock,
        };

        RunWalk(service);
        Assert.Empty(Directory.GetFiles(target));

        clock = Local(2026, 11, 18, 10, 5);
        RunWalk(service);
        var file = Assert.Single(Directory.GetFiles(target));
        Assert.Equal("AI-Usage-2026-W46.csv", Path.GetFileName(file));

        File.WriteAllText(file, "kept");
        clock = Local(2026, 11, 19, 8, 0);
        RunWalk(service);
        Assert.Equal("kept", File.ReadAllText(file));
    }

    [Fact]
    public void An_app_that_was_not_running_at_the_due_time_catches_up_on_its_next_walk()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("weekly-csv-late-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("weekly-csv-late-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("weekly-csv-late-data");
        using var target = TestPaths.CreateDisposableDirectory("weekly-csv-late-target");
        WriteSession(claudeRoot, Line("2026-11-11T12:00:00Z", "m1", 100, 50));
        var store = new StatsStore(dataDir);
        using var service = new StatsIndexerService(store, () => new StatsIndexer(store, claudeRoot, codexRoot))
        {
            WeeklyCsvFolder = target,
            WeeklyCsvDay = DayOfWeek.Tuesday,
            WeeklyCsvHour = 6,
            Clock = () => Local(2026, 11, 21, 17, 0),
        };

        RunWalk(service);

        Assert.Equal(["AI-Usage-2026-W46.csv"], Directory.GetFiles(target).Select(Path.GetFileName));
    }

    [Fact]
    public void A_sunday_due_day_waits_for_the_end_of_the_Monday_to_Sunday_week()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("weekly-csv-sun-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("weekly-csv-sun-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("weekly-csv-sun-data");
        using var target = TestPaths.CreateDisposableDirectory("weekly-csv-sun-target");
        WriteSession(claudeRoot, Line("2026-11-11T12:00:00Z", "m1", 100, 50));
        var store = new StatsStore(dataDir);
        var clock = Local(2026, 11, 21, 23, 0);
        using var service = new StatsIndexerService(store, () => new StatsIndexer(store, claudeRoot, codexRoot))
        {
            WeeklyCsvFolder = target,
            WeeklyCsvDay = DayOfWeek.Sunday,
            WeeklyCsvHour = 0,
            Clock = () => clock,
        };

        RunWalk(service);
        Assert.Empty(Directory.GetFiles(target));

        clock = Local(2026, 11, 22, 0, 30);
        RunWalk(service);
        Assert.Equal(["AI-Usage-2026-W46.csv"], Directory.GetFiles(target).Select(Path.GetFileName));
    }

    private static int ReportedLines(LogService log) =>
        File.Exists(log.CurrentFile)
            ? File.ReadAllLines(log.CurrentFile).Count(line => line.Contains("Statistics weekly CSV", StringComparison.Ordinal))
            : 0;
}
