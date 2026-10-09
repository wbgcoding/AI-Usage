using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>
/// The token index's machine, subagent and session columns: the migration that adds them without
/// losing a stored row, the parsers that fill them, and the one-time backfill of the sessions of rows
/// that were stored before they existed.
/// </summary>
public class StatsSessionTests
{
    private static SqliteConnection OpenRaw(string dbPath) =>
        new(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());

    private static void Run(string dbPath, string sql)
    {
        using var connection = OpenRaw(dbPath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string dbPath, string sql)
    {
        using var connection = OpenRaw(dbPath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>A database exactly as the previous build (schema version 6) wrote it.</summary>
    private static void WriteV6Database(string dbPath)
    {
        Run(dbPath, """
            CREATE TABLE schema_version (id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL);
            INSERT INTO schema_version (id, version) VALUES (1, 6);
            CREATE TABLE usage (
                provider TEXT NOT NULL,
                day TEXT NOT NULL,
                hour INTEGER NOT NULL DEFAULT 0,
                model TEXT NOT NULL,
                project TEXT NOT NULL,
                effort TEXT NOT NULL DEFAULT '',
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, day, hour, model, project, effort)
            );
            CREATE TABLE source_file (
                path TEXT PRIMARY KEY,
                provider TEXT NOT NULL,
                offset INTEGER NOT NULL,
                size INTEGER NOT NULL,
                write_time_utc TEXT NOT NULL,
                current_model TEXT NOT NULL DEFAULT '',
                cumulative_input_tokens INTEGER NOT NULL DEFAULT 0,
                cumulative_output_tokens INTEGER NOT NULL DEFAULT 0,
                cumulative_cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                cumulative_cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                current_effort TEXT NOT NULL DEFAULT '',
                last_message_key TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO usage VALUES ('claude', '2026-01-01', 1, 'modelA', 'C:\Projects\Sample', 'high', 100, 20, 30, 40);
            INSERT INTO usage VALUES ('claude', '2026-01-01', 2, 'modelA', 'subagents', '', 7, 8, 9, 10);
            INSERT INTO usage VALUES ('claude', '2026-01-02', 2, 'modelB', 'subagents', 'low', 1, 2, 3, 4);
            INSERT INTO usage VALUES ('codex', '2026-01-02', 3, 'modelC', 'subagents', '', 50, 60, 70, 80);
            INSERT INTO usage VALUES ('codex', '2026-01-03', 4, 'modelC', 'C:\Projects\Other', 'xhigh', 11, 12, 13, 14);
            INSERT INTO source_file (path, provider, offset, size, write_time_utc)
            VALUES ('gone.jsonl', 'claude', 5, 5, '2026-01-01T00:00:00.0000000Z');
            """);
    }

    private static string Totals(IEnumerable<StatsRecord> rows) =>
        string.Join(";", rows
            .OrderBy(row => row.Provider, StringComparer.Ordinal).ThenBy(row => row.Day).ThenBy(row => row.Hour).ThenBy(row => row.Model, StringComparer.Ordinal)
            .Select(row => $"{row.Provider}|{row.Day:yyyy-MM-dd}|{row.Hour}|{row.Model}|{row.Project}|{row.Effort}|{row.InputTokens}|{row.OutputTokens}|{row.CacheCreationTokens}|{row.CacheReadTokens}"));

    [Fact]
    public void Migration_from_v6_keeps_every_row_and_total_and_flags_the_subagents_rows()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v6-migrate");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteV6Database(dbPath);
        var totalBefore = Convert.ToInt64(
            Scalar(dbPath, "SELECT SUM(input_tokens + output_tokens + cache_creation_tokens + cache_read_tokens) FROM usage"));
        Assert.Equal(544L, totalBefore);

        var store = new StatsStore(dataDir);
        var rows = store.LoadAll();

        Assert.True(store.IsUsable);
        Assert.Equal(5, rows.Count);
        Assert.Equal(totalBefore, rows.Sum(row => row.TotalTokens));

        // Every row is still there with its own numbers, in its own project and effort.
        Assert.Equal(
            "claude|2026-01-01|1|modelA|C:\\Projects\\Sample|high|100|20|30|40;" +
            "claude|2026-01-01|2|modelA|subagents||7|8|9|10;" +
            "claude|2026-01-02|2|modelB|subagents|low|1|2|3|4;" +
            "codex|2026-01-02|3|modelC|subagents||50|60|70|80;" +
            "codex|2026-01-03|4|modelC|C:\\Projects\\Other|xhigh|11|12|13|14",
            Totals(rows));

        // Only the Claude rows that were filed under "subagents" are subagent rows now.
        Assert.Equal(
            ["claude|subagents|True", "claude|subagents|True"],
            rows.Where(row => row.Subagent).Select(row => $"{row.Provider}|{row.Project}|{row.Subagent}"));
        Assert.All(rows.Where(row => !(row.Provider == "claude" && row.Project == "subagents")), row => Assert.False(row.Subagent));
        Assert.All(rows, row => Assert.Equal("", row.Machine));

        Assert.Equal((long)StatsStore.SchemaVersion, Scalar(dbPath, "SELECT version FROM schema_version"));
        Assert.NotNull(store.GetSourceFile("gone.jsonl"));
        Assert.True(File.Exists(Path.Combine(dataDir, "stats.v6.bak")));
        Assert.Equal(5L, Scalar(Path.Combine(dataDir, "stats.v6.bak"), "SELECT COUNT(*) FROM usage"));

        // The sessions of these rows are still to be filled in by the one-time pass.
        Assert.False(store.IsSessionBackfillDone());

        // A second open leaves the migrated file alone.
        Assert.Equal(5, new StatsStore(dataDir).LoadAll().Count);
    }

    [Fact]
    public void Migration_does_not_run_when_the_v6_backup_cannot_be_written()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v6-nobackup");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteV6Database(dbPath);
        Directory.CreateDirectory(Path.Combine(dataDir, "stats.v6.bak"));

        var store = new StatsStore(dataDir);

        Assert.Empty(store.LoadAll());
        Assert.False(store.IsUsable);
        Assert.Equal(6L, Scalar(dbPath, "SELECT version FROM schema_version"));
        Assert.Equal(5L, Scalar(dbPath, "SELECT COUNT(*) FROM usage"));
    }

    /// <summary>A database exactly as the released v1.1.0 (schema version 5) wrote it: a version table
    /// without the id column (here with the second row the old open race could leave) and a source
    /// file table with the message key.</summary>
    private static void WriteV5Database(string dbPath)
    {
        Run(dbPath, """
            CREATE TABLE schema_version (version INTEGER NOT NULL);
            INSERT INTO schema_version (version) VALUES (5);
            INSERT INTO schema_version (version) VALUES (5);
            CREATE TABLE usage (
                provider TEXT NOT NULL, day TEXT NOT NULL, hour INTEGER NOT NULL DEFAULT 0, model TEXT NOT NULL,
                project TEXT NOT NULL, effort TEXT NOT NULL DEFAULT '',
                input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, day, hour, model, project, effort)
            );
            CREATE TABLE source_file (
                path TEXT PRIMARY KEY, provider TEXT NOT NULL, offset INTEGER NOT NULL, size INTEGER NOT NULL,
                write_time_utc TEXT NOT NULL, current_model TEXT NOT NULL DEFAULT '',
                cumulative_input_tokens INTEGER NOT NULL DEFAULT 0, cumulative_output_tokens INTEGER NOT NULL DEFAULT 0,
                cumulative_cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cumulative_cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                current_effort TEXT NOT NULL DEFAULT '', last_message_key TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO usage VALUES ('claude', '2026-02-01', 9, 'modelA', 'C:\Projects\Sample', 'high', 1000, 200, 300, 400);
            INSERT INTO usage VALUES ('claude', '2026-02-01', 9, 'modelA', 'subagents', '', 5, 6, 7, 8);
            INSERT INTO usage VALUES ('claude', '2026-02-02', 10, 'modelB', 'C:\Projects\Sample', 'low', 11, 12, 13, 14);
            INSERT INTO usage VALUES ('codex', '2026-02-02', 11, 'modelC', 'C:\Projects\Other', 'xhigh', 21, 22, 23, 24);
            INSERT INTO source_file (path, provider, offset, size, write_time_utc)
            VALUES ('a.jsonl', 'claude', 5, 5, '2026-02-01T00:00:00.0000000Z');
            """);
    }

    [Fact]
    public void Migration_from_the_released_v5_keeps_every_row_and_total_and_leaves_a_v5_backup()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v5-released");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteV5Database(dbPath);
        var totalBefore = Convert.ToInt64(
            Scalar(dbPath, "SELECT SUM(input_tokens + output_tokens + cache_creation_tokens + cache_read_tokens) FROM usage"));
        Assert.Equal(1900 + 26 + 50 + 90, totalBefore);

        var store = new StatsStore(dataDir);
        var rows = store.LoadAll();

        Assert.True(store.IsUsable);
        Assert.Equal(4, rows.Count);
        Assert.Equal(totalBefore, rows.Sum(row => row.TotalTokens));
        Assert.Equal(
            "claude|2026-02-01|9|modelA|C:\\Projects\\Sample|high|1000|200|300|400;" +
            "claude|2026-02-01|9|modelA|subagents||5|6|7|8;" +
            "claude|2026-02-02|10|modelB|C:\\Projects\\Sample|low|11|12|13|14;" +
            "codex|2026-02-02|11|modelC|C:\\Projects\\Other|xhigh|21|22|23|24",
            Totals(rows));
        Assert.Equal(1, rows.Count(row => row.Subagent));

        Assert.Equal(1L, Scalar(dbPath, "SELECT COUNT(*) FROM schema_version"));
        Assert.Equal((long)StatsStore.SchemaVersion, Scalar(dbPath, "SELECT version FROM schema_version"));
        Assert.NotNull(store.GetSourceFile("a.jsonl"));

        // The backup is the untouched v5 file.
        var backup = Path.Combine(dataDir, "stats.v5.bak");
        Assert.True(File.Exists(backup));
        Assert.Equal(4L, Scalar(backup, "SELECT COUNT(*) FROM usage"));
        Assert.Equal(2L, Scalar(backup, "SELECT COUNT(*) FROM schema_version"));
        Assert.Equal(5L, Scalar(backup, "SELECT MAX(version) FROM schema_version"));
    }

