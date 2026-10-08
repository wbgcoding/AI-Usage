using AiUsage.Stats;

namespace AiUsage.Tests;

public class StatsIndexerProbeTests
{
    private const string UsageLine =
        """{"type":"assistant","timestamp":"2026-01-01T00:00:00Z","message":{"model":"modelA","usage":{"input_tokens":4,"output_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}}}""";

    private static StatsRecord[] Index(string sessionText)
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("probe-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("probe-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("probe-data");
        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "FolderName")).FullName;
        File.WriteAllText(Path.Combine(projectDir, "session.jsonl"), sessionText);
        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        return [.. store.LoadAll()];
    }

    [Fact]
    public void Project_probe_finds_a_cwd_inside_the_first_64_KiB()
    {
        var cwdLine = """{"type":"user","cwd":"C:\\Projects\\Sample"}""";
        var records = Index(new string('a', 30000) + "\n" + cwdLine + "\n" + UsageLine + "\n");

        Assert.All(records, r => Assert.Equal(@"C:\Projects\Sample", r.Project));
    }

    [Fact]
    public void Project_probe_does_not_look_past_the_first_64_KiB()
    {
        var cwdLine = """{"type":"user","cwd":"C:\\Projects\\Sample"}""";
        var records = Index(new string('a', 100 * 1024) + "\n" + cwdLine + "\n" + UsageLine + "\n");

        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.Equal("FolderName", r.Project));
    }

    [Fact]
    public void A_session_file_a_writer_still_holds_open_is_read_again_after_it_grows()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("probe-open-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("probe-open-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("probe-open-data");
        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "FolderName")).FullName;
        var store = new StatsStore(dataDir);
        var indexer = new StatsIndexer(store, claudeRoot, codexRoot);

        using var writer = new FileStream(Path.Combine(projectDir, "session.jsonl"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        void Append(string line)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            writer.Write(bytes);
            writer.Flush(true);
        }

        Append(UsageLine);
        indexer.IndexOnce();
        Thread.Sleep(1500);
        Append(UsageLine.Replace("\"input_tokens\":4", "\"input_tokens\":6"));
        indexer.IndexOnce();

        Assert.Equal(10, store.LoadAll().Sum(r => r.InputTokens));
    }

    [Fact]
    public void A_deleted_stats_database_is_rebuilt_on_the_next_open()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("probe-schema");
        var store = new StatsStore(dataDir);
        Assert.Empty(store.LoadAll());

        var dbPath = Path.Combine(dataDir, "stats.db");
        // Release only this database's pooled connection so the file can be deleted.
        using (var pooled = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, DefaultTimeout = 2 }.ToString()))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pooled);
        File.Delete(dbPath);
        store.SetSourceFile(new StatsSourceFileState("x.jsonl", "claude", 1, 1, DateTime.UtcNow));

        Assert.NotNull(store.GetSourceFile("x.jsonl"));
    }
}
