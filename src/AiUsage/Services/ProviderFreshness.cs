namespace AiUsage.Services;

/// <summary>
/// Decides when a snapshot counts as stale. Two independent signals: ANY window's reset time has
/// already passed, so that window's numbers describe a period that is over, or the data itself is
/// older than <see cref="StaleAfter"/>. A snapshot can carry more than one window (e.g. a five-hour
/// and a weekly one) - folding them into a single "newest reset" would let an expired window hide
/// behind another one that resets later, so every window is checked on its own. Lives here rather
/// than inside a provider so that no provider carries its own threshold and the boundary cases stay
/// testable without any file access.
/// </summary>
public static class ProviderFreshness
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    public static bool IsStale(DateTimeOffset dataTimestamp, IEnumerable<DateTimeOffset?> resetTimes, DateTimeOffset now)
    {
        foreach (var reset in resetTimes)
        {
            if (reset is { } value && value <= now)
                return true;
        }

        return now - dataTimestamp > StaleAfter;
    }
}
