using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// The one place that turns a tile's status into visible text (one central formatter, no
/// ad-hoc strings). Returns resource KEYS only, never finished text, so a language switch always
/// applies. No other code may put <c>Exception.Message</c> in front of the user.
/// </summary>
public static class ErrorPresenter
{
    /// <summary>The one short technical line an error dialog may show next to its plain wording, built
    /// from the failing step and the exception's type and codes only. Never the exception's message
    /// (it can carry a user path or an address), so the line is safe to show and to copy into a report.
    /// Example: <c>download: HttpRequestException 0x80131500 status 404</c>.</summary>
    public static string TechnicalLine(string step, Exception exception)
    {
        var line = $"{step}: {exception.GetType().Name} 0x{exception.HResult:X8}";
        return exception is System.Net.Http.HttpRequestException { StatusCode: { } status }
            ? $"{line} {LocalizationService.Instance.Format("Error.StatusSuffix", (int)status)}"
            : line;
    }

    /// <summary>Headline, reason and an optional action key for a tile's current status.
    /// <paramref name="sourceKind"/> only matters for <see cref="ProviderStatus.Stale"/>: a source
    /// that only moves through use (<see cref="SourceKind.LocalFile"/>, e.g. Codex) offers no
    /// refresh action, since asking again cannot make it any less stale - every other source
    /// (an account the app itself asks) gets the "fetch now" action the other non-Ok states already
    /// support. <paramref name="canSignInOnWeb"/> only matters for <see cref="ProviderStatus.Blocked"/>:
    /// a provider with its own sign-in window offers signing in again, since an expired sign-in is the
    /// usual cause of a turned-away request.</summary>
    public static (string HeadlineKey, string ReasonKey, string? ActionKey) Describe(
        ProviderStatus status, ProviderError? error, SourceKind sourceKind = SourceKind.WebSession,
        bool canSignInOnWeb = false)
    {
        var (headlineKey, defaultReasonKey, defaultActionKey) = status switch
        {
            ProviderStatus.Ok => ("Status_Ok_Headline", "Status_Ok_Reason", (string?)null),
            ProviderStatus.Stale => ("Status_Stale_Headline", "Status_Stale_Reason",
                sourceKind == SourceKind.LocalFile ? null : "Action_FetchNow"),
            ProviderStatus.NoLocalData => ("Status_NoLocalData_Headline", "Status_NoLocalData_Reason", null),
            ProviderStatus.SourceUnavailable => ("Status_SourceUnavailable_Headline", "Status_SourceUnavailable_Reason", null),
            ProviderStatus.NotSignedIn => ("Status_NotSignedIn_Headline", "Status_NotSignedIn_Reason", "Action_SignIn"),
            ProviderStatus.Blocked => ("Status_Blocked_Headline", "Status_Blocked_Reason", canSignInOnWeb ? "Action_SignIn" : null),
            ProviderStatus.RuntimeMissing => ("Status_RuntimeMissing_Headline", "Status_RuntimeMissing_Reason", "Action_OpenWebView2Download"),
            ProviderStatus.Failed => ("Status_Failed_Headline", "Status_Failed_Reason", null),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, message: null),
        };

        return (headlineKey, error?.ReasonKey ?? defaultReasonKey, error?.ActionKey ?? defaultActionKey);
    }
}
