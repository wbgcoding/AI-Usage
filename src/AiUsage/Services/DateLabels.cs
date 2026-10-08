using System.Globalization;

namespace AiUsage.Services;

/// <summary>
/// Short calendar-date labels without a year, shared by every surface that names a distant day.
/// </summary>
public static class DateLabels
{
    /// <summary>Month and day in the culture's own order and punctuation with the month shortened,
    /// e.g. "Oct 22" for en-US and "22. Okt." for de-DE. The culture's month-day pattern spells the
    /// month in full, so only that part is swapped for the abbreviated form.</summary>
    public static string ShortMonthDay(DateTime date, CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal);
        return date.ToString(pattern, culture);
    }
}
