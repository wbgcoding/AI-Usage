using AiUsage.Models;
using AiUsage.Stats;

namespace AiUsage.Services;

/// <summary>
/// How long a usage window lasts, in minutes: the one answer both the pace marker (how much of the
/// window the clock has used up) and the token window (where its slice of the index starts) are
/// built on. A provider's own figure wins; the rest follows what the window is.
/// </summary>
public static class WindowLengths
{
    public const int FiveHourMinutes = 5 * 60;

    public const int WeekMinutes = 7 * 24 * 60;

    /// <summary>The window's length, or null when it is unknown. In order: the window's own <see
    /// cref="UsageWindow.WindowMinutes"/> when above zero; five hours for the 5-hour window and a week
    /// for the weekly one; a week for one of Claude's per model windows (an "other" window with a name);
    /// for a monthly window (the label <c>Window_Month</c>, every Copilot quota and every Cursor
    /// billing cycle window) the length of the calendar month that ends at the reset, counted on the
    /// local calendar. Everything else, and a monthly window without a reset, has none.</summary>
    public static int? MinutesFor(UsageWindow window, string providerId)
    {
        if (window.WindowMinutes is > 0)
            return window.WindowMinutes;

        switch (window.Kind)
        {
            case WindowKind.FiveHour:
                return FiveHourMinutes;
            case WindowKind.Weekly:
                return WeekMinutes;
        }

        if (IsMonthly(window, providerId))
            return MonthMinutesEndingAt(window.ResetsAt);

        return providerId == StatsIndexer.ClaudeProviderId && window.Label.Length > 0 ? WeekMinutes : null;
    }

    private static bool IsMonthly(UsageWindow window, string providerId) =>
        window.Label == "Window_Month" || providerId is "copilot" or "cursor";

    /// <summary>Minutes from one calendar month before <paramref name="resetsAt"/> to it, on the local
    /// calendar (a reset on 1 March spans a 28 day February), or null without a reset.</summary>
    internal static int? MonthMinutesEndingAt(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } reset)
            return null;

        var localEnd = reset.LocalDateTime;
        var localStart = localEnd.AddMonths(-1);
        var start = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart));
        var minutes = (int)(reset - start).TotalMinutes;
        return minutes > 0 ? minutes : null;
    }
}
