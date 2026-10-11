using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>The index finds a session file's row whatever the casing of its path, so a root spelled
/// in another case neither reads the file again from the start nor leaves a second row behind.</summary>
public class StatsSourceFileCaseTests
{
    private static string ClaudeLine(string timestamp, long input, long output) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"message\":{\"model\":\"modelA\"," +
        "\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output +
        ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}";

    private static int RowCount(string dataDir)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "stats.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_file";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void A_root_spelled_in_another_case_adds_no_usage_and_no_second_row()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-case-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-case-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-case-data");
        var project = Directory.CreateDirectory(Path.Combine(claudeRoot, "Proj")).FullName;
        var file = Path.Combine(project, "a.jsonl");
        File.WriteAllText(file, ClaudeLine("2026-01-01T00:00:00Z", 10, 5) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(15, store.LoadAll().Sum(r => r.TotalTokens));

        var lowerRoot = ((string)claudeRoot).ToLowerInvariant();
        Assert.NotEqual((string)claudeRoot, lowerRoot);
        new StatsIndexer(store, lowerRoot, codexRoot).IndexOnce();

        Assert.Equal(15, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.Equal(1, RowCount(dataDir));
    }

    [Fact]
    public void A_grown_file_walked_under_another_case_adds_only_its_new_lines()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-case-grow-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-case-grow-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-case-grow-data");
        var project = Directory.CreateDirectory(Path.Combine(claudeRoot, "Proj")).FullName;
        var file = Path.Combine(project, "a.jsonl");
        File.WriteAllText(file, ClaudeLine("2026-01-01T00:00:00Z", 10, 5) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        File.AppendAllText(file, ClaudeLine("2026-01-01T01:00:00Z", 3, 2) + "\n");
        new StatsIndexer(store, ((string)claudeRoot).ToLowerInvariant(), codexRoot).IndexOnce();

        Assert.Equal(20, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.Equal(1, RowCount(dataDir));
        Assert.Equal(file, store.GetSourceFile(file.ToUpperInvariant())!.Path);
    }

    [Fact]
    public void A_lookup_ignores_the_casing_of_the_path()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-case-lookup");
        var store = new StatsStore(dataDir);
        store.SetSourceFile(new StatsSourceFileState(@"C:\X\a.jsonl", "claude", 100, 100, DateTime.UtcNow));

        Assert.True(store.TryGetSourceFile(@"c:\x\a.jsonl", out var found));
        Assert.Equal(100, found!.Offset);
        Assert.Equal(@"C:\X\a.jsonl", found.Path);
        Assert.True(store.TryLoadSourceFiles(out var all));
        Assert.True(all.ContainsKey(@"c:\x\A.JSONL"));
    }

    [Fact]
    public void Two_rows_that_differ_only_by_case_load_as_the_one_with_the_larger_offset()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-case-twins");
        var store = new StatsStore(dataDir);
        store.SetSourceFile(new StatsSourceFileState(@"C:\X\a.jsonl", "claude", 100, 100, DateTime.UtcNow));
        store.SetSourceFile(new StatsSourceFileState(@"c:\x\a.jsonl", "claude", 250, 250, DateTime.UtcNow));

        var all = store.LoadSourceFiles();

        var single = Assert.Single(all);
        Assert.Equal(250, single.Value.Offset);
        Assert.Equal(250, store.GetSourceFile(@"C:\X\a.jsonl")!.Offset);
    }
}
