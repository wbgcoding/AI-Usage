using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>
/// The one-time pass that moves usage tokens of subagent transcripts onto the subagent rows, for
/// rows that were stored before the subagent flag existed.
/// </summary>
public class StatsSubagentBackfillTests
{
    private const string BackfillKey = "subagent_usage_backfilled";

    private static void Run(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Line(string timestamp, bool sidechain, string model, long input, long output, string messageId) =>
        "{\"type\":\"assistant\",\"sessionId\":\"sess-1\"" + (sidechain ? ",\"isSidechain\":true" : "") +
        ",\"requestId\":\"r-" + messageId + "\",\"cwd\":\"C:\\\\Projects\\\\Sample\",\"timestamp\":\"" + timestamp +
        "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":" + input +
        ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}";

    /// <summary>A main transcript and a subagent transcript whose first lines share one usage key
    /// (same hour, model, project), plus a line of each in a key of their own.</summary>
    private static void WriteTranscripts(string claudeRoot)
    {
        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "C--Projects-Sample")).FullName;
        File.WriteAllText(Path.Combine(projectDir, "sess-1.jsonl"),
            Line("2026-01-01T10:00:00Z", false, "modelA", 100, 10, "m1") + "\n" +
            Line("2026-01-01T12:00:00Z", false, "modelB", 300, 30, "m2") + "\n");
        var subagentDir = Directory.CreateDirectory(Path.Combine(projectDir, "sess-1", "subagents")).FullName;
        File.WriteAllText(Path.Combine(subagentDir, "agent-a1.jsonl"),
            Line("2026-01-01T10:15:00Z", true, "modelA", 40, 4, "s1") + "\n" +
            Line("2026-01-01T14:00:00Z", true, "modelC", 7, 1, "s2") + "\n");
    }

    private static string Rows(StatsStore store) =>
        string.Join(";", store.LoadAll()
            .OrderBy(row => row.Day).ThenBy(row => row.Hour).ThenBy(row => row.Model, StringComparer.Ordinal).ThenBy(row => row.Subagent)
            .Select(row => $"{row.Day:yyyy-MM-dd}|{row.Hour}|{row.Model}|{(row.Subagent ? "sub" : "main")}|{row.InputTokens}|{row.OutputTokens}"));

    /// <summary>Turns a current database into one written before the flag existed: every flagged row
    /// is merged into the unflagged row of its key and the pass is not marked done.</summary>
    private static void MakeOldStyle(string dbPath) => Run(dbPath, $"""
        CREATE TABLE merged AS
            SELECT provider, day, hour, model, project, effort, machine, 0 AS subagent,
                SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens,
                SUM(cache_creation_tokens) AS cache_creation_tokens, SUM(cache_read_tokens) AS cache_read_tokens
            FROM usage GROUP BY provider, day, hour, model, project, effort, machine;
        DELETE FROM usage;
        INSERT INTO usage SELECT * FROM merged;
        DROP TABLE merged;
        DELETE FROM meta WHERE key = '{BackfillKey}';
        """);

    private static (StatsStore Reference, string ReferenceRows, string OldDir, StatsStore Old) OldStyle(
        string claudeRoot, string codexRoot, string referenceDir, string oldDir)
    {
        var reference = new StatsStore(referenceDir);
        new StatsIndexer(reference, claudeRoot, codexRoot).IndexOnce();
        File.Copy(Path.Combine(referenceDir, "stats.db"), Path.Combine(oldDir, "stats.db"));
        MakeOldStyle(Path.Combine(oldDir, "stats.db"));
        return (reference, Rows(reference), oldDir, new StatsStore(oldDir));
    }

    [Fact]
    public void A_new_database_needs_no_subagent_backfill()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-sub-fresh");

        Assert.True(new StatsStore(dataDir).IsSubagentUsageBackfillDone());
    }

    [Fact]
    public void Old_rows_of_a_subagent_transcript_move_to_the_subagent_rows_and_the_second_run_does_nothing()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-sub-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-sub-codex");
        using var referenceDir = TestPaths.CreateDisposableDirectory("stats-sub-ref");
        using var oldDir = TestPaths.CreateDisposableDirectory("stats-sub-old");
        WriteTranscripts(claudeRoot);
        var (_, expected, _, old) = OldStyle(claudeRoot, codexRoot, referenceDir, oldDir);

        Assert.DoesNotContain("|sub|", Rows(old));
        Assert.False(old.IsSubagentUsageBackfillDone());
        var totalBefore = old.LoadAll().Sum(row => row.TotalTokens);

        new StatsIndexer(old, claudeRoot, codexRoot).IndexOnce();

        Assert.True(old.IsSubagentUsageBackfillDone());
        Assert.Equal(expected, Rows(old));
        Assert.Equal(totalBefore, old.LoadAll().Sum(row => row.TotalTokens));
        // The key shared with the main transcript keeps the main tokens; the key of the subagent
        // alone has no unflagged row left.
        Assert.Contains("2026-01-01|", Rows(old));
        Assert.DoesNotContain(old.LoadAll(), row => row.Model == "modelC" && !row.Subagent);

        new StatsIndexer(old, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(expected, Rows(old));
    }

    [Fact]
    public void A_key_with_newer_subagent_rows_moves_only_the_difference()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-sub-diff-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-sub-diff-codex");
        using var referenceDir = TestPaths.CreateDisposableDirectory("stats-sub-diff-ref");
        using var oldDir = TestPaths.CreateDisposableDirectory("stats-sub-diff-old");
        WriteTranscripts(claudeRoot);
        var (_, expected, _, old) = OldStyle(claudeRoot, codexRoot, referenceDir, oldDir);

        // 15 of the 40 input tokens of the shared key were already indexed as subagent tokens.
        var dbPath = Path.Combine(oldDir, "stats.db");
        Run(dbPath, """
            UPDATE usage SET input_tokens = input_tokens - 15 WHERE model = 'modelA';
            INSERT INTO usage (provider, day, hour, model, project, effort, machine, subagent, input_tokens)
            SELECT provider, day, hour, model, project, effort, machine, 1, 15 FROM usage WHERE model = 'modelA';
            """);
        var totalBefore = old.LoadAll().Sum(row => row.TotalTokens);

        new StatsIndexer(old, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(expected, Rows(old));
        Assert.Equal(totalBefore, old.LoadAll().Sum(row => row.TotalTokens));
    }

    [Fact]
    public void A_main_row_never_gives_up_more_than_it_holds()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-sub-cap-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-sub-cap-codex");
        using var referenceDir = TestPaths.CreateDisposableDirectory("stats-sub-cap-ref");
        using var oldDir = TestPaths.CreateDisposableDirectory("stats-sub-cap-old");
        WriteTranscripts(claudeRoot);
        var (_, _, _, old) = OldStyle(claudeRoot, codexRoot, referenceDir, oldDir);

        // The stored main row of the shared key is smaller than the subagent lines claim.
        Run(Path.Combine(oldDir, "stats.db"), "UPDATE usage SET input_tokens = 25, output_tokens = 0 WHERE model = 'modelA'");
        var totalBefore = old.LoadAll().Sum(row => row.TotalTokens);

        new StatsIndexer(old, claudeRoot, codexRoot).IndexOnce();

        var rows = old.LoadAll();
        Assert.All(rows, row => Assert.True(row.InputTokens >= 0 && row.OutputTokens >= 0));
        Assert.Equal(totalBefore, rows.Sum(row => row.TotalTokens));
        var shared = Assert.Single(rows, row => row.Model == "modelA");
        Assert.True(shared.Subagent);
        Assert.Equal(25, shared.InputTokens);
        Assert.True(old.IsSubagentUsageBackfillDone());
    }

    [Fact]
    public void A_pass_that_cannot_write_is_not_marked_done()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-sub-lock-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-sub-lock-codex");
        using var referenceDir = TestPaths.CreateDisposableDirectory("stats-sub-lock-ref");
        using var oldDir = TestPaths.CreateDisposableDirectory("stats-sub-lock-old");
        WriteTranscripts(claudeRoot);
        OldStyle(claudeRoot, codexRoot, referenceDir, oldDir);
        var store = new StatsStore(oldDir, busyTimeoutMs: 200);
        Assert.NotEmpty(store.LoadAll());

        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(oldDir, "stats.db"),
            Pooling = false,
        }.ToString()))
        {
            holder.Open();
            using var begin = holder.CreateCommand();
            begin.CommandText = "BEGIN EXCLUSIVE";
            begin.ExecuteNonQuery();

            Assert.Null(store.MoveSubagentUsageAndMarkBackfilled([]));
        }

        Assert.False(store.IsSubagentUsageBackfillDone());
    }
}
