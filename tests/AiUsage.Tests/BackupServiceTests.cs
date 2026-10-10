using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AiUsage.Models;
using AiUsage.Stats;
using AiUsage.Storage;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>The backup zip: what goes in, what a restore accepts, and what it never does. Every path
/// lives under the shared test root; nothing here touches the real data folder.</summary>
public sealed class BackupServiceTests : IDisposable
{
    private const string IdA = "11111111-1111-1111-1111-111111111111";
    private const string IdB = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LocalNow = new(2026, 10, 10, 12, 0, 0);

    private readonly List<DisposableTestDirectory> _directories = [];

    private string Temp()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-backup");
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

    private static string SettingsJson(string machineId, int refreshSeconds = 60) =>
        JsonSerializer.Serialize(new AppSettings { MachineId = machineId, RefreshSeconds = refreshSeconds }, SettingsStore.JsonOptions);

    /// <summary>A data folder with every kind of file the app keeps, plus decoys a backup must never take.</summary>
    private static void Seed(string directory, string machineId, long tokens, int refreshSeconds = 60)
    {
        File.WriteAllText(Path.Combine(directory, "settings.json"), SettingsJson(machineId, refreshSeconds));
        File.WriteAllText(Path.Combine(directory, "notifications.json"), $"{{\"seed\":{tokens}}}");
        File.WriteAllText(Path.Combine(directory, "history-claude.jsonl"), $"{{\"p\":{tokens}}}\n{{\"p\":{tokens + 1}}}\n");
        File.WriteAllText(Path.Combine(directory, "history-claude-2.jsonl"), $"{{\"q\":{tokens}}}\n");
        File.WriteAllText(Path.Combine(directory, "project-colors.json"), $"{{\"proj\":\"#{tokens % 1000:000000}\"}}");
        var store = new StatsStore(directory);
        store.AddDelta([new StatsRecord("claude", new DateOnly(2026, 5, 1), "modelA", "projA", tokens, 2, 3, 4, Hour: 1)]);
        store.ReplaceSessionsAndMarkBackfilled([new StatsSessionDelta("claude", "s1", "projA", Now, Now, tokens, 2, 3, 4, 0, new Dictionary<string, long> { ["modelA"] = tokens })]);
        IndexPools.Release(directory);

        // Decoys: none of these may ever be in a backup.
        File.WriteAllText(Path.Combine(directory, "credentials.json"), "secret");
        File.WriteAllText(Path.Combine(directory, "other-file.txt"), "C:\\elsewhere");
        File.WriteAllText(Path.Combine(directory, "settings.json.bak"), "old");
        File.WriteAllText(Path.Combine(directory, "history-claude.jsonl.123.tmp"), "tmp");
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        File.WriteAllText(Path.Combine(directory, "logs", "app.log"), "log");
        Directory.CreateDirectory(Path.Combine(directory, "webview", "claude"));
        File.WriteAllText(Path.Combine(directory, "webview", "claude", "Cookies"), "cookie");
    }

    private static List<string> EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private static string ManifestJson(int format = 1, string machineId = IdA, string machineName = "PC-A") =>
        JsonSerializer.Serialize(new { format, appVersion = "1.0.0", machineId, machineName, created = Now }, SettingsStore.JsonOptions);

