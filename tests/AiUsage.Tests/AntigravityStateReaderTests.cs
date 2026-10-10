using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

public class AntigravityStateReaderTests : IDisposable
{
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
