using AiUsage.Models;
using AiUsage.Providers.LocalLogin;
using AiUsage.Providers.Parsing;
using AiUsage.Services;
using AiUsage.Web;

namespace AiUsage.Providers;

/// <summary>
/// Reads Gemini/Antigravity usage through two sources: the sign-in the Antigravity CLI already
/// holds on this machine always (see <see cref="AntigravityLocalLogin"/>), and - once the app's own
/// browser session for aistudio.google.com is signed in - the account's own usage endpoint on top of
/// it, the same dual-source shape <see cref="CodexProvider"/> uses. Both sources go through the
/// shared chooser; they are never mixed into one window list. When no sign-in exists at all the tile
/// asks the user to sign in through Antigravity itself. Never throws.
/// </summary>
public sealed class GeminiProvider : IUsageProvider
{
    // One browser read per this span, however often the tile ticks: a page load in the hidden
    // session is the expensive part, not re-reading the CLI's own local sign-in.
    private static readonly TimeSpan WebReadInterval = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<AntigravityUsage>>? _read;
    private readonly Func<DateTimeOffset> _now;
    private readonly WebUsageSource? _webSource;
    private readonly AppSettings? _settings;
    private readonly Action<AppSettings>? _saveSettings;
    private readonly string _accountKey;
    private readonly Func<string?> _readAccountLabel;

    private ProviderSnapshot? _lastWebSnapshot;
    private DateTimeOffset? _lastWebReadAt;
    private int _signInCompleted;
    private bool? _webSessionSignedIn;
    private string? _lastWebAccountLabel;

    public GeminiProvider() : this(AntigravityLocalLogin.FetchAsync, now: null)
    {
    }

    /// <summary>Real construction once a web session exists: <paramref name="webSource"/> reads the
    /// account's own usage endpoint, <paramref name="settings"/>/<paramref name="saveSettings"/>
    /// carry the discovered path across ticks (see <see cref="AppSettings.WebUsagePaths"/>).</summary>
    public GeminiProvider(AppSettings settings, Action<AppSettings> saveSettings, WebUsageSource webSource)
        : this(AntigravityLocalLogin.FetchAsync, now: null, webSource, settings, saveSettings)
    {
    }

    /// <summary>A further Gemini account: a web session only, no Antigravity CLI read - the CLI's own
    /// local sign-in on this machine belongs to whichever account it is signed into, never to a
    /// further account added here (same reasoning as <see cref="ClaudeProvider"/>'s own web-only
    /// constructor).</summary>
    public static GeminiProvider ForWebOnlyAccount(
        string accountKey, AppSettings settings, Action<AppSettings> saveSettings, WebUsageSource webSource) =>
        new(fetch: null, now: null, webSource, settings, saveSettings, accountKey);

    /// <summary>Test seam: a canned usage result and a fixed clock instead of the real credential
    /// store and network. <paramref name="fetch"/> null means this account has no local Antigravity
    /// read at all (see <see cref="ForWebOnlyAccount"/>).</summary>
    internal GeminiProvider(
        Func<CancellationToken, Task<AntigravityUsage>>? fetch,
        Func<DateTimeOffset>? now,
        WebUsageSource? webSource = null,
        AppSettings? settings = null,
        Action<AppSettings>? saveSettings = null,
        string? accountKey = null,
        Func<string?>? readAccountLabel = null)
    {
        _read = fetch;
        _now = now ?? (() => DateTimeOffset.Now);
        _webSource = webSource;
        _settings = settings;
        _saveSettings = saveSettings;
        _accountKey = accountKey ?? Id;
        // Only the primary account owns the CLI's own sign-in, so only it may take that account's
        // address; a further, web-only account names itself through its own web read or not at all.
        _readAccountLabel = readAccountLabel ?? (fetch is not null ? GeminiAccountLabelReader.Read : () => null);
    }

    public string Id => "gemini";

    /// <summary>This provider's own id for the primary account (the one with the Antigravity CLI's
    /// own local sign-in), or that id plus a "#2"/"#3"/... suffix for a further, web-only one - see
    /// <see cref="Services.IUsageProvider.AccountKey"/>.</summary>
    public string AccountKey => _accountKey;

