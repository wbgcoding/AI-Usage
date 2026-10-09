using System.Globalization;

namespace AiUsage.Services;

/// <summary>
/// Short calendar-date labels without a year, shared by every surface that names a day or month.
/// All of them spell the month with its standalone abbreviation ("Sep", "Okt", "Mär"), never the
/// genitive form some cultures print inside a day-month pattern ("Sept."), so the same month reads
/// the same in a chart axis, the year grid, a tooltip and a card.
/// </summary>
public static class DateLabels
{
    /// <summary>The standalone abbreviated month name, e.g. "Sep" and "Okt" for German, "Oct" for
    /// English.</summary>
    public static string MonthShort(DateOnly date, CultureInfo culture) =>
        culture.DateTimeFormat.AbbreviatedMonthNames[date.Month - 1];

    /// <summary>Day and month without a year: German "1. Okt", English "Oct 1". Any other language
    /// keeps its own month-day order with the month swapped for its standalone abbreviation.</summary>
    public static string DayMonthShort(DateOnly date, CultureInfo culture)
    {
        var month = MonthShort(date, culture);
        var day = date.Day.ToString(culture);
        switch (culture.TwoLetterISOLanguageName)
        {
            case "de":
                return day + ". " + month;
            case "en":
                return month + " " + day;
            default:
                var literal = "'" + month.Replace("'", "''", StringComparison.Ordinal) + "'";
                var pattern = culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", literal, StringComparison.Ordinal);
                return date.ToDateTime(TimeOnly.MinValue).ToString(pattern, culture);
        }
    }

    /// <summary>Weekday, day and month: German "Do, 1. Okt", English "Thu, Oct 1".</summary>
    public static string WeekdayDayMonthShort(DateOnly date, CultureInfo culture) =>
        culture.DateTimeFormat.AbbreviatedDayNames[(int)date.DayOfWeek] + ", " + DayMonthShort(date, culture);

    /// <summary>Same label as <see cref="DayMonthShort"/> for a caller that holds a
    /// <see cref="DateTime"/>.</summary>
    public static string ShortMonthDay(DateTime date, CultureInfo culture) =>
        DayMonthShort(DateOnly.FromDateTime(date), culture);
}
