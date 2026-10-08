using System.Linq;
using AiUsage.Models;
using AiUsage.Providers.LocalLogin;
using AiUsage.Providers.Parsing;
using AiUsage.Services;

namespace AiUsage.Providers;

/// <summary>
/// Reads Claude's local session transcripts first - there is no running percentage locally, only
/// the last already-triggered rate limit's reset time, if it is still in the future, and that is
/// certain, so it always wins. Only when nothing local was found does the web fallback get asked;
/// the two are never mixed into one window list.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private readonly string _projectsRoot;
    private readonly Func<DateTimeOffset> _now;
    private readonly WebUsageSource? _webSource;
    private readonly AppSettings? _settings;
    private readonly Action<AppSettings>? _saveSettings;
    private readonly string _accountKey;
    private readonly Func<CancellationToken, Task<ClaudeCodeUsage>>? _localLogin;
    private readonly Func<TimeSpan> _attentionMaxAge;
    private readonly Func<string?> _readAccountLabel;
    private readonly ClaudeSessionTokenReader _tokenReader = new();

    // The last good read through the local Claude Code sign-in. Its token expires every few hours and
    // only Claude Code itself renews it, on its next start; until then this answers with the last
    // numbers and their real age instead of asking to sign in to a sign-in that is not gone.
    private ProviderSnapshot? _lastLocalLogin;

    public ClaudeProvider() : this(DefaultProjectsRoot(), now: null)
    {
    }

    /// <summary>The primary account reads its usage through the sign-in Claude Code already holds on
    /// this machine (see <see cref="ClaudeCodeLogin"/>) rather than the app's own web session -
    /// <paramref name="localLogin"/> is that read. The local rate-limit scan below still runs first,
    /// so a hit rate limit shows even when signed out. <paramref name="settings"/> is optional so
    /// every existing local-login test seam keeps compiling unchanged; production passes it so
    /// <see cref="MinRefreshInterval"/> can honour the settings floor for this read too (see
    /// <see cref="Services.ProviderRegistry.CreateAll"/>).</summary>
    internal ClaudeProvider(
        string accountKey, Func<CancellationToken, Task<ClaudeCodeUsage>> localLogin, AppSettings? settings = null,
        Func<TimeSpan>? attentionMaxAge = null, Func<string?>? readAccountLabel = null)
        : this(DefaultProjectsRoot(), now: null, webSource: null, settings, saveSettings: null, accountKey, localLogin, attentionMaxAge, readAccountLabel)
    {
    }

    /// <summary>Real construction once a web source exists: <paramref name="webSource"/> is asked
    /// only when nothing local answers, <paramref name="settings"/>/<paramref name="saveSettings"/>
    /// carry the cached usage endpoint (AppSettings.WebUsagePaths) across ticks. <paramref
    /// name="accountKey"/> is this instance's own account (see <see cref="AccountKey"/>) - a further
    /// account of this same provider gets its own <see cref="ClaudeProvider"/> instance, built the
    /// same way, from <see cref="Services.ProviderRegistry"/>.</summary>
    public ClaudeProvider(
        string accountKey, AppSettings settings, Action<AppSettings> saveSettings, WebUsageSource webSource,
        Func<TimeSpan>? attentionMaxAge = null)
        // No projects root: Claude Code's local transcripts belong to the account Claude Code is signed
        // in with, never to a further web account, so a limit found there must not show on this tile.
        : this(projectsRoot: "", now: null, webSource, settings, saveSettings, accountKey, localLogin: null, attentionMaxAge)
    {
    }

    /// <summary>Test seam: a fixture directory and a fixed clock instead of ~/.claude/projects and
    /// DateTimeOffset.Now - defaults to the one account every install starts with (see <see
    /// cref="AppSettings.CreateDefaultAccounts"/>), same as every existing caller that predates the
    /// account-key concept and so never names one.</summary>
    internal ClaudeProvider(
        string projectsRoot, Func<DateTimeOffset>? now,
        WebUsageSource? webSource = null, AppSettings? settings = null, Action<AppSettings>? saveSettings = null,
        Func<CancellationToken, Task<ClaudeCodeUsage>>? localLogin = null, Func<TimeSpan>? attentionMaxAge = null,
        Func<string?>? readAccountLabel = null)
        : this(projectsRoot, now, webSource, settings, saveSettings, AppSettings.CreateDefaultAccounts().Keys.Single(), localLogin, attentionMaxAge, readAccountLabel)
    {
    }

    /// <summary><paramref name="attentionMaxAge"/> is a delegate rather than a snapshot value so a
    /// change to <see cref="AppSettings.AttentionMaxAgeMinutes"/> takes effect on this provider's very
    /// next read instead of only after a restart; it defaults to a fixed <see
    /// cref="AttentionDetector.DefaultMaxAge"/> (today's two hours) so every existing construction
    /// keeps compiling unchanged. <see cref="Services.ProviderRegistry"/> is the one caller that ever
    /// passes a delegate reading the user's own configured value.</summary>
    private ClaudeProvider(
        string projectsRoot, Func<DateTimeOffset>? now,
        WebUsageSource? webSource, AppSettings? settings, Action<AppSettings>? saveSettings, string accountKey,
        Func<CancellationToken, Task<ClaudeCodeUsage>>? localLogin = null, Func<TimeSpan>? attentionMaxAge = null,
        Func<string?>? readAccountLabel = null)
    {
        _projectsRoot = projectsRoot;
        _now = now ?? (() => DateTimeOffset.Now);
        _webSource = webSource;
        _settings = settings;
        _saveSettings = saveSettings;
        _accountKey = accountKey;
        _localLogin = localLogin;
        _attentionMaxAge = attentionMaxAge ?? (() => AttentionDetector.DefaultMaxAge);
        // Only meaningful for the local Claude Code sign-in (see _localLogin) - a further, web-only
        // account has no ~/.claude.json of its own to read, so this is never invoked for one.
        _readAccountLabel = readAccountLabel ?? ClaudeAccountLabelReader.Read;
    }

    public string Id => "claude";

    /// <summary>This provider's own id for a first/only account, or that id plus a "#2"/"#3"/...
    /// suffix for a further one - see <see cref="Services.IUsageProvider.AccountKey"/>. Everything
    /// this instance reports a snapshot under (<see cref="FetchAsync"/>) uses this, never <see
    /// cref="Id"/>, so two accounts' numbers never land on the same tile or the same history
    /// file.</summary>
    public string AccountKey => _accountKey;

    public string DisplayName => "Claude";

    // Only a further account reads through a web session the app owns (see Web/WebViewHost.cs). The
    // primary account reads the sign-in Claude Code already holds, so a browser sign-in into a
    // profile nothing reads would only pretend to fix it; it can still be disconnected and resumed.
    // Whatever this says, the app never starts a provider program and never touches foreign
    // credentials.
    public bool SupportsInAppSignIn => _webSource is not null;

    public bool SupportsSignOut => _webSource is not null || _localLogin is not null;

    // Claude is the one provider that can hold more than one account (a further web session).
    public bool SupportsMultipleAccounts => true;

    // The web fallback below (WebUsageSource.FetchAsync) already marshals its own work onto the
    // WebView2 UI thread it owns and expects to be started from the calling thread itself - the
    // scheduler must not front-run that by starting this provider on the thread pool instead. The
    // synchronous local scan that runs before it (see FetchAsync) wraps itself in Task.Run instead, so
    // that part still moves off the caller's thread even though the provider as a whole opts out.
    public bool RunsOnUiThread => true;

    /// <summary>Settings-driven (<see cref="AppSettings.RemoteRefreshMinutes"/>, default 5 min) rather
    /// than hardcoded, so <see cref="RefreshScheduler"/> picks up a change without ever knowing this
    /// provider has a web path at all. The web session still holds a five-minute floor underneath the
    /// settings value: one browser page load costs more than the local-login path's plain request, so
    /// it never goes below what the other remote providers use. The local-login path pays only that
    /// plain request, so the settings value applies alone, down to <see
    /// cref="SettingsRanges.MinRemoteRefreshMinutes"/>. No settings at all (the local-only test seam,
    /// which reaches neither remote path) falls back to five minutes when a local login exists, or the
    /// scheduler's per-tick default (null) otherwise.</summary>
    public TimeSpan? MinRefreshInterval
    {
        get
        {
            if (_settings is null)
                return _localLogin is not null ? TimeSpan.FromMinutes(5) : null;

            var settingsFloor = TimeSpan.FromMinutes(SettingsRanges.ClampRemoteRefreshMinutes(_settings.RemoteRefreshMinutes));
            if (_webSource is null)
                return settingsFloor;

            var webFloor = TimeSpan.FromMinutes(5);
            return settingsFloor > webFloor ? settingsFloor : webFloor;
        }
    }

    public IReadOnlyList<string> ReadLocations => _projectsRoot.Length == 0
        ? [LocalizationService.Instance["About.ReadLocationWebSession"]]
        :
        [
            DisplayPath(_projectsRoot, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            _localLogin is null
                ? LocalizationService.Instance["About.ReadLocationWebSession"]
                : LocalizationService.Instance["About.ReadLocationClaudeCode"],
        ];

    /// <summary>A folder as the About page shows it: "~/..." under the user profile, otherwise the
    /// full path with the account name stripped, so no user name reaches the screen.</summary>
    internal static string DisplayPath(string path, string userProfile)
    {
        var home = userProfile.TrimEnd('\\', '/');
        if (home.Length > 0 && path.Length > home.Length
            && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            && path[home.Length] is '\\' or '/')
            return "~/" + path[(home.Length + 1)..].Replace('\\', '/');

        return PathSanitizer.Sanitize(path, userProfile);
    }

    private static string DefaultProjectsRoot() => ClaudeConfigRoot.ProjectsRoot(ClaudeConfigRoot.Candidates());

    /// <summary>Same as the default, from a given variable value and profile folder.</summary>
    internal static string ProjectsRootFor(string? configDirValue, string userProfile) =>
        ClaudeConfigRoot.ProjectsRoot(ClaudeConfigRoot.Candidates(configDirValue, userProfile));

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var fetchedAt = _now();

        ClaudeQuotaLimit? limit = null;
        var isWaitingForUser = false;
        DateTimeOffset? waitingSince = null;
        long? sessionTokens = null;
        DateTimeOffset? sessionLastEventAt = null;
        try
        {
            if (_projectsRoot.Length > 0)
            // This provider itself stays off the scheduler's thread-pool wrap (see RunsOnUiThread) so
            // the web fallback below keeps starting on the calling (UI) thread it needs - but that
            // means this synchronous directory walk would otherwise run right there instead. Moving
            // just this call (and the attention read and token sum below, on the same file) to the
            // thread pool keeps the UI thread free without touching the web path.
            (limit, isWaitingForUser, waitingSince, sessionTokens, sessionLastEventAt) = await Task.Run(() =>
            {
                var found = ClaudeLocalLimitReader.FindActiveLimit(_projectsRoot, fetchedAt, out var newestFile, ct);
                var since = newestFile is not null ? WaitingSinceByNewestTurn(newestFile, fetchedAt, _attentionMaxAge()) : null;
                var tokens = newestFile is not null ? _tokenReader.SumTokens(newestFile) : (long?)null;
                return (found, since is not null, since, tokens, newestFile is not null ? _tokenReader.LastEventAt : null);
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            limit = null;
            isWaitingForUser = false;
            waitingSince = null;
            sessionTokens = null;
            sessionLastEventAt = null;
        }

        if (limit is not null)
        {
            var window = new UsageWindow(
                label: "Window_LimitReached",
                kind: MapWindowKind(limit.RateLimitType),
                usedPercent: 100,
                resetsAt: limit.ResetsAt,
                windowMinutes: null,
                tokens: sessionTokens is { } total ? new TokenUsage(total) : null);

            var limitLabel = _localLogin is not null ? await Task.Run(_readAccountLabel, ct) : null;
            return new ProviderSnapshot(
                ProviderId: AccountKey,
                Windows: [window],
                PlanType: null,
                SourceKind: SourceKind.LocalFile,
                FetchedAt: fetchedAt,
                DataTimestamp: fetchedAt,
                Status: ProviderStatus.Ok,
                Error: null,
                AccountLabel: limitLabel,
                IsWaitingForUser: isWaitingForUser,
                WaitingSince: waitingSince);
        }

        // Every configured source builds its own candidate; SnapshotChooser picks between them
        // instead of the old first-source-wins order, so a signed-out local login no longer hides a
        // perfectly usable web session underneath it.
        var candidates = new List<ProviderSnapshot>();
        if (_localLogin is not null)
            candidates.Add(await BuildLocalLoginSnapshot(fetchedAt, ct));

        var localCandidate = candidates.Count > 0 ? candidates[0] : null;

        // Cheap before expensive: the web session is only worth a round trip when the local
        // candidate cannot win on its own - unusable, or old enough that a fresher number matters.
        var localIsFreshEnough = localCandidate is { Status: ProviderStatus.Ok, Windows.Count: > 0 }
            && fetchedAt - (localCandidate.DataTimestamp ?? DateTimeOffset.MinValue)
                < TimeSpan.FromMinutes(SettingsRanges.MinRemoteRefreshMinutes);

        if (_webSource is not null && _settings is not null && _saveSettings is not null && !localIsFreshEnough)
        {
            var web = await BuildWebSnapshot(fetchedAt, ct);
            if (web is not null)
                candidates.Add(web);
        }

        if (candidates.Count > 0)
        {
            var chosen = SnapshotChooser.Pick(candidates) with { IsWaitingForUser = isWaitingForUser, WaitingSince = waitingSince };
            return AttachSessionTokens(chosen, sessionTokens, sessionLastEventAt, fetchedAt);
        }

        var diagnostics = new ProviderDiagnostics();
        if (_projectsRoot.Length > 0)
        {
            diagnostics.SearchedIn(_projectsRoot);
            if (!Directory.Exists(_projectsRoot))
                diagnostics.DirectoryMissing();
            else
                diagnostics.NothingFound();
        }

        return new ProviderSnapshot(
            ProviderId: AccountKey,
            Windows: [],
            PlanType: null,
            SourceKind: SourceKind.None,
            FetchedAt: fetchedAt,
            DataTimestamp: null,
            Status: ProviderStatus.NoLocalData,
            Error: null,
            Diagnostics: diagnostics.Lines,
            IsWaitingForUser: isWaitingForUser,
            WaitingSince: waitingSince);
    }

    /// <summary>The local sign-in read has no token figure of its own; the newest session file does. The
    /// sum goes onto the five-hour window of a local-sign-in snapshot, but only while that session spoke
    /// inside the window - a file last written hours ago is not the session the percentage describes.
    /// A web snapshot never borrows it: its numbers can belong to another machine.</summary>
    private static ProviderSnapshot AttachSessionTokens(
        ProviderSnapshot chosen, long? sessionTokens, DateTimeOffset? lastEventAt, DateTimeOffset fetchedAt)
    {
        if (chosen.SourceKind != SourceKind.LocalLogin || sessionTokens is not > 0 || lastEventAt is not { } last)
            return chosen;

        var index = -1;
        for (var i = 0; i < chosen.Windows.Count; i++)
        {
            if (chosen.Windows[i].Kind == WindowKind.FiveHour)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            return chosen;

        var window = chosen.Windows[index];
        if (last < fetchedAt - TimeSpan.FromMinutes(window.WindowMinutes ?? 300))
            return chosen;

        var windows = chosen.Windows.ToList();
        windows[index] = new UsageWindow(
            window.Label, window.Kind, window.UsedPercent, window.ResetsAt, window.WindowMinutes,
            new TokenUsage(sessionTokens.Value), window.Allowance);
        return chosen with { Windows = windows };
    }

    /// <summary>Reads the newest classifiable turn out of <paramref name="sessionFile"/> - the same
    /// local transcript the rate-limit scan already opened - and asks <see cref="AttentionDetector"/>
    /// whether that leaves the agent waiting for the user right now. Runs on the same background hop
    /// as the rate-limit scan (see <see cref="FetchAsync"/>), never a second directory walk or a
    /// second thread-pool trip. Returns the waiting turn's own timestamp when it does, null
    /// otherwise - the caller derives the plain bool from whether this is null.</summary>
    private static DateTimeOffset? WaitingSinceByNewestTurn(string sessionFile, DateTimeOffset fetchedAt, TimeSpan attentionMaxAge)
    {
        var (kind, timestamp) = SessionTurnReader.ReadNewestClaudeTurn(sessionFile);
        return AttentionDetector.IsWaiting(kind, timestamp, fetchedAt, attentionMaxAge) ? timestamp : null;
    }

    private async Task<ProviderSnapshot> BuildLocalLoginSnapshot(DateTimeOffset fetchedAt, CancellationToken ct)
    {
        // The credentials read and the account record parse are file work: both go to the pool, only
        // the finished result comes back to the calling thread.
        var login = _localLogin!;
        var usage = await Task.Run(() => login(ct), ct);
        var accountLabel = usage.Outcome == LocalLoginOutcome.Ok ? await Task.Run(_readAccountLabel, ct) : null;
        if (usage.Outcome == LocalLoginOutcome.NotSignedIn && usage.Reason == ClaudeCodeLoginReason.TokenExpired
            && _lastLocalLogin is { } last)
            return HoldOverExpiredToken(last, fetchedAt);

        var snapshot = usage.Outcome switch
        {
            LocalLoginOutcome.Ok => new ProviderSnapshot(
                ProviderId: AccountKey,
                Windows: usage.Windows,
                PlanType: usage.PlanType,
                SourceKind: SourceKind.LocalLogin,
                FetchedAt: fetchedAt,
                DataTimestamp: fetchedAt,
                Status: ProviderStatus.Ok,
                Error: null,
                AccountLabel: accountLabel,
                SkipReasonWord: null),
            LocalLoginOutcome.NotSignedIn => new ProviderSnapshot(
                ProviderId: AccountKey, Windows: [], PlanType: null, SourceKind: SourceKind.None,
                FetchedAt: fetchedAt, DataTimestamp: null, Status: ProviderStatus.NotSignedIn,
                Error: new ProviderError(usage.Reason == ClaudeCodeLoginReason.TokenExpired
                    ? "State.NotSignedIn.ClaudeExpired"
                    : "State.NotSignedIn.ClaudeCode"),
                SkipReasonWord: ReasonWord(usage.Reason)),
            _ => ProviderSnapshots.Unavailable(AccountKey, fetchedAt, ReasonWord(usage.Reason)),
        };

        // A failed request keeps the last numbers for the next expiry; a sign-in that is really gone
        // (no file, no token) drops them.
        if (snapshot.Status == ProviderStatus.Ok)
            _lastLocalLogin = snapshot;
        else if (snapshot.Status == ProviderStatus.NotSignedIn)
            _lastLocalLogin = null;
        return snapshot;
    }

    /// <summary>The last good local read standing in while Claude Code's token is expired: windows
    /// that reset since then start over at 0 %, and the data keeps its own timestamp, so the tile
    /// shows how old the numbers are and turns stale after <see cref="ProviderFreshness.StaleAfter"/>.</summary>
    private static ProviderSnapshot HoldOverExpiredToken(ProviderSnapshot last, DateTimeOffset fetchedAt)
    {
        var dataTimestamp = last.DataTimestamp ?? last.FetchedAt;
        return ProviderSnapshots.ExpirePastWindows(last, fetchedAt) with
        {
            FetchedAt = fetchedAt,
            DataTimestamp = dataTimestamp,
            Status = ProviderFreshness.IsStale(dataTimestamp, [], fetchedAt) ? ProviderStatus.Stale : ProviderStatus.Ok,
            SkipReasonWord = ReasonWord(ClaudeCodeLoginReason.TokenExpired),
            HeldOver = true,
        };
    }

    /// <summary>Null only when the web call failed while online with nothing else to say - the
    /// caller's own "nothing found" fallback already covers that honestly, so this does not compete
    /// as a second, less informative candidate.</summary>
    private async Task<ProviderSnapshot?> BuildWebSnapshot(DateTimeOffset fetchedAt, CancellationToken ct)
    {
        var web = await _webSource!.FetchAsync(_settings!, _saveSettings!, ct);
        return web.Outcome switch
        {
            WebUsageOutcome.Ok => ProviderSnapshots.FromWeb(AccountKey, web, fetchedAt, web.PlanType),
            WebUsageOutcome.NotSignedIn => new ProviderSnapshot(
                ProviderId: AccountKey, Windows: [], PlanType: null, SourceKind: SourceKind.None,
                FetchedAt: fetchedAt, DataTimestamp: null, Status: ProviderStatus.NotSignedIn,
                Error: new ProviderError("State.NotSignedIn.WebSession")),
            WebUsageOutcome.Blocked => new ProviderSnapshot(
                ProviderId: AccountKey, Windows: [], PlanType: null, SourceKind: SourceKind.None,
                FetchedAt: fetchedAt, DataTimestamp: null, Status: ProviderStatus.Blocked, Error: null),
            // The request never got out the door - say so, rather than the "nothing found locally"
            // text, which would point the user at the wrong place to look.
            // Failed while online: no candidate at all, same as no web source being configured.
            WebUsageOutcome.Failed => ProviderSnapshots.OfflineOrNull(AccountKey, fetchedAt),
            _ => null,
        };
    }

    // The same stable word for both the About window's diagnostic line and the one log line raised
    // over this event - never localised, never a token, never a path.
    private static string? ReasonWord(ClaudeCodeLoginReason reason) => reason switch
    {
        ClaudeCodeLoginReason.MissingFile => "missing_file",
        ClaudeCodeLoginReason.NoToken => "no_token",
        ClaudeCodeLoginReason.TokenExpired => "token_expired",
        ClaudeCodeLoginReason.NonOkResponse => "non_ok_response",
        _ => null,
    };

    private static WindowKind MapWindowKind(string rateLimitType) => rateLimitType switch
    {
        "five_hour" => WindowKind.FiveHour,
        "seven_day" or "weekly" => WindowKind.Weekly,
        _ => WindowKind.Other,
    };
}
