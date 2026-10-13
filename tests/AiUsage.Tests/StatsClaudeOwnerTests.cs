using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>
/// Claude Code copies earlier history into a second transcript (a resumed or forked session): a
/// response counts once, for the first file that counted it, and what an earlier build counted twice
/// is taken out once from the files that still exist.
/// </summary>
public class StatsClaudeOwnerTests
{
    private const string OwnerPassKey = "claude_owner_pass";

    private static string Line(string timestamp, string messageId, long input, long output, string sessionId = "sess-1") =>
        "{\"type\":\"assistant\",\"sessionId\":\"" + sessionId + "\",\"requestId\":\"r-" + messageId + "\",\"cwd\":\"C:\\\\Projects\\\\Sample\",\"timestamp\":\"" +
        timestamp + "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"modelA\",\"usage\":{\"input_tokens\":" + input +
        ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}";

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

    private static long OwnedBy(string dbPath, string transcript) =>
        (long)Scalar(dbPath, $"SELECT COUNT(*) FROM claude_message WHERE path = '{transcript.Replace("'", "''")}'")!;

    private static (long Input, long Output) Totals(StatsStore store)
    {
        var rows = store.LoadAll();
        return (rows.Sum(row => row.InputTokens), rows.Sum(row => row.OutputTokens));
    }

    /// <summary>A transcript with the given creation and write times, so the age order of two files
    /// (and which one the walk reaches first) does not depend on how fast the test runs.</summary>
    private static string WriteTranscript(string claudeRoot, string project, string name, DateTime created, DateTime written, params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(claudeRoot, project)).FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Concat(lines.Select(line => line + "\n")));
        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    private static readonly DateTime Old = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    private static string[] OriginalLines() =>
    [
        Line("2026-01-01T10:00:00Z", "m1", 100, 5),
        Line("2026-01-01T10:00:01Z", "m1", 100, 467),
        Line("2026-01-01T10:00:02Z", "m2", 50, 20),
    ];

    private static string[] ResumedLines() =>
    [
        // The history copied from the original, then the resumed session's own response.
        Line("2026-01-01T10:00:00Z", "m1", 100, 5),
        Line("2026-01-01T10:00:01Z", "m1", 100, 467),
        Line("2026-01-01T10:00:02Z", "m2", 50, 20),
        Line("2026-01-01T12:00:00Z", "m3", 7, 3, "sess-2"),
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_response_in_two_transcripts_counts_once_whichever_file_is_walked_first(bool originalWalkedFirst)
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-owner-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-owner-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-data");
        WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl", Old, originalWalkedFirst ? New : Old, OriginalLines());
        WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-2.jsonl", New, originalWalkedFirst ? Old : New, ResumedLines());

        var store = new StatsStore(dataDir);
        var result = new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        // m1 (fullest copy 467) + m2 + m3, each once.
        Assert.Equal((157L, 490L), Totals(store));
        Assert.Equal(3L, Scalar(Path.Combine(dataDir, "stats.db"), "SELECT COUNT(*) FROM claude_message"));
        Assert.Equal((4L, 3L), (result.Claude.LinesParsed, result.Claude.LinesSkipped));
        var sessions = store.LoadSessions().ToDictionary(session => session.SessionId);
        Assert.Equal(487L, sessions["sess-1"].OutputTokens);
        Assert.Equal(3L, sessions["sess-2"].OutputTokens);

        // A later walk with nothing new changes nothing.
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal((157L, 490L), Totals(store));
    }

    [Fact]
    public void A_fresh_index_gives_a_response_to_the_older_transcript_even_when_the_newer_was_written_last()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-owner-order-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-owner-order-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-order-data");
        var original = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl", Old, Old, OriginalLines());
        var resumed = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-2.jsonl", New, New, ResumedLines());
        var store = new StatsStore(dataDir);

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        var dbPath = Path.Combine(dataDir, "stats.db");
        Assert.Equal(2L, OwnedBy(dbPath, original));
        Assert.Equal(1L, OwnedBy(dbPath, resumed));
    }

    [Fact]
    public void The_owner_is_not_blocked_when_it_is_read_again_from_scratch()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-owner-again-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-owner-again-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-again-data");
        var original = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl", Old, New, OriginalLines());
        WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-2.jsonl", New, Old, ResumedLines());
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        // The original's marker is gone (the file is read from its first line again): its own
        // responses are its own and are parsed, not skipped as somebody else's.
        Run(Path.Combine(dataDir, "stats.db"), $"DELETE FROM source_file WHERE path = '{original.Replace("'", "''")}'");
        var again = new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(1, again.Claude.FilesRead);
        Assert.Equal((3L, 0L), (again.Claude.LinesParsed, again.Claude.LinesSkipped));
    }

    [Fact]
    public void The_owner_still_counts_the_fullest_copy_of_a_response_a_copy_repeated()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-owner-fullest-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-owner-fullest-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-fullest-data");
        var original = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl", Old, New, Line("2026-01-01T10:00:00Z", "m1", 100, 5));
        WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-2.jsonl", New, Old, Line("2026-01-01T10:00:00Z", "m1", 100, 5));
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal((100L, 5L), Totals(store));

        // The response finishes streaming in the owner: its fullest copy adds the excess there.
        File.AppendAllText(original, Line("2026-01-01T10:00:03Z", "m1", 100, 467) + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal((100L, 467L), Totals(store));
        Assert.Equal(467L, store.GetSourceFile(original)!.CountedOutputTokens);
    }

    [Fact]
    public void A_copy_in_a_file_that_grows_later_stays_skipped()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-owner-grow-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-owner-grow-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-grow-data");
        WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl", Old, New, OriginalLines());
        var resumed = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-2.jsonl", New, Old, ResumedLines());
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        File.AppendAllText(resumed, Line("2026-01-01T10:00:02Z", "m2", 50, 20) + "\n" + Line("2026-01-01T13:00:00Z", "m4", 1, 1, "sess-2") + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal((158L, 491L), Totals(store));
    }

    /// <summary>The index as the previous build left it: both transcripts counted, nothing recorded
    /// about who counted what, the owner pass still to run.</summary>
    private static (StatsStore Store, string Original, string Resumed) DoubleCountedIndex(
        string claudeRoot, string codexRoot, string dataDir)
    {
        var original = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl", Old, Old, OriginalLines());
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        var dbPath = Path.Combine(dataDir, "stats.db");
        Run(dbPath, "DELETE FROM claude_message");

        var resumed = WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-2.jsonl", New, New, ResumedLines());
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Run(dbPath, $"DELETE FROM claude_message; DELETE FROM meta WHERE key = '{OwnerPassKey}';");
        return (store, original, resumed);
    }

    [Fact]
    public void The_one_time_pass_takes_out_what_a_second_transcript_counted_and_runs_once()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-owner-pass-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-owner-pass-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-pass-data");
        var (store, _, _) = DoubleCountedIndex(claudeRoot, codexRoot, dataDir);
        // m1 (467) and m2 (20) twice, m3 once.
        Assert.Equal((157L + 150L, 490L + 487L), Totals(store));
        Assert.False(store.IsClaudeOwnerPassDone());

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.True(store.IsClaudeOwnerPassDone());
        Assert.Equal((157L, 490L), Totals(store));
        var sessions = store.LoadSessions().ToDictionary(session => session.SessionId);
        Assert.Equal(487L, sessions["sess-1"].OutputTokens);
        Assert.Equal(3L, sessions["sess-2"].OutputTokens);
        Assert.Equal(3L, Scalar(Path.Combine(dataDir, "stats.db"), "SELECT COUNT(*) FROM claude_message"));

        // Once: the done mark withdrawn is the only way to offer it again, so a second walk changes nothing.
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal((157L, 490L), Totals(store));
    }

    [Fact]
    public void A_pass_that_failed_leaves_the_walk_counting_like_before_and_loses_no_token_when_it_runs_later()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-owner-failed-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-owner-failed-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-failed-data");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-1.jsonl", Old, Old, OriginalLines());
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Run(dbPath, $"DELETE FROM claude_message; DELETE FROM meta WHERE key = '{OwnerPassKey}';");

        // The pass cannot write its claims; the resumed transcript shows up meanwhile.
        Run(dbPath, "CREATE TRIGGER no_claims BEFORE INSERT ON claude_message BEGIN SELECT RAISE(ABORT, 'locked'); END");
        WriteTranscript(claudeRoot, "C--Projects-Sample", "sess-2.jsonl", New, New, ResumedLines());
        var walk = new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.False(store.IsClaudeOwnerPassDone());
        Assert.Equal(1, walk.Claude.FilesRead);
        Assert.Equal((157L + 150L, 490L + 487L), Totals(store));
        Assert.Equal(0L, Scalar(dbPath, "SELECT COUNT(*) FROM claude_message"));

        Run(dbPath, "DROP TRIGGER no_claims");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        // The parent keeps everything, the copied history is taken out exactly once.
        Assert.True(store.IsClaudeOwnerPassDone());
        Assert.Equal((157L, 490L), Totals(store));
        var sessions = store.LoadSessions().ToDictionary(session => session.SessionId);
        Assert.Equal(487L, sessions["sess-1"].OutputTokens);
        Assert.Equal(3L, sessions["sess-2"].OutputTokens);
    }

    [Fact]
    public void An_index_without_a_remembered_claude_file_marks_the_pass_done()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-owner-nofile-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-owner-nofile-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-nofile-data");
        var dbPath = Path.Combine(dataDir, "stats.db");
        var store = new StatsStore(dataDir);
        store.AddDelta([new StatsRecord("codex", new DateOnly(2026, 1, 1), "modelC", "proj", 10, 20, 0, 0, 5, "", Subagent: false)]);
        Run(dbPath, $"DELETE FROM meta WHERE key = '{OwnerPassKey}'");
        Assert.False(store.IsClaudeOwnerPassDone());

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.True(store.IsClaudeOwnerPassDone());
    }

    [Fact]
    public void The_one_time_pass_leaves_a_transcript_that_is_gone_as_it_was()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-owner-gone-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-owner-gone-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-gone-data");
        var (store, _, resumed) = DoubleCountedIndex(claudeRoot, codexRoot, dataDir);
        File.Delete(resumed);
        var before = Totals(store);

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        // Nothing to read the duplicate from, so nothing is taken out and nothing else is lost.
        Assert.Equal(before, Totals(store));
        Assert.True(store.IsClaudeOwnerPassDone());
    }

    [Fact]
    public void The_one_time_pass_does_not_take_out_the_only_copy_that_is_left()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-owner-sole-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-owner-sole-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-sole-data");
        var (store, original, _) = DoubleCountedIndex(claudeRoot, codexRoot, dataDir);
        File.Delete(original);
        var before = Totals(store);

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        // The remaining transcript is the only one that holds the responses, so it owns them all.
        Assert.Equal(before, Totals(store));
    }

    [Fact]
    public void A_correction_never_takes_a_row_below_zero()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-floor");
        var store = new StatsStore(dataDir);
        store.AddDelta([new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "proj", 10, 20, 0, 0, 5, "", Subagent: false)]);

        var removed = store.ApplyClaudeOwnerCorrection(
            [new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "proj", 50, 5, 0, 0, 5, "", Subagent: false),
             new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "other", 9, 9, 0, 0, 5, "", Subagent: false)],
            []);

        Assert.NotNull(removed);
        var row = Assert.Single(store.LoadAll());
        Assert.Equal((0L, 15L), (row.InputTokens, row.OutputTokens));
    }

    /// <summary>A database exactly as a v7 or v8 build left it, with one remembered file and one session.</summary>
    private static void WritePreOwnerDatabase(string dbPath, int version)
    {
        Run(dbPath, """
            CREATE TABLE schema_version (id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL);
            INSERT INTO schema_version (id, version) VALUES (1, SCHEMA_VERSION);
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
                current_effort TEXT NOT NULL DEFAULT '', last_message_key TEXT NOT NULL DEFAULT '',
                counted_input INTEGER, counted_output INTEGER, counted_cache_creation INTEGER, counted_cache_read INTEGER
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
            INSERT INTO meta VALUES ('claude_copy_backfilled', '1');
            INSERT INTO usage VALUES ('claude', '2026-01-01', 1, 'modelA', 'C:\Projects\Sample', 'high', '', 0, 100, 20, 30, 40);
            INSERT INTO usage VALUES ('claude', '2026-01-01', 2, 'modelA', 'C:\Projects\Sample', '', '', 1, 7, 8, 9, 10);
            INSERT INTO usage VALUES ('codex', '2026-01-03', 4, 'modelC', 'C:\Projects\Other', 'xhigh', '', 0, 11, 12, 13, 14);
            INSERT INTO source_file (path, provider, offset, size, write_time_utc, last_message_key, counted_input, counted_output, counted_cache_creation, counted_cache_read)
            VALUES ('gone.jsonl', 'claude', 5, 5, '2026-01-01T00:00:00.0000000Z', 'm1|r-m1', 100, 20, 30, 40);
            INSERT INTO session VALUES ('claude', '', 'sess-1', 'C:\Projects\Sample', '2026-01-01T10:00:00.0000000Z', '2026-01-01T11:00:00.0000000Z', 107, 28, 39, 50, 'modelA', 34);
            INSERT INTO session_model VALUES ('claude', '', 'sess-1', 'modelA', 224);
            """.Replace("SCHEMA_VERSION", version.ToString()));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void Migration_from_v7_or_v8_keeps_every_row_offers_the_pass_and_leaves_a_backup(int version)
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-v8");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WritePreOwnerDatabase(dbPath, version);

        var store = new StatsStore(dataDir);
        var rows = store.LoadAll();

        Assert.True(store.IsUsable);
        Assert.Equal(3, rows.Count);
        Assert.Equal((118L, 40L, 52L, 64L), (rows.Sum(r => r.InputTokens), rows.Sum(r => r.OutputTokens),
            rows.Sum(r => r.CacheCreationTokens), rows.Sum(r => r.CacheReadTokens)));
        var session = Assert.Single(store.LoadSessions());
        Assert.Equal((107L, 28L, 39L, 50L, 34L), (session.InputTokens, session.OutputTokens, session.CacheCreationTokens, session.CacheReadTokens, session.SubagentTokens));
        var state = store.GetSourceFile("gone.jsonl")!;
        Assert.Equal("m1|r-m1", state.LastMessageKey);
        Assert.Equal(20L, state.CountedOutputTokens);
        Assert.Equal(224L, Scalar(dbPath, "SELECT tokens FROM session_model"));
        Assert.Equal((long)StatsStore.SchemaVersion, Scalar(dbPath, "SELECT version FROM schema_version"));
        Assert.Equal(0L, Scalar(dbPath, "SELECT COUNT(*) FROM claude_message"));
        Assert.False(store.IsClaudeOwnerPassDone());

        // The untouched file is kept beside it, as the older migrations do.
        var backup = Path.Combine(dataDir, $"stats.v{version}.bak");
        Assert.Equal((long)version, Scalar(backup, "SELECT version FROM schema_version"));
        Assert.Equal(3L, Scalar(backup, "SELECT COUNT(*) FROM usage"));

        // A second open leaves the migrated file alone.
        Assert.Equal(3, new StatsStore(dataDir).LoadAll().Count);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void Migration_from_v7_or_v8_does_not_run_when_the_backup_cannot_be_written(int version)
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-owner-nobackup");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WritePreOwnerDatabase(dbPath, version);
        Directory.CreateDirectory(Path.Combine(dataDir, $"stats.v{version}.bak"));

        var store = new StatsStore(dataDir);

        Assert.Empty(store.LoadAll());
        Assert.False(store.IsUsable);
        Assert.Equal((long)version, Scalar(dbPath, "SELECT version FROM schema_version"));
        Assert.Equal(3L, Scalar(dbPath, "SELECT COUNT(*) FROM usage"));
    }

    [Fact]
    public void A_new_database_needs_no_owner_pass_and_a_newer_one_is_left_alone()
    {
        using var freshDir = TestPaths.CreateDisposableDirectory("stats-owner-fresh");
        using var newerDir = TestPaths.CreateDisposableDirectory("stats-owner-newer");
        Assert.True(new StatsStore(freshDir).IsClaudeOwnerPassDone());

        var newerDb = Path.Combine(newerDir, "stats.db");
        Run(newerDb, $"CREATE TABLE schema_version (id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL); INSERT INTO schema_version VALUES (1, {StatsStore.SchemaVersion + 1});");
        var newer = new StatsStore(newerDir);

        Assert.Empty(newer.LoadAll());
        Assert.False(newer.IsUsable);
        Assert.Equal(StatsStore.SchemaVersion + 1, StatsStore.TryReadSchemaVersion(newerDb));
        Assert.Equal(0L, Scalar(newerDb, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'claude_message'"));
    }
}