    [Fact]
    public void Migration_from_v4_ends_at_the_current_schema_keeping_the_codex_rows_and_their_totals()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v4-to-current");
        var dbPath = Path.Combine(dataDir, "stats.db");
        Run(dbPath, """
            CREATE TABLE schema_version (version INTEGER NOT NULL);
            INSERT INTO schema_version (version) VALUES (4);
            CREATE TABLE usage (
                provider TEXT NOT NULL, day TEXT NOT NULL, hour INTEGER NOT NULL DEFAULT 0, model TEXT NOT NULL,
                project TEXT NOT NULL, effort TEXT NOT NULL DEFAULT '',
                input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, day, hour, model, project, effort)
            );
            CREATE TABLE source_file (
                path TEXT PRIMARY KEY, provider TEXT NOT NULL, offset INTEGER NOT NULL, size INTEGER NOT NULL,
                write_time_utc TEXT NOT NULL, current_model TEXT NOT NULL DEFAULT '',
                cumulative_input_tokens INTEGER NOT NULL DEFAULT 0, cumulative_output_tokens INTEGER NOT NULL DEFAULT 0,
                cumulative_cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cumulative_cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                current_effort TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO usage VALUES ('codex', '2026-03-01', 5, 'modelC', 'C:\\Projects\\Other', 'high', 31, 32, 33, 34);
            INSERT INTO usage VALUES ('codex', '2026-03-02', 6, 'modelD', 'C:\\Projects\\Other', '', 41, 42, 43, 44);
            INSERT INTO usage VALUES ('claude', '2026-03-02', 6, 'modelA', 'C:\\Projects\\Sample', '', 1, 2, 3, 4);
            INSERT INTO source_file (path, provider, offset, size, write_time_utc)
            VALUES ('x.jsonl', 'codex', 6, 6, '2026-03-01T00:00:00.0000000Z');
            """);

