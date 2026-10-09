using System.Text.RegularExpressions;

namespace AiUsage.Services;

/// <summary>
/// Redirects <see cref="ErrorPresenter"/>'s internal status keys and <see cref="Models.SourceKind"/>
/// to <see cref="LocalizationService"/> (delivering on the promise this file's own comment made
/// earlier: one central place, so no other call site ever had to change). The internal
/// keys stay their own small vocabulary rather than being renamed to the resx keys directly, because
/// <see cref="ErrorPresenter"/> and <see cref="Models.ProviderError"/> already ship those names as a
/// public contract independent of how the resource file happens to be keyed.
/// </summary>
public static class StatusTextMap
{
    private static readonly Dictionary<string, string> ResourceKeys = new()
    {
        ["Status_Ok_Headline"] = "",
        ["Status_Ok_Reason"] = "",
        ["Status_Stale_Headline"] = "State.Stale.Head",
        ["Status_Stale_Reason"] = "State.Stale.Meaning",
        ["Status_NoLocalData_Headline"] = "State.NoLocalData.Head",
        ["Status_NoLocalData_Reason"] = "State.NoLocalData.Reason",
        ["Status_SourceUnavailable_Headline"] = "State.SourceUnavailable.Head",
        ["Status_SourceUnavailable_Reason"] = "State.SourceUnavailable.Reason",
        ["Status_NotSignedIn_Headline"] = "State.NotSignedIn.Head",
        ["Status_NotSignedIn_Reason"] = "State.NotSignedIn.Reason",
        ["Status_Blocked_Headline"] = "State.Blocked.Head",
        ["Status_Blocked_Reason"] = "State.Blocked.Reason",
        ["Status_GitHubCliMissing_Reason"] = "State.GitHubCliMissing.Reason",
        ["Status_GitHubCliNotSignedIn_Reason"] = "State.GitHubCliNotSignedIn.Reason",
        ["Status_Offline_Reason"] = "State.Offline.Reason",
        ["Status_UnreadableAnswer_Reason"] = "State.UnreadableAnswer.Reason",
        ["Status_SignOutIncomplete_Reason"] = "State.SignOutIncomplete.Reason",
        ["Status_RuntimeMissing_Headline"] = "State.RuntimeMissing.Head",
        ["Status_RuntimeMissing_Reason"] = "State.RuntimeMissing.Reason",
        ["Status_Failed_Headline"] = "State.Failed.Head",
        ["Status_Failed_Reason"] = "State.Failed.Reason",
        ["Status_Failed_Server_Headline"] = "State.Failed.Server.Head",
        ["Status_Failed_Server_Reason"] = "State.Failed.Server.Reason",
        ["Status_Failed_Refused_Headline"] = "State.Failed.Refused.Head",
        ["Status_Failed_Refused_Reason"] = "State.Failed.Refused.Reason",
        ["Status_Failed_Timeout_Headline"] = "State.Failed.Timeout.Head",
        ["Status_Failed_Timeout_Reason"] = "State.Failed.Timeout.Reason",
        ["Status_Failed_Network_Headline"] = "State.Failed.Network.Head",
        ["Status_Failed_Network_Reason"] = "State.Failed.Network.Reason",
        ["Action_SignIn"] = "State.NotSignedIn.Action",
        ["Action_FetchNow"] = "State.Stale.Action",
        ["Action_OpenWebView2Download"] = "State.RuntimeMissing.Action",
        ["Action_Retry"] = "Action.RefreshNow",
        ["Window_FiveHour"] = "Window.FiveHour",
        ["Window_Weekly"] = "Window.Weekly",
        ["Window_Other"] = "Window.Other",
        ["Window_Month"] = "Window.Month",
        ["Window_CursorModels"] = "Window.CursorModels",
        ["Window_OtherModels"] = "Window.OtherModels",
        ["Window_GrokBotWeekly"] = "Window.GrokBotWeekly",
        ["Window_CopilotChat"] = "Window.CopilotChat",
        ["Window_CopilotCompletions"] = "Window.CopilotCompletions",
        ["Window_CopilotPremium"] = "Window.CopilotPremium",
        ["Window_LimitReached"] = "State.LimitReached",
    };

