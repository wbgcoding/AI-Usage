using AiUsage.Providers;

namespace AiUsage.Tests;

public class LocalFileScanTests : IDisposable
{
    [Fact]
    public void Only_the_newest_files_are_kept_and_returned_newest_first()
    {
        var root = TempDirectory();
        var now = DateTime.UtcNow;
        var paths = new List<string>();
        for (var i = 0; i < 100; i++)
        {
            var path = Path.Combine(root, $"file-{i}.txt");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-(100 - i)));
            paths.Add(path);
        }

        var result = LocalFileScan.NewestFiles(root, "*", maxDepth: 0, maxFiles: 5);

        Assert.Equal(paths.AsEnumerable().Reverse().Take(5), result.Select(f => f.FullName));
    }

    [Fact]
    public void A_tree_past_the_touched_entry_ceiling_stops_walking_instead_of_scanning_everything()
    {
        var root = TempDirectory();
        for (var i = 0; i < 5100; i++)
            File.WriteAllText(Path.Combine(root, $"file-{i}.txt"), "x");

        var touched = 0;
        LocalFileScan.NewestFiles(root, "*", maxDepth: 0, maxFiles: 40, isExcluded: _ =>
        {
            touched++;
            return true;
        });

        Assert.Equal(5000, touched);
    }

    [Fact]
    public void A_tree_of_five_hundred_files_across_six_levels_returns_at_most_the_configured_cap()
    {
        var root = TempDirectory();
        BuildDeepTree(root, levels: 6, filesPerLevel: 84); // 6 * 84 = 504 files, comfortably past the cap

        var result = LocalFileScan.NewestFiles(root, "*", maxDepth: 6, maxFiles: 40);

        Assert.True(result.Count <= 40);
    }

    [Fact]
    public void Results_come_back_newest_write_time_first()
    {
        var root = TempDirectory();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            var path = Path.Combine(root, $"file-{i}.txt");
            File.WriteAllText(path, "x");
            // Deliberately not in creation order, so a correct sort is the only way to pass.
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-(10 - i) * (i % 3 + 1)));
        }

        var result = LocalFileScan.NewestFiles(root, "*", maxDepth: 2, maxFiles: 100);

        Assert.Equal(10, result.Count);
        for (var i = 1; i < result.Count; i++)
            Assert.True(result[i - 1].LastWriteTimeUtc >= result[i].LastWriteTimeUtc, "not newest-first");
    }

    [Fact]
    public void A_file_the_exclusion_predicate_rejects_is_left_out_of_the_result()
    {
        var root = TempDirectory();
        var kept = Path.Combine(root, "kept.txt");
        var excluded = Path.Combine(root, "excluded.txt");
        File.WriteAllText(kept, "x");
        File.WriteAllText(excluded, "x");

        var result = LocalFileScan.NewestFiles(root, "*", maxDepth: 2, maxFiles: 100, isExcluded: path => path == excluded);

        Assert.Single(result);
        Assert.Equal(kept, result[0].FullName);
    }

    [Fact]
    public void An_empty_root_returns_an_empty_list_instead_of_throwing()
    {
        var result = LocalFileScan.NewestFiles(TempDirectory(), "*", maxDepth: 2, maxFiles: 10);

        Assert.Empty(result);
    }

    [Fact]
    public void A_missing_root_returns_an_empty_list_instead_of_throwing()
    {
        var missing = TestPaths.GetPath("ai-usage-missing");

        var result = LocalFileScan.NewestFiles(missing, "*", maxDepth: 2, maxFiles: 10);

        Assert.Empty(result);
    }

    [Fact]
    public void A_date_tree_past_the_touched_ceiling_still_returns_the_newest_day()
    {
        // 6000 files in year/month/day folders: a name-ascending walk would spend its whole budget on
        // the oldest days and never reach the newest folder.
        var root = TempDirectory();
        var old = DateTime.UtcNow.AddDays(-30);
        for (var day = 1; day <= 12; day++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "2024", "01", day.ToString("00"))).FullName;
            for (var i = 0; i < 500; i++)
            {
                var path = Path.Combine(folder, $"rollout-{i}.jsonl");
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, old);
            }
        }

        var newestFolder = Directory.CreateDirectory(Path.Combine(root, "2026", "10", "04")).FullName;
        var newest = Path.Combine(newestFolder, "rollout-newest.jsonl");
        File.WriteAllText(newest, "x");

        var result = LocalFileScan.NewestFiles(root, "rollout-*.jsonl", maxDepth: 4, maxFiles: 40);

        Assert.Equal(newest, result[0].FullName);
    }

    [Fact]
    public void Subfolders_are_walked_most_recently_changed_first_so_the_ceiling_cuts_the_stale_ones()
    {
        // The stale folder sorts first by name and holds more files than the whole budget: a
        // name-ordered walk spends everything there and never reaches the folder that changed lately.
        var root = TempDirectory();
        var stale = Directory.CreateDirectory(Path.Combine(root, "z-stale")).FullName;
        var old = DateTime.UtcNow.AddDays(-30);
        for (var i = 0; i < 5200; i++)
        {
            var path = Path.Combine(stale, $"f-{i}.txt");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, old);
        }

        Directory.SetLastWriteTimeUtc(stale, old);

        var recent = Directory.CreateDirectory(Path.Combine(root, "a-recent")).FullName;
        var newest = Path.Combine(recent, "f-newest.txt");
        File.WriteAllText(newest, "x");

        var result = LocalFileScan.NewestFiles(root, "*", maxDepth: 2, maxFiles: 10);

        Assert.Equal(newest, result[0].FullName);
    }

    [Fact]
    public void A_file_held_open_by_a_writer_reports_its_current_length_and_write_time()
    {
        // The directory entry of a file with an open writer is updated lazily; the scan must not trust it.
        var root = TempDirectory();
        var path = Path.Combine(root, "open.jsonl");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("hello"u8);
        writer.Flush(true);
        Thread.Sleep(1500);
        writer.Write(new byte[5000]);
        writer.Flush(true);

        var result = LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 1, maxFiles: 5);

        Assert.Equal(5005, result.Single().Length);
        Assert.True(result.Single().LastWriteTimeUtc >= DateTime.UtcNow.AddSeconds(-1));
    }

    private static void BuildDeepTree(string root, int levels, int filesPerLevel)
    {
        var current = root;
        for (var level = 0; level < levels; level++)
        {
            for (var i = 0; i < filesPerLevel; i++)
                File.WriteAllText(Path.Combine(current, $"f{level}-{i}.txt"), "x");
            current = Directory.CreateDirectory(Path.Combine(current, $"level{level}")).FullName;
        }
    }

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-scan");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }
}
