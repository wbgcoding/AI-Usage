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

    private static readonly Lazy<string> LazyDataDirectory = new(() => ResolveDataDirectory(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Path.GetTempPath()));

    /// <summary>Set once by a completed "move my data" operation - see
    /// <see cref="ViewModels.SettingsViewModel"/>. Checked before the lazily-resolved default so a
    /// move takes effect for the rest of the running process without a restart.</summary>
    private static string? _overrideDirectory;

    /// <summary>Test seam only: the floor <see cref="ClearOverrideForTests"/> falls back to instead of
    /// null - see that method for why null is never safe once a test process is running.</summary>
    private static string? _overrideFloorForTests;

    public static string DataDirectory => _overrideDirectory ?? LazyDataDirectory.Value;

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
    private static bool IsUnderRoot(string path, string root)
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
        AppFolderName, PointerFileName);

    /// <summary>Test seam only: see <see cref="_pointerRootOverride"/>.</summary>
    internal static void SetPointerRootOverride(string root) => _pointerRootOverride = root;

    /// <summary>Test seam only: undoes <see cref="SetPointerRootOverride"/> so one test's fake pointer
    /// root does not leak into whichever test happens to run in this process next.</summary>
    internal static void ClearPointerRootOverrideForTests() => _pointerRootOverride = null;

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string NotificationsFile => Path.Combine(DataDirectory, "notifications.json");

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
    internal static string ResolveDataDirectory(string appDataRoot, string tempRoot)
    {
        var appDataCandidate = Path.Combine(appDataRoot, AppFolderName);
        if (TryReadPointer(Path.Combine(appDataCandidate, PointerFileName), out var pointedDirectory))
        {
            if (IsUnderRoot(pointedDirectory, tempRoot))
                LogService.AppendTo(Path.Combine(appDataCandidate, "logs"),
                    "Ignored a data-folder pointer into the temp directory.");
            else if (TryEnsureWritableDirectory(pointedDirectory))
                return pointedDirectory;
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
            if (text.Length == 0 || !Path.IsPathFullyQualified(text))
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
        || name.StartsWith("notifications.json", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("history-", StringComparison.OrdinalIgnoreCase)
            && name.Contains(".jsonl.", StringComparison.OrdinalIgnoreCase));

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