    // Shape of this class's own internal keys (Window_FiveHour, Status_Blocked_Reason, ...) -
    // letters and underscores only. A key that doesn't look like this is not a typo'd internal
    // key, it is a value handed straight through from a provider response, so it is returned as-is
    // rather than resolved.
    private static readonly Regex InternalKeyShape = new("^[A-Za-z]+_[A-Za-z_]+$", RegexOptions.Compiled);

    /// <summary>Resolves one of this class's own internal keys through <see
    /// cref="LocalizationService"/>; empty for a blank or unknown internal-shaped key rather than
    /// throwing, so a placeholder tile never crashes the window. A key that does not match the
    /// internal key shape (for example a model display name coming straight from a provider
    /// response) is returned verbatim instead of being swallowed to "" - it was never a resource
    /// key to begin with.</summary>
    public static string Resolve(string? key)
    {
        if (key is null)
            return "";
        if (ResourceKeys.TryGetValue(key, out var resourceKey))
            return resourceKey.Length > 0 ? LocalizationService.Instance[resourceKey] : "";
        if (InternalKeyShape.IsMatch(key))
            return "";

        // A provider may also hand over a resource key directly instead of one of the internal
        // words above - ClaudeProvider does it for the sign-in hints (State.NotSignedIn.ClaudeCode,
        // State.NotSignedIn.WebSession, State.NotSignedIn.ClaudeExpired). Those carry dots, so they
        // never matched the internal key shape and used to be passed through verbatim, which put
        // the bare key on the tile where the sentence belongs. Resolve it when the resource file
        // knows it; a value that is genuinely no resource key (a model display name from a provider
        // response) still comes back untouched.
        var resolved = LocalizationService.Instance[key];
        return resolved.Length > 0 ? resolved : key;
    }

    /// <summary>Like <see cref="Resolve(string?)"/> for the failure wordings that name the provider
    /// ({0}) and the HTTP status ({1}); a text without those placeholders comes back unchanged.</summary>
    public static string ResolveFailure(string key, string providerName, int? httpStatus)
    {
        var text = Resolve(key);
        return text.Contains('{', StringComparison.Ordinal)
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, text, providerName, httpStatus)
            : text;
    }

    /// <summary>The reason line of a tile: the status' reason, or, when the error names a cause
    /// (<see cref="Models.ProviderError.DetailKey"/>), the reason with that cause in brackets. Resolved
    /// here at display time so a language switch applies to the cause as well. A reason that already
    /// names its own kind of failure (the server error, the timeout) carries the cause itself.</summary>
    public static string ResolveReason(string reasonKey, Models.ProviderError? error, string providerName = "")
    {
        if (error?.DetailKey is not { } detailKey || reasonKey != "Status_Failed_Reason")
            return ResolveFailure(reasonKey, providerName, error?.HttpStatus);

        var loc = LocalizationService.Instance;
        var detail = error.DetailArg is null ? loc[detailKey] : loc.Format(detailKey, error.DetailArg);
        return loc.Format("State.Failed.ReasonWithDetail", detail);
    }

    public static string SourceBadge(Models.SourceKind sourceKind) => sourceKind switch
    {
        Models.SourceKind.LocalFile or Models.SourceKind.LocalDatabase => LocalizationService.Instance["Source.Local"],
        Models.SourceKind.WebSession => LocalizationService.Instance["Source.Web"],
        Models.SourceKind.LocalLogin => LocalizationService.Instance["Source.LocalLogin"],
        _ => LocalizationService.Instance["Source.None"],
    };

    /// <summary>The one place a usage percentage is rounded to a whole number for display - away
    /// from zero, matching what the tile's own bar has always shown. Before this, the tray tooltip
    /// used the same away-from-zero rounding but the threshold notification used bare
    /// <c>Math.Round</c> (banker's rounding, to even), so 62.5% could read 63% on the tile and tray
    /// but 62% in the balloon.</summary>
    public static int UsagePercent(double percent) => (int)Math.Round(percent, MidpointRounding.AwayFromZero);

    /// <summary>A formatted number with the percent sign in the active language's own convention
    /// ("42%" in English, "42 %" in German). Callers format and round the number themselves.</summary>
    public static string FormatPercent(string number) => LocalizationService.Instance.Format("Number.Percent", number);
}