    public string DisplayName => "Gemini";

    // Sign-in through the app's own browser session becomes possible only once that session's
    // provider actually exists - a build without one still routes through the Antigravity CLI's own
    // login, outside the app, exactly as before.
    public bool SupportsInAppSignIn => _webSource is not null;

    /// <summary>A sign-in just finished: the cached answer from before it (usually "not signed in")
    /// must not stand for the rest of the throttle interval.</summary>
    public void SignInCompleted() => Interlocked.Exchange(ref _signInCompleted, 1);

    // The web read below marshals its own work onto the WebView2 thread it owns and expects to start
    // on the calling thread, so with a web session the fetch stays on the UI thread. The local read
    // (credential store, JSON parse, the binary scan behind an expired token) hops to the pool
    // itself.
    public bool RunsOnUiThread => _webSource is not null;

    // Two lightweight reads against a remote endpoint - hold to the same web-session floor Claude
    // uses rather than the local-file default, so it is not polled every tick.
    public TimeSpan? MinRefreshInterval => TimeSpan.FromMinutes(5);

    // Only the primary account (the one with the Antigravity CLI's own local sign-in) can hold a
    // further, web-only account - that further account can never itself sprout a third layer.
    public bool SupportsMultipleAccounts => _read is not null;

    public IReadOnlyList<string> ReadLocations => (_read is not null, _webSource is not null) switch
    {
        (true, false) => [LocalizationService.Instance["About.ReadLocationAntigravity"]],
        (true, true) => [LocalizationService.Instance["About.ReadLocationAntigravity"], LocalizationService.Instance["About.ReadLocationWebSession"]],
        _ => [LocalizationService.Instance["About.ReadLocationWebSession"]],
    };

    /// <summary>Both sources are offered to the shared chooser (available beats nothing, more windows
    /// beat fewer, newer beats older), never merged. Without a web session that is a single result -
    /// wrapping it still routes it through the same rule every other provider follows.</summary>
    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var fetchedAt = _now();

        // A further, web-only account (see ForWebOnlyAccount): no Antigravity CLI read to fall back
        // to at all, only the web read below ever answers for it.
        if (_read is null)
        {
            var webOnly = await ReadWebAsync(fetchedAt, ct);
            return webOnly is not null
                ? SnapshotChooser.Pick([webOnly]) with
                {
                    WebSessionSignedIn = _webSessionSignedIn,
                    AccountLabel = webOnly.AccountLabel ?? _lastWebAccountLabel,
                }
                : new ProviderSnapshot(
                    ProviderId: AccountKey, Windows: [], PlanType: null, SourceKind: SourceKind.None,
                    FetchedAt: fetchedAt, DataTimestamp: null,
                    Status: _webSessionSignedIn == false ? ProviderStatus.NotSignedIn : ProviderStatus.Failed,
                    Error: null, WebSessionSignedIn: _webSessionSignedIn);
        }

        var readLocal = _read;
        var usage = await Task.Run(() => readLocal(ct), ct);
        var local = BuildSnapshot(usage, fetchedAt);

        if (_webSource is null)
            return await WithAccountLabelAsync(SnapshotChooser.Pick([local]));

        var web = await ReadWebAsync(fetchedAt, ct);

        var candidates = new List<ProviderSnapshot> { local };
        if (web is not null)
            candidates.Add(web);

        var chosen = SnapshotChooser.Pick(candidates);

