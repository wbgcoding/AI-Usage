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
        CreateFiles(root, 20100, "file", ".txt", DateTime.UtcNow);

        var touched = 0;
        LocalFileScan.NewestFiles(root, "*", maxDepth: 0, maxFiles: 40, isExcluded: _ =>
        {
            touched++;
            return true;
        });

        Assert.Equal(20000, touched);
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
        CreateFiles(stale, 20100, "f", ".txt", old);

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
        // Measured against the second write itself, not against "now": a slow scan on a loaded
        // machine must not fail the check. The tolerance stays well below the 1.5 s gap.
        var secondWriteAt = DateTime.UtcNow;
        writer.Write(new byte[5000]);
        writer.Flush(true);

        var result = LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 1, maxFiles: 5);

        Assert.Equal(5005, result.Single().Length);
        Assert.True(result.Single().LastWriteTimeUtc >= secondWriteAt.AddMilliseconds(-500));
    }

    [Fact]
    public void A_long_running_session_in_an_old_folder_is_found_past_the_ceiling()
    {
        // Appending to a file leaves its folder's stamp alone, so the folder of a live session looks
        // old while newer-stamped folders hold more files than the whole walk budget.
        var root = TempDirectory();
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 5; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, $"new-{i}")).FullName;
            CreateFiles(folder, 4020, "s", ".jsonl", hourAgo);
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
        }

        var oldFolder = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
        var live = Path.Combine(oldFolder, "live.jsonl");
        File.WriteAllText(live, "x");
        Directory.SetLastWriteTimeUtc(oldFolder, DateTime.UtcNow.AddDays(-30));

        var result = LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 2, maxFiles: 10);

        Assert.Equal(live, result[0].FullName);
    }

    [Fact]
    public void Subfolders_are_ranked_by_the_newest_matching_file_directly_inside()
    {
        var root = TempDirectory();
        var now = DateTime.UtcNow;
        var fresh = MakeFolderWithFile(root, "a-stale-stamp", "fresh.jsonl", fileAt: now, folderAt: now.AddDays(-1));
        var stale = MakeFolderWithFile(root, "b-recent-stamp", "stale.jsonl", fileAt: now.AddDays(-2), folderAt: now.AddHours(-1));
        var unrelated = MakeFolderWithFile(root, "c-other", "notes.txt", fileAt: now, folderAt: now.AddDays(-3));

        var visited = new List<string>();
        LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 2, maxFiles: 10, isExcluded: path =>
        {
            visited.Add(path);
            return false;
        });

        Assert.Equal([fresh, stale], visited);
        Assert.DoesNotContain(unrelated, visited);
    }

    [Fact]
    public void A_file_found_by_the_last_scan_is_returned_even_when_the_walk_no_longer_reaches_it()
    {
        var root = TempDirectory();
        var deepFolder = Directory.CreateDirectory(Path.Combine(root, "old", "subagents")).FullName;
        var live = Path.Combine(deepFolder, "live.jsonl");
        File.WriteAllText(live, "x");
        Assert.Equal(live, LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 3, maxFiles: 5)[0].FullName);

        // The live file is written again (newest of all) but its folders keep their old stamps and hold
        // no direct file that ranks them up, while newer-stamped folders fill the whole walk budget.
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 5; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, $"new-{i}")).FullName;
            CreateFiles(folder, 4020, "s", ".jsonl", hourAgo);
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
        }

        File.AppendAllText(live, "more");
        Directory.SetLastWriteTimeUtc(deepFolder, DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "old"), DateTime.UtcNow.AddDays(-30));

        // Same files under another pattern spelling = no memory yet: the walk alone misses the live file.
        var withoutMemory = LocalFileScan.NewestFiles(root, "?*.jsonl", maxDepth: 3, maxFiles: 5);
        Assert.DoesNotContain(live, withoutMemory.Select(f => f.FullName));

        var result = LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 3, maxFiles: 5);

        Assert.Equal(live, result[0].FullName);
    }

    [Fact]
    public void A_remembered_file_that_was_deleted_is_skipped()
    {
        var root = TempDirectory();
        var gone = Path.Combine(root, "gone.jsonl");
        var stays = Path.Combine(root, "stays.jsonl");
        File.WriteAllText(gone, "x");
        File.WriteAllText(stays, "x");
        Assert.Equal(2, LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 1, maxFiles: 5).Count);

        File.Delete(gone);
        var result = LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 1, maxFiles: 5);

        Assert.Equal(stays, result.Single().FullName);
    }

    [Fact]
    public void A_remembered_file_the_exclusion_predicate_rejects_is_left_out()
    {
        var root = TempDirectory();
        var first = Path.Combine(root, "first.jsonl");
        File.WriteAllText(first, "x");
        Assert.Single(LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 1, maxFiles: 5));

        var result = LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 1, maxFiles: 5, isExcluded: path => path == first);

        Assert.Empty(result);
    }

    [Fact]
    public void A_file_the_memory_and_the_walk_both_meet_is_returned_once()
    {
        var root = TempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        for (var i = 0; i < 6; i++)
            File.WriteAllText(Path.Combine(root, i % 2 == 0 ? "sub" : "", $"s{i}.jsonl"), "x");

        for (var scan = 0; scan < 3; scan++)
        {
            var paths = LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 2, maxFiles: 50).Select(f => f.FullName).ToList();

            Assert.Equal(6, paths.Count);
            Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Parallel_scans_of_the_same_root_do_not_interfere()
    {
        var root = TempDirectory();
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(root, $"s{i}.jsonl"), "x");

        var counts = new int[16];
        Parallel.For(0, counts.Length, n => counts[n] = LocalFileScan.NewestFiles(root, "*.jsonl", maxDepth: 1, maxFiles: 50).Count);

        Assert.All(counts, count => Assert.Equal(20, count));
    }

    private static string MakeFolderWithFile(string root, string folderName, string fileName, DateTime fileAt, DateTime folderAt)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, folderName)).FullName;
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, fileAt);
        Directory.SetLastWriteTimeUtc(folder, folderAt);
        return path;
    }

    private static void CreateFiles(string folder, int count, string prefix, string extension, DateTime writeTime) =>
        Parallel.For(0, count, i =>
        {
            var path = Path.Combine(folder, $"{prefix}-{i}{extension}");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, writeTime);
        });

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
