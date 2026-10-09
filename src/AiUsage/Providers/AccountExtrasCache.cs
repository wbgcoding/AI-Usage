namespace AiUsage.Providers;

/// <summary>The account details a web read may reuse instead of asking the provider for them again:
/// the plan wording (Claude), the account e-mail (Codex, Cursor) and the weekly Grok Bot bar (Cursor),
/// each null when nothing fresh enough is held.</summary>
public sealed record CachedAccountExtras(string? Plan, string? Email, string? GrokBotJson)
{
    public static readonly CachedAccountExtras None = new(null, null, null);
}

/// <summary>Holds those details for one hour per web source, which makes it one per hidden session:
/// a sign-out builds a new source and a new session, so nothing outlives the account. They change
/// rarely and each costs a request of its own, so the usage read that happens every few minutes
/// leaves them out while a held value is still young. A failed request is never held, the next read
/// simply asks again.</summary>
internal sealed class AccountExtrasCache(TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private Entry? _plan;
    private Entry? _email;
    private Entry? _grokBot;

    private readonly record struct Entry(string Value, DateTimeOffset At);

    public CachedAccountExtras Current()
    {
        var now = _time.GetUtcNow();
        return new CachedAccountExtras(Fresh(_plan, now), Fresh(_email, now), Fresh(_grokBot, now));
    }

    /// <summary>Remembers what a read just fetched; a null part leaves what is held untouched.</summary>
    public void Store(string? plan, string? email, string? grokBotJson)
    {
        var now = _time.GetUtcNow();
        if (plan is not null)
            _plan = new Entry(plan, now);
        if (email is not null)
            _email = new Entry(email, now);
        if (grokBotJson is not null)
            _grokBot = new Entry(grokBotJson, now);
    }

    public void Clear()
    {
        _plan = null;
        _email = null;
        _grokBot = null;
    }

    private static string? Fresh(Entry? entry, DateTimeOffset now) =>
        entry is { } held && now - held.At is { } age && age >= TimeSpan.Zero && age < Lifetime ? held.Value : null;
}