        var store = new StatsStore(dataDir);
        var rows = store.LoadAll();

        Assert.True(store.IsUsable);
        Assert.Equal((long)StatsStore.SchemaVersion, Scalar(dbPath, "SELECT version FROM schema_version"));
        var codex = rows.Where(row => row.Provider == "codex").ToList();
        Assert.Equal(2, codex.Count);
        Assert.Equal(31 + 32 + 33 + 34 + 41 + 42 + 43 + 44, codex.Sum(row => row.TotalTokens));
        Assert.True(File.Exists(Path.Combine(dataDir, "stats.v4.bak")));
        Assert.Equal(3L, Scalar(Path.Combine(dataDir, "stats.v4.bak"), "SELECT COUNT(*) FROM usage"));
    }

    [Fact]
    public void A_failed_migration_step_is_not_retried_with_another_full_copy()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v6-step-fails");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteV6Database(dbPath);
        // A leftover table of the target's name makes the rebuild step throw after the backup was taken.
        Run(dbPath, "CREATE TABLE usage_v7 (x INTEGER);");
        var backup = Path.Combine(dataDir, "stats.v6.bak");

        var store = new StatsStore(dataDir);
        Assert.Empty(store.LoadAll());
        Assert.True(File.Exists(backup));
        Assert.Equal(6L, Scalar(dbPath, "SELECT version FROM schema_version"));
        Assert.Equal(5L, Scalar(dbPath, "SELECT COUNT(*) FROM usage"));

        // Later opens of the same instance sit the session out: the backup is not written again.
        File.WriteAllText(backup, "marker");
        Assert.Empty(store.LoadAll());
        Assert.Empty(store.LoadAll());
        Assert.Equal("marker", File.ReadAllText(backup));
        Assert.False(store.IsUsable);
        Assert.Equal(5L, Scalar(dbPath, "SELECT COUNT(*) FROM usage"));
    }

    [Fact]
    public void A_backup_that_fails_halfway_never_destroys_the_good_backup_of_an_earlier_attempt()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v6-keep-bak");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteV6Database(dbPath);
        var backup = Path.Combine(dataDir, "stats.v6.bak");
        File.WriteAllText(backup, "good earlier copy");
        // The temporary name taken by a folder: the new copy cannot be written.
        Directory.CreateDirectory(backup + ".tmp");

        var store = new StatsStore(dataDir);

        Assert.Empty(store.LoadAll());
        Assert.False(store.IsUsable);
        Assert.Equal("good earlier copy", File.ReadAllText(backup));
        Assert.Equal(6L, Scalar(dbPath, "SELECT version FROM schema_version"));
        Assert.Equal(5L, Scalar(dbPath, "SELECT COUNT(*) FROM usage"));
    }

    [Fact]
    public void A_successful_backup_leaves_no_temporary_file_behind()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v6-no-tmp");
        WriteV6Database(Path.Combine(dataDir, "stats.db"));

        var store = new StatsStore(dataDir);
        Assert.NotEmpty(store.LoadAll());
        Assert.True(store.IsUsable);

        Assert.True(File.Exists(Path.Combine(dataDir, "stats.v6.bak")));
        Assert.False(File.Exists(Path.Combine(dataDir, "stats.v6.bak.tmp")));
    }

    [Fact]
    public void A_source_file_snapshot_that_cannot_be_read_is_reported_as_failed_not_as_empty()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-snapshot-fail");
        var dbPath = Path.Combine(dataDir, "stats.db");
        var store = new StatsStore(dataDir, busyTimeoutMs: 200);
        store.AddDelta([new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "P", 1, 0, 0, 0)]);
        Assert.True(store.TryLoadSourceFiles(out _));

        using var holder = OpenRaw(dbPath);
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE";
            begin.ExecuteNonQuery();
        }

        Assert.False(store.TryLoadSourceFiles(out var failed));
        Assert.Empty(failed);
    }

    [Fact]
    public void The_backfill_is_neither_run_nor_marked_done_from_a_snapshot_that_failed_to_load()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-m1-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-m1-codex");
        using var firstDir = TestPaths.CreateDisposableDirectory("stats-m1-first");
        using var oldDir = TestPaths.CreateDisposableDirectory("stats-m1-old");
        WriteSessionFixture(claudeRoot, codexRoot);
        var reference = new StatsStore(firstDir);
        new StatsIndexer(reference, claudeRoot, codexRoot).IndexOnce();
        var dbPath = DowngradeToV6(Path.Combine(firstDir, "stats.db"), oldDir);

        using var emptyClaude = TestPaths.CreateDisposableDirectory("stats-m1-empty-claude");
        using var emptyCodex = TestPaths.CreateDisposableDirectory("stats-m1-empty-codex");
        var store = new StatsStore(oldDir, busyTimeoutMs: 200);
        Assert.NotEmpty(store.LoadAll());

        // The index is locked while the walk asks for its snapshot: nothing may be marked.
        using (var holder = OpenRaw(dbPath))
        {
            holder.Open();
            using var begin = holder.CreateCommand();
            begin.CommandText = "BEGIN EXCLUSIVE";
            begin.ExecuteNonQuery();
            new StatsIndexer(store, emptyClaude, emptyCodex).IndexOnce();
        }

        Assert.False(store.IsSessionBackfillDone());

        // An index with usage rows but no remembered file is no snapshot to build sessions from either.
        Run(dbPath, "DELETE FROM source_file");
        new StatsIndexer(store, emptyClaude, emptyCodex).IndexOnce();
        Assert.False(store.IsSessionBackfillDone());
        Assert.Empty(store.LoadSessions());

        // With the files in place the pass still runs once the snapshot is good.
        using var healthyDir = TestPaths.CreateDisposableDirectory("stats-m1-healthy");
        DowngradeToV6(Path.Combine(firstDir, "stats.db"), healthyDir);
        var healthy = new StatsStore(healthyDir);
        new StatsIndexer(healthy, claudeRoot, codexRoot).IndexOnce();
        Assert.True(healthy.IsSessionBackfillDone());
        Assert.NotEmpty(healthy.LoadSessions());
    }

    [Fact]
    public void The_first_line_probe_of_a_codex_file_never_reads_past_its_cap()
    {
        using var dir = TestPaths.CreateDisposableDirectory("stats-first-line");
        var path = Path.Combine(dir, "rollout.jsonl");

        File.WriteAllText(path, "{\"a\":1}\r\n{\"b\":2}\n");
        Assert.Equal("{\"a\":1}", StatsIndexer.ReadFirstLineCapped(path, 1024));

        File.WriteAllText(path, "{\"only\":1}");
        Assert.Equal("{\"only\":1}", StatsIndexer.ReadFirstLineCapped(path, 1024));

        // A long line without a break (a damaged file) is not read into memory: no first line.
        File.WriteAllText(path, new string('x', 200_000));
        Assert.Null(StatsIndexer.ReadFirstLineCapped(path, 100_000));
        File.WriteAllText(path, new string('x', 200_000) + "\n{}\n");
        Assert.Null(StatsIndexer.ReadFirstLineCapped(path, 100_000));

        File.WriteAllText(path, "");
        Assert.Null(StatsIndexer.ReadFirstLineCapped(path, 1024));
    }

    [Fact]
    public void A_new_database_needs_no_backfill_and_has_the_session_tables()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v7-fresh");
        var store = new StatsStore(dataDir);

        Assert.Empty(store.LoadAll());
        Assert.True(store.IsSessionBackfillDone());
        Assert.Empty(store.LoadSessions());
        Assert.Equal(0L, Scalar(Path.Combine(dataDir, "stats.db"), "SELECT COUNT(*) FROM session"));
    }

    [Fact]
    public void ClaudeUsageLogParser_reads_the_session_id_and_the_sidechain_flag()
    {
        const string main = """
            {"type":"assistant","sessionId":"s-1","timestamp":"2026-08-21T05:46:56.649Z","message":{"model":"m","usage":{"input_tokens":2,"output_tokens":3}}}
            """;
        const string sidechain = """
            {"type":"assistant","isSidechain":true,"sessionId":"s-1","agentId":"a1","timestamp":"2026-08-21T05:46:56.649Z","message":{"model":"m","usage":{"input_tokens":2,"output_tokens":3}}}
            """;

        Assert.True(ClaudeUsageLogParser.TryParse(main, out var mainEvent));
        Assert.Equal("s-1", mainEvent.SessionId);
        Assert.False(mainEvent.IsSidechain);

        Assert.True(ClaudeUsageLogParser.TryParse(sidechain, out var sidechainEvent));
        Assert.Equal("s-1", sidechainEvent.SessionId);
        Assert.True(sidechainEvent.IsSidechain);
    }

    [Fact]
    public void CodexUsageLogParser_reads_the_session_id_from_the_session_meta_line()
    {
        Assert.Equal("019a-1", CodexUsageLogParser.TryExtractSessionIdFromSessionMetaLine(
            """{"type":"session_meta","payload":{"id":"019a-1","cwd":"C:\\P"}}"""));
        Assert.Equal("old-1", CodexUsageLogParser.TryExtractSessionIdFromSessionMetaLine(
            """{"type":"session_meta","payload":{"session_id":"old-1"}}"""));
        Assert.Null(CodexUsageLogParser.TryExtractSessionIdFromSessionMetaLine("""{"type":"turn_context","payload":{"id":"x"}}"""));
        Assert.Null(CodexUsageLogParser.TryExtractSessionIdFromSessionMetaLine("not json"));
    }

    private static string SessionLine(string timestamp, string sessionId, bool sidechain, string model, long input, long output, string messageId) =>
        "{\"type\":\"assistant\",\"sessionId\":\"" + sessionId + "\"" + (sidechain ? ",\"isSidechain\":true" : "") +
        ",\"requestId\":\"r-" + messageId + "\",\"cwd\":\"C:\\\\Projects\\\\Sample\",\"timestamp\":\"" + timestamp +
        "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":" + input +
        ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}";

    private static string CodexSessionFile(string sessionId) =>
        "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + sessionId + "\",\"cwd\":\"C:\\\\Projects\\\\Other\"}}\n" +
        "{\"type\":\"turn_context\",\"payload\":{\"model\":\"modelC\",\"effort\":\"high\"}}\n" +
        "{\"type\":\"event_msg\",\"timestamp\":\"2026-01-03T10:00:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":10,\"cache_write_input_tokens\":0,\"output_tokens\":20}}}}\n" +
        "{\"type\":\"event_msg\",\"timestamp\":\"2026-01-03T11:00:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":150,\"cached_input_tokens\":30,\"cache_write_input_tokens\":0,\"output_tokens\":50}}}}\n";

    private static void WriteSessionFixture(string claudeRoot, string codexRoot)
    {
        // The main transcript and one subagent transcript of the same session, plus a Codex rollout.
        var sessionDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "C--Projects-Sample")).FullName;
        File.WriteAllText(Path.Combine(sessionDir, "sess-1.jsonl"),
            SessionLine("2026-01-01T10:00:00Z", "sess-1", false, "modelA", 100, 10, "m1") + "\n" +
            SessionLine("2026-01-01T10:30:00Z", "sess-1", false, "modelB", 300, 30, "m2") + "\n" +
            SessionLine("2026-01-01T11:00:00Z", "sess-1", false, "modelB", 50, 5, "m3") + "\n");
        var subagentDir = Directory.CreateDirectory(Path.Combine(sessionDir, "sess-1", "subagents")).FullName;
        File.WriteAllText(Path.Combine(subagentDir, "agent-a1.jsonl"),
            SessionLine("2026-01-01T10:15:00Z", "sess-1", true, "modelA", 40, 4, "s1") + "\n");

        var codexDir = Directory.CreateDirectory(Path.Combine(codexRoot, "2026", "01", "03")).FullName;
        File.WriteAllText(Path.Combine(codexDir, "rollout-2026-01-03T10-00-00-abc.jsonl"), CodexSessionFile("codex-sess-1"));
    }

    [Fact]
    public void Subagent_transcript_counts_for_the_real_project_flagged_and_inside_its_session()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-sess-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-sess-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-sess-data");
        WriteSessionFixture(claudeRoot, codexRoot);

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        var usage = store.LoadAll();
        Assert.DoesNotContain(usage, row => row.Project == "subagents");
        var subagentRow = Assert.Single(usage, row => row.Subagent);
        Assert.Equal("C:\\Projects\\Sample", subagentRow.Project);
        Assert.Equal(44, subagentRow.TotalTokens);
        Assert.Equal(100 + 10 + 300 + 30 + 50 + 5, usage.Where(row => row.Provider == "claude" && !row.Subagent).Sum(row => row.TotalTokens));

        var sessions = store.LoadSessions();
        var claude = Assert.Single(sessions, session => session.Provider == "claude");
        Assert.Equal("sess-1", claude.SessionId);
        Assert.Equal("C:\\Projects\\Sample", claude.Project);
        Assert.Equal(44, claude.SubagentTokens);
        Assert.Equal("modelB", claude.MainModel);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), claude.FirstUtc);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero), claude.LastUtc);

        // Every session adds up to the usage rows of its provider.
        Assert.Equal(usage.Where(row => row.Provider == "claude").Sum(row => row.TotalTokens), claude.TotalTokens);
        Assert.Equal(usage.Where(row => row.Provider == "claude").Sum(row => row.InputTokens), claude.InputTokens);
        Assert.Equal(usage.Where(row => row.Provider == "claude").Sum(row => row.OutputTokens), claude.OutputTokens);

        var codex = Assert.Single(sessions, session => session.Provider == "codex");
        Assert.Equal("codex-sess-1", codex.SessionId);
        Assert.Equal("C:\\Projects\\Other", codex.Project);
        Assert.Equal("modelC", codex.MainModel);
        Assert.Equal(0, codex.SubagentTokens);
        Assert.Equal(usage.Where(row => row.Provider == "codex").Sum(row => row.TotalTokens), codex.TotalTokens);
    }

    [Fact]
    public void Session_totals_keep_adding_up_when_a_transcript_grows_between_walks()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-sess-grow-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-sess-grow-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-sess-grow-data");
        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "P")).FullName;
        var file = Path.Combine(projectDir, "sess-1.jsonl");
        File.WriteAllText(file, SessionLine("2026-01-01T10:00:00Z", "sess-1", false, "modelA", 10, 1, "m1") + "\n");

        var store = new StatsStore(dataDir);
        var indexer = new StatsIndexer(store, claudeRoot, codexRoot, () => DateTime.UtcNow.AddHours(1));
        indexer.IndexOnce();

        File.AppendAllText(file, SessionLine("2026-01-01T12:00:00Z", "sess-1", false, "modelA", 20, 2, "m2") + "\n");
        indexer.IndexOnce();

        var session = Assert.Single(store.LoadSessions());
        Assert.Equal(33, session.TotalTokens);
        Assert.Equal(store.LoadAll().Sum(row => row.TotalTokens), session.TotalTokens);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), session.LastUtc);
    }

    /// <summary>Turns a database the current build wrote into the previous schema, as a copy in a new
    /// folder: the session tables go away and the usage rows lose the two new key columns (their
    /// totals are merged, as the old key had no room for them).</summary>
    private static string DowngradeToV6(string currentDbPath, string newDirectory)
    {
        var path = Path.Combine(newDirectory, "stats.db");
        File.Copy(currentDbPath, path);
        Run(path, """
            DROP TABLE session;
            DROP TABLE session_model;
            DROP TABLE meta;
            CREATE TABLE usage_v6 (
                provider TEXT NOT NULL, day TEXT NOT NULL, hour INTEGER NOT NULL DEFAULT 0, model TEXT NOT NULL,
                project TEXT NOT NULL, effort TEXT NOT NULL DEFAULT '',
                input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, day, hour, model, project, effort)
            );
            INSERT INTO usage_v6
            SELECT provider, day, hour, model, project, effort,
                SUM(input_tokens), SUM(output_tokens), SUM(cache_creation_tokens), SUM(cache_read_tokens)
            FROM usage GROUP BY provider, day, hour, model, project, effort;
            DROP TABLE usage;
            ALTER TABLE usage_v6 RENAME TO usage;
            UPDATE schema_version SET version = 6;
            """);
        return path;
    }

    [Fact]
    public void Backfill_fills_the_sessions_of_migrated_rows_without_touching_the_usage()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-fill-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-fill-codex");
        using var firstDir = TestPaths.CreateDisposableDirectory("stats-fill-first");
        using var oldDir = TestPaths.CreateDisposableDirectory("stats-fill-old");
        WriteSessionFixture(claudeRoot, codexRoot);

        // The reference: what a walk over these files stores today.
        var reference = new StatsStore(firstDir);
        new StatsIndexer(reference, claudeRoot, codexRoot).IndexOnce();
        var expectedSessions = reference.LoadSessions().OrderBy(session => session.SessionId, StringComparer.Ordinal).ToList();

        // The same files as an index of the previous build left them: usage and file markers, no sessions.
        DowngradeToV6(Path.Combine(firstDir, "stats.db"), oldDir);

        var migrated = new StatsStore(oldDir);
        var usageBefore = migrated.LoadAll();
        Assert.Empty(migrated.LoadSessions());
        Assert.False(migrated.IsSessionBackfillDone());

        new StatsIndexer(migrated, claudeRoot, codexRoot).IndexOnce();

        Assert.True(migrated.IsSessionBackfillDone());
        Assert.Equal(Totals(usageBefore), Totals(migrated.LoadAll()));
        Assert.Equal(usageBefore.Sum(row => row.TotalTokens), migrated.LoadAll().Sum(row => row.TotalTokens));

        var sessions = migrated.LoadSessions().OrderBy(session => session.SessionId, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedSessions, sessions);
        Assert.Equal(usageBefore.Sum(row => row.TotalTokens), sessions.Sum(session => session.TotalTokens));

        // Walking again neither repeats the pass nor changes anything.
        new StatsIndexer(migrated, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(expectedSessions, migrated.LoadSessions().OrderBy(session => session.SessionId, StringComparer.Ordinal).ToList());
        Assert.Equal(Totals(usageBefore), Totals(migrated.LoadAll()));
    }

    [Fact]
    public void Backfill_covers_only_what_the_usage_rows_already_cover_and_the_walk_adds_the_rest()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-fill-grow-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-fill-grow-codex");
        using var firstDir = TestPaths.CreateDisposableDirectory("stats-fill-grow-first");
        using var oldDir = TestPaths.CreateDisposableDirectory("stats-fill-grow-old");
        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "P")).FullName;
        var file = Path.Combine(projectDir, "sess-1.jsonl");
        File.WriteAllText(file, SessionLine("2026-01-01T10:00:00Z", "sess-1", false, "modelA", 10, 1, "m1") + "\n");

        var reference = new StatsStore(firstDir);
        new StatsIndexer(reference, claudeRoot, codexRoot, () => DateTime.UtcNow.AddHours(1)).IndexOnce();
        DowngradeToV6(Path.Combine(firstDir, "stats.db"), oldDir);

        // The transcript grows after the old index and before the first walk of the new build.
        File.AppendAllText(file, SessionLine("2026-01-01T12:00:00Z", "sess-1", false, "modelA", 20, 2, "m2") + "\n");

        var migrated = new StatsStore(oldDir);
        new StatsIndexer(migrated, claudeRoot, codexRoot, () => DateTime.UtcNow.AddHours(1)).IndexOnce();

        var session = Assert.Single(migrated.LoadSessions());
        Assert.Equal(33, session.TotalTokens);
        Assert.Equal(migrated.LoadAll().Sum(row => row.TotalTokens), session.TotalTokens);
    }
}
