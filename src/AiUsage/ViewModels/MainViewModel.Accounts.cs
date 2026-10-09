using System.Collections.ObjectModel;
using System.Linq;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;
using AiUsage.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiUsage.ViewModels;

/// <summary>What <see cref="MainViewModel.RemoveAccountAsync"/> did: nothing to remove (the primary
/// account, an unknown key), the account is gone, its sign-in files could not be deleted so it
/// was left exactly as it was, the removal broke off with an error (the account is kept the
/// same way), or the account is gone but its history, which was asked to go too, could not be
/// deleted.</summary>
internal enum RemoveAccountResult { NotRemoved, Removed, SignInFilesLocked, Failed, RemovedHistoryKept }

public sealed partial class MainViewModel
{
    /// <summary>Test seam for the sign-out folder delete: production deletes the real profile folder
    /// for whichever account key is asked, a test substitutes a delegate that records the call
    /// without touching disk.</summary>
    internal Func<string, SignOutResult> SignOutWebView { get; set; } =
        accountKey => new WebViewHost(ProviderRegistry.WebSessionFor(accountKey)).SignOut();

    /// <summary>How long <see cref="SignOutAsync"/> waits between two attempts at the same folder.
    /// The browser process does not disappear the moment its last wrapper is released, so the first
    /// delete after a sign-out regularly still finds one of its own files open - which used to leave
    /// the session on disk and the account signed in. A test seam so proving the retry does not cost
    /// real seconds.</summary>
    internal TimeSpan SignOutRetryDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Test seam for building one web-backed account's provider and runner: production
    /// calls the real registry (a live WebView2-backed runner) for whichever provider the account key
    /// names, a test substitutes a delegate returning a fake provider/runner pair instead. Used by
    /// both <see cref="AddAccount"/> and <see cref="SignOutAsync"/>, which rebuilds the account's
    /// provider fresh rather than reusing one whose runner a prior sign-out already disposed.</summary>
    internal Func<string, (IUsageProvider Provider, IAsyncDisposable Runner)> CreateWebAccount { get; set; }

    /// <summary>Copilot has no web session to add - a further account is another GitHub user the CLI
    /// already lists as signed in (see <see cref="ListAvailableCopilotLoginsAsync"/>), so this is a
    /// separate flow the view opens a small picker for rather than adding anything straight away. An
    /// empty list means no further candidate exists at all (every signed-in user is already an
    /// account here, or none is signed in) - the view's own message names the "gh auth login" way to
    /// add one. Raised only from the Copilot branch of <see cref="AddAccount"/>.</summary>
    public event EventHandler<IReadOnlyList<string>>? CopilotLoginChoiceRequested;

    /// <summary>The Settings.AddAccount action - builds and wires a new tile
    /// for one further account the same way the constructor does for every provider at startup,
    /// without touching any of the others. <paramref name="providerId"/> is which provider this
    /// account signs into: every web-backed provider (Claude, Codex, Cursor, Gemini) goes through
    /// <see cref="CreateWebAccount"/> below; Copilot has no web session at all and is routed to
    /// <see cref="AddCopilotAccountAsync"/> instead.</summary>
    [RelayCommand]
    private async Task AddAccount(string providerId)
    {
        if (providerId == "copilot")
        {
            await AddCopilotAccountAsync();
            return;
        }

        var accountKey = NextAccountKey(providerId);
        var (provider, runner) = CreateWebAccount(accountKey);

        _settings.Accounts[accountKey] = providerId;
        GetOrCreateProviderSettings(accountKey);
        _webRunners[accountKey] = runner;

        BuildTile(provider);
        _scheduler.AddProvider(provider);
        RenumberRowOrders();
        ApplyTileOrder();
        RefreshMoveEligibility();

        _settingsStore.RequestSave(_settings);
    }

