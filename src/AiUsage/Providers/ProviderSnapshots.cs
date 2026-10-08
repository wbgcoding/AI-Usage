using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Providers;

/// <summary>
/// The snapshot shapes more than one provider builds the same way: the answer of a web session
/// read, the "this route did not answer" stand-in that tells an offline machine from a failing
/// source, and the signed-in state a web read implies.
/// </summary>
internal static class ProviderSnapshots
{
    private static ProviderError OfflineError => new("Status_Offline_Reason", "Action_Retry");

    /// <summary>A failed read with nothing to show. Offline, the tile says so with a retry action;
    /// online it stays silent about the cause, because the failure could be anything.</summary>
    internal static ProviderSnapshot Unavailable(string accountKey, DateTimeOffset fetchedAt, string? skipReasonWord = null) => new(
        ProviderId: accountKey,
        Windows: [],
        PlanType: null,
        SourceKind: SourceKind.None,
        FetchedAt: fetchedAt,
        DataTimestamp: null,
        Status: ProviderStatus.Failed,
        Error: NetworkStatus.HasInternet() ? null : OfflineError,
        SkipReasonWord: skipReasonWord);

    /// <summary>The offline stand-in alone: null while the machine is online, so a caller whose
    /// failure case has nothing better to say can fall through to its own fallback.</summary>
    internal static ProviderSnapshot? OfflineOrNull(string accountKey, DateTimeOffset fetchedAt) =>
        NetworkStatus.HasInternet() ? null : Unavailable(accountKey, fetchedAt);

    /// <summary>A window whose reset instant has already gone by is over: it stands at 0 % with no
    /// known reset yet. Used where a provider shows numbers it read earlier, so an old percentage
    /// never keeps a window full for hours after it reset or fires threshold notices for it.</summary>
    internal static ProviderSnapshot ExpirePastWindows(ProviderSnapshot snapshot, DateTimeOffset now)
    {
        if (!snapshot.Windows.Any(window => window.ResetsAt is { } reset && reset <= now))
            return snapshot;

        return snapshot with
        {
            Windows = snapshot.Windows
                .Select(window => window.ResetsAt is { } reset && reset <= now
                    ? new UsageWindow(window.Label, window.Kind, 0, null, window.WindowMinutes, window.Tokens, window.Allowance)
                    : window)
                .ToList(),
        };
    }

    /// <summary>The numbers of a successful web read as one candidate snapshot.</summary>
    internal static ProviderSnapshot FromWeb(
        string accountKey, WebUsageResult web, DateTimeOffset fetchedAt, string? planType) => new(
        ProviderId: accountKey,
        Windows: web.Windows,
        PlanType: planType,
        SourceKind: SourceKind.WebSession,
        FetchedAt: fetchedAt,
        DataTimestamp: fetchedAt,
        Status: ProviderStatus.Ok,
        Error: null,
        AccountLabel: web.AccountLabel);

    /// <summary>The browser session's signed-in state after a read. Blocked or failed says nothing
    /// about the session itself, so the last known value stands rather than flipping the tile's
    /// buttons on one bad answer.</summary>
    internal static bool? SessionSignedIn(WebUsageResult result, bool? lastKnown) => result.Outcome switch
    {
        WebUsageOutcome.Ok => true,
        WebUsageOutcome.NotSignedIn => false,
        // A signed-in answer whose numbers could not be read is still a signed-in session.
        _ when result.SessionSignedIn => true,
        _ => lastKnown,
    };
}
