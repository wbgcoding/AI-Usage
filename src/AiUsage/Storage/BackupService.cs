using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiUsage.Stats;

namespace AiUsage.Storage;

/// <summary>Why a backup file was not accepted. Every case shows the user the same message; the
/// distinction is for the log and the tests.</summary>
public enum BackupRefusal
{
    None,

    /// <summary>Not a zip, a zip with unknown or unsafe content, a damaged file, or a manifest that
    /// does not parse.</summary>
    NotABackup,

    /// <summary>Written by a newer version of the app than this one.</summary>
    NewerVersion,
}

/// <summary>The identity card inside a backup (<c>manifest.json</c>).</summary>
public sealed record BackupManifest(int Format, string AppVersion, string MachineId, string MachineName, DateTimeOffset Created);

/// <summary>The result of looking inside a backup without changing anything.</summary>
public sealed record BackupInspection(BackupRefusal Refusal, BackupManifest? Manifest)
{
    public bool IsAccepted => Refusal == BackupRefusal.None;
}

/// <summary>What <see cref="BackupService.ApplyPendingRestore"/> did.</summary>
public enum RestoreOutcome
{
    /// <summary>No restore was waiting.</summary>
    NoPending,

    /// <summary>The backup is in place; the previous files sit in a <c>before-restore-*</c> folder.</summary>
    Restored,

    /// <summary>The backup was not accepted, or the restore failed and was rolled back: the data is as it was.</summary>
    Unchanged,
}

/// <summary>
/// One zip with everything in the data folder (settings, notification state, quota history, project
/// colours and the token index) and the restore of it. The web sign-in profiles live outside the data
/// folder and are never part of it. Reading a zip trusts nothing in it: only the file names this app
/// itself writes are accepted (so a path outside the target, a drive letter, a stream suffix, a
/// reserved device name or a nested folder can never match), entry count and sizes are capped by the
/// declared and by the really decompressed bytes, links are refused, and every entry's checksum is
/// verified. A restore never deletes the old state: it is moved aside first and put back if the swap
/// fails.
/// </summary>
public static class BackupService
{
    public const int FormatVersion = 1;

    public const string ManifestName = "manifest.json";

    /// <summary>Written next to the data by "restore"; the next start applies it before anything
    /// opens the files (see <see cref="ApplyPendingRestore"/>).</summary>
    public const string PendingFileName = "restore-pending.txt";

    private const string StagingFolderName = "restore-staging";
    private const string SetAsidePrefix = "before-restore-";

    /// <summary>The oldest token index schema a backup may carry: the one the backup feature itself shipped with.</summary>
    internal const int MinimumStatsSchema = 7;

    internal const int MaxEntries = 256;
    internal const long MaxTotalBytes = 3L * 1024 * 1024 * 1024;
    internal const long MaxStatsBytes = 2L * 1024 * 1024 * 1024;
    internal const long MaxHistoryBytes = 256L * 1024 * 1024;
    internal const long MaxSmallFileBytes = 4L * 1024 * 1024;
    internal const long MaxManifestBytes = 64 * 1024;

    /// <summary>A big entry that shrinks further than this is a decompression bomb, not a database.</summary>
    internal const long MaxCompressionRatio = 200;
    private const long RatioCheckFloorBytes = 16L * 1024 * 1024;

    /// <summary>A restore request older than this is dropped instead of applied, so one left behind
    /// by a start that never happened cannot surprise a much later start.</summary>
    internal static readonly TimeSpan PendingMaxAge = TimeSpan.FromHours(1);

    private const string SettingsName = "settings.json";
    private const string NotificationsName = "notifications.json";
    private const string ProjectColorsName = "project-colors.json";
    private const string StatsName = "stats.db";

    private static readonly Regex HistoryName = new(@"^history-[A-Za-z0-9_-]{1,100}\.jsonl$", RegexOptions.CultureInvariant);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>True for the exact file names a backup may hold besides the manifest.</summary>
    internal static bool IsDataFileName(string name) =>
        name is SettingsName or NotificationsName or ProjectColorsName or StatsName || HistoryName.IsMatch(name);

    private static long MaxBytesFor(string name) => name switch
    {
        ManifestName => MaxManifestBytes,
        StatsName => MaxStatsBytes,
        SettingsName or NotificationsName or ProjectColorsName => MaxSmallFileBytes,
        _ => MaxHistoryBytes,
    };

