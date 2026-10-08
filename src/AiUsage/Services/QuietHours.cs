using System.Globalization;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// Pure quiet-hours check for suppressing notification balloons overnight - kept separate from the
/// view model that actually forwards those balloons so the midnight-wrap edge case stays trivially
/// testable without any settings or dispatcher plumbing. The window's own start is inclusive, its end exclusive, in both
/// the plain and the wrapping form, so "exactly at start" and "exactly at end" never agree with each
/// other by accident.
/// </summary>
public static class QuietHours
{
    public static bool IsQuiet(TimeOnly now, TimeOnly start, TimeOnly end) =>
        start > end
            ? now >= start || now < end // wraps over midnight, e.g. 22:00-08:00
            : now >= start && now < end;

    /// <summary>Disabled, or a stored value that fails to parse, both resolve the same way a missing
    /// setting always has in this app: the documented default (22:00-08:00) rather than a crash or a
    /// silently-disabled feature.</summary>
    public static bool IsQuiet(AppSettings settings, DateTimeOffset now)
    {
        if (!settings.QuietHoursEnabled)
            return false;

        var start = ParseOrDefault(settings.QuietHoursStart, new TimeOnly(22, 0));
        var end = ParseOrDefault(settings.QuietHoursEnd, new TimeOnly(8, 0));
        return IsQuiet(TimeOnly.FromDateTime(now.LocalDateTime), start, end);
    }

    private static TimeOnly ParseOrDefault(string value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;
}
