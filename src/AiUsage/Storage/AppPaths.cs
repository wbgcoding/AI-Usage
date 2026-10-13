using System.Text;
using AiUsage.Services;

namespace AiUsage.Storage;

/// <summary>
/// Resolves where this app's own data lives: always in the user profile, whether the app was
/// installed or copied somewhere as a single file. Never throws: a permission problem just falls
/// back to the next candidate, down to %TEMP%.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "AI-Usage";

    /// <summary>Name of the one small file that can redirect the whole data directory elsewhere -
    /// see <see cref="LocationPointerFile"/>.</summary>
    private const string PointerFileName = "location.txt";

    private static readonly Lazy<string> LazyDataDirectory = new(() =>
    {
        var directory = ResolveDataDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.GetTempPath(), out var unreachable);
        _unreachableAtStart = unreachable;
        return directory;
    });

    /// <summary>The folder the pointer file named at start when it could not be used, else null.</summary>
    private static string? _unreachableAtStart;

    /// <summary>Set once by a completed "move my data" operation - see
    /// <see cref="ViewModels.SettingsViewModel"/>. Checked before the lazily-resolved default so a
    /// move takes effect for the rest of the running process without a restart.</summary>
    private static string? _overrideDirectory;

    /// <summary>The folder <see cref="SetStartRedirect"/> chose at start, so the note about an
    /// unreachable pointer target holds until a later move replaces it.</summary>
    private static string? _startRedirect;

    /// <summary>Test seam only: the floor <see cref="ClearOverrideForTests"/> falls back to instead of
    /// null - see that method for why null is never safe once a test process is running.</summary>
    private static string? _overrideFloorForTests;

    public static string DataDirectory => _overrideDirectory ?? LazyDataDirectory.Value;

    /// <summary>The folder the user chose for the data when the pointer file names one that was not
    /// available at start (a drive that is not plugged in, a share that is asleep), so this session
    /// runs in the default folder; null otherwise, and again once the data was moved in this session.</summary>
    public static string? UnreachableDataFolder
    {
        get
        {
            _ = DataDirectory;
            return _overrideDirectory is null || _overrideDirectory == _startRedirect ? _unreachableAtStart : null;
        }
    }

    /// <summary>How a folder is a poor home for the token index, which runs with write-ahead files:
    /// a network share or a drive that can go away, and a folder a sync client uploads half written.</summary>
    public enum FolderRisk { None, Unc, NetworkDrive, RemovableDrive, CloudSync }

    private static readonly string[] CloudSyncFolderNames = ["Dropbox", "Google Drive", "My Drive", "iCloudDrive"];

    /// <summary>Classifies <paramref name="path"/> against the real drives and the OneDrive folders of
    /// this account.</summary>
    public static FolderRisk ClassifyFolderRisk(string path) => ClassifyFolderRisk(path, RealDriveType, RealCloudSyncRoots());

    /// <summary>The pure decision: <paramref name="driveTypeOf"/> answers for a drive root such as
    /// <c>D:\</c>, <paramref name="cloudRoots"/> are folders a sync client owns. A path that cannot be
    /// read counts as no risk, since the writability check right after refuses it anyway.</summary>
    internal static FolderRisk ClassifyFolderRisk(string path, Func<string, DriveType> driveTypeOf, IEnumerable<string> cloudRoots)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return FolderRisk.None;
        }

        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return FolderRisk.Unc;
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            full = full[4..];
        else if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return FolderRisk.Unc;

        switch (driveTypeOf(Path.GetPathRoot(full) ?? ""))
        {
            case DriveType.Network:
                return FolderRisk.NetworkDrive;
            case DriveType.Removable:
                return FolderRisk.RemovableDrive;
        }

        foreach (var root in cloudRoots)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(root) && IsUnderRoot(full, root))
                    return FolderRisk.CloudSync;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
                // A root that is no path says nothing about this folder.
            }
        }

        var segments = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => CloudSyncFolderNames.Contains(segment, StringComparer.OrdinalIgnoreCase))
            ? FolderRisk.CloudSync
            : FolderRisk.None;
    }

    private static DriveType RealDriveType(string root)
    {
        try
        {
            return root.Length == 0 ? DriveType.Unknown : new DriveInfo(root).DriveType;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return DriveType.Unknown;
        }
    }

    private static readonly string[] CloudSyncEnvironmentVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    private static IEnumerable<string> RealCloudSyncRoots() =>
        CloudSyncEnvironmentVariables
            .Select(Environment.GetEnvironmentVariable)
            .Where(value => !string.IsNullOrWhiteSpace(value))!;

    /// <summary>Which of the three cases <see cref="DataDirectory"/> currently is, for
    /// <see cref="Views.SettingsWindow"/> to say alongside the path itself - see
    /// <see cref="ClassifyDataDirectory(string,string,string)"/> for how the three are told apart.</summary>
    public enum DataFolderKind { Default, Chosen, Fallback }

    /// <summary>Classifies <see cref="DataDirectory"/> against the real ApplicationData and Temp
    /// folders.</summary>
    public static DataFolderKind ClassifyDataDirectory() => ClassifyDataDirectory(
        DataDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Path.GetTempPath());

    /// <summary>
    /// Exposed for testing the decision itself against fake roots, without touching the real
    /// %APPDATA% or %TEMP% - the pure counterpart to <see cref="ResolveDataDirectory"/>: a directory
    /// that is neither the ordinary profile candidate nor the temp candidate can only be there because
    /// a pointer file sent it there, so it counts as chosen even though nothing here reads the pointer
    /// file itself.
    /// </summary>
    internal static DataFolderKind ClassifyDataDirectory(string dataDirectory, string appDataRoot, string tempRoot)
    {
        if (PathsEqual(dataDirectory, Path.Combine(tempRoot, AppFolderName)))
            return DataFolderKind.Fallback;
        if (PathsEqual(dataDirectory, Path.Combine(appDataRoot, AppFolderName)))
            return DataFolderKind.Default;
        return DataFolderKind.Chosen;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> itself or lies inside
    /// it - boundary-safe, so a sibling folder that merely shares the root as a string prefix
    /// (<c>C:\Temp2</c> against root <c>C:\Temp</c>) is never mistaken for being under it.</summary>
    internal static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Test seam only: redirects <see cref="LocationPointerFile"/> under a fake root instead
    /// of the real ApplicationData folder, so a test never writes the real
    /// <c>%APPDATA%\AI-Usage\location.txt</c>.</summary>
    private static string? _pointerRootOverride;

    /// <summary>Always at the same, well-known place - <c>%APPDATA%\AI-Usage\location.txt</c> - never
    /// wherever the data currently lives, so a user (or this app) can always find it again even after
    /// the data itself moved somewhere else entirely.</summary>
    public static string LocationPointerFile => Path.Combine(
        _pointerRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName, _secondInstancePointer ? SecondInstanceMode.FolderName : "", PointerFileName);

    /// <summary>True once this process runs as the second instance: its pointer file then lives in its
    /// own subfolder, so moving its data never rewrites the everyday copy's pointer.</summary>
    private static bool _secondInstancePointer;

    /// <summary>Switches <see cref="LocationPointerFile"/> to the second instance's own file.</summary>
    internal static void UseSecondInstancePointer() => _secondInstancePointer = true;

    internal static bool IsSecondInstance => _secondInstancePointer;

    /// <summary>The folder the everyday copy keeps its data in: what its pointer file names, else the
    /// default profile folder. Read only, so it is safe to ask from the second instance.</summary>
    internal static string EverydayDataDirectory()
    {
        var candidate = Path.Combine(
            _pointerRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
        return TryReadPointer(Path.Combine(candidate, PointerFileName), out var pointed) ? pointed : candidate;
    }

    /// <summary>Exposed so a test can name a fake temp root: the real one holds every test folder.</summary>
    internal static string? ReadPointedDirectory(string tempRoot) => ReadPointedDirectory(tempRoot, out _);

    /// <summary><paramref name="unreachableTarget"/> is the folder the pointer named when it could not
    /// be used (not writable right now), else null.</summary>
    internal static string? ReadPointedDirectory(string tempRoot, out string? unreachableTarget)
    {
        unreachableTarget = null;
        if (!TryReadPointer(LocationPointerFile, out var directory) || IsUnderRoot(directory, tempRoot))
            return null;
        if (!TryEnsureWritableDirectory(directory))
        {
            unreachableTarget = directory;
            return null;
        }
        return directory;
    }

    /// <summary>The start-time choice of a process that does not use the lazily resolved folder (the
    /// second instance): sets the folder and remembers the pointer target that could not be used, so
    /// it is reported like the everyday copy reports it.</summary>
    internal static void SetStartRedirect(string directory, string? unreachableTarget)
    {
        _overrideDirectory = _startRedirect = directory;
        _unreachableAtStart = unreachableTarget;
    }

    /// <summary>Test seam only: see <see cref="_pointerRootOverride"/>.</summary>
    internal static void SetPointerRootOverride(string root) => _pointerRootOverride = root;

    /// <summary>Test seam only: undoes <see cref="SetPointerRootOverride"/> so one test's fake pointer
    /// root does not leak into whichever test happens to run in this process next.</summary>
    internal static void ClearPointerRootOverrideForTests()
    {
        _pointerRootOverride = null;
        _secondInstancePointer = false;
        _startRedirect = null;
        _unreachableAtStart = null;
    }

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string HistoryFile(string accountKey) =>
        Path.Combine(DataDirectory, $"history-{SanitizeAccountKeyForFileName(accountKey)}.jsonl");

    /// <summary>Turns an account key into a safe file/folder name fragment - "#" (the separator a
    /// further account's key uses, see <see cref="Models.AppSettings.Accounts"/>) becomes "-", so
    /// "claude#2" becomes "claude-2". Every other character outside letters, digits, "-" and "_"
    /// becomes "-" as well, so a hand-edited key can never carry a path separator or "..". The one place <see cref="HistoryFile"/>, <see
    /// cref="HistoryStore"/> and the web-session profile folder (see
    /// <see cref="Services.ProviderRegistry.WebSessionFor"/>) all go through, so an account's file
    /// name is never computed two different ways.
    ///
    /// Never returns a name with no letter or digit in it: every real account key carries its
    /// provider id ("claude", "claude#2", ...), so that only happens for a hand-edited or corrupted
    /// key that is empty, or built entirely from characters the loop above replaces. Two different
    /// keys of that kind (an empty key and one made only of forbidden characters, or two keys made of
    /// different forbidden characters) would otherwise sanitize down to the exact same name - most
    /// dangerously, an empty key sanitizes to an empty name, and <see
    /// cref="Web.WebViewHost.ResolveUserDataFolder"/> combining an empty name onto the shared root
    /// folder returns that root folder itself. A short, stable hash of the untouched key is appended
    /// in that case, keeping every one of these distinct and never empty, without changing anything
    /// for an ordinary key.</summary>
    public static string SanitizeAccountKeyForFileName(string accountKey)
    {
        var sanitized = string.Concat(accountKey.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        if (sanitized.Any(char.IsAsciiLetterOrDigit))
            return sanitized;

        var suffix = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(accountKey)))[..8].ToLowerInvariant();
        return sanitized.Length == 0 ? suffix : $"{sanitized}-{suffix}";
    }

    /// <summary>Makes <see cref="DataDirectory"/> return <paramref name="directory"/> for the rest of
    /// the running process, without touching <see cref="LazyDataDirectory"/> - a completed move
    /// writes the pointer file first (see <see cref="ViewModels.SettingsViewModel"/>), then calls this
    /// so every store already open in this process starts using the new location immediately.</summary>
    internal static void SetOverride(string directory) => _overrideDirectory = directory;

    /// <summary>Test seam only: undoes <see cref="SetOverride"/> so one test's completed "move my
    /// data" does not leak into whichever test happens to run in this process next. Falls back to
    /// <see cref="_overrideFloorForTests"/>, never to null: <see cref="DataDirectory"/> is a
    /// process-wide static shared with every other test in the run, so leaving it null here would let
    /// whichever test next happens to be the first one anywhere in the process to read
    /// <see cref="DataDirectory"/> fall through to <see cref="LazyDataDirectory"/> and resolve the real
    /// profile folder - observed in practice as stray "Ignored a data-folder pointer..." lines
    /// appearing in the real app.log purely from running the test suite.</summary>
    internal static void ClearOverrideForTests() => _overrideDirectory = _overrideFloorForTests;

    /// <summary>Test seam only: installs the one safety net an entire test run relies on - see
    /// <see cref="ClearOverrideForTests"/> - called once, before the first test, by the test
    /// assembly's own module initializer.</summary>
    internal static void EstablishOverrideFloorForTests(string directory) =>
        _overrideDirectory = _overrideFloorForTests = directory;

    /// <summary>
    /// Exposed for testing the decision itself against fake roots, without touching the real
    /// %APPDATA% or %TEMP%. A pointer file naming a directory that is actually writable always wins,
    /// even over the ordinary profile folder - it exists only because a user or this app deliberately
    /// chose to move the data there.
    /// </summary>
    internal static string ResolveDataDirectory(string appDataRoot, string tempRoot) =>
        ResolveDataDirectory(appDataRoot, tempRoot, out _);

    /// <summary><paramref name="unreachablePointerTarget"/> is the folder the pointer file named when
    /// that folder could not be used (it is logged once, the path without the user's name), else null.</summary>
    internal static string ResolveDataDirectory(string appDataRoot, string tempRoot, out string? unreachablePointerTarget)
    {
        unreachablePointerTarget = null;
        var appDataCandidate = Path.Combine(appDataRoot, AppFolderName);
        if (TryReadPointer(Path.Combine(appDataCandidate, PointerFileName), out var pointedDirectory))
        {
            if (IsUnderRoot(pointedDirectory, tempRoot))
                LogService.AppendTo(Path.Combine(appDataCandidate, "logs"),
                    "Ignored a data-folder pointer into the temp directory.");
            else if (TryEnsureWritableDirectory(pointedDirectory))
                return pointedDirectory;
            else
            {
                unreachablePointerTarget = pointedDirectory;
                LogService.AppendTo(Path.Combine(appDataCandidate, "logs"),
                    $"The data folder {PathSanitizer.Sanitize(pointedDirectory)} is not available; this session uses the default folder.");
            }
        }

        if (TryEnsureWritableDirectory(appDataCandidate))
            return appDataCandidate;

        var tempCandidate = Path.Combine(tempRoot, AppFolderName);
        TryEnsureWritableDirectory(tempCandidate); // best effort - this is already the last resort
        return tempCandidate;
    }

    private static bool TryReadPointer(string pointerPath, out string directory)
    {
        directory = "";
        try
        {
            if (!File.Exists(pointerPath))
                return false;

            var text = File.ReadAllText(pointerPath).Trim();
            if (text.Length == 0 || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(text))
                return false;

            directory = text;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the app's own leftover temp files under <see cref="DataDirectory"/>: the ones <see
    /// cref="SettingsStore.SaveNow"/>, <see cref="HistoryStore"/>'s atomic rewrite and the
    /// notification state write leave behind when the process dies before moving them into place.
    /// The data folder may be any folder the user picked, so a <c>*.tmp</c> file under another name
    /// is never touched. A file is deleted only once it is at least an hour old, so a write in
    /// progress right now is safe. Called once at startup, the same way <see
    /// cref="Providers.Parsing.AntigravityStateReader.CleanUpLeftoverSnapshots()"/> does for its own
    /// temp folder.
    /// </summary>
    public static void CleanUpLeftoverTempFiles() => CleanUpLeftoverTempFiles(DataDirectory);

    /// <summary>Exposed so a test can plant a leftover in a folder of its own.</summary>
    internal static void CleanUpLeftoverTempFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var path in Directory.EnumerateFiles(directory, "*.tmp"))
            {
                if (!IsOwnTempFileName(Path.GetFileName(path)))
                    continue;

                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                        File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best effort - a file still being written or held open is simply left for the
                    // next startup to try again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsOwnTempFileName(string name) =>
        name.StartsWith("settings.json.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("status.json.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("notifications.json", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(BackupService.PendingFileName + ".", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(BackupService.JournalFileName + ".", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("history-",StringComparison.OrdinalIgnoreCase)
            && name.Contains(".jsonl.", StringComparison.OrdinalIgnoreCase))
        || (name.StartsWith("stats.v", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".bak.tmp", StringComparison.OrdinalIgnoreCase));

    /// <summary>Internal, not private: <see cref="ViewModels.SettingsViewModel"/>'s own "change data
    /// folder" flow needs the exact same writability check before it trusts a user-picked folder.</summary>
    internal static bool TryEnsureWritableDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probePath = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probePath, []);
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            // A pointer naming something no folder can be created at (an embedded NUL, say) counts
            // as unwritable, so the caller falls back instead of failing the whole start.
            return false;
        }
    }
}
