using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiUsage.Io;
using AiUsage.Stats;
using Microsoft.Data.Sqlite;

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
    /// by a start that never happened cannot surprise a much later start. A day leaves room for a
    /// restart that came late (the previous copy was slow to end).</summary>
    internal static readonly TimeSpan PendingMaxAge = TimeSpan.FromHours(24);

    private const string SettingsName = "settings.json";
    private const string NotificationsName = "notifications.json";
    private const string ProjectColorsName = "project-colors.json";
    private const string StatsName = "stats.db";

    private static readonly Regex HistoryName = new(@"^history-[A-Za-z0-9_-]{1,100}\.jsonl\z", RegexOptions.CultureInvariant);

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

    /// <summary>True when <paramref name="path"/> lies in <paramref name="folder"/> or below it.</summary>
    internal static bool IsInsideFolder(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name of a data file that is a link (symlink or junction), or null.</summary>
    private static string? LinkedDataFileIn(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
            return null;

        foreach (var path in Directory.EnumerateFiles(dataDirectory))
        {
            var name = Path.GetFileName(path);
            if (IsDataFileName(name) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return name;
        }

        return null;
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
        if (IsInsideFolder(full, dataDirectory))
            throw new IOException("A backup is never written into the data folder it is made from.");
        // A linked data file would be left out silently; better no backup than one missing the index.
        if (LinkedDataFileIn(dataDirectory) is { } linked)
            throw new IOException($"{linked} is a link; the backup would miss it.");
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
        var bytes = AppEncoding.Utf8NoBom.GetBytes(text);
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
                // The central directory is only read into memory once its own entry count is known to be sane.
                if (!(DeclaredEntryCount(stream) is { } count && count >= 1 && count <= MaxEntries))
                    throw new RefusedException(BackupRefusal.NotABackup);
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
            return AppEncoding.Utf8NoBom.GetString(memory.GetBuffer(), 0, (int)memory.Length);
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
            // Never past what the entry itself declares: a size that lies low is caught as soon as the
            // real data exceeds it, not after the whole thing has been unpacked.
            var limit = Math.Min(MaxBytesFor(name), entry.Length);
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

    /// <summary>The entry's file directly inside <paramref name="directory"/>. <see cref="Validate"/>
    /// already admits only fixed plain names; this second check keeps any name that would land
    /// elsewhere from ever being written.</summary>
    internal static string PathInside(string directory, string name)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length == root.Length
            || full.IndexOf(Path.DirectorySeparatorChar, root.Length) >= 0)
            throw new RefusedException(BackupRefusal.NotABackup);
        return full;
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

        // Every backup this app writes has the manifest and the settings: without the settings a
        // restore would leave this PC with a new identity and its imported histories unlisted.
        if (!entries.ContainsKey(ManifestName) || !entries.ContainsKey(SettingsName))
            throw new RefusedException(BackupRefusal.NotABackup);

        return entries;
    }

    /// <summary>The entry count the end-of-central-directory record names (zip64 record when the
    /// plain field is saturated), read from the file's tail without building any entry. Null when
    /// no such record is found.</summary>
    private static long? DeclaredEntryCount(FileStream stream)
    {
        const int endRecordSize = 22;
        const int maxComment = 65535;
        var length = stream.Length;
        if (length < endRecordSize)
            return null;

        var tailLength = (int)Math.Min(length, endRecordSize + maxComment);
        var tail = new byte[tailLength];
        stream.Seek(length - tailLength, SeekOrigin.Begin);
        stream.ReadExactly(tail, 0, tailLength);

        for (var i = tailLength - endRecordSize; i >= 0; i--)
        {
            if (tail[i] != 0x50 || tail[i + 1] != 0x4B || tail[i + 2] != 5 || tail[i + 3] != 6)
                continue;

            var count = BitConverter.ToUInt16(tail, i + 10);
            if (count != 0xFFFF)
                return count;

            // Zip64: the locator sits right before the end record and points at the zip64 end record.
            var locator = i - 20;
            if (locator < 0 || tail[locator] != 0x50 || tail[locator + 1] != 0x4B || tail[locator + 2] != 6 || tail[locator + 3] != 7)
                return null;
            var offset = BitConverter.ToInt64(tail, locator + 8);
            if (offset < 0 || offset > length - 56)
                return null;

            var record = new byte[56];
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(record, 0, record.Length);
            if (record[0] != 0x50 || record[1] != 0x4B || record[2] != 6 || record[3] != 6)
                return null;
            var total = BitConverter.ToInt64(record, 32);
            return total < 0 ? null : total;
        }

        return null;
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

    /// <summary>Lists every move of a swap before it happens, so a start after a crash can put the
    /// previous files back whatever else is gone (the zip, the request, the staging folder).</summary>
    public const string JournalFileName = "restore-journal.json";

    private static readonly Regex SetAsideNamePattern = new(@"^before-restore-[0-9]{8}-[0-9]{6}(-[0-9]{1,3})?\z", RegexOptions.CultureInvariant);

    private static readonly string[] AsideExtraNames = [SettingsName + ".bak", StatsName + "-wal", StatsName + "-shm", StatsName + "-journal"];

    /// <summary>Test seam: called with the index of each move right before it is made. Throwing an IO
    /// exception makes that move fail; any other exception stands for a crash.</summary>
    internal static Action<int>? BeforeSwapMove;

    private sealed class SwapJournal
    {
        public string SetAside { get; set; } = "";

        /// <summary>Data folder files to move into the set-aside folder, one group per step, in order.</summary>
        public List<List<string>> Aside { get; set; } = [];

        /// <summary>The staged files that are moved into the data folder afterwards, in order.</summary>
        public List<string> Place { get; set; } = [];
    }

    private sealed record PendingRequest(string ZipPath, string? SetAsideName);

    /// <summary>The folder (not created yet) a restore started at <paramref name="now"/> sets the
    /// current data aside into. The confirm dialog names it before the restore is asked for.</summary>
    public static string PlanSetAside(string dataDirectory, DateTime now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"{SetAsidePrefix}{stamp}";
        for (var suffix = 2; Directory.Exists(Path.Combine(dataDirectory, name)) && suffix < 1000; suffix++)
            name = $"{SetAsidePrefix}{stamp}-{suffix}";
        return Path.Combine(dataDirectory, name);
    }

    /// <summary>Asks the next start to restore <paramref name="zipPath"/>, setting the current data
    /// aside in <paramref name="setAsidePath"/> (a folder of the data folder, see
    /// <see cref="PlanSetAside"/>). Nothing in the data folder is touched now: the running process
    /// still holds those files.</summary>
    public static void SchedulePendingRestore(string dataDirectory, string zipPath, string? setAsidePath = null)
    {
        var name = Path.GetFileName(setAsidePath ?? PlanSetAside(dataDirectory, DateTime.Now));
        var pending = Path.Combine(dataDirectory, PendingFileName);
        AtomicFile.WriteAllBytes(pending, AppEncoding.Utf8NoBom.GetBytes(Path.GetFullPath(zipPath) + "\n" + name));
    }

    /// <summary>Takes a scheduled restore back (the restart did not happen).</summary>
    public static void CancelPendingRestore(string dataDirectory) => TryDeleteFile(Path.Combine(dataDirectory, PendingFileName));

    public static RestoreOutcome ApplyPendingRestore(string dataDirectory, DateTime now, Action<string> log) =>
        ApplyPendingRestore(dataDirectory, now, log, out _);

    /// <summary>
    /// Run once at start, before anything opens the data files. First an interrupted earlier swap is
    /// undone from its journal (whatever else is missing). Then, when a restore is waiting: the whole
    /// zip is checked and extracted into a staging folder (any failure ends here with the data
    /// untouched), the staged index and settings are made this PC's (own machine id kept, histories
    /// relabelled), the swap is journalled, the current files are moved into the set-aside folder and
    /// the staged files are moved in. A failure in the swap puts the moved files back. Nothing the old
    /// state held is ever deleted. <paramref name="setAsidePath"/> is the folder holding the previous
    /// files after a restore.
    /// </summary>
    public static RestoreOutcome ApplyPendingRestore(string dataDirectory, DateTime now, Action<string> log, out string? setAsidePath)
    {
        setAsidePath = null;
        var recovered = RecoverInterruptedSwap(dataDirectory, log);

        var pending = Path.Combine(dataDirectory, PendingFileName);
        if (!File.Exists(pending))
            return recovered ? RestoreOutcome.Unchanged : RestoreOutcome.NoPending;

        var request = ReadPending(pending);
        if (request is null)
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
            BackupManifest manifest;
            using (var backup = OpenedBackup.Open(request.ZipPath))
            {
                long total = 0;
                foreach (var name in backup.DataNames)
                    backup.Extract(name, PathInside(staging, name), ref total);
                stagedNames = [.. backup.DataNames];
                manifest = backup.Manifest;
            }

            // The staged copies are what gets installed, so they are what gets checked.
            CheckSettingsText(File.ReadAllText(Path.Combine(staging, SettingsName)));
            if (stagedNames.Contains(StatsName))
                CheckStatsSchema(Path.Combine(staging, StatsName));

            // Read before any file moves: this PC keeps its identity, and whatever the backup holds
            // under its own id or under the id of the PC that made it is this PC's history now.
            var ownId = ReadOwnMachineId(dataDirectory);
            PrepareStagedSettings(Path.Combine(staging, SettingsName), ownId, manifest.MachineId);
            if (stagedNames.Contains(StatsName))
                StatsStore.PrepareRestoredIndex(Path.Combine(staging, StatsName), [manifest.MachineId, ownId]);

            var setAsideName = request.SetAsideName ?? Path.GetFileName(PlanSetAside(dataDirectory, now));
            return Swap(dataDirectory, staging, stagedNames, setAsideName, now, log, out setAsidePath);
        }
        catch (RefusedException ex)
        {
            log($"Restore: the backup was refused ({ex.Refusal}); nothing was changed.");
            return RestoreOutcome.Unchanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or SqliteException)
        {
            log($"Restore: the backup could not be read ({ex.GetType().Name}); nothing was changed.");
            return RestoreOutcome.Unchanged;
        }
        finally
        {
            DeleteStaging(staging);
            TryDeleteFile(pending);
        }
    }

    private static PendingRequest? ReadPending(string pending)
    {
        try
        {
            if (new FileInfo(pending).Length > 4096 || DateTime.UtcNow - File.GetLastWriteTimeUtc(pending) > PendingMaxAge)
                return null;

            var lines = File.ReadAllText(pending).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length is < 1 or > 2)
                return null;

            var text = lines[0];
            if (text.Length == 0 || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(text) || !File.Exists(text))
                return null;

            string? setAside = null;
            if (lines.Length == 2)
            {
                if (!SetAsideNamePattern.IsMatch(lines[1]))
                    return null;
                setAside = lines[1];
            }

            return new PendingRequest(text, setAside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>This PC's machine id from the current settings; a new one when there is none to read,
    /// so the restored settings never adopt the identity of the PC the backup came from.</summary>
    private static string ReadOwnMachineId(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, SettingsName);
            if (File.Exists(path)
                && JsonNode.Parse(File.ReadAllText(path)) is JsonObject current
                && current["machineId"] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && TryNormalizeMachineId(text, out var id))
                return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
        }

        return Guid.NewGuid().ToString("D");
    }

    /// <summary>The staged settings get this PC's machine id, and lose any imported-PC entry for this
    /// PC or for the PC the backup came from: their rows are this PC's own history now.</summary>
    private static void PrepareStagedSettings(string stagedSettings, string ownId, string backupMachineId)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(stagedSettings)) is not JsonObject staged)
                throw new RefusedException(BackupRefusal.NotABackup);

            staged["machineId"] = ownId;
            if (staged["importedMachines"] is JsonArray list)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] is JsonObject item
                        && item["id"] is JsonValue idValue
                        && idValue.TryGetValue<string>(out var text)
                        && TryNormalizeMachineId(text, out var id)
                        && (id == ownId || id == backupMachineId))
                        list.RemoveAt(i);
                }
            }

            File.WriteAllBytes(stagedSettings, AppEncoding.Utf8NoBom.GetBytes(staged.ToJsonString(SettingsStore.JsonOptions)));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new RefusedException(BackupRefusal.NotABackup);
        }
    }

    /// <summary>What a restore moves aside, in the order it is moved: every data file the data folder
    /// holds (a link under a data name moves as the link), the settings backup copy, and, when the
    /// backup brings its own index, the index together with its write-ahead files as one step. A
    /// folder without a backup index keeps its index.</summary>
    private static List<List<string>> AsideGroups(string dataDirectory, bool replacesIndex)
    {
        var groups = new List<List<string>>();
        if (!Directory.Exists(dataDirectory))
            return groups;

        var names = Directory.EnumerateFiles(dataDirectory).Select(Path.GetFileName).OfType<string>()
            .Where(n => IsDataFileName(n) && n != StatsName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (File.Exists(Path.Combine(dataDirectory, SettingsName + ".bak")))
            names.Add(SettingsName + ".bak");
        foreach (var name in names)
            groups.Add([name]);

        if (replacesIndex)
        {
            var index = new[] { StatsName, StatsName + "-wal", StatsName + "-shm", StatsName + "-journal" }
                .Where(n => File.Exists(Path.Combine(dataDirectory, n))).ToList();
            if (index.Count > 0)
                groups.Add(index);
        }

        return groups;
    }

    private static RestoreOutcome Swap(string dataDirectory, string staging, string[] stagedNames, string setAsideName, DateTime now,
        Action<string> log, out string? setAsidePath)
    {
        setAsidePath = null;
        var setAside = Path.Combine(dataDirectory, setAsideName);
        if (Directory.Exists(setAside))
            setAside = PlanSetAside(dataDirectory, now);

        var journal = new SwapJournal
        {
            SetAside = Path.GetFileName(setAside),
            Aside = AsideGroups(dataDirectory, stagedNames.Contains(StatsName)),
            Place = [.. stagedNames],
        };

        try
        {
            Directory.CreateDirectory(setAside);
            WriteJournal(dataDirectory, journal);

            // The write-ahead log of the old index goes into its database file first, so the file
            // that is moved aside is complete on its own.
            if (stagedNames.Contains(StatsName))
                StatsStore.CheckpointIndex(Path.Combine(dataDirectory, StatsName));

            var step = 0;
            foreach (var group in journal.Aside)
            {
                BeforeSwapMove?.Invoke(step++);
                foreach (var name in group)
                {
                    // The checkpoint above removes the write-ahead files it folded in.
                    var source = Path.Combine(dataDirectory, name);
                    if (File.Exists(source))
                        MoveWithRetry(source, Path.Combine(setAside, name));
                }
            }

            foreach (var name in stagedNames)
            {
                BeforeSwapMove?.Invoke(step++);
                MoveWithRetry(Path.Combine(staging, name), Path.Combine(dataDirectory, name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Restore: the swap failed ({ex.GetType().Name}); putting the previous files back.");
            if (RollBack(dataDirectory, journal, log))
                TryDeleteFile(Path.Combine(dataDirectory, JournalFileName));
            return RestoreOutcome.Unchanged;
        }

        TryDeleteFile(Path.Combine(dataDirectory, JournalFileName));
        setAsidePath = setAside;
        log($"Restore: done; the previous files are in {Path.GetFileName(setAside)}.");
        return RestoreOutcome.Restored;
    }

    private static void MoveWithRetry(string from, string to)
    {
        // A virus scanner can hold a freshly written file for a moment.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 3)
            {
                Thread.Sleep(150 * attempt);
            }
        }
    }

    private static void WriteJournal(string dataDirectory, SwapJournal journal)
    {
        var path = Path.Combine(dataDirectory, JournalFileName);
        AtomicFile.WriteAllBytes(path, AppEncoding.Utf8NoBom.GetBytes(JsonSerializer.Serialize(journal)));
    }

    private static bool IsJournalName(string? name) => name is not null && (IsDataFileName(name) || AsideExtraNames.Contains(name, StringComparer.Ordinal));

    private static SwapJournal? ReadJournal(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 64 * 1024)
                return null;

            var journal = JsonSerializer.Deserialize<SwapJournal>(File.ReadAllText(path));
            if (journal is null
                || !SetAsideNamePattern.IsMatch(journal.SetAside ?? "")
                || journal.Aside is null || journal.Place is null
                || journal.Aside.Count > 64 || journal.Place.Count > 64
                || journal.Aside.Any(g => g is null || g.Count > 8 || !g.All(IsJournalName))
                || !journal.Place.All(IsJournalName))
                return null;

            return journal;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Undoes an unfinished swap from its journal: the files already placed are removed (the zip still
    /// has them), then everything that was moved aside goes back. Done from what the folders hold now,
    /// so it works after a crash at any move. True when every file is back.
    /// </summary>
    private static bool RollBack(string dataDirectory, SwapJournal journal, Action<string> log)
    {
        var setAside = Path.Combine(dataDirectory, journal.SetAside);
        var asideNames = journal.Aside.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
        var complete = true;

        foreach (var name in journal.Place)
        {
            var current = Path.Combine(dataDirectory, name);
            if (!File.Exists(current))
                continue;
            // A name that was due to move aside but is not in the set-aside folder is still the old file.
            if (asideNames.Contains(name) && !File.Exists(Path.Combine(setAside, name)))
                continue;

            TryDeleteFile(current);
            if (File.Exists(current))
            {
                complete = false;
                log($"Restore: {name} could not be removed again.");
            }
        }

        for (var g = journal.Aside.Count - 1; g >= 0; g--)
        {
            var group = journal.Aside[g];
            for (var i = group.Count - 1; i >= 0; i--)
            {
                var from = Path.Combine(setAside, group[i]);
                var to = Path.Combine(dataDirectory, group[i]);
                if (!File.Exists(from))
                    continue;
                if (File.Exists(to))
                {
                    complete = false;
                    log($"Restore: {group[i]} could not be put back, a file with that name exists; it stays in {journal.SetAside}.");
                    continue;
                }

                try
                {
                    MoveWithRetry(from, to);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    complete = false;
                    log($"Restore: {group[i]} could not be put back; it stays in {journal.SetAside}.");
                }
            }
        }

        return complete;
    }

    /// <summary>Start-up recovery: a journal in the data folder means a swap never finished. True when one was found.</summary>
    private static bool RecoverInterruptedSwap(string dataDirectory, Action<string> log)
    {
        var path = Path.Combine(dataDirectory, JournalFileName);
        if (!File.Exists(path))
            return false;

        DeleteStaging(Path.Combine(dataDirectory, StagingFolderName));
        var journal = ReadJournal(path);
        if (journal is null)
        {
            log("Restore: an interrupted restore left a journal that cannot be read; it was set aside as it is.");
            try
            {
                File.Move(path, path + ".bad", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return true;
        }

        log("Restore: an interrupted restore was found; putting the previous files back.");
        if (RollBack(dataDirectory, journal, log))
            TryDeleteFile(path);
        else
            log($"Restore: some files are still in {journal.SetAside}; the journal stays for the next start.");
        return true;
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
