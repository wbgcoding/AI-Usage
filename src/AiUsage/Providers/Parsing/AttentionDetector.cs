namespace AiUsage.Providers.Parsing;

/// <summary>What the newest record in a provider's session file represents - the shapes <see
/// cref="AttentionDetector"/> needs to decide whether an agent is waiting on the user. A per-provider
/// reader (Claude, Codex - the two that write a readable, append-only session file) reduces its own
/// file format down to this one small vocabulary before calling <see cref="AttentionDetector.IsWaiting"/>.</summary>
public enum SessionRecordKind
{
    /// <summary>The assistant finished a turn and nothing has been appended since - the file's
    /// append-only nature makes this decidable from the newest record alone.</summary>
    AssistantTurn,

    /// <summary>The newest record is the user's own turn - the agent has already been handed
    /// something to do, it is not sitting idle waiting for input.</summary>
    UserTurn,

    /// <summary>The newest record is a tool call/result - the agent is still working, not waiting.</summary>
    ToolCall,
}

/// <summary>
/// Pure decision, no file or clock access of its own: given the newest session record's kind and
/// timestamp, is that agent waiting for the user right now? Lives apart from every provider reader so
/// the boundary cases (how young is too young, how old is too old) stay testable without any file
/// fixture at all.
/// </summary>
public static class AttentionDetector
{
    /// <summary>The record has to be at least this old before the tile calls it "waiting" - a turn
    /// that finished moments ago is still mid-render or mid-tool-call in practice, not yet something
    /// the user has actually been left looking at.</summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(20);

    /// <summary>The setting's own default for <c>maxAge</c> below when nothing configures it
    /// otherwise (<see cref="Models.AppSettings.AttentionMaxAgeMinutes"/>'s default) - two hours, since
    /// half a day of "waiting" reads as abandoned sessions that simply stopped, not ones actually
    /// parked on a prompt.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(2);

    /// <summary>The providers that read a session file and so can tell whether an agent is waiting
    /// at all; every other provider never reports it, so the settings offer no switch for them.</summary>
    public static readonly IReadOnlyList<string> SupportedProviderIds = ["claude", "codex"];

    public static bool CanDetect(string providerId) => SupportedProviderIds.Contains(providerId);

    /// <summary>True exactly when <paramref name="kind"/> is <see cref="SessionRecordKind.AssistantTurn"/>
    /// and its <paramref name="timestamp"/> is between <see cref="MinAge"/> and <paramref
    /// name="maxAge"/> old, inclusive of both ends. Every other shape - a user turn, a tool call, or
    /// no record at all (<paramref name="kind"/> or <paramref name="timestamp"/> null, e.g. no
    /// readable session file was found) - is "not waiting". <paramref name="maxAge"/> is a parameter
    /// rather than a fixed field so the user's own setting (<see
    /// cref="Models.AppSettings.AttentionMaxAgeMinutes"/>) decides how old is too old, instead of a
    /// figure baked into the code.</summary>
    public static bool IsWaiting(SessionRecordKind? kind, DateTimeOffset? timestamp, DateTimeOffset now, TimeSpan maxAge)
    {
        if (kind != SessionRecordKind.AssistantTurn || timestamp is not { } at)
            return false;

        var age = now - at;
        return age >= MinAge && age <= maxAge;
    }
}
