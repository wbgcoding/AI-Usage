namespace AiUsage.Providers.Parsing;

/// <summary>Shared by every parser that turns a foreign JSON integer into a reset timestamp.</summary>
internal static class UnixTimeConversion
{
    /// <summary>Null instead of throwing when <paramref name="seconds"/> falls outside the range
    /// DateTimeOffset can represent - a foreign file can carry any integer, valid JSON or not.</summary>
    internal static DateTimeOffset? FromUnixSecondsOrNull(long seconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>An epoch count that may be in seconds or in milliseconds: nothing in this century is a
    /// twelve-digit second count, so the length tells them apart. Null when the result is out of
    /// range.</summary>
    internal static DateTimeOffset? FromUnixSecondsOrMillisecondsOrNull(long value) =>
        FromUnixSecondsOrNull(value >= 100_000_000_000 ? value / 1000 : value);

    /// <summary>Null instead of the value itself when it falls more than 400 days from now - far
    /// enough that a seconds/milliseconds unit mix-up can never slip through as a valid-looking but
    /// wildly wrong countdown.</summary>
    internal static DateTimeOffset? PlausibleOrNull(DateTimeOffset value) =>
        Math.Abs((value - DateTimeOffset.UtcNow).TotalDays) <= 400 ? value : null;
}
