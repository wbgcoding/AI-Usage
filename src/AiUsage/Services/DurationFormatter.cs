using System.Globalization;

namespace AiUsage.Services;

/// <summary>
/// The one place that labels a retention duration: days below a year, years
/// (one decimal between whole years) from 365 days up. A year always counts as exactly 365 days.
/// The wording (Duration.Days/.Year/.Years) is deliberately independent of the chosen UI language,
/// same as every other number in the app (numbers/times follow the OS regional
/// format, not the text language); the fractional-year decimal separator follows
/// <see cref="CultureInfo.CurrentCulture"/> for the same reason.
/// </summary>
public static class DurationFormatter
{
    private const int DaysPerYear = 365;

    public static string Describe(int days)
    {
        if (days < DaysPerYear)
            return days == 1 ? Loc["Duration.Day"] : Loc.Format("Duration.Days", days);

        var years = Math.Round(days / (double)DaysPerYear, 1, MidpointRounding.AwayFromZero);
        var isWholeYear = Math.Abs(years % 1) < 0.05;

        if (isWholeYear)
        {
            var wholeYears = (int)Math.Round(years);
            return wholeYears == 1 ? Loc["Duration.Year"] : Loc.Format("Duration.Years", wholeYears);
        }

        return Loc.Format("Duration.Years", years.ToString("0.0", CultureInfo.CurrentCulture));
    }

    private static LocalizationService Loc => LocalizationService.Instance;
}
