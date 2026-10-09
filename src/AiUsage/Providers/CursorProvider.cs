using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Providers;

/// <summary>
/// Reads Cursor's usage through the app's own hidden, signed-in browser session (see
/// <see cref="WebUsageSource"/> and <see cref="CursorDiscoveryScript"/>) - Cursor keeps its quota on
/// its own servers, so there is no local file to read instead. One source only, so there is nothing
/// for <see cref="Services.SnapshotChooser"/> to pick between.
/// </summary>
public sealed class CursorProvider : IUsageProvider
{
    private readonly Func<CancellationToken, Task<WebUsageResult>> _read;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _accountKey;

    /// <summary><paramref name="accountKey"/> is this instance's own account (see
    /// <see cref="AccountKey"/>) - a further account of this provider gets its own instance, built
    /// the same way, from <see cref="Services.ProviderRegistry"/>.</summary>
    public CursorProvider(string accountKey, WebUsageSource webSource, AppSettings settings, Action<AppSettings> saveSettings)
        : this(ct => webSource.FetchAsync(settings, saveSettings, ct), now: null, accountKey)
    {
    }

    /// <summary>Throwaway instance built only to read <see cref="DisplayName"/> - the same pattern
    /// <c>ClaudeProvider()</c> and <c>CodexProvider()</c> already offer for the same reason (see
    /// <see cref="Stats.StatsViewModel"/>'s own display-name lookup). Cursor has no local read to
    /// fall back to the way those two do, so this instance's read delegate is never meant to be
    /// invoked - it throws instead of silently returning an empty result.</summary>
    public CursorProvider() : this(_ => throw new NotSupportedException("Throwaway instance never fetches."), now: null)
    {
    }

    /// <summary>Test seam: a canned <see cref="WebUsageResult"/> read and a fixed clock instead of a
    /// live <see cref="WebUsageSource"/>.</summary>
    internal CursorProvider(Func<CancellationToken, Task<WebUsageResult>> read, Func<DateTimeOffset>? now, string? accountKey = null)
    {
        _read = read;
        _now = now ?? (() => DateTimeOffset.Now);
        _accountKey = accountKey ?? Id;
    }

    public string Id => "cursor";

    /// <summary>This provider's own id for the primary account, or that id plus a "#2"/"#3"/...
    /// suffix for a further one - see <see cref="Services.IUsageProvider.AccountKey"/>.</summary>
    public string AccountKey => _accountKey;

    public string DisplayName => "Cursor";

    // This provider reads its numbers through a web session the app owns (see Web/WebViewHost.cs),
    // so the app can offer sign-in/sign-out for it directly, the same as the other web-backed
    // providers. A further account is another one of the app's own web sessions, its own profile
    // folder (see Services.ProviderRegistry.WebSessionFor) - Cursor has no local file at all, so
    // unlike Codex/Gemini a further account loses nothing a primary one had.
    public bool SupportsInAppSignIn => true;

    public bool SupportsMultipleAccounts => true;

    // The web read (WebUsageSource.FetchAsync) already marshals its own work onto the WebView2 UI
    // thread it owns and expects to be started from the calling thread itself - the scheduler must
    // not front-run that by starting this provider on the thread pool instead.
    public bool RunsOnUiThread => true;

    public TimeSpan? MinRefreshInterval => ProviderRegistry.RemoteReadFloor;

    public IReadOnlyList<string> ReadLocations => [LocalizationService.Instance["About.ReadLocationWebSession"]];

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var fetchedAt = _now();

        WebUsageResult web;
        try
        {
            web = await _read(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Sign-out or exit cut the read short: that is no offline error to show.
            throw;
        }
        catch (Exception)
        {
            // The read never throws in production (WebUsageSource.FetchAsync catches everything
            // itself), but the test seam above can - this provider's own contract still never does.
            web = WebUsageResult.Failed;
        }

        return BuildSnapshot(web, fetchedAt);
    }

    private ProviderSnapshot BuildSnapshot(WebUsageResult web, DateTimeOffset fetchedAt) => web.Outcome switch
    {
        WebUsageOutcome.Ok when web.Windows.Count > 0 => ProviderSnapshots.FromWeb(AccountKey, web, fetchedAt, web.PlanType),
        // A response that parsed but carried no usable window reads as a failure, not a silent zero -
        // and names what actually happened, rather than the bare, indistinguishable-from-a-dead-
        // connection message a null error leaves on the tile.
        WebUsageOutcome.Ok => Empty(fetchedAt, ProviderStatus.Failed,
            new ProviderError("Status_UnreadableAnswer_Reason", "Action_Retry")),
        WebUsageOutcome.NotSignedIn => Empty(fetchedAt, ProviderStatus.NotSignedIn, error: null),
        WebUsageOutcome.Blocked => Empty(fetchedAt, ProviderStatus.Blocked, error: null),
        // The request never got out the door - say so, rather than a plain, less informative failure.
        _ => ProviderSnapshots.Unavailable(AccountKey, fetchedAt),
    };

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
