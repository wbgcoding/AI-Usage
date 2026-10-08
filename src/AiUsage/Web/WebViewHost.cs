using Microsoft.Web.WebView2.Core;
using AiUsage.Services;
using AiUsage.Storage;

namespace AiUsage.Web;

/// <summary>What a sign-out did to the session folder: it is gone (or was never there), a file
/// inside it is still open so it is worth trying again in a moment, or the folder resolved somewhere
/// that must never be deleted and trying again would only repeat that refusal.</summary>
public enum SignOutResult { Removed, StillOpen, Refused }

/// <summary>
/// Owns one web-backed provider's own, persistent WebView2 session - separate from the user's real
/// browser profile, never touching it, and separate from every other provider's own session (one
/// subfolder per provider under the shared webview profile root). Created on demand, released after
/// each use: no browser process runs while idle.
/// </summary>
public sealed class WebViewHost : IAsyncDisposable
{
    private readonly string _userDataFolder;
    private readonly Func<string, Task<CoreWebView2Environment>> _environmentFactory;
    // What SignOut() below writes to for its two rare guard branches. Real logging only for a real,
    // descriptor-built session (set by the public constructor below, after the chain to the test
    // seams has already run) - the two internal test seams a unit test builds from a bare folder
    // default to writing nothing at all, so a test that happens to hit one of those branches can
    // never open the app's own real log file the way SignOut() used to (see PrivacyTests).
    private readonly Action<string> _logRefusedRootDeletion = _ => { };
    private readonly Action _logSignOutStillOpen = () => { };

    // Guards only the creation of _environment itself (a plain field read/write is not atomic with
    // an await in between - see EnsureEnvironmentAsync) - never held across the factory's own await.
    private readonly object _environmentGate = new();
    private Lazy<Task<CoreWebView2Environment>>? _environment;

    public WebViewHost(WebSessionDescriptor descriptor) : this(ResolveUserDataFolder(descriptor.ProfileFolderName))
    {
        _logRefusedRootDeletion = message => LogService.Shared.LogError(message);
        _logSignOutStillOpen = () => LogService.Shared.LogInfo(
            "Sign-out could not remove the session folder: a file inside is still open elsewhere.");
    }

    /// <summary>Test seam: an already-resolved folder instead of deriving one from a provider's own
    /// profile folder name.</summary>
    internal WebViewHost(string userDataFolder)
        : this(userDataFolder, userData => CoreWebView2Environment.CreateAsync(userDataFolder: userData))
    {
    }

    /// <summary>Test seam one level further down: a fake environment factory instead of a real
    /// CoreWebView2Environment, which a test process cannot create - see WebViewHostTests.</summary>
    internal WebViewHost(string userDataFolder, Func<string, Task<CoreWebView2Environment>> environmentFactory)
    {
        _userDataFolder = userDataFolder;
        _environmentFactory = environmentFactory;
    }

    /// <summary>Test seam only: redirects <see cref="RootFolder"/> under a fake root instead of the
    /// real LocalApplicationData folder, so a test can prove the deletion guard below fires without
    /// ever pointing at the real shared webview folder.</summary>
    private static string? _rootOverride;

    private static string RootFolder() => _rootOverride ?? SharedRootFolder();

    /// <summary>The ordinary profile root, ignoring any override - what a second instance's own root
    /// is derived from (see <see cref="Services.SecondInstanceMode"/>).</summary>
    public static string SharedRootFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Usage", "webview");

    /// <summary>Where the profile folder of <paramref name="profileFolderName"/> lives, without
    /// creating or migrating anything - for asking whether one is still on disk.</summary>
    internal static string ProfileFolderPath(string profileFolderName) => Path.Combine(RootFolder(), profileFolderName);

    /// <summary>Points every session at another root: a second instance's own profile folder, or a
    /// test's fake root well away from the real shared one.</summary>
    internal static void SetRootOverride(string root) => _rootOverride = root;

    /// <summary>Test seam only: undoes <see cref="SetRootOverride"/> so one test's fake root does not
    /// leak into whichever test happens to run in this process next.</summary>
    internal static void ClearRootOverrideForTests() => _rootOverride = null;

    // Which provider's sign-in a still-flat profile root can only have belonged to: back then the
    // primary account was the only one with a web session at all.
    private const string LegacyFlatProfileOwner = "claude";

    // Used only when profileFolderName below is empty. Path.Combine drops an empty second argument
    // and returns the first unchanged, so an empty name would otherwise resolve to the root itself -
    // exactly the folder the guard in SignOut exists to protect. AppPaths.SanitizeAccountKeyForFileName
    // already keeps a real account key from ever producing an empty name; this is the second,
    // independent layer that holds even for a descriptor built without going through it.
    private const string FallbackProfileFolderName = "_unnamed-session";

    /// <summary>The one subfolder this provider's session lives in, migrating an existing flat
    /// profile into it first if one is still there from before providers had their own subfolders.</summary>
    public static string ResolveUserDataFolder(string profileFolderName)
    {
        var root = RootFolder();
        var safeFolderName = string.IsNullOrEmpty(profileFolderName) ? FallbackProfileFolderName : profileFolderName;
        MigrateFlatProfileIfNeeded(root, safeFolderName);
        return Path.Combine(root, safeFolderName);
    }

