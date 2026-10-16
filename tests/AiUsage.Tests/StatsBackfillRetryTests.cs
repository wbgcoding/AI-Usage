using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>
/// The one-time passes over the remembered session files: each finishes by its own flag only, and
/// never while a remembered file that still exists could not be read (it is retried next walk).
/// </summary>
public class StatsBackfillRetryTests
{
    private const string SessionsKey = "sessions_backfilled";
    private const string SubagentKey = "subagent_usage_backfilled";
    private const string CopyKey = "claude_copy_backfilled";
    private const string OwnerKey = "claude_owner_pass";

    private static string Line(string timestamp, string messageId, long input, long output, string sessionId = "sess-1") =>
        "{\"type\":\"assistant\",\"sessionId\":\"" + sessionId + "\",\"requestId\":\"r-" + messageId + "\",\"cwd\":\"C:\\\\Projects\\\\Sample\",\"timestamp\":\"" +
        timestamp + "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"modelA\",\"usage\":{\"input_tokens\":" + input +
        ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}";

    private const string CodexFile =
        "{\"type\":\"session_meta\",\"payload\":{\"id\":\"codex-sess-1\",\"cwd\":\"C:\\\\Projects\\\\Other\"}}\n" +
        "{\"type\":\"turn_context\",\"payload\":{\"model\":\"modelC\",\"effort\":\"high\"}}\n" +
        "{\"type\":\"event_msg\",\"timestamp\":\"2026-01-03T10:00:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":10,\"cache_write_input_tokens\":0,\"output_tokens\":20}}}}\n";

    private static void Run(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string WriteTranscript(string claudeRoot, string name, DateTime created, params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(claudeRoot, "C--Projects-Sample")).FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Concat(lines.Select(line => line + "\n")));
        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, created);
        return path;
    }

    private static string WriteCodex(string codexRoot)
    {
        var dir = Directory.CreateDirectory(Path.Combine(codexRoot, "2026", "01", "03")).FullName;
        var path = Path.Combine(dir, "rollout-2026-01-03T10-00-00-abc.jsonl");
        File.WriteAllText(path, CodexFile);
        return path;
    }

    private static FileStream Lock(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    private static void OfferAllPasses(string dbPath) =>
        Run(dbPath, $"DELETE FROM session; DELETE FROM session_model; DELETE FROM meta WHERE key IN ('{SessionsKey}', '{SubagentKey}', '{CopyKey}');");

    private static readonly DateTime Old = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_locked_codex_file_keeps_the_session_pass_open_without_blocking_the_other_passes()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-retry-codex-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-retry-codex-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-retry-codex-data");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteTranscript(claudeRoot, "sess-1.jsonl", Old, Line("2026-01-01T10:00:00Z", "m1", 100, 5));
        var codex = WriteCodex(codexRoot);
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        OfferAllPasses(dbPath);

        using (Lock(codex))
            new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.False(store.IsSessionBackfillDone());
        Assert.True(store.IsSubagentUsageBackfillDone());
        Assert.True(store.IsClaudeCopyBackfillDone());

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.True(store.IsSessionBackfillDone());
        Assert.Contains(store.LoadSessions(), session => session.Provider == "codex" && session.SessionId == "codex-sess-1");
    }

    [Fact]
    public void A_locked_claude_file_keeps_every_claude_pass_open_until_it_can_be_read()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-retry-claude-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-retry-claude-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-retry-claude-data");
        var dbPath = Path.Combine(dataDir, "stats.db");
        var transcript = WriteTranscript(claudeRoot, "sess-1.jsonl", Old, Line("2026-01-01T10:00:00Z", "m1", 100, 5));
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        OfferAllPasses(dbPath);

        using (Lock(transcript))
            new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.False(store.IsSessionBackfillDone());
        Assert.False(store.IsSubagentUsageBackfillDone());
        Assert.False(store.IsClaudeCopyBackfillDone());

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.True(store.IsSessionBackfillDone());
        Assert.True(store.IsSubagentUsageBackfillDone());
        Assert.True(store.IsClaudeCopyBackfillDone());
        Assert.Single(store.LoadSessions());
    }
}
