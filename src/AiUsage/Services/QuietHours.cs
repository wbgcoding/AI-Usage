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

    /// <summary>Whether notifications are quiet at <paramref name="now"/>: always on a day the person
    /// unchecked (see <see cref="AppSettings.AllDaysAllowed"/>), otherwise inside the daily window when
    /// that is enabled. An unparseable start or end falls back to 22:00 / 08:00; a disabled window is
    /// never quiet.</summary>
    public static bool IsQuiet(AppSettings settings, DateTimeOffset now)
    {
        // The day list stands on its own: it works with the daily window switched off, and the local
        // calendar day decides, not UTC.
        if (IsQuietDay(settings, now.LocalDateTime.DayOfWeek))
            return true;

        if (!settings.QuietHoursEnabled)
            return false;

        var start = ParseOrDefault(settings.QuietHoursStart, new TimeOnly(22, 0));
        var end = ParseOrDefault(settings.QuietHoursEnd, new TimeOnly(8, 0));
        return IsQuiet(TimeOnly.FromDateTime(now.LocalDateTime), start, end);
    }

    /// <summary>True when notifications are off for the whole of <paramref name="day"/>.</summary>
    public static bool IsQuietDay(AppSettings settings, DayOfWeek day) =>
        !settings.AllDaysAllowed && settings.QuietDays.Contains(day.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The distinct valid day names of <paramref name="days"/> in their canonical spelling,
    /// Monday first; anything else (a typo in a hand-edited file) is dropped.</summary>
    public static List<string> NormalizeDays(IEnumerable<string>? days)
    {
        var wanted = (days ?? []).Where(name => name is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. WeekOrder.Where(day => wanted.Contains(day.ToString())).Select(day => day.ToString())];
    }

    /// <summary>The week as the settings list it, Monday first.</summary>
    public static readonly IReadOnlyList<DayOfWeek> WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private static TimeOnly ParseOrDefault(string value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;
}