    /// <summary>A hand-built zip: the manifest, a valid settings file, and whatever else the test adds.</summary>
    private static string MakeZip(string folder, Action<ZipArchive> add, bool withManifest = true, bool withSettings = true, string? manifest = null)
    {
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}.zip");
        using var stream = new FileStream(path, FileMode.CreateNew);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            if (withManifest)
                AddText(zip, "manifest.json", manifest ?? ManifestJson());
            if (withSettings)
                AddText(zip, "settings.json", SettingsJson(IdA));
            add(zip);
        }

        return path;
    }

    private static ZipArchiveEntry AddText(ZipArchive zip, string name, string text, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var target = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        target.Write(bytes, 0, bytes.Length);
        return entry;
    }

    private static Dictionary<string, byte[]> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(directory, p), File.ReadAllBytes);

    private static List<StatsRecord> Rows(string directory)
    {
        var rows = new StatsStore(directory).LoadAll().OrderBy(r => r.Machine).ThenBy(r => r.Day).ThenBy(r => r.Hour).ToList();
        IndexPools.Release(directory);
        return rows;
    }

    // ---- create ----------------------------------------------------------------------------

    [Fact]
    public void Create_writes_the_manifest_and_only_the_known_files()
    {
        var data = Temp();
        Seed(data, IdA, 100);
        var zip = Path.Combine(Temp(), "ai-usage-backup-2026-10-10.zip");

        var manifest = BackupService.Create(data, zip, "1.2.3", IdA, "PC-A", Now, Temp());

        Assert.Equal(
            ["history-claude-2.jsonl", "history-claude.jsonl", "manifest.json", "notifications.json", "project-colors.json", "settings.json", "stats.db"],
            EntryNames(zip));
        Assert.Equal(new BackupManifest(1, "1.2.3", IdA, "PC-A", Now), manifest);

        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        using var json = JsonDocument.Parse(reader.ReadToEnd());
        Assert.Equal(1, json.RootElement.GetProperty("format").GetInt32());
        Assert.Equal("1.2.3", json.RootElement.GetProperty("appVersion").GetString());
        Assert.Equal(IdA, json.RootElement.GetProperty("machineId").GetString());
        Assert.Equal("PC-A", json.RootElement.GetProperty("machineName").GetString());
    }

    [Fact]
    public void A_backup_never_carries_sign_in_profiles_credentials_or_anything_outside_the_known_names()
    {
        var data = Temp();
        Seed(data, IdA, 100);
        File.WriteAllText(Path.Combine(data, "restore-pending.txt"), "x");
        File.WriteAllText(Path.Combine(data, "stats.v5.bak"), "x");
        var zip = Path.Combine(Temp(), "b.zip");

        BackupService.Create(data, zip, "1", IdA, "PC", Now, Temp());

        var names = EntryNames(zip);
        Assert.DoesNotContain(names, n => n.Contains("webview", StringComparison.OrdinalIgnoreCase) || n.Contains("Cookies") || n.Contains("credentials")
            || n.Contains("logs") || n.Contains(".bak") || n.Contains(".tmp") || n.Contains("pending") || n.Contains('/') || n.Contains('\\'));
        Assert.All(names, n => Assert.True(n == "manifest.json" || BackupService.IsDataFileName(n), n));
    }

    [Fact]
    public void Create_leaves_no_half_written_zip_and_keeps_an_older_one_when_it_fails()
    {
        var data = Temp();
        Seed(data, IdA, 100);
        File.WriteAllBytes(Path.Combine(data, "stats.db"), Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray());
        var folder = Temp();
        var zip = Path.Combine(folder, "keep.zip");
        File.WriteAllText(zip, "older backup");

        Assert.ThrowsAny<Exception>(() => BackupService.Create(data, zip, "1", IdA, "PC", Now, Temp()));

        Assert.Equal("older backup", File.ReadAllText(zip));
        Assert.Equal(["keep.zip"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).ToList());
    }

    [Fact]
    public void Create_replaces_an_existing_backup_only_with_a_complete_one()
    {
        var data = Temp();
        Seed(data, IdA, 100);
        var folder = Temp();
        var zip = Path.Combine(folder, "b.zip");
        File.WriteAllText(zip, "older backup");

        BackupService.Create(data, zip, "1", IdA, "PC", Now, Temp());

        Assert.True(BackupService.Inspect(zip, Temp()).IsAccepted);
        Assert.Equal(["b.zip"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).ToList());
    }

    // ---- round trip ------------------------------------------------------------------------

    [Fact]
    public void A_backup_restores_into_another_folder_with_every_file_and_every_row_equal()
    {
        var source = Temp();
        var target = Temp();
        Seed(source, IdA, 100, refreshSeconds: 120);
        Seed(target, IdA, 5);
        var zip = Path.Combine(Temp(), "b.zip");
        BackupService.Create(source, zip, "1", IdA, "PC-A", Now, Temp());
        var oldTarget = Snapshot(target);

        BackupService.SchedulePendingRestore(target, zip);
        var log = new List<string>();
        var outcome = BackupService.ApplyPendingRestore(target, LocalNow, log.Add);

        Assert.Equal(RestoreOutcome.Restored, outcome);
        foreach (var name in new[] { "settings.json", "notifications.json", "history-claude.jsonl", "history-claude-2.jsonl", "project-colors.json" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, name)), File.ReadAllBytes(Path.Combine(target, name)));
        Assert.Equal(Rows(source), Rows(target));
        var restoredStore = new StatsStore(target);
        Assert.Equal(new StatsStore(source).LoadSessions(), restoredStore.LoadSessions());
        IndexPools.Release(source);
        IndexPools.Release(target);

        Assert.False(File.Exists(Path.Combine(target, "restore-pending.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "restore-staging")));
        // The previous state is set aside, complete, never deleted.
        var setAside = Assert.Single(Directory.EnumerateDirectories(target, "before-restore-*"));
        Assert.Equal("before-restore-20261010-120000", Path.GetFileName(setAside));
        foreach (var name in new[] { "settings.json", "notifications.json", "history-claude.jsonl", "project-colors.json", "stats.db", "settings.json.bak" })
            Assert.Equal(oldTarget[name], File.ReadAllBytes(Path.Combine(setAside, name)));
        // What is not a data file stays where it was.
        Assert.Equal("secret", File.ReadAllText(Path.Combine(target, "credentials.json")));
        Assert.True(File.Exists(Path.Combine(target, "webview", "claude", "Cookies")));
    }

    [Fact]
    public void A_restore_keeps_this_PCs_machine_id_and_takes_everything_else_from_the_backup()
    {
        var source = Temp();
        var target = Temp();
        Seed(source, IdA, 100, refreshSeconds: 120);
        Seed(target, IdB, 5);
        var zip = Path.Combine(Temp(), "b.zip");
        BackupService.Create(source, zip, "1", IdA, "PC-A", Now, Temp());

        BackupService.SchedulePendingRestore(target, zip);
        Assert.Equal(RestoreOutcome.Restored, BackupService.ApplyPendingRestore(target, LocalNow, _ => { }));

        var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(target, "settings.json")), SettingsStore.JsonOptions)!;
        Assert.Equal(IdB, restored.MachineId);
        Assert.Equal(120, restored.RefreshSeconds);
    }

    [Fact]
    public void A_restore_with_nothing_pending_changes_nothing()
    {
        var data = Temp();
        Seed(data, IdA, 5);
        var before = Snapshot(data);

        Assert.Equal(RestoreOutcome.NoPending, BackupService.ApplyPendingRestore(data, LocalNow, _ => { }));

        Assert.Equal(before.Keys.OrderBy(k => k), Snapshot(data).Keys.OrderBy(k => k));
    }

    [Fact]
    public void A_failed_swap_puts_the_previous_files_back()
    {
        var source = Temp();
        var target = Temp();
        Seed(source, IdA, 100);
        Seed(target, IdA, 5);
        var zip = Path.Combine(Temp(), "b.zip");
        BackupService.Create(source, zip, "1", IdA, "PC-A", Now, Temp());
        var before = Snapshot(target);

        BackupService.SchedulePendingRestore(target, zip);
        RestoreOutcome outcome;
        var log = new List<string>();
        // A file another program holds open cannot be moved aside.
        using (new FileStream(Path.Combine(target, "stats.db"), FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = BackupService.ApplyPendingRestore(target, LocalNow, log.Add);

        Assert.Equal(RestoreOutcome.Unchanged, outcome);
        var after = Snapshot(target);
        foreach (var (name, bytes) in before)
            Assert.Equal(bytes, after[name]);
        Assert.Equal(before.Count, after.Count(p => !p.Key.StartsWith("before-restore-", StringComparison.Ordinal)));
        Assert.Empty(Directory.EnumerateFiles(Directory.EnumerateDirectories(target, "before-restore-*").Single()));
        Assert.False(File.Exists(Path.Combine(target, "restore-pending.txt")));
        Assert.Contains(log, line => line.Contains("putting the previous files back"));
    }

    [Fact]
    public void A_restore_request_that_is_stale_relative_or_points_nowhere_is_dropped()
    {
        var data = Temp();
        Seed(data, IdA, 5);
        var source = Temp();
        Seed(source, IdA, 100);
        var zip = Path.Combine(Temp(), "b.zip");
        BackupService.Create(source, zip, "1", IdA, "PC-A", Now, Temp());
        var before = Snapshot(data);
        var pending = Path.Combine(data, "restore-pending.txt");

        File.WriteAllText(pending, zip);
        File.SetLastWriteTimeUtc(pending, DateTime.UtcNow.AddHours(-3));
        Assert.Equal(RestoreOutcome.Unchanged, BackupService.ApplyPendingRestore(data, LocalNow, _ => { }));
        Assert.False(File.Exists(pending));

        File.WriteAllText(pending, "backup.zip");
        Assert.Equal(RestoreOutcome.Unchanged, BackupService.ApplyPendingRestore(data, LocalNow, _ => { }));

        File.WriteAllText(pending, Path.Combine(data, "missing.zip"));
        Assert.Equal(RestoreOutcome.Unchanged, BackupService.ApplyPendingRestore(data, LocalNow, _ => { }));

        Assert.Equal(before.Keys.OrderBy(k => k), Snapshot(data).Keys.OrderBy(k => k));
        Assert.Equal(before["settings.json"], File.ReadAllBytes(Path.Combine(data, "settings.json")));
    }

    // ---- refusing what is not ours ---------------------------------------------------------

    public static TheoryData<string> UnsafeEntryNames() => new()
    {
        "../evil.txt", "..\\evil.txt", "/abs/evil.txt", "\\\\server\\share\\evil.txt", "C:\\evil.txt", "C:/evil.txt", "C:evil.txt",
        "settings.json:evil", "settings.json::$DATA", "stats.db:stream:$DATA", "nested/settings.json", "nested\\settings.json",
        "history-..\\x.jsonl", "history-../x.jsonl", "history-a.b.jsonl", "history-.jsonl", "history-x.jsonl.exe",
        "CON", "NUL", "aux.txt", "COM1.json", "LPT1", "settings.json ", "settings.json.", "Settings.json", "SETTINGS.JSON", "evil.exe", "logs/app.log",
        "", " ", "settings.json/", "folder/",
    };

    [Theory]
    [MemberData(nameof(UnsafeEntryNames))]
    public void An_entry_with_a_name_outside_the_known_list_refuses_the_zip_and_writes_nothing(string entryName)
    {
        var parent = Temp();
        var data = Path.Combine(parent, "data");
        Directory.CreateDirectory(data);
        Seed(data, IdA, 5);
        var before = Snapshot(data);
        var zipFolder = Temp();
        string zip;
        try
        {
            zip = MakeZip(zipFolder, z => AddText(z, entryName, "evil"));
        }
        catch (ArgumentException)
        {
            return; // the zip library itself cannot write that name
        }

        var inspection = BackupService.Inspect(zip, Temp());
        Assert.Equal(BackupRefusal.NotABackup, inspection.Refusal);
        Assert.Null(BackupService.OpenForImport(zip, out var importRefusal, Temp()));
        Assert.Equal(BackupRefusal.NotABackup, importRefusal);

        BackupService.SchedulePendingRestore(data, zip);
        Assert.Equal(RestoreOutcome.Unchanged, BackupService.ApplyPendingRestore(data, LocalNow, _ => { }));

        var after = Snapshot(data);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (name, bytes) in before)
            Assert.Equal(bytes, after[name]);
        // Nothing appeared beside the data folder or anywhere else under the test parent either.
        Assert.Equal(["data"], Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName).ToList());
        Assert.Empty(Directory.EnumerateDirectories(data, "before-restore-*"));
    }

    [Fact]
    public void Two_entries_that_differ_only_in_case_refuse_the_zip()
    {
        var zip = MakeZip(Temp(), z =>
        {
            AddText(z, "history-a.jsonl", "1");
            AddText(z, "history-A.jsonl", "2");
        });

        Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(zip, Temp()).Refusal);
    }

    [Fact]
    public void A_zip_with_too_many_entries_is_refused()
    {
        var zip = MakeZip(Temp(), z =>
        {
            for (var i = 0; i < BackupService.MaxEntries + 1; i++)
                AddText(z, $"history-{i}.jsonl", "x");
        });

        Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(zip, Temp()).Refusal);
    }

    [Fact]
    public void A_big_entry_that_shrinks_beyond_the_ratio_limit_is_refused_as_a_decompression_bomb()
    {
        var zip = MakeZip(Temp(), z =>
        {
            var entry = z.CreateEntry("history-bomb.jsonl", CompressionLevel.SmallestSize);
            using var target = entry.Open();
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 24; i++)
                target.Write(block, 0, block.Length);
        });

        Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(zip, Temp()).Refusal);
    }

    [Fact]
    public void An_entry_above_its_size_cap_is_refused()
    {
        var zip = MakeZip(Temp(), z =>
        {
            var entry = z.CreateEntry("notifications.json", CompressionLevel.Optimal);
            using var target = entry.Open();
            var rng = new Random(1);
            var block = new byte[64 * 1024];
            for (var i = 0; i < 80; i++)
            {
                rng.NextBytes(block);
                target.Write(block, 0, block.Length); // 5 MiB of incompressible data, over the 4 MiB cap
            }
        });

        Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(zip, Temp()).Refusal);
    }

    [Fact]
    public void An_entry_that_lies_about_its_size_is_stopped_by_the_bytes_really_decompressed()
    {
        var folder = Temp();
        var zip = MakeZip(folder, z =>
        {
            AddText(z, "history-liar.jsonl", string.Concat(Enumerable.Repeat("{\"a\":1}\n", 20000)));
        }, withSettings: true);
        var bytes = File.ReadAllBytes(zip);
        PatchDeclaredSize(bytes, "history-liar.jsonl", 10);
        File.WriteAllBytes(zip, bytes);

        Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(zip, Temp()).Refusal);
        var data = Temp();
        Seed(data, IdA, 5);
        var before = Snapshot(data);
        BackupService.SchedulePendingRestore(data, zip);
        Assert.Equal(RestoreOutcome.Unchanged, BackupService.ApplyPendingRestore(data, LocalNow, _ => { }));
        Assert.Equal(before["settings.json"], File.ReadAllBytes(Path.Combine(data, "settings.json")));
    }

    /// <summary>Overwrites the uncompressed size an entry declares, in both its headers.</summary>
    private static void PatchDeclaredSize(byte[] zip, string entryName, uint declared)
    {
        for (var i = 0; i + 46 < zip.Length; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4B || zip[i + 2] != 0x01 || zip[i + 3] != 0x02)
                continue;
            var nameLength = BitConverter.ToUInt16(zip, i + 28);
            if (Encoding.UTF8.GetString(zip, i + 46, nameLength) != entryName)
                continue;

            BitConverter.GetBytes(declared).CopyTo(zip, i + 24);
            var local = (int)BitConverter.ToUInt32(zip, i + 42);
            BitConverter.GetBytes(declared).CopyTo(zip, local + 22);
            return;
        }

        throw new InvalidOperationException("entry not found");
    }

    [Fact]
    public void A_link_entry_is_refused()
    {
        var zip = MakeZip(Temp(), z =>
        {
            var entry = AddText(z, "history-link.jsonl", "C:\\Windows\\win.ini");
            entry.ExternalAttributes = (0xA000 | 0x1ED) << 16; // symbolic link, rwxr-xr-x
        });

        Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(zip, Temp()).Refusal);
    }

    [Fact]
    public void A_damaged_entry_fails_its_checksum_and_the_zip_is_refused()
    {
        var data = Temp();
        Seed(data, IdA, 5);
        var zipFolder = Temp();
        var zip = MakeZip(zipFolder, z => AddText(z, "history-x.jsonl", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", CompressionLevel.NoCompression));
        var bytes = File.ReadAllBytes(zip);
        var at = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
        var index = IndexOf(bytes, at);
        Assert.True(index > 0);
        bytes[index + 5] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);
        var before = Snapshot(data);

        Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(zip, Temp()).Refusal);
        BackupService.SchedulePendingRestore(data, zip);
        Assert.Equal(RestoreOutcome.Unchanged, BackupService.ApplyPendingRestore(data, LocalNow, _ => { }));
        Assert.Equal(before["settings.json"], File.ReadAllBytes(Path.Combine(data, "settings.json")));
        Assert.Empty(Directory.EnumerateDirectories(data, "before-restore-*"));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }

    [Fact]
    public void A_file_that_is_not_a_zip_or_not_a_backup_is_refused()
    {
        var folder = Temp();
        var text = Path.Combine(folder, "notes.zip");
        File.WriteAllText(text, "just some text");
        var empty = Path.Combine(folder, "empty.zip");
        File.WriteAllBytes(empty, []);
        var foreign = MakeZip(folder, z => AddText(z, "readme.txt", "hi"), withManifest: false, withSettings: false);
        var noManifest = MakeZip(folder, z => AddText(z, "notifications.json", "{}"), withManifest: false, withSettings: false);
        var manifestOnly = MakeZip(folder, _ => { }, withSettings: false);

        foreach (var path in new[] { text, empty, foreign, noManifest, manifestOnly, Path.Combine(folder, "missing.zip") })
            Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(path, Temp()).Refusal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"1\",\"machineId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("{\"format\":0,\"machineId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("{\"format\":1,\"machineId\":\"not-a-guid\"}")]
    [InlineData("{\"format\":1,\"machineId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"format\":1}")]
    public void A_manifest_that_does_not_parse_is_refused(string manifest)
    {
        var zip = MakeZip(Temp(), _ => { }, manifest: manifest);

        Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(zip, Temp()).Refusal);
    }

    [Fact]
    public void A_backup_from_a_newer_format_is_refused_as_newer()
    {
        var zip = MakeZip(Temp(), _ => { }, manifest: ManifestJson(format: 2));

        Assert.Equal(BackupRefusal.NewerVersion, BackupService.Inspect(zip, Temp()).Refusal);
    }

    [Fact]
    public void A_backup_with_a_newer_settings_file_is_refused_as_newer()
    {
        var newer = JsonSerializer.Serialize(new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion + 1, MachineId = IdA }, SettingsStore.JsonOptions);
        var zip = MakeZip(Temp(), z => AddText(z, "notifications.json", "{}"), withSettings: false);
        var path = MakeZip(Temp(), z => AddText(z, "settings.json", newer), withSettings: false);

        Assert.Equal(BackupRefusal.NewerVersion, BackupService.Inspect(path, Temp()).Refusal);
        Assert.True(BackupService.Inspect(zip, Temp()).IsAccepted);
    }

    [Fact]
    public void A_backup_with_a_newer_token_index_is_refused_as_newer_and_an_unreadable_one_as_not_a_backup()
    {
        var folder = Temp();
        var newerDb = MakeIndexWithVersion(folder, StatsStore.SchemaVersion + 1);
        var olderDb = MakeIndexWithVersion(folder, 6);
        var garbage = Path.Combine(folder, "garbage.db");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 5000).Select(i => (byte)(i * 13)).ToArray());

        Assert.Equal(BackupRefusal.NewerVersion, BackupService.Inspect(ZipWithStats(folder, newerDb), Temp()).Refusal);
        Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(ZipWithStats(folder, olderDb), Temp()).Refusal);
        Assert.Equal(BackupRefusal.NotABackup, BackupService.Inspect(ZipWithStats(folder, garbage), Temp()).Refusal);
    }

    private static string ZipWithStats(string folder, string databasePath) =>
        MakeZip(folder, z =>
        {
            var entry = z.CreateEntry("stats.db");
            using var target = entry.Open();
            using var source = File.OpenRead(databasePath);
            source.CopyTo(target);
        });

    /// <summary>A real index whose version row is changed, so the schema check has something to read.</summary>
    private string MakeIndexWithVersion(string folder, int version)
    {
        var directory = Path.Combine(folder, $"index-{version}");
        Directory.CreateDirectory(directory);
        new StatsStore(directory).AddDelta([new StatsRecord("claude", new DateOnly(2026, 1, 1), "m", "p", 1, 0, 0, 0)]);
        IndexPools.Release(directory);
        var path = Path.Combine(directory, "stats.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE schema_version SET version = {version}";
            command.ExecuteNonQuery();
        }

        return path;
    }

    [Fact]
    public void A_restore_of_a_refused_backup_leaves_the_current_data_exactly_as_it_was()
    {
        var data = Temp();
        Seed(data, IdA, 5);
        var before = Snapshot(data);
        var zip = MakeZip(Temp(), _ => { }, manifest: ManifestJson(format: 9));

        BackupService.SchedulePendingRestore(data, zip);
        var outcome = BackupService.ApplyPendingRestore(data, LocalNow, _ => { });

        Assert.Equal(RestoreOutcome.Unchanged, outcome);
        var after = Snapshot(data);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (name, bytes) in before)
            Assert.Equal(bytes, after[name]);
    }

    // ---- machine id and the settings around it ---------------------------------------------

    [Fact]
    public void The_first_load_creates_a_machine_id_and_later_loads_keep_it()
    {
        var data = Temp();
        var first = new SettingsStore(data);
        var settings = first.Load();
        Assert.True(first.MachineIdWasCreated);
        Assert.True(BackupService.TryNormalizeMachineId(settings.MachineId, out var id));
        first.SaveNow(settings);

        var second = new SettingsStore(data);
        var again = second.Load();

        Assert.False(second.MachineIdWasCreated);
        Assert.Equal(id, again.MachineId);
    }

    [Fact]
    public void A_hand_edited_machine_id_and_imported_list_are_cleaned_on_load()
    {
        var data = Temp();
        var json = SettingsJson("garbage").Replace("\"importedMachines\": []",
            "\"importedMachines\": [{\"id\":\"" + IdB + "\",\"name\":\"  Pc\\u0007 B \"},{\"id\":\"" + IdB.ToUpperInvariant() + "\",\"name\":\"dup\"},{\"id\":\"x\",\"name\":\"bad\"},null]");
        File.WriteAllText(Path.Combine(data, "settings.json"), json);

        var store = new SettingsStore(data);
        var settings = store.Load();

        Assert.True(store.MachineIdWasCreated);
        var kept = Assert.Single(settings.ImportedMachines);
        Assert.Equal(IdB, kept.Id);
        Assert.Equal("Pc B", kept.Name);
    }

    [Fact]
    public void A_machine_name_from_a_foreign_file_is_cleaned_and_never_empty()
    {
        Assert.Equal("Pc B", BackupService.CleanMachineName(" Pc\u0000 B\n", IdB));
        Assert.Equal("22222222", BackupService.CleanMachineName("  ", IdB));
        Assert.Equal(64, BackupService.CleanMachineName(new string('x', 500), IdB).Length);
    }
}
