namespace AiUsage.Models;

/// <summary>
/// One provider's full state after a fetch. Neither the UI nor the history store ever see a provider
/// name directly - everything they need is on this type.
/// </summary>
public sealed record ProviderSnapshot(
    string ProviderId,
    IReadOnlyList<UsageWindow> Windows,
    string? PlanType,
    SourceKind SourceKind,
    DateTimeOffset FetchedAt,
    DateTimeOffset? DataTimestamp,
    ProviderStatus Status,
    ProviderError? Error,
    // Already-localised lines saying where the provider looked and what it found, shown behind the
    // tile's "Details" disclosure. Optional so a snapshot that has nothing to explain - every
    // successful one - stays as short to write as it was before.
    IReadOnlyList<string>? Diagnostics = null,
    // Which account this is, when a display name is available - may be the account's email address
    // or login (an email address is allowed here on purpose). Shown on the tile itself, but never
    // anywhere the value could leave the screen: not diagnostics, not the clipboard, not the log file.
    // Same "must never reach a log" contract as PathSanitizer.cs applies to user paths.
    string? AccountLabel = null,
    // A provider with more than one read route (Claude: the local Claude Code sign-in, then the
    // app's own browser session) fills this with a plain, stable word naming why the primary route
    // was skipped instead of answering - "missing_file", "no_token", "token_expired" or
    // "non_ok_response" - null once it did answer, and always null for a provider with only one
    // route. Never a token, never a path: safe for the About window and for a log line alike.
    string? SkipReasonWord = null,
    // True when the newest record in this provider's own session file is a finished assistant turn
    // old enough, and not too old, to read as "waiting for the user" (see
    // Providers.Parsing.AttentionDetector). Only Claude and Codex ever set this - they are the two
    // providers with a readable, append-only local session file; Gemini and Copilot have none, so it
    // stays false for them. Defaulted so every existing construction site keeps compiling unchanged.
    bool IsWaitingForUser = false,
    // Whether the app's own browser session for this provider is signed in, when that is known from
    // a route other than the snapshot actually shown. Codex needs it: its local session files answer
    // fine while the browser session is signed out, so without this the tile would show numbers and
    // never offer the sign-in that would make them live. Null means "no web route, or nothing learned
    // this tick" - the tile then keeps deriving the state from Status and SourceKind as before.
    bool? WebSessionSignedIn = null,
    // The timestamp of the waiting turn behind IsWaitingForUser, null whenever that is false. Lets
    // the tile tell "still the same wait" from "a newer one arrived" once the user has ticked a wait
    // off - a plain bool cannot carry that distinction. Defaulted so every existing construction site
    // keeps compiling unchanged.
    DateTimeOffset? WaitingSince = null,
    // True when this snapshot only re-offers an earlier reading (an expired-token hold-over, a cached
    // web answer) under a new fetch time. The tile still shows it, but it is no new point for the
    // history chart. A fresh read of an idle source stays false even though its data time is old.
    bool HeldOver = false);
