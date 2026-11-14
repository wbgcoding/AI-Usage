using AiUsage.Services;
using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>
/// Extra Claude session folders added in Settings: read on every walk like the machine's own, a copy
/// of an indexed file adds nothing, an offline folder is skipped quietly, and one folder can be read
/// once on demand.
/// </summary>
public class StatsExtraFoldersTests
{
    private static readonly DateTime Old = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private static string Line(string timestamp, string messageId, long input, long output, string sessionId = "sess-1") =>
        "{\"type\":\"assistant\",\"sessionId\":\"" + sessionId + "\",\"requestId\":\"r-" + messageId + "\",\"cwd\":\"C:\\\\Projects\\\\Sample\",\"timestamp\":\"" +
        timestamp + "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"modelA\",\"usage\":{\"input_tokens\":" + input +
        ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}";

    private static string Write(string root, string name, params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "C--Projects-Sample")).FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Concat(lines.Select(line => line + "\n")));
        File.SetCreationTimeUtc(path, Old);
        File.SetLastWriteTimeUtc(path, Old);
        return path;
    }

    private static long Total(StatsStore store) => store.LoadAll().Sum(row => row.InputTokens + row.OutputTokens);

    private static int MissingFolderLines(LogService log) =>
        File.Exists(log.CurrentFile)
            ? File.ReadAllLines(log.CurrentFile).Count(line => line.Contains("an added session folder is not available", StringComparison.Ordinal))
            : 0;

    [Theory]
    [InlineData(@"D:\", @"D:\Sessions\a.jsonl", true)]
    [InlineData(@"D:\Sessions", @"D:\Sessions\a.jsonl", true)]
    [InlineData(@"D:\Sessions\", @"D:\sessions\sub\a.jsonl", true)]
    [InlineData(@"D:\Sessions", @"D:\SessionsOther\a.jsonl", false)]
    [InlineData(@"D:\", @"E:\Sessions\a.jsonl", false)]
    public void A_file_is_under_an_extra_folder_also_when_that_is_a_drive_root(string root, string path, bool expected)
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-extra-root-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-extra-root-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-extra-root-data");
        var indexer = new StatsIndexer(new StatsStore(dataDir), claudeRoot, codexRoot) { ExtraClaudeRoots = [root] };

        Assert.Equal(expected, indexer.IsUnderExtraRoot(path));
    }

    [Fact]
    public void A_folder_holding_a_copy_of_an_indexed_file_adds_no_tokens()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-extra-copy-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-extra-copy-codex");
        using var extraRoot = TestPaths.CreateDisposableDirectory("stats-extra-copy-extra");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-extra-copy-data");
        Write(claudeRoot, "sess-1.jsonl", Line("2026-01-01T10:00:00Z", "m1", 100, 50));
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(150L, Total(store));

        Write(extraRoot, "sess-1-copy.jsonl", Line("2026-01-01T10:00:00Z", "m1", 100, 50));
        new StatsIndexer(store, claudeRoot, codexRoot) { ExtraClaudeRoots = [extraRoot] }.IndexOnce();

        Assert.Equal(150L, Total(store));
    }

    [Fact]
    public void A_folder_with_a_new_session_adds_its_tokens_once()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-extra-new-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-extra-new-codex");
        using var extraRoot = TestPaths.CreateDisposableDirectory("stats-extra-new-extra");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-extra-new-data");
        Write(claudeRoot, "sess-1.jsonl", Line("2026-01-01T10:00:00Z", "m1", 100, 50));
        Write(extraRoot, "sess-2.jsonl", Line("2026-01-02T10:00:00Z", "m2", 200, 70, "sess-2"));
        var store = new StatsStore(dataDir);

        new StatsIndexer(store, claudeRoot, codexRoot) { ExtraClaudeRoots = [extraRoot] }.IndexOnce();
        Assert.Equal(150L + 270L, Total(store));

        new StatsIndexer(store, claudeRoot, codexRoot) { ExtraClaudeRoots = [extraRoot] }.IndexOnce();
        Assert.Equal(150L + 270L, Total(store));
    }

    [Fact]
    public void An_offline_folder_is_skipped_without_an_error_and_reported_once_per_run()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-extra-off-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-extra-off-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-extra-off-data");
        using var logDir = TestPaths.CreateDisposableDirectory("stats-extra-off-log");
        Write(claudeRoot, "sess-1.jsonl", Line("2026-01-01T10:00:00Z", "m1", 100, 50));
        var offline = Path.Combine(dataDir, "no-such-drive", "projects");
        var log = new LogService(logDir);
        var store = new StatsStore(dataDir);
        using var service = new StatsIndexerService(store, () => new StatsIndexer(store, claudeRoot, codexRoot) { Log = log }, log)
        {
            ExtraClaudeRoots = [offline],
        };

        for (var run = 0; run < 3; run++)
            RunWalk(service);

        Assert.Equal(150L, Total(store));
        Assert.Equal(1, MissingFolderLines(log));
        Assert.DoesNotContain("ERROR", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
    }

    [Fact]
    public void A_locked_file_in_an_added_folder_does_not_hold_a_one_time_pass_back_but_one_in_the_own_folder_does()
    {
        foreach (var lockedInExtra in new[] { true, false })
        {
            using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-extra-lock-claude");
            using var codexRoot = TestPaths.CreateDisposableDirectory("stats-extra-lock-codex");
            using var extraRoot = TestPaths.CreateDisposableDirectory("stats-extra-lock-extra");
            using var dataDir = TestPaths.CreateDisposableDirectory("stats-extra-lock-data");
            var own = Write(claudeRoot, "sess-1.jsonl", Line("2026-01-01T10:00:00Z", "m1", 100, 5));
            var added = Write(extraRoot, "sess-2.jsonl", Line("2026-01-02T10:00:00Z", "m2", 100, 5, "sess-2"));
            var store = new StatsStore(dataDir);
            new StatsIndexer(store, claudeRoot, codexRoot) { ExtraClaudeRoots = [extraRoot] }.IndexOnce();
            OfferSessionPass(Path.Combine(dataDir, "stats.db"));

            using (new FileStream(lockedInExtra ? added : own, FileMode.Open, FileAccess.Read, FileShare.None))
                new StatsIndexer(store, claudeRoot, codexRoot) { ExtraClaudeRoots = [extraRoot] }.IndexOnce();

            Assert.Equal(lockedInExtra, store.IsSessionBackfillDone());
        }
    }

    [Fact]
    public async Task Reading_a_folder_once_reports_the_files_read_and_the_ones_that_added_messages()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-extra-once-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-extra-once-codex");
        using var importRoot = TestPaths.CreateDisposableDirectory("stats-extra-once-import");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-extra-once-data");
        Write(claudeRoot, "sess-1.jsonl", Line("2026-01-01T10:00:00Z", "m1", 100, 50));
        Write(importRoot, "sess-1-copy.jsonl", Line("2026-01-01T10:00:00Z", "m1", 100, 50));
        Write(importRoot, "sess-2.jsonl", Line("2026-01-02T10:00:00Z", "m2", 200, 70, "sess-2"));
        var store = new StatsStore(dataDir);
        using var service = new StatsIndexerService(store, () => new StatsIndexer(store, claudeRoot, codexRoot));
        RunWalk(service);
        Assert.Equal(150L, Total(store));

        var (read, added) = await service.IndexFolderOnceAsync(importRoot);

        Assert.Equal(2, read);
        Assert.Equal(1, added);
        Assert.Equal(150L + 270L, Total(store));

        var (readAgain, addedAgain) = await service.IndexFolderOnceAsync(importRoot);

        Assert.Equal(0, addedAgain);
        Assert.True(readAgain <= 2);
        Assert.Equal(150L + 270L, Total(store));
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task Reading_a_folder_once_waits_for_a_running_walk()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-extra-wait-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-extra-wait-codex");
        using var importRoot = TestPaths.CreateDisposableDirectory("stats-extra-wait-import");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-extra-wait-data");
        Write(importRoot, "sess-2.jsonl", Line("2026-01-02T10:00:00Z", "m2", 200, 70, "sess-2"));
        var store = new StatsStore(dataDir);
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var walkIndexers = 0;
        using var service = new StatsIndexerService(store, () =>
        {
            if (Interlocked.Increment(ref walkIndexers) == 1)
            {
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }

            return new StatsIndexer(store, claudeRoot, codexRoot);
        });
        service.StartInBackground();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        var import = service.IndexFolderOnceAsync(importRoot);
        await Task.Delay(300);
        Assert.False(import.IsCompleted);
        Assert.Equal(0L, Total(store));

        release.Set();
        var (read, added) = await import.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal((1, 1), (read, added));
        Assert.Equal(270L, Total(store));
    }

    private static void OfferSessionPass(string dbPath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM session; DELETE FROM session_model; DELETE FROM meta WHERE key = 'sessions_backfilled';";
        command.ExecuteNonQuery();
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
}
