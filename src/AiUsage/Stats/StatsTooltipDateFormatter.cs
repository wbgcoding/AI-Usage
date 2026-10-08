using System.Globalization;

namespace AiUsage.Stats;

/// <summary>Which kind of time bucket a tooltip's own date line is naming - what decides whether
/// that line carries a weekday, a time, or neither.</summary>
public enum StatsTooltipGranularity
{
    Hour,
    Day,
    Week,
    Month,
}

/// <summary>
/// The one place every tooltip in the statistics window that shows a time or a weekday builds that
/// text from - always <c>[culture's own short date] [culture's own abbreviated weekday] [time]</c>,
/// omitting whichever part the bucket makes pointless: an hourly bucket keeps all three, a daily one
/// drops the time, a weekly or monthly one is a date range with neither a weekday nor a time (a
/// range spans more than one of each). Built from <paramref name="culture"/> rather than a
/// hardcoded "dd.MM.yyyy" pattern, so English gets its own short-date order (e.g. "09/26/2026")
/// while German keeps "26.09.2026" - only the part order and the presence of each piece is fixed,
/// never the literal characters between them.
/// </summary>
public static class StatsTooltipDateFormatter
{
    /// <summary>One point in time: a day, a weekday name and (for an hourly bucket) a time - the
    /// same three pieces every instant-based tooltip line needs, built once and shared instead of
    /// three separate string formats drifting apart from each other.</summary>
    public static string FormatInstant(DateOnly day, StatsTooltipGranularity granularity, CultureInfo culture, TimeOnly? time = null, string? timeRangeText = null)
    {
        var datePart = day.ToDateTime(TimeOnly.MinValue).ToString("d", culture);
        if (granularity is StatsTooltipGranularity.Week or StatsTooltipGranularity.Month)
            return datePart;

        var weekdayPart = culture.DateTimeFormat.AbbreviatedDayNames[(int)day.DayOfWeek];
        if (granularity == StatsTooltipGranularity.Day)
            return $"{datePart} {weekdayPart}";

        // Hour: a caller that already shows its own "14:00-15:00" range passes it through unchanged
        // rather than this method reformatting a single instant into one.
        var timePart = timeRangeText ?? time?.ToString("t", culture) ?? "";
        return string.IsNullOrEmpty(timePart) ? $"{datePart} {weekdayPart}" : $"{datePart} {weekdayPart} {timePart}";
    }

    /// <summary>The one formatter for every date range the app shows (a week bar, a project's active
    /// span, the year grid's summary). A single day prints as the culture's short date; a range joins
    /// the two ends with a bare hyphen and leaves out of the start whatever trailing pieces it shares
    /// with the end: "01.-15.09.2026" (same month), "28.09.-03.10.2026" (same year), both ends in full
    /// across a year boundary ("28.12.2025-03.01.2026"). English reads "9/1-9/15/2026" and
    /// "9/28-10/3/2026". Built from the culture's own short date pattern, never a fixed layout.</summary>
    public static string FormatRange(DateOnly start, DateOnly end, CultureInfo culture)
    {
        var startDate = start.ToDateTime(TimeOnly.MinValue);
        var endDate = end.ToDateTime(TimeOnly.MinValue);
        var endText = endDate.ToString("d", culture);
        if (start == end)
            return endText;

        var startText = CompactStart(start, end, culture) ?? startDate.ToString("d", culture);
        var joiner = startText.Contains('-') || endText.Contains('-') ? " - " : "-";
        return startText + joiner + endText;
    }

    /// <summary>The start date with the culture's trailing year (and month, when it is shared too)
    /// cut off, or null when the pattern is not a plain d/M/y one or nothing is shared.</summary>
    private static string? CompactStart(DateOnly start, DateOnly end, CultureInfo culture)
    {
        if (start.Year != end.Year)
            return null;

        var pattern = culture.DateTimeFormat.ShortDatePattern;
        var components = new List<(char Kind, int Start, int End)>();
        for (var i = 0; i < pattern.Length;)
        {
            var c = pattern[i];
            if (c is 'd' or 'M' or 'y')
            {
                var j = i;
                while (j < pattern.Length && pattern[j] == c)
                    j++;
                components.Add((c, i, j));
                i = j;
            }
            else if (char.IsLetter(c) || c is '\\' or '\'' or '"' or '%')
            {
                return null;
            }
            else
            {
                i++;
            }
        }

        // Only a pattern that ends in the year can drop it; the month goes too when it comes second
        // after the day (day.month.year) and both ends sit in it.
        if (components.Count != 3 || components[2].Kind != 'y')
            return null;

        var kept = 2;
        if (components[0].Kind == 'd' && components[1].Kind == 'M' && start.Month == end.Month)
            kept = 1;

        var cut = components[kept - 1].End;
        if (cut < pattern.Length && pattern[cut] == '.')
            cut++;
        var compact = pattern[..cut];
        return start.ToDateTime(TimeOnly.MinValue).ToString(compact.Length == 1 ? "%" + compact : compact, culture);
    }
}
