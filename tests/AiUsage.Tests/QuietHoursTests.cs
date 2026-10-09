using AiUsage.Models;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class QuietHoursTests
{
    [Theory]
    [InlineData(9, 0, true)]
    [InlineData(10, 0, true)]
    [InlineData(16, 59, true)]
    [InlineData(17, 0, false)]
    [InlineData(20, 0, false)]
    [InlineData(8, 59, false)]
    public void WindowInsideOneDayIsQuietOnlyBetweenStartAndEnd(int hour, int minute, bool expected)
    {
        var now = new TimeOnly(hour, minute);

        Assert.Equal(expected, QuietHours.IsQuiet(now, new TimeOnly(9, 0), new TimeOnly(17, 0)));
    }

    [Theory]
    [InlineData(22, 0, true)]
    [InlineData(23, 30, true)]
    [InlineData(0, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(7, 59, true)]
    [InlineData(8, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(21, 59, false)]
    public void WindowWrappingMidnightIsQuietOnEitherSideOfMidnight(int hour, int minute, bool expected)
    {
        var now = new TimeOnly(hour, minute);

        Assert.Equal(expected, QuietHours.IsQuiet(now, new TimeOnly(22, 0), new TimeOnly(8, 0)));
    }

    [Fact]
    public void ExactlyAtStartIsQuietBothPlainAndWrapping()
    {
        Assert.True(QuietHours.IsQuiet(new TimeOnly(9, 0), new TimeOnly(9, 0), new TimeOnly(17, 0)));
        Assert.True(QuietHours.IsQuiet(new TimeOnly(22, 0), new TimeOnly(22, 0), new TimeOnly(8, 0)));
    }

    [Fact]
    public void ExactlyAtEndIsNotQuietBothPlainAndWrapping()
    {
        Assert.False(QuietHours.IsQuiet(new TimeOnly(17, 0), new TimeOnly(9, 0), new TimeOnly(17, 0)));
        Assert.False(QuietHours.IsQuiet(new TimeOnly(8, 0), new TimeOnly(22, 0), new TimeOnly(8, 0)));
    }

    [Fact]
    public void DisabledSettingIsNeverQuietEvenWithAnAllDayWindow()
    {
        var settings = new AppSettings { QuietHoursEnabled = false, QuietHoursStart = "00:00", QuietHoursEnd = "23:59" };

        Assert.False(QuietHours.IsQuiet(settings, LocalNoon(5)));
    }

    [Fact]
    public void EnabledSettingUsesTheStoredHoursAgainstTheGivenLocalTime()
    {
        var settings = new AppSettings { QuietHoursEnabled = true, QuietHoursStart = "00:00", QuietHoursEnd = "23:59" };

        Assert.True(QuietHours.IsQuiet(settings, LocalNoon(5)));
    }

    private static DateTimeOffset LocalNoon(int day) =>
        new(new DateTime(2026, 1, day, 12, 0, 0, DateTimeKind.Local));

    [Theory]
    [InlineData(3)] // Saturday
    [InlineData(4)] // Sunday
    public void TheWeekendSwitchMakesSaturdayAndSundayQuietEvenWithTheDailyWindowOff(int day)
    {
        var settings = new AppSettings { QuietHoursEnabled = false, QuietWeekend = true };

        Assert.True(QuietHours.IsQuiet(settings, LocalNoon(day)));
    }

    [Fact]
    public void WithoutTheWeekendSwitchSaturdayNoonIsNotQuiet()
    {
        var settings = new AppSettings { QuietHoursEnabled = false, QuietWeekend = false };

        Assert.False(QuietHours.IsQuiet(settings, LocalNoon(3)));
    }

    [Fact]
    public void TheWeekendSwitchLeavesAMondayToTheDailyWindow()
    {
        var off = new AppSettings { QuietHoursEnabled = false, QuietWeekend = true };
        var daily = new AppSettings { QuietHoursEnabled = true, QuietWeekend = true, QuietHoursStart = "22:00", QuietHoursEnd = "08:00" };

        Assert.False(QuietHours.IsQuiet(off, LocalNoon(5)));
        Assert.False(QuietHours.IsQuiet(daily, LocalNoon(5)));
        Assert.True(QuietHours.IsQuiet(daily, new DateTimeOffset(new DateTime(2026, 1, 5, 23, 0, 0, DateTimeKind.Local))));
    }

    [Fact]
    public void AnUnparsableStoredValueFallsBackToTheDocumentedDefaultRatherThanCrashing()
    {
        var settings = new AppSettings { QuietHoursEnabled = true, QuietHoursStart = "not-a-time", QuietHoursEnd = "also-not-a-time" };
        // Falls back to 22:00-08:00 - noon sits outside that window regardless of what the broken
        // strings said.
        var noonLocal = LocalNoon(5);

        Assert.False(QuietHours.IsQuiet(settings, noonLocal));
    }
}
