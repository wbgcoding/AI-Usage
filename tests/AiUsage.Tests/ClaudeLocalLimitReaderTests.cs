using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

public class ClaudeLocalLimitReaderTests : IDisposable
{
    private static readonly string FixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T10:00:00Z");

    [Fact]
    public void FindActiveLimit_reads_a_future_reset_from_the_newest_rejection()
    {
        var directory = ProjectsDirectoryWith("claude-429.jsonl");

        var limit = ClaudeLocalLimitReader.FindActiveLimit(directory, Now);

        Assert.NotNull(limit);
        Assert.Equal("five_hour", limit!.RateLimitType);
        Assert.Equal(DateTimeOffset.Parse("2026-09-05T13:00:00Z"), limit.ResetsAt);
    }

    [Fact]
    public void FindActiveLimit_finds_nothing_when_no_line_carries_quota_limits()
    {
        var directory = ProjectsDirectoryWith("claude-no-limits.jsonl");

        var limit = ClaudeLocalLimitReader.FindActiveLimit(directory, Now);

        Assert.Null(limit);
    }

    [Fact]
    public void FindActiveLimit_returns_null_once_the_newest_logged_reset_is_in_the_past()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "session.jsonl"),
            """{"quotaLimits":{"status":"rejected","resetsAt":1788598800,"rateLimitType":"five_hour"},"error":"rate_limit"}""" + "\n");

        var limit = ClaudeLocalLimitReader.FindActiveLimit(directory, Now);

        Assert.Null(limit);
    }

    [Fact]
    public void FindActiveLimit_skips_a_line_whose_reset_timestamp_is_out_of_range()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "session.jsonl"),
            """{"quotaLimits":{"status":"rejected","resetsAt":99999999999999,"rateLimitType":"five_hour"},"error":"rate_limit"}""" + "\n");

        var limit = ClaudeLocalLimitReader.FindActiveLimit(directory, Now);

        Assert.Null(limit);
    }

    [Fact]
    public void FindActiveLimit_skips_a_line_whose_reset_timestamp_is_implausibly_distant()
    {
        // 253402300799 is 9999-12-31T23:59:59Z: representable, so FromUnixSecondsOrNull alone would
        // accept it - the plausibility bound is what has to reject it as a stuck "forever" limit.
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "session.jsonl"),
            """{"quotaLimits":{"status":"rejected","resetsAt":253402300799,"rateLimitType":"five_hour"},"error":"rate_limit"}""" + "\n");

        var limit = ClaudeLocalLimitReader.FindActiveLimit(directory, Now);

        Assert.Null(limit);
    }

    [Fact]
    public void FindActiveLimit_keeps_scanning_past_an_expired_record_for_an_older_but_still_active_one()
    {
        // The five_hour rejection is written LAST (newest, but already expired by Now); the
        // seven_day rejection was written first (older) and is still in force. ReverseLineReader
        // sees the expired one first - the fix must not stop there.
        var directory = TempDirectory();
        var activeSevenDay = DateTimeOffset.Parse("2026-09-12T10:00:00Z").ToUnixTimeSeconds();
        var expiredFiveHour = DateTimeOffset.Parse("2026-09-05T08:00:00Z").ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(directory, "session.jsonl"),
            $"{{\"quotaLimits\":{{\"status\":\"rejected\",\"resetsAt\":{activeSevenDay},\"rateLimitType\":\"seven_day\"}}}}\n" +
            $"{{\"quotaLimits\":{{\"status\":\"rejected\",\"resetsAt\":{expiredFiveHour},\"rateLimitType\":\"five_hour\"}}}}\n");

        var limit = ClaudeLocalLimitReader.FindActiveLimit(directory, Now);

        Assert.NotNull(limit);
        Assert.Equal("seven_day", limit!.RateLimitType);
    }

    [Fact]
    public void FindActiveLimit_returns_null_when_the_projects_directory_is_missing()
    {
        var limit = ClaudeLocalLimitReader.FindActiveLimit(Path.Combine(FixturesDirectory, "does-not-exist"), Now);

        Assert.Null(limit);
    }

    private const string ActiveLimit =
        """{"quotaLimits":{"status":"rejected","resetsAt":1788613200,"rateLimitType":"five_hour"},"timestamp":"{0}"}""";

    private static string LimitLine(DateTimeOffset at) => ActiveLimit.Replace("{0}", at.ToString("o"));

    private static string PlainLine(DateTimeOffset at) =>
        """{"type":"user","timestamp":"{0}"}""".Replace("{0}", at.ToString("o"));

    [Fact]
    public void FindActiveLimit_stops_a_file_at_the_first_reverse_line_older_than_a_week()
    {
        var directory = TempDirectory();
        // The limit line precedes the final line; the final (first one read) is already 8 days old,
        // so nothing before it can still describe a live window.
        File.WriteAllLines(Path.Combine(directory, "session.jsonl"),
            [LimitLine(Now.AddDays(-9)), PlainLine(Now.AddDays(-8))]);

        Assert.Null(ClaudeLocalLimitReader.FindActiveLimit(directory, Now));
    }

    [Fact]
    public void FindActiveLimit_still_reads_past_recent_lines_to_the_limit_line()
    {
        var directory = TempDirectory();
        File.WriteAllLines(Path.Combine(directory, "session.jsonl"),
            [LimitLine(Now.AddDays(-1)), PlainLine(Now.AddHours(-1))]);

        Assert.NotNull(ClaudeLocalLimitReader.FindActiveLimit(directory, Now));
    }

    [Fact]
    public void FindActiveLimit_skips_a_file_last_written_more_than_a_week_ago()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllLines(path, [LimitLine(Now.AddHours(-1))]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-8).UtcDateTime);

        Assert.Null(ClaudeLocalLimitReader.FindActiveLimit(directory, Now));
    }

    private static string RejectionLine(string type, DateTimeOffset resetsAt) =>
        $"{{\"quotaLimits\":{{\"status\":\"rejected\",\"resetsAt\":{resetsAt.ToUnixTimeSeconds()},\"rateLimitType\":\"{type}\"}}}}";

    [Fact]
    public void FindActiveLimit_reads_nothing_from_an_unchanged_file_on_the_second_call()
    {
        var directory = ProjectsDirectoryWith("claude-429.jsonl");
        var first = new ClaudeLocalLimitReader.ReadStats();
        var second = new ClaudeLocalLimitReader.ReadStats();

        var a = ClaudeLocalLimitReader.FindActiveLimit(directory, Now, out _, first);
        var b = ClaudeLocalLimitReader.FindActiveLimit(directory, Now, out _, second);

        Assert.Equal(1, first.FilesRead);
        Assert.Equal(0, second.FilesRead);
        Assert.Equal(0, second.BytesRead);
        Assert.Equal(a, b);
        Assert.NotNull(b);
    }

    [Fact]
    public void FindActiveLimit_reads_only_the_appended_bytes_and_the_newer_line_wins()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.jsonl");
        var first = RejectionLine("five_hour", Now.AddHours(1)) + "\n";
        File.WriteAllText(path, first);
        Assert.Equal(Now.AddHours(1), ClaudeLocalLimitReader.FindActiveLimit(directory, Now)!.ResetsAt);

        var appended = RejectionLine("five_hour", Now.AddHours(4)) + "\n";
        File.AppendAllText(path, appended);
        var stats = new ClaudeLocalLimitReader.ReadStats();
        var limit = ClaudeLocalLimitReader.FindActiveLimit(directory, Now, out _, stats);

        Assert.Equal(Now.AddHours(4), limit!.ResetsAt);
        Assert.Equal(appended.Length, stats.BytesRead);
    }

    [Fact]
    public void FindActiveLimit_keeps_an_unfinished_last_line_out_of_the_cache_until_it_is_complete()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.jsonl");
        var line = RejectionLine("five_hour", Now.AddHours(2));
        File.WriteAllText(path, line[..40]);
        Assert.Null(ClaudeLocalLimitReader.FindActiveLimit(directory, Now));

        File.AppendAllText(path, line[40..] + "\n");

        Assert.Equal(Now.AddHours(2), ClaudeLocalLimitReader.FindActiveLimit(directory, Now)!.ResetsAt);
    }

    [Fact]
    public void FindActiveLimit_reads_a_last_line_without_newline_like_a_full_scan()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "session.jsonl"), RejectionLine("five_hour", Now.AddHours(2)));

        Assert.Equal(Now.AddHours(2), ClaudeLocalLimitReader.FindActiveLimit(directory, Now)!.ResetsAt);
    }

    [Fact]
    public void FindActiveLimit_rereads_a_file_that_was_replaced_by_a_shorter_one()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(path, RejectionLine("five_hour", Now.AddHours(5)) + "\n" + PlainLine(Now) + "\n");
        Assert.NotNull(ClaudeLocalLimitReader.FindActiveLimit(directory, Now));

        File.WriteAllText(path, RejectionLine("five_hour", Now.AddHours(-3)) + "\n");

        Assert.Null(ClaudeLocalLimitReader.FindActiveLimit(directory, Now));
    }

    private string ProjectsDirectoryWith(string fixtureName)
    {
        var directory = TempDirectory();
        File.Copy(Path.Combine(FixturesDirectory, fixtureName), Path.Combine(directory, "session.jsonl"));
        return directory;
    }

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-claude");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }
}
