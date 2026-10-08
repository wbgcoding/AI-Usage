using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// One provider (Codex, Claude, Gemini, Copilot, ...). A new provider needs only a class implementing
/// this plus a registry entry - the acceptance test for the fourth provider.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Stable key, e.g. "codex". Never shown to the user directly.</summary>
    string Id { get; }

    /// <summary>Which account this is: equal to <see cref="Id"/> for the first/only account of a
    /// provider, "&lt;id&gt;#2", "#3", ... for a further one of the same provider (see
    /// <see cref="Models.AppSettings.Accounts"/>). The key everything account-specific goes through -
    /// settings, the history file, the browser sign-in profile, the tile itself - so two accounts of
    /// the same provider never collide. Defaults to <see cref="Id"/>, so a provider that can only ever
    /// have one account (every provider except Claude, today) needs to do nothing to get this right.</summary>
    string AccountKey => Id;

    /// <summary>Display name for the tile header, e.g. "Codex".</summary>
    string DisplayName { get; }

    /// <summary>Never throws - every failure becomes a <see cref="ProviderStatus"/> and a <see cref="ProviderError"/>.</summary>
    Task<ProviderSnapshot> FetchAsync(CancellationToken ct);

    /// <summary>
    /// Null uses the scheduler's configured interval. A provider with a real rate-limited floor (the
    /// Claude web fallback) returns it here instead of the scheduler hardcoding
    /// a provider name.
    /// </summary>
    TimeSpan? MinRefreshInterval => null;

    /// <summary>
    /// True once a provider reads its numbers through a web session this app owns, so the app can
    /// offer sign-in/sign-out for it directly. Defaults to false: a provider only speaks up once its
    /// own web route actually exists.
    /// </summary>
    bool SupportsInAppSignIn => false;

    /// <summary>
    /// True when this app can disconnect the account even without a web session of its own: it
    /// stops reading a sign-in another tool already holds on this machine (GitHub CLI, Claude Code)
    /// and resumes on "sign in". Every provider with an in-app sign-in supports it too.
    /// </summary>
    bool SupportsSignOut => SupportsInAppSignIn;

    /// <summary>
    /// Called once the app's own sign-in window finished for this account, right before the refresh
    /// that follows it - a provider that caches its web answer reads the web again instead.
    /// </summary>
    void SignInCompleted() { }

    /// <summary>
    /// True for a provider that can hold more than one account at once (a second Claude sign-in gets
    /// its own tile and web session). Drives the Settings window's "add account" button; false for a
    /// provider where a single machine-wide tool login is the only account there can be.
    /// </summary>
    bool SupportsMultipleAccounts => false;

    /// <summary>
    /// True for a provider whose <see cref="FetchAsync"/> must start on the calling thread itself -
    /// a web-session-backed provider already marshals its own work internally and would misbehave
    /// started from a thread-pool thread instead of the UI thread it expects. Defaults to false, which
    /// lets <see cref="RefreshScheduler"/> start the fetch on the thread pool instead, so a provider
    /// that does synchronous file I/O before its first await never blocks the UI thread with it.
    /// </summary>
    bool RunsOnUiThread => false;

    /// <summary>
    /// Where this provider actually looks, in already-sanitised form for the About window's "what
    /// this program reads" section (a tilde-shorthand folder pattern like <c>~/.codex/sessions</c>,
    /// never the expanded real user path, or a localized phrase for a web-backed read). Never empty
    /// in a shipped provider - <c>TokenSafetyTests</c> enforces this, so a provider added later
    /// without filling this in shows up as a visibly empty list instead of silently doing nothing.
    /// </summary>
    IReadOnlyList<string> ReadLocations => [];
}
