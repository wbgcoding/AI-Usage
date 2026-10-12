using AiUsage.Services;
using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>A walk that could not store a file's result must not count the file as read, and says so
/// once; the index lookups survive a row they cannot parse and move-detection asks the database only
/// for paths that can match.</summary>
public sealed class StatsIndexWriteFailureTests
{
    private const string UsageLine =
        """{"type":"assistant","timestamp":"2026-01-01T00:00:00Z","message":{"model":"modelA","usage":{"input_tokens":4,"output_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}}}""";

    private static SqliteConnection HoldWriteLock(string dataDir)
    {
        var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "stats.db"), Pooling = false }.ToString());
        blocker.Open();
        using var command = blocker.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE";
        command.ExecuteNonQuery();
        return blocker;
    }

    [Fact]
    public void A_file_whose_result_could_not_be_stored_is_not_counted_as_read_and_is_logged_once()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("write-fail-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("write-fail-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("write-fail-data");
        using var logDir = TestPaths.CreateDisposableDirectory("write-fail-log");
        var log = new LogService(logDir);
        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "FolderName")).FullName;
        File.WriteAllText(Path.Combine(projectDir, "a.jsonl"), UsageLine + "\n");
        File.WriteAllText(Path.Combine(projectDir, "b.jsonl"), UsageLine + "\n");
        var store = new StatsStore(dataDir, busyTimeoutMs: 100);
        store.LoadAll(); // primes the schema
        var indexer = new StatsIndexer(store, claudeRoot, codexRoot) { Log = log };

        StatsIndexResult blocked;
        using (HoldWriteLock(dataDir))
            blocked = indexer.IndexOnce();

        Assert.Equal(2, blocked.Claude.FilesSeen);
        Assert.Equal(0, blocked.Claude.FilesRead);
        Assert.Equal(1, File.ReadAllLines(log.CurrentFile).Count(line => line.Contains("could not be stored")));

        // Nothing moved, so the next walk reads both files and the numbers add up once.
        var retry = indexer.IndexOnce();
        Assert.Equal(2, retry.Claude.FilesRead);
        Assert.Equal(8, store.LoadAll().Sum(r => r.InputTokens));
    }

    [Fact]
    public void A_source_file_row_with_an_unreadable_time_neither_throws_nor_stops_the_walk()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("bad-time-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("bad-time-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("bad-time-data");
        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "FolderName")).FullName;
        var broken = Path.Combine(projectDir, "broken.jsonl");
        var healthy = Path.Combine(projectDir, "healthy.jsonl");
        File.WriteAllText(broken, UsageLine + "\n");
        File.WriteAllText(healthy, UsageLine + "\n");
        File.SetLastWriteTimeUtc(healthy, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(broken, DateTime.UtcNow.AddMinutes(-10));
        var store = new StatsStore(dataDir);
        store.SetSourceFile(new StatsSourceFileState(broken, "claude", 0, 0, DateTime.UtcNow));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "stats.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE source_file SET write_time_utc = 'not a time'";
            command.ExecuteNonQuery();
        }

        Assert.True(store.TryGetSourceFile(broken, out var state));
        Assert.NotNull(state);

        var result = new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(2, result.Claude.FilesRead);
        Assert.Equal(8, store.LoadAll().Sum(r => r.InputTokens));
    }

    [Theory]
    [InlineData(@"C:\gone\old\rollout-1.jsonl", "rollout-1.jsonl", true)]
    // Shares the ending but is another file name.
    [InlineData(@"C:\gone\old\prerollout-2.jsonl", "rollout-2.jsonl", false)]
    // LIKE wildcards in a name are literal characters.
    [InlineData(@"C:\gone\old\a_b%c.jsonl", "a_b%c.jsonl", true)]
    [InlineData(@"C:\gone\old\aXbYc.jsonl", "a_b%c.jsonl", false)]
    public void A_moved_codex_file_is_found_by_its_exact_name_only(string storedPath, string newName, bool adopted)
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("adopt-sql");
        using var newDir = TestPaths.CreateDisposableDirectory("adopt-sql-new");
        var store = new StatsStore(dataDir);
        store.SetSourceFile(new StatsSourceFileState(storedPath, "codex", 5, 5, DateTime.UtcNow));
        var newPath = Path.Combine(newDir, newName);

        var result = store.TryAdoptMovedSourceFile(newPath, "codex");

        if (adopted)
        {
            Assert.NotNull(result);
            Assert.Equal(newPath, result!.Path);
            Assert.Equal(5, result.Offset);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Fact]
    public void ApplyIndexResult_and_SetSourceFile_report_whether_they_stored()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("apply-bool");
        var store = new StatsStore(dataDir, busyTimeoutMs: 100);
        var state = new StatsSourceFileState("f.jsonl", "claude", 1, 1, DateTime.UtcNow);
        Assert.True(store.SetSourceFile(state));
        Assert.True(store.ApplyIndexResult([], state));

        using (HoldWriteLock(dataDir))
        {
            Assert.False(store.SetSourceFile(state));
            Assert.False(store.ApplyIndexResult([], state));
        }
    }
}
