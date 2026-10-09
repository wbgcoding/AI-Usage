using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Web;

namespace AiUsage.Providers;

/// <summary>
/// The web half of a provider that also has a cheap local source: at most one browser read per
/// <see cref="ProviderRegistry.RemoteReadFloor"/> however often the tile ticks (in between, the last
/// snapshot is offered again), no browser at all while nothing was ever signed in, and a sign-in that
/// finishes mid-read still counts for the next one. Codex and Gemini read the web the same way and
/// differ only in the two switches of the constructor.
/// </summary>
internal sealed class ThrottledWebReader
{
    // After a discovery walk that found no usable address, the page is left alone this long: asking
    // every candidate again every few minutes would only repeat the same answers.
    internal static readonly TimeSpan FailedDiscoveryPause = TimeSpan.FromMinutes(30);

    private readonly string _accountKey;
    private readonly WebUsageSource _source;
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _saveSettings;
    private readonly bool _showsReportedPlan;
    private readonly bool _pausesAfterFailedDiscovery;

    private ProviderSnapshot? _lastSnapshot;
    private DateTimeOffset? _lastReadAt;
    private DateTimeOffset? _discoveryPausedUntil;
    private int _signInCompleted;

    /// <param name="showsReportedPlan">True when the snapshot carries the plan the read reported;
    /// false when the provider shows none from the web.</param>
    /// <param name="pausesAfterFailedDiscovery">True when an online discovery that found no usable
    /// address holds further reads for <see cref="FailedDiscoveryPause"/>.</param>
    public ThrottledWebReader(
        string accountKey, WebUsageSource source, AppSettings settings, Action<AppSettings> saveSettings,
        bool showsReportedPlan, bool pausesAfterFailedDiscovery)
    {
        _accountKey = accountKey;
        _source = source;
        _settings = settings;
        _saveSettings = saveSettings;
        _showsReportedPlan = showsReportedPlan;
        _pausesAfterFailedDiscovery = pausesAfterFailedDiscovery;
    }

    /// <summary>Null until a read has said whether the session is signed in.</summary>
    public bool? SessionSignedIn { get; private set; }

    /// <summary>The last account address a read reported, kept for the ticks that answer from
    /// elsewhere.</summary>
    public string? LastAccountLabel { get; private set; }

    /// <summary>The sign-in window finished: the next read goes out at once and is not held back by
    /// the throttle or a discovery pause.</summary>
    public void SignInCompleted() => Interlocked.Exchange(ref _signInCompleted, 1);

    public async Task<ProviderSnapshot?> ReadAsync(DateTimeOffset fetchedAt, CancellationToken ct)
    {
        // Nothing was ever signed in here: starting a browser session would cost a whole WebView2
        // process and a request to the provider's site for a user who may never want that route at
        // all. Reporting "signed out" without asking is both honest and free - it is exactly what the
        // tile needs to offer the sign-in, and the moment that sign-in happens the profile folder
        // exists and the read below starts.
        if (!Directory.Exists(WebViewHost.ProfileFolderPath(_source.Descriptor.ProfileFolderName)))
        {
            SessionSignedIn = false;
            return null;
        }

        // Taken here, before the read, so a sign-in finishing while a read is already running still
        // counts for the next one.
        var signInJustCompleted = Interlocked.Exchange(ref _signInCompleted, 0) == 1;
        if (signInJustCompleted)
            _discoveryPausedUntil = null;
        if (_discoveryPausedUntil is { } pausedUntil && fetchedAt < pausedUntil)
            return null;
        if (!signInJustCompleted && _lastReadAt is { } lastReadAt && fetchedAt - lastReadAt < ProviderRegistry.RemoteReadFloor)
            // A window that reset since that read is over, the same as for a session file.
            return _lastSnapshot is { } cached
                ? ProviderSnapshots.ExpirePastWindows(cached, fetchedAt) with { FetchedAt = fetchedAt, HeldOver = true }
                : null;

        WebUsageResult result;
        try
        {
            result = await _source.FetchAsync(_settings, _saveSettings, ct);
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

        _lastReadAt = fetchedAt;

        if (_pausesAfterFailedDiscovery)
        {
            // A walk that ended without a usable address while the machine is online: the next tries
            // wait. Offline says nothing about the page, so it never starts the pause.
            if (result.Outcome == WebUsageOutcome.Failed && !result.SessionSignedIn && NetworkStatus.HasInternet())
                _discoveryPausedUntil = fetchedAt + FailedDiscoveryPause;
            else if (result.Outcome != WebUsageOutcome.Failed)
                _discoveryPausedUntil = null;
        }

        if (result.AccountLabel is { } webLabel)
            LastAccountLabel = webLabel;

        SessionSignedIn = ProviderSnapshots.SessionSignedIn(result, SessionSignedIn);

        _lastSnapshot = result.Outcome == WebUsageOutcome.Ok && result.Windows.Count > 0
            ? ProviderSnapshots.FromWeb(_accountKey, result, fetchedAt, _showsReportedPlan ? result.PlanType : null)
            : null;

        return _lastSnapshot;
    }
}
