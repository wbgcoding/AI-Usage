using System.Text;
using AiUsage.Providers.Parsing;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

public class AntigravityStateReaderTests : IDisposable
{
    [Fact]
    public void TryReadPlanTier_returns_null_when_the_database_file_does_not_exist()
    {
        var path = Track(TestPaths.GetPath("ai-usage-missing", ".vscdb"));

        Assert.Null(AntigravityStateReader.TryReadPlanTier(path));
    }

    [Fact]
    public void TryReadPlanTier_returns_null_when_the_key_is_missing()
    {
        var path = CreateDatabase(rows: []);

        Assert.Null(AntigravityStateReader.TryReadPlanTier(path));
    }

    [Fact]
    public void TryReadPlanTier_returns_null_for_an_unparseable_base64_value()
    {
        var path = CreateDatabase([("antigravityUnifiedStateSync.userStatus", "not valid base64!!")]);

        Assert.Null(AntigravityStateReader.TryReadPlanTier(path));
    }

    [Fact]
    public void TryReadPlanTier_returns_null_when_the_decoded_value_names_no_known_plan()
    {
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes("some-unrelated-protobuf-noise"));
        var path = CreateDatabase([("antigravityUnifiedStateSync.userStatus", value)]);

        Assert.Null(AntigravityStateReader.TryReadPlanTier(path));
    }

    [Fact]
    public void TryReadPlanTier_finds_the_plan_marker_inside_the_decoded_bytes_without_a_protobuf_schema()
    {
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes("Google AI Pronoise"));
        var path = CreateDatabase([("antigravityUnifiedStateSync.userStatus", value)]);

        Assert.Equal("Pro", AntigravityStateReader.TryReadPlanTier(path));
    }

    [Fact]
    public void An_immutable_read_only_open_really_works_on_this_sqlite_build()
    {
        // Without this the reader would still return the right answer - by silently copying a
        // foreign application's database on every single read, which is the thing being avoided.
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes("Google AI Ultranoise"));
        var path = CreateDatabase([("antigravityUnifiedStateSync.userStatus", value)]);

        using var connection = new SqliteConnection(AntigravityStateReader.ImmutableConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ItemTable";

        Assert.Equal(1L, command.ExecuteScalar());
    }

    /// <summary>An immutable open pins a snapshot of the main database file at open time and never
    /// looks at the WAL, so it silently misses a write the IDE committed but has not yet checkpointed
    /// back into that file - the reader's normal read-only attempt (tried first) must not have the
    /// same blind spot.</summary>
    [Fact]
    public void TryReadPlanTier_sees_a_write_still_sitting_in_an_uncheckpointed_WAL()
    {
        var path = Track(TestPaths.GetPath("ai-usage-wal", ".vscdb"));
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes("Google AI Standardnoise"));

        // A second connection to the same file, kept open past the writer's own Dispose, stops
        // SQLite from performing its usual automatic checkpoint when the writer below closes - the
        // insert below stays parked in the "-wal" side file instead of being folded back into path.
        using var holdOpen = new SqliteConnection($"Data Source={path}");
        holdOpen.Open();

        using (var writer = new SqliteConnection($"Data Source={path}"))
        {
            writer.Open();
            using (var walMode = writer.CreateCommand())
            {
                walMode.CommandText = "PRAGMA journal_mode=WAL;";
                walMode.ExecuteNonQuery();
            }
            using (var create = writer.CreateCommand())
            {
                create.CommandText = "CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)";
                create.ExecuteNonQuery();
            }
            using var insert = writer.CreateCommand();
            insert.CommandText = "INSERT INTO ItemTable (key, value) VALUES ($key, $value)";
            insert.Parameters.AddWithValue("$key", "antigravityUnifiedStateSync.userStatus");
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }

        Assert.True(File.Exists(path + "-wal"));
        Assert.Equal("Google AI Standard", AntigravityStateReader.TryReadPlanTier(path));
    }

    [Fact]
    public void CleanUpLeftoverSnapshots_removes_a_copy_an_earlier_run_could_not_delete()
    {
        var directory = Track(TestPaths.CreateDirectory("ai-usage-cache"));
        var leftover = Path.Combine(directory, "antigravity-0123456789abcdef.vscdb");
        File.WriteAllText(leftover, "leftover");
        File.WriteAllText(leftover + "-wal", "leftover");
        var old = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(leftover, old);
        File.SetLastWriteTimeUtc(leftover + "-wal", old);
        var unrelated = Path.Combine(directory, "settings.json");
        File.WriteAllText(unrelated, "{}");

        AntigravityStateReader.CleanUpLeftoverSnapshots(directory);

        Assert.False(File.Exists(leftover));
        Assert.False(File.Exists(leftover + "-wal"));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void CleanUpLeftoverSnapshots_keeps_a_copy_younger_than_an_hour()
    {
        var directory = Track(TestPaths.CreateDirectory("ai-usage-cache"));
        var fresh = Path.Combine(directory, "antigravity-fedcba9876543210.vscdb");
        File.WriteAllText(fresh, "in use");

        AntigravityStateReader.CleanUpLeftoverSnapshots(directory);

        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void A_snapshot_read_leaves_no_file_behind_and_writes_nothing_into_the_data_folder()
    {
        var source = CreateDatabase([("antigravityUnifiedStateSync.userStatus",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("Google AI Pro noise")))]);
        var snapshotDirectory = Track(TestPaths.CreateDirectory("ai-usage-snapshots"));
        var dataDirectory = AiUsage.Storage.AppPaths.DataDirectory;
        var before = Directory.EnumerateFiles(dataDirectory, "antigravity-*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(snapshotDirectory, StringComparison.OrdinalIgnoreCase)).ToHashSet();

        var plan = AntigravityStateReader.ReadPlanTierFromSnapshot(source, snapshotDirectory);

        Assert.Equal("Pro", plan);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshotDirectory));
        Assert.DoesNotContain(Directory.EnumerateFiles(dataDirectory, "antigravity-*", SearchOption.AllDirectories),
            f => !before.Contains(f));
    }

    [Fact]
    public void A_fresh_snapshot_of_an_old_database_is_not_old_enough_for_the_sweep()
    {
        var directory = Track(TestPaths.CreateDirectory("ai-usage-snapshots"));
        var source = Path.Combine(directory, "source.vscdb");
        var copy = Path.Combine(directory, "antigravity-copy.vscdb");
        File.WriteAllText(source, "data");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-3));

        AntigravityStateReader.CopySnapshot(source, copy);
        AntigravityStateReader.CleanUpLeftoverSnapshots(directory);

        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void The_live_database_read_does_not_keep_a_pooled_handle()
    {
        Assert.Contains("Pooling=False", AntigravityStateReader.ReadOnlySharedConnectionString(@"C:\x\state.vscdb"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_old_roaming_cache_folder_loses_its_leftover_copies_and_goes_away_when_empty()
    {
        var directory = Track(TestPaths.CreateDirectory("ai-usage-legacy-cache"));
        var old = DateTime.UtcNow.AddDays(-2);
        var leftover = Path.Combine(directory, "antigravity-0123456789abcdef.vscdb");
        File.WriteAllText(leftover, "x");
        File.WriteAllText(leftover + "-shm", "x");
        File.SetLastWriteTimeUtc(leftover, old);
        File.SetLastWriteTimeUtc(leftover + "-shm", old);

        AntigravityStateReader.CleanUpLegacyCache(directory);

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void The_old_roaming_cache_folder_stays_when_it_holds_something_else()
    {
        var directory = Track(TestPaths.CreateDirectory("ai-usage-legacy-cache-other"));
        var other = Path.Combine(directory, "notes.txt");
        File.WriteAllText(other, "x");

        AntigravityStateReader.CleanUpLegacyCache(directory);

        Assert.True(File.Exists(other));
    }

    [Fact]
    public void The_default_snapshot_folder_is_machine_local()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(local, AntigravityStateReader.SnapshotDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanUpLeftoverSnapshots_does_nothing_when_the_folder_does_not_exist()
    {
        var directory = Track(TestPaths.GetPath("ai-usage-cache"));

        AntigravityStateReader.CleanUpLeftoverSnapshots(directory);

        Assert.False(Directory.Exists(directory));
    }

    private string CreateDatabase(IEnumerable<(string Key, string Value)> rows)
    {
        var path = Track(TestPaths.GetPath("ai-usage-vscdb", ".vscdb"));
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)";
            create.ExecuteNonQuery();
        }

        foreach (var (key, value) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO ItemTable (key, value) VALUES ($key, $value)";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", value);
            insert.ExecuteNonQuery();
        }

        return path;
    }

    private readonly List<string> _cleanupPaths = [];

    private string Track(string path)
    {
        _cleanupPaths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _cleanupPaths)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
                foreach (var companion in new[] { path + "-wal", path + "-shm" })
                    if (File.Exists(companion))
                        File.Delete(companion);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