    /// <summary>Every GitHub login the CLI already lists as signed in (see
    /// <see cref="Providers.LocalLogin.GitHubCliUsage.ListSignedInUsersAsync"/>) that is not already
    /// one of this app's Copilot accounts. The primary account always reads as the CLI's active
    /// github.com user, so that login is shown already and is never offered again (offering it made
    /// a second tile that only duplicated the first).</summary>
    internal async Task<IReadOnlyList<string>> ListAvailableCopilotLoginsAsync(CancellationToken ct = default)
    {
        var signedIn = await GetGitHubAccountsAsync(ct);
        var alreadyAdded = _settings.CopilotAccountLogins.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var primary in signedIn.Where(IsPrimaryCopilotLogin))
            alreadyAdded.Add(primary.Login);
        // Only github.com users: the read asks github.com for the token and the usage, so a user from
        // another host would stay "not signed in" for good.
        return signedIn
            .Where(a => string.Equals(a.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Login)
            .Where(login => !alreadyAdded.Contains(login))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPrimaryCopilotLogin(Providers.LocalLogin.GitHubAccount account) =>
        account.Active && string.Equals(account.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Test seam for <see cref="ListAvailableCopilotLoginsAsync"/> - production asks the real
    /// GitHub CLI, a test injects a canned list instead.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<Providers.LocalLogin.GitHubAccount>>> GetGitHubAccountsAsync { get; set; }
        = ct => Providers.LocalLogin.GitHubCliUsage.ListSignedInUsersAsync(ct);

    private async Task AddCopilotAccountAsync()
    {
        var candidates = await ListAvailableCopilotLoginsAsync();
        if (candidates.Count == 0)
        {
            // Nothing to offer: every signed-in user is already an account here, or none exists yet -
            // either way the view's own message names the "gh auth login" way to add one.
            CopilotLoginChoiceRequested?.Invoke(this, []);
            return;
        }

        if (candidates.Count == 1)
        {
            FinishAddingCopilotAccount(candidates[0]);
            return;
        }

        CopilotLoginChoiceRequested?.Invoke(this, candidates);
    }

    /// <summary>Called back once the view's picker (or the single-candidate shortcut above) has
    /// settled on one GitHub login - builds and wires this account's tile exactly the way a web
    /// account's own <see cref="AddAccount"/> branch does, minus the runner neither this provider nor
    /// its settings entry ever has.</summary>
    internal void FinishAddingCopilotAccount(string githubLogin)
    {
        var accountKey = NextAccountKey("copilot");
        _settings.Accounts[accountKey] = "copilot";
        _settings.CopilotAccountLogins[accountKey] = githubLogin;
        GetOrCreateProviderSettings(accountKey);

        var provider = ProviderRegistry.CreateCopilotAccount(accountKey, _settings);
        BuildTile(provider);
        _scheduler.AddProvider(provider);
        RenumberRowOrders();
        ApplyTileOrder();
        RefreshMoveEligibility();

        _settingsStore.RequestSave(_settings);
    }

    /// <summary>The first account-key suffix not already taken, so removing "claude#2" and adding
    /// again never collides with an account key a still-untouched history file or browser profile
    /// still refers to: a key whose history file or sign-in folder is still on disk (an account
    /// removed with its history kept) is skipped, so a new account never inherits that history.</summary>
    private string NextAccountKey(string providerId)
    {
        var n = 2;
        while (KeyIsTaken($"{providerId}#{n}"))
            n++;
        return $"{providerId}#{n}";
    }

    private bool KeyIsTaken(string accountKey) =>
        _settings.Accounts.ContainsKey(accountKey)
        || _historyStore.HasHistory(accountKey)
        || Directory.Exists(WebViewHost.ProfileFolderPath(ProviderRegistry.WebSessionFor(accountKey).ProfileFolderName));

    // Accounts whose removal is under way: the scheduler skips them (see IsAccountDisconnected) so no
    // new fetch starts while the running one is cancelled and its browser session is torn down.
    private readonly HashSet<string> _removingAccounts = [];

    // Accounts whose sign-out is under way: skipped by the scheduler the same way, so a tick cannot
    // start a fetch between the cancel and the persisted Disconnected flag.
    private readonly HashSet<string> _signingOutAccounts = [];

    /// <summary>The Settings.RemoveAccount action for a second (or later) account -
    /// first stops its running fetch and shuts its browser session down, deletes the account's own
    /// browser sign-in folder through the same path the sign-out uses (with its guard against ever
    /// deleting the shared profile root), then stops it being scheduled and drops its tile and its
    /// settings entries. The account's recorded history is deleted only when <paramref
    /// name="alsoHistory"/> is set. Nothing outside the app's own per-account folder and history file
    /// is ever touched. When the sign-in folder cannot be deleted the removal is abandoned: the
    /// account keeps its tile, its settings and its history, and gets a fresh browser session
    /// (<see cref="RemoveAccountResult.SignInFilesLocked"/>). Changes nothing for the one account every
    /// provider always has: that tile never shows a "remove" button to begin with, so this is defence
    /// in depth only.</summary>
    internal async Task<RemoveAccountResult> RemoveAccountAsync(string accountKey, bool alsoHistory = false)
    {
        if (!_tilesById.TryGetValue(accountKey, out var tile) || !tile.IsExtraAccount)
            return RemoveAccountResult.NotRemoved;
        if (SharesFolderNameWithAnotherAccount(accountKey))
        {
            _logService?.LogError($"Removing {accountKey}: its folder name is the same as another account's, nothing is deleted.");
            return RemoveAccountResult.NotRemoved;
        }

        if (!_removingAccounts.Add(accountKey))
            return RemoveAccountResult.NotRemoved;

        var hadWebSession = false;
        var accountGone = false;
        var historyKept = false;
        try
        {
            // The runner must not be torn down underneath a fetch that still uses it.
            await _scheduler.CancelProvider(accountKey);

            hadWebSession = _webRunners.Remove(accountKey, out var runner);
            if (hadWebSession)
            {
                try
                {
                    await runner!.DisposeAsync();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logService?.LogError($"Removing {accountKey}: the browser session did not close cleanly ({ex.GetType().Name}).");
                }
            }

            var folderResult = await DeleteWebProfileAsync(accountKey);
            if (folderResult != SignOutResult.Removed)
            {
                _logService?.LogError($"Removing {accountKey}: the sign-in folder was not deleted ({folderResult}), the account stays.");
                return RemoveAccountResult.SignInFilesLocked;
            }

            accountGone = true;
            _scheduler.RemoveProvider(accountKey);
            _tilesById.Remove(accountKey);
            Tiles.Remove(tile);
            DisplayRows.Remove(tile);
            _ownRows.Remove(tile);
            _fetchStartedAt.Remove(accountKey);
            _pendingFetchClear.Remove(accountKey);
            _notifications.RemoveAccount(accountKey);
            RenumberRowOrders();
            RefreshHiddenCount();
            RefreshMoveEligibility();

            _settings.Accounts.Remove(accountKey);
            _settings.Providers.Remove(accountKey);
            _settings.CopilotAccountLogins.Remove(accountKey);
            _settings.WebUsagePaths.Remove(accountKey);
            // Not the debounced save: a queued older one must not write the account back, and a
            // failure has to show up in the log.
            if (!_settingsStore.SaveImmediately(_settings))
                _logService?.LogError($"Removing {accountKey}: the settings could not be saved, the account may reappear after a restart.");

            // After the tile and the scheduler entry are gone, so no late snapshot writes the file
            // again; the store only ever deletes this account's own history file.
            if (alsoHistory)
                historyKept = !_historyStore.DeleteAll([accountKey]);
        }
        finally
        {
            // The old runner is gone however the removal ended, and a provider left on it could never
            // read again: a kept account (locked folder, or an error part-way) gets a fresh one.
            // Done before the account counts as live again so no sign-in sees it half-built.
            try
            {
                if (hadWebSession && !accountGone)
                {
                    _scheduler.RemoveProvider(accountKey);
                    var (freshProvider, freshRunner) = CreateWebAccount(accountKey);
                    _webRunners[accountKey] = freshRunner;
                    _scheduler.AddProvider(freshProvider);
                }
            }
            finally
            {
                _removingAccounts.Remove(accountKey);
            }
        }

        return historyKept ? RemoveAccountResult.RemovedHistoryKept : RemoveAccountResult.Removed;
    }

    /// <summary>Whether another account (or a built-in provider id) cleans down to the same sign-in
    /// folder and history file name as <paramref name="accountKey"/>, compared without case because
    /// Windows paths are case-insensitive: a hand-edited key such as "Codex" or "claude#a.b" would
    /// otherwise delete a different account's files.</summary>
    private bool SharesFolderNameWithAnotherAccount(string accountKey)
    {
        var cleaned = AppPaths.SanitizeAccountKeyForFileName(accountKey);
        return _settings.Accounts.Keys.Concat(AppSettings.KnownProviderIds).Any(other =>
            !string.Equals(other, accountKey, StringComparison.Ordinal)
            && string.Equals(AppPaths.SanitizeAccountKeyForFileName(other), cleaned, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What the Settings window's remove button calls from its event handler: an unexpected
    /// failure is logged and reported as <see cref="RemoveAccountResult.Failed"/> instead of escaping an
    /// async event handler and ending the app.</summary>
    internal async Task<RemoveAccountResult> TryRemoveAccountAsync(string accountKey, bool alsoHistory = false)
    {
        try
        {
            return await RemoveAccountAsync(accountKey, alsoHistory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logService?.LogError($"Removing {accountKey} failed: {ex.GetType().Name}: {PathSanitizer.Sanitize(ex.Message)}");
            return RemoveAccountResult.Failed;
        }
    }

    /// <summary>Deletes one account's browser session folder through <see cref="SignOutWebView"/>.
    /// Up to four further attempts over roughly a second and a half, only while a file inside the
    /// folder is still open: a refusal repeats itself however often it is asked, and a folder already
    /// gone has nothing left to delete.</summary>
    private async Task<SignOutResult> DeleteWebProfileAsync(string accountKey)
    {
        var result = SignOutWebView(accountKey);
        for (var attempt = 0; result == SignOutResult.StillOpen && attempt < 4; attempt++)
        {
            await Task.Delay(SignOutRetryDelay);
            result = SignOutWebView(accountKey);
        }

        return result;
    }

    /// <summary>What the sign-out buttons call from their event handlers: an unexpected failure (a
    /// browser process that will not shut down cleanly) is logged and reported as an incomplete
    /// sign-out instead of escaping an async event handler and ending the app.</summary>
    internal async Task<bool> TrySignOutAsync(string accountKey)
    {
        try
        {
            return await SignOutAsync(accountKey);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logService?.LogError($"Sign-out failed for {accountKey}: {ex.GetType().Name}: {PathSanitizer.Sanitize(ex.Message)}");
            return false;
        }
    }

    /// <summary>
    /// Tears down the live browser session before deleting its profile folder, instead of deleting
    /// underneath a runner that still has it open - the disposal must finish first, or the delete
    /// usually only partially clears the folder and the tile keeps showing numbers through the
    /// still-live session. Returns false when the folder still could not be fully cleared, so the
    /// caller can tell the user rather than claim success silently.
    ///
    /// Rebuilds the provider and its runner in the same step (same as <see cref="AddAccount"/>
    /// does for a brand new one), rather than merely removing the old runner: the old runner is
    /// permanently disabled once disposed (see <see cref="WebSessionScriptRunner"/>) so leaving the
    /// provider's already-captured delegate pointed at it would make the tile stay "not signed in"
    /// forever even after a fresh, successful sign-in on the same tile.
    /// </summary>
    internal async Task<bool> SignOutAsync(string accountKey)
    {
        // An account that is gone or on its way out must not get a runner, a provider or a profile
        // folder back from a sign-out that was started before the removal.
        if (!IsLiveAccount(accountKey) || !_signingOutAccounts.Add(accountKey))
            return false;

        try
        {
            return await SignOutCoreAsync(accountKey);
        }
        finally
        {
            _signingOutAccounts.Remove(accountKey);
        }
    }

    private async Task<bool> SignOutCoreAsync(string accountKey)
    {
        // The profile folder SignOutWebView is about to delete must not still be read by a fetch
        // that is currently in flight for this account - cancel it and wait for RunOneAsync to
        // actually finish before touching the folder.
        await _scheduler.CancelProvider(accountKey);

        var hadWebSession = _webRunners.Remove(accountKey, out var runner);
        SignOutResult result;
        try
        {
            if (hadWebSession)
                await runner!.DisposeAsync();

            result = await DeleteWebProfileAsync(accountKey);
        }
        finally
        {
            // Only a web-backed account gets a fresh provider, and it gets one even when the teardown
            // above failed halfway: its old runner is gone either way, and a provider left on it
            // could never read again. An account that reads another tool's sign-in (Copilot, the
            // primary Claude account) keeps its own provider: rebuilding it turned it into a
            // web-only Claude account until the next start.
            // Unless a removal overtook this sign-out meanwhile: that account must stay gone.
            if (hadWebSession && IsLiveAccount(accountKey))
            {
                _scheduler.RemoveProvider(accountKey);
                var (freshProvider, freshRunner) = CreateWebAccount(accountKey);
                _webRunners[accountKey] = freshRunner;
                _scheduler.AddProvider(freshProvider);
            }
        }

        if (!IsLiveAccount(accountKey))
            return false;

        var signedOut = result == SignOutResult.Removed;

        _settings.WebUsagePaths.Remove(accountKey);

        // The one line that actually stops this account's reads (see IsAccountDisconnected): every
        // source, not only the web session this method already tore down above - a local file or a
        // CLI call would otherwise keep being read for a provider that has no web session at all.
        GetOrCreateProviderSettings(accountKey).Disconnected = true;
        _settingsStore.RequestSave(_settings);

        if (_tilesById.TryGetValue(accountKey, out var tile))
        {
            var now = DateTimeOffset.Now;
            tile.Apply(new ProviderSnapshot(accountKey, [], null, SourceKind.None, now, null, ProviderStatus.NotSignedIn, null), now);
        }

        return signedOut;
    }

    /// <summary>The Settings.SignIn action: the opposite of the flag <see cref="SignOutAsync"/> sets - resumes every
    /// read for this account (web session, local file, or CLI, whichever it actually uses) and
    /// refreshes it right away rather than waiting for the next scheduled tick. A web-backed provider
    /// still needs its own <see cref="Views.WebSignInFlow"/> afterward when its session was deleted
    /// on sign-out; a local-login or CLI-backed one needs nothing further at all.</summary>
    public void Reconnect(string accountKey)
    {
        if (!IsLiveAccount(accountKey))
            return;

        GetOrCreateProviderSettings(accountKey).Disconnected = false;
        _settingsStore.RequestSave(_settings);
        _scheduler.RefreshNow(accountKey, _lifetimeCts.Token);
    }

    /// <summary>What the sign-in window's close does for the account it was opened for, the one place
    /// both entry points (a tile's button, the settings button) call. Only a sign-in that finished
    /// resumes the account's reads: until then a signed-out account stays unread, so no local source
    /// can put numbers or a chart on a tile whose sign-in the user is still in the middle of. A
    /// window closed part-way changes nothing for that account. Every account refreshes afterwards
    /// either way, as before.</summary>
    public void CompleteSignIn(string accountKey, bool signedIn)
    {
        if (!IsLiveAccount(accountKey))
            return;

        if (signedIn)
        {
            // Marked first: the provider must know a sign-in finished before the read that
            // Reconnect starts, or that read would still be throttled to the old answer.
            MarkSignedIn(accountKey);
            Reconnect(accountKey);
        }

        RefreshNow();
    }
}
