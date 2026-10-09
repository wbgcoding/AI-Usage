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
