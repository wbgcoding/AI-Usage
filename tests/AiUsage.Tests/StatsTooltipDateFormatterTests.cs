using System.Globalization;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Coverage for the one central formatter every time/weekday-showing tooltip in the
/// statistics window builds its date line from - one case per bucket granularity, in both DE and
/// EN, so a future caller cannot drift into its own ad-hoc date string.</summary>
public class StatsTooltipDateFormatterTests
{
    private static readonly CultureInfo German = new("de-DE");
    private static readonly CultureInfo English = new("en-US");

    [Fact]
    public void HourlyBucketShowsDateWeekdayAndTimeInGerman()
    {
        var text = StatsTooltipDateFormatter.FormatInstant(
            new DateOnly(2026, 9, 26), StatsTooltipGranularity.Hour, German, new TimeOnly(14, 30));

        Assert.Equal("26.09.2026 Sa 14:30", text);
    }

    [Fact]
    public void HourlyBucketShowsDateWeekdayAndTimeInEnglish()
    {
        var text = StatsTooltipDateFormatter.FormatInstant(
            new DateOnly(2026, 9, 26), StatsTooltipGranularity.Hour, English, new TimeOnly(14, 30));

        Assert.Equal("9/26/2026 Sat 2:30 PM", text);
    }

    [Fact]
    public void HourlyBucketPrefersAnAlreadyBuiltTimeRangeOverASingleTime()
    {
        var text = StatsTooltipDateFormatter.FormatInstant(
            new DateOnly(2026, 9, 26), StatsTooltipGranularity.Hour, German, timeRangeText: "14:00-15:00");

        Assert.Equal("26.09.2026 Sa 14:00-15:00", text);
    }

    [Fact]
    public void DailyBucketShowsDateAndWeekdayButNoTime()
    {
        var textDe = StatsTooltipDateFormatter.FormatInstant(new DateOnly(2026, 9, 26), StatsTooltipGranularity.Day, German);
        var textEn = StatsTooltipDateFormatter.FormatInstant(new DateOnly(2026, 9, 26), StatsTooltipGranularity.Day, English);

        Assert.Equal("26.09.2026 Sa", textDe);
        Assert.Equal("9/26/2026 Sat", textEn);
    }

    [Fact]
    public void WeeklyAndMonthlyBucketsShowOnlyTheDateNeitherWeekdayNorTime()
    {
        var week = StatsTooltipDateFormatter.FormatInstant(new DateOnly(2026, 9, 26), StatsTooltipGranularity.Week, German);
        var month = StatsTooltipDateFormatter.FormatInstant(new DateOnly(2026, 9, 26), StatsTooltipGranularity.Month, German);

        Assert.Equal("26.09.2026", week);
        Assert.Equal("26.09.2026", month);
    }

    [Fact]
    public void FormatRangeCollapsesToASingleDateWhenStartAndEndAreTheSameDay()
    {
        var text = StatsTooltipDateFormatter.FormatRange(new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 26), German);

        Assert.Equal("26.09.2026", text);
    }

    [Theory]
    [InlineData("de-DE", 2026, 9, 1, 2026, 9, 15, "01.-15.09.2026")]
    [InlineData("de-DE", 2026, 9, 28, 2026, 10, 3, "28.09.-03.10.2026")]
    [InlineData("de-DE", 2025, 12, 28, 2026, 1, 3, "28.12.2025-03.01.2026")]
    [InlineData("de-DE", 2026, 9, 15, 2026, 9, 15, "15.09.2026")]
    [InlineData("en-US", 2026, 9, 1, 2026, 9, 15, "9/1-9/15/2026")]
    [InlineData("en-US", 2026, 9, 28, 2026, 10, 3, "9/28-10/3/2026")]
    [InlineData("en-US", 2025, 12, 28, 2026, 1, 3, "12/28/2025-1/3/2026")]
    [InlineData("en-US", 2026, 9, 15, 2026, 9, 15, "9/15/2026")]
    [InlineData("en-US", 2026, 9, 15, 2026, 10, 15, "9/15-10/15/2026")]
    [InlineData("en-GB", 2026, 9, 1, 2026, 9, 15, "01-15/09/2026")]
    public void FormatRangeUsesTheCompactHyphenForm(string cultureName, int y1, int m1, int d1, int y2, int m2, int d2, string expected)
    {
        var text = StatsTooltipDateFormatter.FormatRange(new DateOnly(y1, m1, d1), new DateOnly(y2, m2, d2), new CultureInfo(cultureName));

        Assert.Equal(expected, text);
        Assert.DoesNotContain((char)0x2013, text); // en dash
        Assert.DoesNotContain((char)0x2014, text); // em dash
    }
}