    /// <summary>
    /// One-time migration for a profile root that still holds browser data directly (the old,
    /// single-provider layout) instead of one subfolder per provider: moves everything found
    /// directly under <paramref name="root"/> into <paramref name="profileFolderName"/>, so an
    /// existing sign-in survives the move to one-subfolder-per-provider. A no-op once that subfolder
    /// already exists (either migration already ran, or the root is already laid out per-provider) or
    /// while the root does not exist or is empty. Never throws: a failed move degrades to "not signed
    /// in", fixable by signing in again, rather than blocking startup.
    /// </summary>
    internal static void MigrateFlatProfileIfNeeded(string root, string profileFolderName)
    {
        // A flat profile can only ever have belonged to the one provider that had a web session back
        // then. Without this, whichever provider happened to resolve its folder first would inherit
        // that sign-in - and a later sign-out of that provider would delete it.
        if (profileFolderName != LegacyFlatProfileOwner)
            return;

        var targetFolder = Path.Combine(root, profileFolderName);
        if (Directory.Exists(targetFolder))
            return;

        try
        {
            // Only a real flat profile (its browser data folder sits directly in the root) is
            // migrated. Without this check, resolving a folder that does not exist yet swept every
            // other account's own profile folder into it and signed those accounts out.
            if (!Directory.Exists(Path.Combine(root, "EBWebView")))
                return;

            var looseEntries = Directory.EnumerateFileSystemEntries(root)
                .Where(entry => !Directory.Exists(Path.Combine(entry, "EBWebView")))
                .ToList();
            if (looseEntries.Count == 0)
                return;

            Directory.CreateDirectory(targetFolder);
            foreach (var entry in looseEntries)
            {
                var destination = Path.Combine(targetFolder, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                    Directory.Move(entry, destination);
                else
                    File.Move(entry, destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever moved before the failure stays moved; nothing here rolls back, and the next
            // sign-in attempt just starts fresh in whatever is left.
        }
    }

    /// <summary>
    /// Creates the environment on first use and reuses it for the lifetime of this host. A sign-in
    /// and a background refresh can both call this before either has finished creating anything - the
    /// Lazy&lt;Task&lt;...&gt;&gt; itself is only ever constructed once (under <see
    /// cref="_environmentGate"/>), so both callers end up awaiting the exact same creation instead of
    /// each starting its own CoreWebView2Environment.CreateAsync over the same user-data folder.
    /// </summary>
    public Task<CoreWebView2Environment> EnsureEnvironmentAsync()
    {
        Lazy<Task<CoreWebView2Environment>> environment;
        lock (_environmentGate)
        {
            // A creation that failed once is not kept for the host's lifetime - the next call retries.
            if (_environment is { IsValueCreated: true } existing && (existing.Value.IsFaulted || existing.Value.IsCanceled))
                _environment = null;
            _environment ??= new Lazy<Task<CoreWebView2Environment>>(() => _environmentFactory(_userDataFolder));
            environment = _environment;
        }
        return environment.Value;
    }

    /// <summary>
    /// Signs out: deletes only this provider's own session folder (absolute path, no wildcards - the
    /// user's real browser profile is never in this path, and neither is any other provider's own
    /// folder). Never throws; a locked file just leaves the folder partially cleared rather than
    /// failing the whole operation. Says which of the three things happened, so a caller can tell a
    /// browser that has not finished closing yet (worth another try in a moment) apart from a folder
    /// that must never be deleted at all (trying again would only repeat the refusal).
    /// </summary>
    public SignOutResult SignOut()
    {
        if (!Directory.Exists(_userDataFolder))
            return SignOutResult.Removed;

        if (IsSharedRootFolder(_userDataFolder))
        {
            // A single account's own folder must never BE the shared root every provider's sessions
            // live under - deleting it here would sign out every account at once. Two other layers
            // already keep a resolved folder from ever landing here: AppPaths.
            // SanitizeAccountKeyForFileName never turns a real account key into an empty name, and
            // ResolveUserDataFolder's own fallback keeps even an empty name below the root. This
            // check is the last one, in case some future caller skips both.
            // The folder's own name is named too (never the path above it): the guard firing at all
            // means some account resolved somewhere it never should, and without the name there is
            // nothing in the log to say which one.
            _logRefusedRootDeletion(
                $"Refused to delete the shared webview root folder as a single account's session: {Path.GetFileName(_userDataFolder)}");
            return SignOutResult.Refused;
        }

        try
        {
            Directory.Delete(_userDataFolder, recursive: true);
            return SignOutResult.Removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file the browser process still has open. The caller decides what to tell the user;
            // the next sign-in attempt reuses whatever is left. Logged with wording distinct from the
            // guard above, so a log naming one of the two always says which one actually happened.
            _logSignOutStillOpen();
            return SignOutResult.StillOpen;
        }
    }

    private static bool IsSharedRootFolder(string path) => PathsEqual(path, RootFolder());

    private static bool PathsEqual(string a, string b) => string.Equals(
        Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// CoreWebView2Environment exposes no public Dispose/Close in this SDK version - its underlying
    /// browser process lifetime is reference-counted internally by the WebView2 runtime and released
    /// once nothing (including this field) still holds the wrapper, so clearing the field is the only
    /// release available here. Confirmed against Microsoft.Web.WebView2 1.0.3179.45's public API
    /// (a build against the actual package, not a guess) rather than added speculatively.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _environment = null;
        return ValueTask.CompletedTask;
    }
}