    /// <summary>Canonical lower-case GUID text, or false when the text is not a GUID (or is the empty one).</summary>
    internal static bool TryNormalizeMachineId(string? text, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64 || !Guid.TryParse(text, out var guid) || guid == Guid.Empty)
            return false;

        id = guid.ToString("D");
        return true;
    }

    /// <summary>A PC name from a file we did not write: control characters out, cut short, never empty.</summary>
    internal static string CleanMachineName(string? name, string machineId)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > 64)
            cleaned = cleaned[..64].TrimEnd();
        return cleaned.Length > 0 ? cleaned : machineId[..Math.Min(8, machineId.Length)];
    }

    /// <summary>The files of the data folder a backup carries (and a restore replaces).</summary>
    private static List<string> DataFilesIn(string dataDirectory)
    {
        var found = new List<string>();
        if (!Directory.Exists(dataDirectory))
            return found;

        foreach (var path in Directory.EnumerateFiles(dataDirectory))
        {
            var name = Path.GetFileName(path);
            if (!IsDataFileName(name))
                continue;
            // A link planted under one of these names would read or move something else entirely.
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                continue;
            found.Add(path);
        }

        return found;
    }

    /// <summary>Everything a restore moves aside: the data files plus what only exists next to them.</summary>
    private static List<string> SetAsideCandidates(string dataDirectory)
    {
        var found = DataFilesIn(dataDirectory);
        foreach (var extra in new[] { SettingsName + ".bak", StatsName + "-wal", StatsName + "-shm", StatsName + "-journal" })
        {
            var path = Path.Combine(dataDirectory, extra);
            if (File.Exists(path))
                found.Add(path);
        }

        return found;
    }

    // ---- create ----------------------------------------------------------------------------

    /// <summary>
    /// Writes the backup zip. The file appears under its final name only once complete (a temporary
    /// file next to it, then a rename), so a failure never leaves a half-written backup under the real
    /// name or damages an older one. The token index goes in through SQLite's own backup, never as a
    /// raw copy of a file that may be mid-write.
    /// </summary>
    public static BackupManifest Create(string dataDirectory, string zipPath, string appVersion, string machineId,
        string machineName, DateTimeOffset now, string? scratchDirectory = null)
    {
        var full = Path.GetFullPath(zipPath);
        var folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        var temp = $"{full}.{Guid.NewGuid():N}.tmp";
        var scratch = Path.Combine(scratchDirectory ?? Path.GetTempPath(), $"AI-Usage-backup-{Guid.NewGuid():N}");
        var manifest = new BackupManifest(FormatVersion, appVersion, machineId, machineName, now);

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteText(zip, ManifestName, JsonSerializer.Serialize(new
                {
                    format = manifest.Format,
                    appVersion = manifest.AppVersion,
                    machineId = manifest.MachineId,
                    machineName = manifest.MachineName,
                    created = manifest.Created,
                }, SettingsStore.JsonOptions));

                foreach (var path in DataFilesIn(dataDirectory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(path);
                    if (name == StatsName)
                        continue;
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var target = entry.Open();
                    source.CopyTo(target);
                }

                var statsPath = Path.Combine(dataDirectory, StatsName);
                if (File.Exists(statsPath) && (File.GetAttributes(statsPath) & FileAttributes.ReparsePoint) == 0)
                {
                    Directory.CreateDirectory(scratch);
                    var snapshot = Path.Combine(scratch, StatsName);
                    StatsStore.SnapshotIndex(statsPath, snapshot);
                    using var source = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var entry = zip.CreateEntry(StatsName, CompressionLevel.Optimal);
                    using var target = entry.Open();
                    source.CopyTo(target);
                }
            }

            File.Move(temp, full, overwrite: true);
            return manifest;
        }
        finally
        {
            TryDeleteFile(temp);
            TryDeleteDirectory(scratch);
        }
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var target = entry.Open();
        var bytes = Utf8NoBom.GetBytes(text);
        target.Write(bytes, 0, bytes.Length);
    }

    // ---- reading a zip we did not write ------------------------------------------------------

    /// <summary>Thrown inside this class when a zip must be refused.</summary>
    private sealed class RefusedException(BackupRefusal refusal) : Exception(refusal.ToString())
    {
        public BackupRefusal Refusal { get; } = refusal;
    }

    /// <summary>A zip opened and checked entry by entry: nothing has been extracted yet.</summary>
    internal sealed class OpenedBackup : IDisposable
    {
        private readonly FileStream _stream;
        private readonly ZipArchive _zip;
        private readonly Dictionary<string, ZipArchiveEntry> _entries;

        private OpenedBackup(FileStream stream, ZipArchive zip, Dictionary<string, ZipArchiveEntry> entries, BackupManifest manifest)
        {
            _stream = stream;
            _zip = zip;
            _entries = entries;
            Manifest = manifest;
        }

        public BackupManifest Manifest { get; }

        /// <summary>The data entries (manifest excluded) by name.</summary>
        public IReadOnlyCollection<string> DataNames => _entries.Keys.Where(n => n != ManifestName).ToList();

        public bool Has(string name) => _entries.ContainsKey(name);

        public static OpenedBackup Open(string zipPath)
        {
            FileStream? stream = null;
            ZipArchive? zip = null;
            try
            {
                stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                zip = new ZipArchive(stream, ZipArchiveMode.Read);
                var entries = Validate(zip);
                var manifest = ReadManifest(entries[ManifestName]);
                return new OpenedBackup(stream, zip, entries, manifest);
            }
            catch
            {
                zip?.Dispose();
                stream?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _zip.Dispose();
            _stream.Dispose();
        }

        /// <summary>The whole entry as text, bounded by the entry's own cap.</summary>
        public string ReadText(string name)
        {
            using var memory = new MemoryStream();
            long total = 0;
            CopyChecked(_entries[name], memory, name, ref total);
            return Utf8NoBom.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        }

        /// <summary>Reads the whole entry and discards it: the really decompressed size and the stored
        /// checksum are checked like in <see cref="Extract"/>, without writing anything.</summary>
        public void Verify(string name, ref long totalRead) => CopyChecked(_entries[name], Stream.Null, name, ref totalRead);

        /// <summary>Writes the entry to a new file (never over an existing one), counting the really
        /// decompressed bytes against the caps and checking the stored checksum.</summary>
        public void Extract(string name, string destinationPath, ref long totalWritten)
        {
            using var target = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyChecked(_entries[name], target, name, ref totalWritten);
        }

        private static void CopyChecked(ZipArchiveEntry entry, Stream target, string name, ref long totalWritten)
        {
            var limit = MaxBytesFor(name);
            long written = 0;
            uint crc = 0xFFFFFFFF;
            var buffer = new byte[81920];
            try
            {
                using var source = entry.Open();
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    totalWritten += read;
                    if (written > limit || totalWritten > MaxTotalBytes)
                        throw new RefusedException(BackupRefusal.NotABackup);
                    crc = Crc32Update(crc, buffer, read);
                    target.Write(buffer, 0, read);
                }
            }
            catch (InvalidDataException)
            {
                throw new RefusedException(BackupRefusal.NotABackup);
            }

            if (~crc != entry.Crc32 || written != entry.Length)
                throw new RefusedException(BackupRefusal.NotABackup);
        }
    }

    /// <summary>Checks every entry's name, kind and declared sizes; returns the accepted entries by name.</summary>
    private static Dictionary<string, ZipArchiveEntry> Validate(ZipArchive zip)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var seenIgnoringCase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count == 0 || zip.Entries.Count > MaxEntries)
            throw new RefusedException(BackupRefusal.NotABackup);

        long declaredTotal = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            // Exact names only. A path outside the target (.., rooted, drive letter), a nested
            // folder, a stream suffix (name:stream), a reserved device name or anything else this
            // app never writes cannot equal one of these.
            if (name != ManifestName && !IsDataFileName(name))
                throw new RefusedException(BackupRefusal.NotABackup);
            if (!seenIgnoringCase.Add(name))
                throw new RefusedException(BackupRefusal.NotABackup);

            // A link or folder entry: Unix type bits in the high half, DOS attributes in the low half.
            var attributes = (uint)entry.ExternalAttributes;
            var unixType = (attributes >> 16) & 0xF000;
            if (unixType is 0xA000 or 0x4000 || (attributes & 0x10) != 0 || (attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new RefusedException(BackupRefusal.NotABackup);

            if (entry.Length < 0 || entry.CompressedLength < 0 || entry.Length > MaxBytesFor(name))
                throw new RefusedException(BackupRefusal.NotABackup);
            if (entry.Length > RatioCheckFloorBytes && entry.CompressedLength * MaxCompressionRatio < entry.Length)
                throw new RefusedException(BackupRefusal.NotABackup);

            declaredTotal += entry.Length;
            if (declaredTotal > MaxTotalBytes)
                throw new RefusedException(BackupRefusal.NotABackup);

            entries[name] = entry;
        }

        if (!entries.ContainsKey(ManifestName) || entries.Count < 2)
            throw new RefusedException(BackupRefusal.NotABackup);

        return entries;
    }

    private static BackupManifest ReadManifest(ZipArchiveEntry entry)
    {
        long total = 0;
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        using (var source = entry.Open())
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxManifestBytes)
                    throw new RefusedException(BackupRefusal.NotABackup);
                memory.Write(buffer, 0, read);
            }
        }

        try
        {
            using var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var formatElement)
                || formatElement.ValueKind != JsonValueKind.Number
                || !formatElement.TryGetInt32(out var format)
                || format < 1)
                throw new RefusedException(BackupRefusal.NotABackup);

            if (format > FormatVersion)
                throw new RefusedException(BackupRefusal.NewerVersion);

            var id = root.TryGetProperty("machineId", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            if (!TryNormalizeMachineId(id, out var machineId))
                throw new RefusedException(BackupRefusal.NotABackup);

            var name = root.TryGetProperty("machineName", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
            var version = root.TryGetProperty("appVersion", out var versionElement) && versionElement.ValueKind == JsonValueKind.String ? versionElement.GetString() : null;
            var created = root.TryGetProperty("created", out var createdElement) && createdElement.ValueKind == JsonValueKind.String && createdElement.TryGetDateTimeOffset(out var parsed) ? parsed : default;
            return new BackupManifest(format, (version ?? "")[..Math.Min((version ?? "").Length, 32)], machineId, CleanMachineName(name, machineId), created);
        }
        catch (JsonException)
        {
            throw new RefusedException(BackupRefusal.NotABackup);
        }
    }

    /// <summary>
    /// Looks inside a backup and changes nothing: the zip structure, the manifest, the settings file
    /// against the same gate a normal load uses, and the token index (a copy is read from a scratch
    /// folder that is removed again). Anything unreadable, foreign, damaged or from a newer version is
    /// refused here, before the user is asked to confirm a restore.
    /// </summary>
    public static BackupInspection Inspect(string zipPath, string? scratchDirectory = null)
    {
        var scratch = Path.Combine(scratchDirectory ?? Path.GetTempPath(), $"AI-Usage-backup-{Guid.NewGuid():N}");
        try
        {
            using var backup = OpenedBackup.Open(zipPath);
            CheckContent(backup, scratch);
            return new BackupInspection(BackupRefusal.None, backup.Manifest);
        }
        catch (RefusedException ex)
        {
            return new BackupInspection(ex.Refusal, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return new BackupInspection(BackupRefusal.NotABackup, null);
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    /// <summary>The checks that need the entries' content: the settings file and the token index.</summary>
    private static void CheckContent(OpenedBackup backup, string scratch)
    {
        if (backup.Has(SettingsName))
            CheckSettingsText(backup.ReadText(SettingsName));

        // Every other entry is read through once, so a damaged one is found now and not half way
        // through a restore.
        long total = 0;
        foreach (var name in backup.DataNames.Where(n => n is not (SettingsName or StatsName)))
            backup.Verify(name, ref total);

        if (!backup.Has(StatsName))
            return;

        Directory.CreateDirectory(scratch);
        var extracted = Path.Combine(scratch, StatsName);
        backup.Extract(StatsName, extracted, ref total);
        CheckStatsSchema(extracted);
    }

    /// <summary>The same gate a normal load applies to the settings file.</summary>
    private static void CheckSettingsText(string text)
    {
        var (settings, refusal) = SettingsStore.Validate(text);
        if (refusal == SettingsStore.ImportRefusal.NewerVersion)
            throw new RefusedException(BackupRefusal.NewerVersion);
        if (settings is null)
            throw new RefusedException(BackupRefusal.NotABackup);
    }

    private static void CheckStatsSchema(string databasePath)
    {
        var version = StatsStore.TryReadSchemaVersion(databasePath);
        if (version is null || version < MinimumStatsSchema)
            throw new RefusedException(BackupRefusal.NotABackup);
        if (version > StatsStore.SchemaVersion)
            throw new RefusedException(BackupRefusal.NewerVersion);
    }

    /// <summary>A backup's token index copied to a scratch file, for importing another PC's history.
    /// Disposing removes the scratch copy.</summary>
    public sealed class ImportSource : IDisposable
    {
        private readonly string _scratch;

        internal ImportSource(BackupManifest manifest, string statsDatabasePath, string scratch)
        {
            Manifest = manifest;
            StatsDatabasePath = statsDatabasePath;
            _scratch = scratch;
        }

        public BackupManifest Manifest { get; }

        public string StatsDatabasePath { get; }

        public void Dispose() => TryDeleteDirectory(_scratch);
    }

    /// <summary>Opens a backup for the history import. Null when it is not accepted or carries no
    /// token index; <paramref name="refusal"/> then says why.</summary>
    public static ImportSource? OpenForImport(string zipPath, out BackupRefusal refusal, string? scratchDirectory = null)
    {
        var scratch = Path.Combine(scratchDirectory ?? Path.GetTempPath(), $"AI-Usage-backup-{Guid.NewGuid():N}");
        try
        {
            using var backup = OpenedBackup.Open(zipPath);
            if (!backup.Has(StatsName))
            {
                refusal = BackupRefusal.NotABackup;
                TryDeleteDirectory(scratch);
                return null;
            }

            CheckContent(backup, Path.Combine(scratch, "check"));
            var path = Path.Combine(scratch, StatsName);
            long total = 0;
            backup.Extract(StatsName, path, ref total);
            refusal = BackupRefusal.None;
            return new ImportSource(backup.Manifest, path, scratch);
        }
        catch (RefusedException ex)
        {
            refusal = ex.Refusal;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            refusal = BackupRefusal.NotABackup;
        }

        TryDeleteDirectory(scratch);
        return null;
    }

    // ---- restore ----------------------------------------------------------------------------

    /// <summary>Asks the next start to restore <paramref name="zipPath"/>. Nothing in the data folder
    /// is touched now: the running process still holds those files.</summary>
    public static void SchedulePendingRestore(string dataDirectory, string zipPath)
    {
        var pending = Path.Combine(dataDirectory, PendingFileName);
        var temp = $"{pending}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(temp, Utf8NoBom.GetBytes(Path.GetFullPath(zipPath)));
        File.Move(temp, pending, overwrite: true);
    }

    /// <summary>Takes a scheduled restore back (the restart did not happen).</summary>
    public static void CancelPendingRestore(string dataDirectory) => TryDeleteFile(Path.Combine(dataDirectory, PendingFileName));

    /// <summary>
    /// Run once at start, before anything opens the data files. Order: read the request, check and
    /// extract the whole zip into a staging folder (any failure ends here with the data untouched),
    /// move the current files into <c>before-restore-*</c>, move the staged files in, drop the request.
    /// A failure in the swap puts the moved files back. Nothing the old state held is ever deleted; a
    /// crash in the middle leaves every old file in a before-restore folder.
    /// </summary>
    public static RestoreOutcome ApplyPendingRestore(string dataDirectory, DateTime now, Action<string> log)
    {
        var pending = Path.Combine(dataDirectory, PendingFileName);
        if (!File.Exists(pending))
            return RestoreOutcome.NoPending;

        var zipPath = ReadPending(pending);
        if (zipPath is null)
        {
            TryDeleteFile(pending);
            log("Restore: the request was invalid or too old and was dropped.");
            return RestoreOutcome.Unchanged;
        }

        var staging = Path.Combine(dataDirectory, StagingFolderName);
        try
        {
            DeleteStaging(staging);
            Directory.CreateDirectory(staging);
            string[] stagedNames;
            using (var backup = OpenedBackup.Open(zipPath))
            {
                long total = 0;
                foreach (var name in backup.DataNames)
                    backup.Extract(name, Path.Combine(staging, name), ref total);
                stagedNames = [.. backup.DataNames];
            }

            // The staged copies are what gets installed, so they are what gets checked.
            if (stagedNames.Contains(SettingsName))
                CheckSettingsText(File.ReadAllText(Path.Combine(staging, SettingsName)));
            if (stagedNames.Contains(StatsName))
                CheckStatsSchema(Path.Combine(staging, StatsName));

            KeepOwnMachineId(dataDirectory, staging);
            return Swap(dataDirectory, staging, stagedNames, now, log);
        }
        catch (RefusedException ex)
        {
            log($"Restore: the backup was refused ({ex.Refusal}); nothing was changed.");
            return RestoreOutcome.Unchanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            log($"Restore: the backup could not be read ({ex.GetType().Name}); nothing was changed.");
            return RestoreOutcome.Unchanged;
        }
        finally
        {
            DeleteStaging(staging);
            // Dropped last: a crash before this line leaves the request, and the next start redoes
            // the whole restore from the zip (any file already moved stays in its before-restore folder).
            TryDeleteFile(pending);
        }
    }

    private static string? ReadPending(string pending)
    {
        try
        {
            if (new FileInfo(pending).Length > 4096 || DateTime.UtcNow - File.GetLastWriteTimeUtc(pending) > PendingMaxAge)
                return null;

            var text = File.ReadAllText(pending).Trim();
            if (text.Length == 0 || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(text) || !File.Exists(text))
                return null;

            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The backup may come from another PC; this PC keeps its own machine id so its imported
    /// histories and "same PC" checks stay right. Only rewritten when the two differ.</summary>
    private static void KeepOwnMachineId(string dataDirectory, string staging)
    {
        var stagedSettings = Path.Combine(staging, SettingsName);
        var currentSettings = Path.Combine(dataDirectory, SettingsName);
        if (!File.Exists(stagedSettings) || !File.Exists(currentSettings))
            return;

        try
        {
            var current = JsonNode.Parse(File.ReadAllText(currentSettings)) as JsonObject;
            if (!TryNormalizeMachineId(current?["machineId"]?.GetValue<string>(), out var ownId))
                return;

            var staged = JsonNode.Parse(File.ReadAllText(stagedSettings)) as JsonObject;
            if (staged is null)
                return;
            if (TryNormalizeMachineId(staged["machineId"]?.GetValue<string>(), out var stagedId) && stagedId == ownId)
                return;

            staged["machineId"] = ownId;
            File.WriteAllBytes(stagedSettings, Utf8NoBom.GetBytes(staged.ToJsonString(SettingsStore.JsonOptions)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            // Best effort: without it the restored file keeps the id it came with.
        }
    }

    private static RestoreOutcome Swap(string dataDirectory, string staging, string[] stagedNames, DateTime now, Action<string> log)
    {
        var setAside = Path.Combine(dataDirectory, $"{SetAsidePrefix}{now:yyyyMMdd-HHmmss}");
        for (var suffix = 2; Directory.Exists(setAside); suffix++)
            setAside = Path.Combine(dataDirectory, $"{SetAsidePrefix}{now:yyyyMMdd-HHmmss}-{suffix}");
        Directory.CreateDirectory(setAside);

        var moved = new List<(string From, string To)>();
        var placed = new List<string>();
        try
        {
            foreach (var path in SetAsideCandidates(dataDirectory))
            {
                var target = Path.Combine(setAside, Path.GetFileName(path));
                File.Move(path, target);
                moved.Add((path, target));
            }

            foreach (var name in stagedNames)
            {
                var target = Path.Combine(dataDirectory, name);
                File.Move(Path.Combine(staging, name), target);
                placed.Add(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Restore: the swap failed ({ex.GetType().Name}); putting the previous files back.");
            foreach (var path in placed)
                TryDeleteFile(path);
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    File.Move(moved[i].To, moved[i].From);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    log($"Restore: {Path.GetFileName(moved[i].From)} could not be put back; it stays in {setAside}.");
                }
            }

            return RestoreOutcome.Unchanged;
        }

        log($"Restore: done; the previous files are in {Path.GetFileName(setAside)}.");
        return RestoreOutcome.Restored;
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static void DeleteStaging(string staging)
    {
        try
        {
            if (!Directory.Exists(staging))
                return;
            // A link planted under this name is removed as a link, never followed.
            var recursive = (File.GetAttributes(staging) & FileAttributes.ReparsePoint) == 0;
            Directory.Delete(staging, recursive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc32Update(uint crc, byte[] buffer, int count)
    {
        for (var i = 0; i < count; i++)
            crc = Crc32Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
