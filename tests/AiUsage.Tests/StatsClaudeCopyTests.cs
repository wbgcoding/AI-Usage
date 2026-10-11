using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>
/// A Claude response is written several times while it streams, the early copies with placeholder
/// output counts: the index counts the fullest copy, carries the counted tokens of a file's last
/// response across runs, and adds what earlier builds missed in one pass.
/// </summary>
public class StatsClaudeCopyTests
{
    private const string BackfillKey = "claude_copy_backfilled";

    private static string Line(string timestamp, string messageId, long input, long output, long cacheCreation = 0, long cacheRead = 0) =>
        "{\"type\":\"assistant\",\"sessionId\":\"sess-1\",\"requestId\":\"r-" + messageId + "\",\"cwd\":\"C:\\\\Projects\\\\Sample\",\"timestamp\":\"" +
        timestamp + "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"modelA\",\"usage\":{\"input_tokens\":" + input +
        ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":" + cacheCreation + ",\"cache_read_input_tokens\":" + cacheRead + "}}}";

    private static void Run(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static (long Input, long Output, long CacheCreation, long CacheRead) Totals(StatsStore store)
    {
        var rows = store.LoadAll();
        return (rows.Sum(row => row.InputTokens), rows.Sum(row => row.OutputTokens),
            rows.Sum(row => row.CacheCreationTokens), rows.Sum(row => row.CacheReadTokens));
    }

    private static string WriteTranscript(string claudeRoot, string project, string name, params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(claudeRoot, project)).FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Concat(lines.Select(line => line + "\n")));
        return path;
    }

    [Fact]
    public void The_fullest_copy_of_a_response_counts_once()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-copy-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-copy-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-copy-data");
        WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl",
            Line("2026-01-01T10:00:00Z", "m1", 100, 5, 30, 40),
            Line("2026-01-01T10:00:01Z", "m1", 100, 5, 30, 40),
            Line("2026-01-01T10:00:02Z", "m1", 100, 467, 30, 40),
            Line("2026-01-01T10:00:03Z", "m1", 100, 467, 30, 40));

        var store = new StatsStore(dataDir);
        var result = new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal((100L, 467L, 30L, 40L), Totals(store));
        Assert.Equal(2, result.Claude.LinesParsed);
        Assert.Equal(2, result.Claude.LinesSkipped);
        Assert.Equal(467L, store.LoadSessions().Single().OutputTokens);
    }

    [Fact]
    public void Copies_split_across_two_runs_count_the_fullest_once()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-copy-split-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-copy-split-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-copy-split-data");
        var path = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl", Line("2026-01-01T10:00:00Z", "m1", 100, 5));
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        var state = store.GetSourceFile(path)!;
        Assert.Equal(5L, state.CountedOutputTokens);
        Assert.Equal(100L, state.CountedInputTokens);

        File.AppendAllText(path, Line("2026-01-01T10:00:02Z", "m1", 100, 467) + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal((100L, 467L, 0L, 0L), Totals(store));
        Assert.Equal(467L, store.GetSourceFile(path)!.CountedOutputTokens);

        // A later identical copy adds nothing.
        File.AppendAllText(path, Line("2026-01-01T10:00:03Z", "m1", 100, 467) + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal((100L, 467L, 0L, 0L), Totals(store));
    }

    [Fact]
    public void A_row_without_counted_tokens_does_not_count_its_response_twice()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-copy-legacy-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-copy-legacy-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-copy-legacy-data");
        var path = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl", Line("2026-01-01T10:00:00Z", "m1", 100, 5));
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Run(Path.Combine(dataDir, "stats.db"),
            "UPDATE source_file SET counted_input = NULL, counted_output = NULL, counted_cache_creation = NULL, counted_cache_read = NULL");
        Assert.Null(store.GetSourceFile(path)!.CountedForLastMessage);

        File.AppendAllText(path, Line("2026-01-01T10:00:02Z", "m1", 100, 467) + "\n" + Line("2026-01-01T10:00:03Z", "m2", 7, 3) + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        // The copy met first only seeds the row; the input is not counted a second time.
        Assert.Equal((107L, 8L, 0L, 0L), Totals(store));
        Assert.Equal(3L, store.GetSourceFile(path)!.CountedOutputTokens);
    }

    [Fact]
    public void The_backfill_adds_what_earlier_builds_missed_and_runs_once()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-copy-fill-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-copy-fill-codex");
        using var referenceDir = TestPaths.CreateDisposableDirectory("stats-copy-fill-ref");
        using var oldDir = TestPaths.CreateDisposableDirectory("stats-copy-fill-old");
        var fileA = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl",
            Line("2026-01-01T10:00:00Z", "m1", 100, 5),
            Line("2026-01-01T10:00:01Z", "m1", 100, 467),
            Line("2026-01-01T10:00:02Z", "m2", 50, 2),
            Line("2026-01-01T10:00:03Z", "m2", 50, 2),
            Line("2026-01-01T10:00:04Z", "m2", 50, 408));

        // What the earlier build stored for this file: the first copy of each response only.
        var oldStore = new StatsStore(oldDir);
        new StatsIndexer(oldStore, claudeRoot, codexRoot).IndexOnce();
        var oldDb = Path.Combine(oldDir, "stats.db");
        Run(oldDb, $"""
            UPDATE usage SET output_tokens = 7;
            UPDATE session SET output = 7;
            UPDATE session_model SET tokens = 100 + 5 + 50 + 2;
            UPDATE source_file SET counted_input = NULL, counted_output = NULL, counted_cache_creation = NULL, counted_cache_read = NULL;
            DELETE FROM meta WHERE key = '{BackfillKey}';
            """);
        Assert.False(oldStore.IsClaudeCopyBackfillDone());
        Assert.Equal(7L, Totals(oldStore).Output);

        // A file the old store never saw is read by the walk with the fullest copy and must not be
        // corrected a second time by the pass.
        WriteTranscript(claudeRoot, "C--Projects-Other", "sess-2.jsonl",
            Line("2026-01-01T11:00:00Z", "n1", 10, 1),
            Line("2026-01-01T11:00:01Z", "n1", 10, 90));

        new StatsIndexer(oldStore, claudeRoot, codexRoot).IndexOnce();

        var reference = new StatsStore(referenceDir);
        new StatsIndexer(reference, claudeRoot, codexRoot).IndexOnce();
        Assert.True(oldStore.IsClaudeCopyBackfillDone());
        Assert.Equal(Totals(reference), Totals(oldStore));
        Assert.Equal((160L, 965L, 0L, 0L), Totals(oldStore));
        Assert.Equal(
            reference.LoadSessions().OrderBy(session => session.Project).Select(session => session.OutputTokens),
            oldStore.LoadSessions().OrderBy(session => session.Project).Select(session => session.OutputTokens));
        Assert.Equal(reference.GetSourceFile(fileA)!.CountedForLastMessage, oldStore.GetSourceFile(fileA)!.CountedForLastMessage);

        // Run once: a second walk, even with the pass offered again, leaves the totals alone.
        new StatsIndexer(oldStore, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal((160L, 965L, 0L, 0L), Totals(oldStore));
    }

    [Fact]
    public void The_backfill_of_a_file_that_is_gone_adds_nothing_and_loses_nothing()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-copy-gone-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-copy-gone-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-copy-gone-data");
        var path = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl", Line("2026-01-01T10:00:00Z", "m1", 100, 5));
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Run(Path.Combine(dataDir, "stats.db"), $"DELETE FROM meta WHERE key = '{BackfillKey}'");
        File.Delete(path);

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        // Nothing to read, nothing added, nothing lost; the pass is done.
        Assert.Equal((100L, 5L, 0L, 0L), Totals(store));
        Assert.True(store.IsClaudeCopyBackfillDone());
    }

    [Fact]
    public void A_new_database_needs_no_backfill()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-copy-fresh");

        Assert.True(new StatsStore(dataDir).IsClaudeCopyBackfillDone());
    }

    [Fact]
    public void Migration_from_v7_keeps_every_row_and_leaves_the_counted_tokens_unset()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-copy-v7");
        var dbPath = Path.Combine(dataDir, "stats.db");
        Run(dbPath, """
            CREATE TABLE schema_version (id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL);
            INSERT INTO schema_version (id, version) VALUES (1, 7);
            CREATE TABLE usage (
                provider TEXT NOT NULL, day TEXT NOT NULL, hour INTEGER NOT NULL DEFAULT 0, model TEXT NOT NULL, project TEXT NOT NULL,
                effort TEXT NOT NULL DEFAULT '', machine TEXT NOT NULL DEFAULT '', subagent INTEGER NOT NULL DEFAULT 0,
                input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, day, hour, model, project, effort, machine, subagent)
            );
            CREATE TABLE source_file (
                path TEXT PRIMARY KEY, provider TEXT NOT NULL, offset INTEGER NOT NULL, size INTEGER NOT NULL,
                write_time_utc TEXT NOT NULL, current_model TEXT NOT NULL DEFAULT '',
                cumulative_input_tokens INTEGER NOT NULL DEFAULT 0, cumulative_output_tokens INTEGER NOT NULL DEFAULT 0,
                cumulative_cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cumulative_cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                current_effort TEXT NOT NULL DEFAULT '', last_message_key TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE session (
                provider TEXT NOT NULL, machine TEXT NOT NULL DEFAULT '', session_id TEXT NOT NULL, project TEXT NOT NULL DEFAULT '',
                first_utc TEXT NOT NULL, last_utc TEXT NOT NULL, input INTEGER NOT NULL DEFAULT 0, output INTEGER NOT NULL DEFAULT 0,
                cache_creation INTEGER NOT NULL DEFAULT 0, cache_read INTEGER NOT NULL DEFAULT 0, main_model TEXT NOT NULL DEFAULT '',
                subagent_tokens INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (provider, machine, session_id)
            );
            CREATE TABLE session_model (
                provider TEXT NOT NULL, machine TEXT NOT NULL DEFAULT '', session_id TEXT NOT NULL, model TEXT NOT NULL,
                tokens INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (provider, machine, session_id, model)
            );
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO meta VALUES ('sessions_backfilled', '1');
            INSERT INTO meta VALUES ('subagent_usage_backfilled', '1');
            INSERT INTO usage VALUES ('claude', '2026-01-01', 1, 'modelA', 'C:\Projects\Sample', 'high', '', 0, 100, 20, 30, 40);
            INSERT INTO usage VALUES ('claude', '2026-01-01', 2, 'modelA', 'C:\Projects\Sample', '', '', 1, 7, 8, 9, 10);
            INSERT INTO usage VALUES ('codex', '2026-01-03', 4, 'modelC', 'C:\Projects\Other', 'xhigh', '', 0, 11, 12, 13, 14);
            INSERT INTO source_file (path, provider, offset, size, write_time_utc, last_message_key)
            VALUES ('gone.jsonl', 'claude', 5, 5, '2026-01-01T00:00:00.0000000Z', 'm1|r-m1');
            """);

        var store = new StatsStore(dataDir);
        var rows = store.LoadAll();

        Assert.True(store.IsUsable);
        Assert.Equal(3, rows.Count);
        Assert.Equal((118L, 40L, 52L, 64L), (rows.Sum(r => r.InputTokens), rows.Sum(r => r.OutputTokens),
            rows.Sum(r => r.CacheCreationTokens), rows.Sum(r => r.CacheReadTokens)));
        Assert.Equal((long)StatsStore.SchemaVersion, Scalar(dbPath, "SELECT version FROM schema_version"));
        var state = store.GetSourceFile("gone.jsonl")!;
        Assert.Equal("m1|r-m1", state.LastMessageKey);
        Assert.Null(state.CountedForLastMessage);
        Assert.False(store.IsClaudeCopyBackfillDone());

        // A second open leaves the migrated file alone.
        Assert.Equal(3, new StatsStore(dataDir).LoadAll().Count);
    }
}
