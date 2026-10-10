using System.Globalization;

namespace AiUsage.Services;

/// <summary>
/// Formats a window's reset time as "noch …"/"resets in …" text.
/// Pure function: the caller always supplies "now" explicitly, so this never reads the clock itself
/// and stays trivially testable and independent of the minute-tick timer that calls it.
/// </summary>
public static class CountdownFormatter
{
    /// <summary>An instant already in the past only reads "resetting now" for the first two minutes
    /// after it passed - older than that, the row simply drops the segment instead of claiming a
    /// reset is happening right now when it may have been hours or days ago (a Codex session file can
    /// carry a reset instant from a session that ended long before the app last ran).</summary>
    private static readonly TimeSpan JustResetWindow = TimeSpan.FromMinutes(2);

    public static string Format(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset)
            return string.Empty;

        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero)
            return remaining > -JustResetWindow ? Loc["Reset.Now"] : string.Empty;

        var days = (int)remaining.TotalDays;
        if (days >= 1)
            return remaining.Hours == 0
                ? Loc.Format("Reset.InDays", days)
                : Loc.Format("Reset.InDaysHours", days, remaining.Hours);

        if (remaining.TotalHours >= 1)
            return remaining.Minutes == 0
                ? Loc.Format("Reset.InHours", remaining.Hours)
                : Loc.Format("Reset.InHoursMinutes", remaining.Hours, remaining.Minutes);

        if (remaining.TotalMinutes >= 1)
            return Loc.Format("Reset.InMinutes", remaining.Minutes);

        return Loc["Reset.LessThanMinute"];
    }

    /// <summary>The local wall-clock instant a window resets at: a bare time ("21:15") under 24h
    /// away, with the weekday ("Mi 21:15") from 24h on, and the short date ("22. Okt. 21:15") from six
    /// days on, all through <see cref="CultureInfo.CurrentCulture"/> so the OS regional format decides
    /// the exact rendering. Empty when no reset time is known.</summary>
    internal static string FormatClock(DateTimeOffset? resetsAt, DateTimeOffset now) =>
        FormatClock(resetsAt, now, TimeZoneInfo.Local);

    /// <summary>The zone is a parameter so a test can pin a daylight-saving change; production passes the local one.</summary>
    internal static string FormatClock(DateTimeOffset? resetsAt, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (resetsAt is not { } reset)
            return "";

        var culture = CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(reset, zone);
        var remaining = reset - now;
        if (remaining < TimeSpan.FromHours(24))
            return local.ToString("t", culture);

        // The longer forms keep the culture's own clock (12 or 24 hour), the same one "t" gives.
        // From six days out a weekday name repeats within the window, so the date names the day.
        var time = culture.DateTimeFormat.ShortTimePattern;
        // Whole calendar days, not elapsed time: a clock change in between would otherwise move the
        // switch by an hour.
        var calendarDays = (local.Date - TimeZoneInfo.ConvertTime(now, zone).Date).Days;
        return calendarDays < 6
            ? local.ToString("ddd " + time, culture)
            : DateLabels.ShortMonthDay(local.DateTime, culture) + " " + local.ToString(time, culture);
    }

    /// <summary>"Last update … ago" for the Stale state
    /// - same day/hour/minute rounding as <see cref="Format"/>, just phrased as
    /// elapsed time. The bare duration ("2h 15m") is not looked up per language: the identical
    /// "d"/"h"/"m" unit letters are used in both EN and DE, so this is
    /// locale-invariant the same way a raw number is, not a piece of translatable prose.</summary>
    public static string FormatAge(DateTimeOffset? dataTimestamp, DateTimeOffset now)
    {
        if (dataTimestamp is not { } timestamp)
            return "";

        return Loc.Format("State.Stale.Reason", FormatElapsed(now - timestamp));
    }

    /// <summary>Bare "2h 15m"-style duration text (a zero unit is left out: "2h", "19d"), locale-invariant like a raw number - reused by
    /// <see cref="ViewModels.UsageRowViewModel"/> for the usage forecast segment instead of a second
    /// duration formatter. Below a minute the seconds run along instead of a flat "&lt;1m", so the
    /// number on screen keeps moving instead of sitting still for up to 59 seconds.</summary>
    internal static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        var days = (int)elapsed.TotalDays;
        if (days >= 1)
            return elapsed.Hours == 0 ? $"{days}d" : $"{days}d {elapsed.Hours}h";

        if (elapsed.TotalHours >= 1)
            return elapsed.Minutes == 0 ? $"{elapsed.Hours}h" : $"{elapsed.Hours}h {elapsed.Minutes}m";

        if (elapsed.TotalMinutes >= 1)
            return $"{elapsed.Minutes}m";

        return $"{(int)elapsed.TotalSeconds}s";
    }

    private static LocalizationService Loc => LocalizationService.Instance;
}
