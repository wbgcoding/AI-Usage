using System.Globalization;
using AiUsage.Services;

namespace AiUsage.Tests;

public class DateLabelsTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(2026, 9, 1, "Sep", "Sep")]
    [InlineData(2026, 10, 1, "Okt", "Oct")]
    [InlineData(2026, 3, 5, "Mär", "Mar")]
    [InlineData(2026, 6, 5, "Jun", "Jun")]
    [InlineData(2026, 7, 5, "Jul", "Jul")]
    public void MonthShortIsTheStandaloneAbbreviation(int year, int month, int day, string german, string english)
    {
        var date = new DateOnly(year, month, day);

        Assert.Equal(german, DateLabels.MonthShort(date, German));
        Assert.Equal(english, DateLabels.MonthShort(date, English));
    }

    [Theory]
    [InlineData(2026, 9, 1, "1. Sep", "Sep 1")]
    [InlineData(2026, 10, 22, "22. Okt", "Oct 22")]
    [InlineData(2026, 3, 9, "9. Mär", "Mar 9")]
    public void DayMonthShortPutsTheDayBeforeTheMonthInGermanAndAfterItInEnglish(int year, int month, int day, string german, string english)
    {
        var date = new DateOnly(year, month, day);

        Assert.Equal(german, DateLabels.DayMonthShort(date, German));
        Assert.Equal(english, DateLabels.DayMonthShort(date, English));
    }

    [Fact]
    public void DayMonthShortNeverUsesTheGenitiveAbbreviation()
    {
        // The culture's own "d. MMM" pattern would print "1. Sept." for September.
        Assert.DoesNotContain("Sept", DateLabels.DayMonthShort(new DateOnly(2026, 9, 1), German));
    }

    [Fact]
    public void DayMonthShortFallsBackToTheCulturesOwnOrderForOtherLanguages()
    {
        var french = CultureInfo.GetCultureInfo("fr-FR");

        var text = DateLabels.DayMonthShort(new DateOnly(2026, 10, 1), french);

        Assert.StartsWith("1 ", text);
        Assert.Contains(french.DateTimeFormat.AbbreviatedMonthNames[9], text);
    }

    [Theory]
    [InlineData(2026, 10, 1, "Do, 1. Okt", "Thu, Oct 1")]
    [InlineData(2026, 9, 3, "Do, 3. Sep", "Thu, Sep 3")]
    [InlineData(2026, 3, 2, "Mo, 2. Mär", "Mon, Mar 2")]
    public void WeekdayDayMonthShortLeadsWithTheAbbreviatedWeekday(int year, int month, int day, string german, string english)
    {
        var date = new DateOnly(year, month, day);

        Assert.Equal(german, DateLabels.WeekdayDayMonthShort(date, German));
        Assert.Equal(english, DateLabels.WeekdayDayMonthShort(date, English));
    }

    [Fact]
    public void WeekdayDayMonthLongHasNoYearInTheSameYearAndTheYearInAnother()
    {
        var date = new DateOnly(2026, 11, 7);

        Assert.Equal("Samstag, 7. November", DateLabels.WeekdayDayMonthLong(date, German, new DateOnly(2026, 11, 14)));
        Assert.Equal("Saturday, November 7", DateLabels.WeekdayDayMonthLong(date, English, new DateOnly(2026, 1, 2)));
        Assert.Equal("Samstag, 7. November 2026", DateLabels.WeekdayDayMonthLong(date, German, new DateOnly(2027, 1, 2)));
        Assert.Equal("Saturday, November 7, 2026", DateLabels.WeekdayDayMonthLong(date, English, new DateOnly(2027, 1, 2)));
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("fr-FR")]
    [InlineData("it-IT")]
    [InlineData("ja-JP")]
    [InlineData("nl-NL")]
    [InlineData("pl-PL")]
    [InlineData("pt-BR")]
    [InlineData("ro-RO")]
    [InlineData("tr-TR")]
    [InlineData("zh-CN")]
    public void WeekdayDayMonthLongDropsOnlyTheYearInEveryLanguage(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        var date = new DateOnly(2026, 11, 7);

        var same = DateLabels.WeekdayDayMonthLong(date, culture, new DateOnly(2026, 11, 14));
        var other = DateLabels.WeekdayDayMonthLong(date, culture, new DateOnly(2027, 1, 2));

        Assert.DoesNotContain("2026", same);
        Assert.Contains("2026", other);
        Assert.Contains("7", same);
        Assert.Equal(culture.DateTimeFormat.GetDayName(DayOfWeek.Saturday), culture.DateTimeFormat.GetDayName(date.DayOfWeek));
        Assert.Contains(culture.DateTimeFormat.GetDayName(DayOfWeek.Saturday), same);
        Assert.False(same.StartsWith(' ') || same.EndsWith(' ') || same.EndsWith(',') || same.Contains("  "));
    }

    [Fact]
    public void ShortMonthDayAgreesWithDayMonthShort()
    {
        var date = new DateTime(2026, 9, 25);

        Assert.Equal("25. Sep", DateLabels.ShortMonthDay(date, German));
        Assert.Equal("Sep 25", DateLabels.ShortMonthDay(date, English));
    }
}
