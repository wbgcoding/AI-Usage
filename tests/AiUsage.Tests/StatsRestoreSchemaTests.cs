using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>A token index taken from a backup is a foreign file: whatever else it declares must not
/// survive the restore, and an unusable version value must read as "not a usable index".</summary>
public sealed class StatsRestoreSchemaTests : IDisposable
{
    private readonly List<DisposableTestDirectory> _directories = [];

    private string Temp()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-restore-schema");
        _directories.Add(directory);
        return directory.Path;
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
            IndexPools.ReleaseUnder(directory.Path);
        foreach (var directory in _directories)
            directory.Dispose();
    }

    private static StatsRecord Row(int day) =>
        new("claude", new DateOnly(2026, 1, day), "m", "p", 10, 0, 0, 0);

    private static string MakeIndex(string directory)
    {
        new StatsStore(directory).AddDelta([Row(1)]);
        IndexPools.Release(directory);
        return Path.Combine(directory, "stats.db");
    }

    private static void Raw(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> Names(string path, string type)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%'";
        command.Parameters.AddWithValue("$type", type);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public void A_restored_index_loses_triggers_views_and_foreign_tables_and_keeps_its_rows_on_insert()
    {
        var directory = Temp();
        var path = MakeIndex(directory);
        Raw(path, """
            CREATE TRIGGER wipe AFTER INSERT ON usage BEGIN DELETE FROM usage; END;
            CREATE VIEW everything AS SELECT * FROM usage;
            CREATE TABLE extra (x INTEGER);
            CREATE INDEX extra_x ON extra (x);
            CREATE INDEX usage_day ON usage (day);
            """);

        StatsStore.PrepareRestoredIndex(path, []);

        Assert.Empty(Names(path, "trigger"));
        Assert.Empty(Names(path, "view"));
        Assert.Empty(Names(path, "index"));
        Assert.DoesNotContain("extra", Names(path, "table"));
        Assert.Contains("usage", Names(path, "table"));

        var store = new StatsStore(directory);
        store.AddDelta([Row(2)]);
        Assert.Equal(2, store.LoadAll().Count);
    }

    [Fact]
    public void A_version_that_is_not_a_whole_number_reads_as_unusable_without_throwing()
    {
        var path = MakeIndex(Temp());
        Assert.NotNull(StatsStore.TryReadSchemaVersion(path));

        Raw(path, "UPDATE schema_version SET version = 'abc'");
        Assert.Null(StatsStore.TryReadSchemaVersion(path));

        Raw(path, "UPDATE schema_version SET version = x'00ff'");
        Assert.Null(StatsStore.TryReadSchemaVersion(path));

        Raw(path, "UPDATE schema_version SET version = 7.5");
        Assert.Null(StatsStore.TryReadSchemaVersion(path));
    }
}