        // The sign-in state belongs to the browser session, not to whichever source happened to win:
        // the local sign-in answers perfectly well while the session is signed out, and the tile
        // still has to be able to offer the sign-in that makes the numbers live.
        return (await WithAccountLabelAsync(chosen)) with { WebSessionSignedIn = _webSessionSignedIn };
    }

    /// <summary>The account's address does not depend on which source won the numbers: whichever
    /// source knows it names the tile, the web session's own answer first, then the CLI's record of
    /// its active account.</summary>
    private async Task<ProviderSnapshot> WithAccountLabelAsync(ProviderSnapshot chosen)
    {
        var label = chosen.AccountLabel ?? _lastWebAccountLabel;
        // The CLI record is a file read: only taken when nothing else named the account, and off the UI thread.
        label ??= await Task.Run(_readAccountLabel);
        return chosen with { AccountLabel = label };
    }

    /// <summary>The web read, throttled to <see cref="WebReadInterval"/>: in between, the snapshot
    /// from the last real read is offered again, so the cheap local read can keep ticking at the
    /// scheduler's own pace without paying for a page load every time.</summary>
    private async Task<ProviderSnapshot?> ReadWebAsync(DateTimeOffset fetchedAt, CancellationToken ct)
    {
        if (_webSource is null || _settings is null || _saveSettings is null)
            return null;

        // Nothing was ever signed in here: starting a browser session would cost a whole WebView2
        // process and a request to the provider's site for a user who may never want that route at
        // all. Reporting "signed out" without asking is both honest and free - it is exactly what the
        // tile needs to offer the sign-in, and the moment that sign-in happens the profile folder
        // exists and the read below starts.
        if (!Directory.Exists(WebViewHost.ResolveUserDataFolder(_webSource.Descriptor.ProfileFolderName)))
        {
            _webSessionSignedIn = false;
            return null;
        }

        // Taken here, before the read, so a sign-in finishing while a read is already running still
        // counts for the next one.
        var signInJustCompleted = Interlocked.Exchange(ref _signInCompleted, 0) == 1;
        if (!signInJustCompleted && _lastWebReadAt is { } lastReadAt && fetchedAt - lastReadAt < WebReadInterval)
            return _lastWebSnapshot is { } cached
                ? ProviderSnapshots.ExpirePastWindows(cached, fetchedAt) with { FetchedAt = fetchedAt, HeldOver = true }
                : null;

        WebUsageResult result;
        try
        {
            result = await _webSource.FetchAsync(_settings, _saveSettings, ct);
        }
        catch (OperationCanceledException)
        {
            if (signInJustCompleted)
                Interlocked.Exchange(ref _signInCompleted, 1);
            // Cancelled mid-read (a refresh cut short, a sign-out, shutdown): the local snapshot this
            // tick already produced still stands, and the next tick may read the web again - the
            // throttle below is deliberately not advanced for a read that never finished.
            return null;
        }

        _lastWebReadAt = fetchedAt;
        if (result.AccountLabel is { } webLabel)
            _lastWebAccountLabel = webLabel;

        _webSessionSignedIn = ProviderSnapshots.SessionSignedIn(result, _webSessionSignedIn);

        _lastWebSnapshot = result.Outcome == WebUsageOutcome.Ok && result.Windows.Count > 0
            ? ProviderSnapshots.FromWeb(AccountKey, result, fetchedAt, planType: null)
            : null;

        return _lastWebSnapshot;
    }

    private ProviderSnapshot BuildSnapshot(AntigravityUsage usage, DateTimeOffset fetchedAt)
    {
        switch (usage.Outcome)
        {
            case LocalLoginOutcome.NotSignedIn:
                return Empty(fetchedAt, ProviderStatus.NotSignedIn, error: null);

            case LocalLoginOutcome.Ok:
                var windows = GeminiUsageParser.Parse(usage.QuotaSummaryJson);
                if (windows.Count == 0)
                    return Empty(fetchedAt, ProviderStatus.Failed, error: null);

                return new ProviderSnapshot(
                    ProviderId: AccountKey,
                    Windows: windows,
                    PlanType: usage.PlanName,
                    SourceKind: SourceKind.LocalLogin,
                    FetchedAt: fetchedAt,
                    DataTimestamp: fetchedAt,
                    Status: ProviderStatus.Ok,
                    Error: null);

            default:
                return ProviderSnapshots.Unavailable(AccountKey, fetchedAt);
        }
    }

    private ProviderSnapshot Empty(DateTimeOffset fetchedAt, ProviderStatus status, ProviderError? error) => new(
        ProviderId: AccountKey,
        Windows: [],
        PlanType: null,
        SourceKind: SourceKind.None,
        FetchedAt: fetchedAt,
        DataTimestamp: null,
        Status: status,
        Error: error);
}
