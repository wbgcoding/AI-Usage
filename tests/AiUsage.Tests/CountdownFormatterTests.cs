using System.Globalization;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class CountdownFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoHoursFourteenMinutes() =>
        Assert.Equal("noch 2h 14m", CountdownFormatter.Format(Now + TimeSpan.FromMinutes(134), Now));

    [Fact]
    public void FourteenMinutes() =>
        Assert.Equal("noch 14m", CountdownFormatter.Format(Now + TimeSpan.FromMinutes(14), Now));

    [Fact]
    public void UnderOneMinute() =>
        Assert.Equal("noch <1m", CountdownFormatter.Format(Now + TimeSpan.FromSeconds(40), Now));

    [Fact]
    public void Expired() =>
        Assert.Equal("wird gerade zurückgesetzt", CountdownFormatter.Format(Now - TimeSpan.FromSeconds(1), Now));

    [Fact]
    public void ResetThirtySecondsAgoStillReadsResettingNow() =>
        Assert.Equal("wird gerade zurückgesetzt", CountdownFormatter.Format(Now - TimeSpan.FromSeconds(30), Now));

    [Fact]
    public void ResetTenMinutesAgoReadsEmpty() =>
        Assert.Equal("", CountdownFormatter.Format(Now - TimeSpan.FromMinutes(10), Now));

    [Fact]
    public void ResetInTheFutureIsUnaffectedByTheExpiryWindow() =>
        Assert.Equal("noch 14m", CountdownFormatter.Format(Now + TimeSpan.FromMinutes(14), Now));

    [Fact]
    public void ThreeDaysFourHours() =>
        Assert.Equal("noch 3d 4h", CountdownFormatter.Format(Now + TimeSpan.FromHours(76), Now));

    [Fact]
    public void UnknownResetStaysEmpty() =>
        Assert.Equal("", CountdownFormatter.Format(null, Now));

    [Fact]
    public void AgeFormatsAsElapsed() =>
        Assert.Equal("Letzte Aktualisierung vor 3h", CountdownFormatter.FormatAge(Now - TimeSpan.FromHours(3), Now));

    [Fact]
    public void AgeWithoutTimestampStaysEmpty() =>
        Assert.Equal("", CountdownFormatter.FormatAge(null, Now));

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(1, "1s")]
    [InlineData(59, "59s")]
    public void ElapsedUnderOneMinuteRunsTheSecondsAlong(int seconds, string expected) =>
        Assert.Equal(expected, CountdownFormatter.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ElapsedSixtySecondsStillReadsOneMinute() =>
        Assert.Equal("1m", CountdownFormatter.FormatElapsed(TimeSpan.FromSeconds(60)));

    [Theory]
    [InlineData("de", 9, "noch 9h")]
    [InlineData("en", 9, "resets in 9h")]
    public void NineHoursExactlyDropsTheMinutes(string language, int hours, string expected)
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(language);
        try
        {
            Assert.Equal(expected, CountdownFormatter.Format(Now + TimeSpan.FromHours(hours), Now));
            Assert.Equal("9h", CountdownFormatter.FormatElapsed(TimeSpan.FromHours(hours)));
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Theory]
    [InlineData("de", 19, "noch 19d")]
    [InlineData("en", 19, "resets in 19d")]
    public void NineteenDaysExactlyDropsTheHours(string language, int days, string expected)
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(language);
        try
        {
            Assert.Equal(expected, CountdownFormatter.Format(Now + TimeSpan.FromDays(days), Now));
            Assert.Equal("19d", CountdownFormatter.FormatElapsed(TimeSpan.FromDays(days)));
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void NonZeroRemaindersKeepBothUnits()
    {
        Assert.Equal("19d 1h", CountdownFormatter.FormatElapsed(TimeSpan.FromHours(19 * 24 + 1)));
        Assert.Equal("9h 1m", CountdownFormatter.FormatElapsed(TimeSpan.FromMinutes(9 * 60 + 1)));
    }

    [Fact]
    public void ClockAppendedAsBareTimeWhenUnderOneDayAway()
    {
        var reset = Now + TimeSpan.FromMinutes(134);
        var expectedClock = reset.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

        Assert.Equal($"noch 2h 14m · {expectedClock}", CountdownFormatter.FormatWithClock(reset, Now));
    }

    [Fact]
    public void ClockAppendedWithWeekdayWhenADayOrMoreAway()
    {
        var reset = Now + TimeSpan.FromHours(76);
        var expectedClock = reset.ToLocalTime().ToString("ddd " + CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, CultureInfo.CurrentCulture);

        Assert.Equal($"noch 3d 4h · {expectedClock}", CountdownFormatter.FormatWithClock(reset, Now));
    }

    [Fact]
    public void AWeekdayClockUsesTheCulturesOwnTimeFormat()
    {
        var reset = new DateTimeOffset(2026, 10, 5, 23, 59, 0, TimeSpan.Zero);
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var clock = CountdownFormatter.FormatClock(reset, reset - TimeSpan.FromDays(3));

            Assert.EndsWith(reset.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture), clock);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("en-US", "Oct 22 9:32 PM")]
    [InlineData("de-DE", "22. Okt 21:32")]
    public void ClockUsesTheDateFromSixDaysOut(string culture, string expected)
    {
        var reset = new DateTimeOffset(new DateTime(2026, 10, 22, 21, 32, 0, DateTimeKind.Local));
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            Assert.Equal(expected, CountdownFormatter.FormatClock(reset, reset - TimeSpan.FromDays(19)));
            Assert.Equal(expected, CountdownFormatter.FormatClock(reset, reset - TimeSpan.FromDays(6)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ClockKeepsTheWeekdayUnderSixDays()
    {
        var reset = new DateTimeOffset(new DateTime(2026, 10, 22, 21, 32, 0, DateTimeKind.Local));
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal("Thu 9:32 PM", CountdownFormatter.FormatClock(reset, reset - TimeSpan.FromDays(5.5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FormatWithClockStaysEmptyWithoutAKnownResetTime() =>
        Assert.Equal("", CountdownFormatter.FormatWithClock(null, Now));

    // Format subtracts two DateTimeOffset values, which is absolute-time arithmetic - immune by
    // construction to a daylight-saving change, but that was never pinned by a test. These three
    // facts pin it against the real European transitions in 2026, using explicit offsets rather
    // than the machine's own zone so the result does not depend on where the test happens to run.

    [Fact]
    public void ACountdownAcrossTheEuropeanFallBackTransitionStaysCorrect()
    {
        var reset = new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2));
        var now = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal("noch 1h", CountdownFormatter.Format(reset, now));
    }

    [Fact]
    public void TheSameCountdownSeenFromTheRepeatedHourAfterClocksGoBackStaysCorrect()
    {
        // The same reset/now shape as above, one real hour apart, viewed entirely from the
        // repeated hour after the clocks fell back (+01:00 instead of +02:00) - the two instants
        // are still exactly an hour apart, which side of the transition their offsets fall on
        // changes nothing about the subtraction.
        var reset = new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(1));
        var now = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.FromHours(1));

        Assert.Equal("noch 1h", CountdownFormatter.Format(reset, now));
    }

    [Fact]
    public void AResetInsideTheSpringForwardSkippedHourStillRendersAClockTimeThatExists()
    {
        // 2026-03-29 02:00-03:00 never exists as a local Europe/Berlin wall-clock reading - clocks
        // jump straight from 02:00 CET to 03:00 CEST. A DateTimeOffset with an explicit numeric
        // offset never consults real zone rules, though, so 02:30+01:00 still constructs as a
        // perfectly ordinary absolute instant. FormatClock renders it through
        // DateTimeOffset.ToLocalTime(), which can never reproduce an invalid wall-clock reading
        // regardless of the host's own zone - pinned against the actually rendered value rather
        // than an assumed one.
        var reset = new DateTimeOffset(2026, 3, 29, 2, 30, 0, TimeSpan.FromHours(1));
        var now = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        var expectedClock = reset.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

        Assert.Equal(expectedClock, CountdownFormatter.FormatClock(reset, now));
    }

    [Fact]
    public void TheDateLabelFollowsCalendarDaysAcrossASpringForward()
    {
        TimeZoneInfo berlin;
        try
        {
            berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        }
        catch (TimeZoneNotFoundException)
        {
            berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }

        // Six calendar days apart, but the clock change in between makes it one hour less than 144 hours.
        var now = new DateTimeOffset(2026, 3, 23, 12, 30, 0, TimeSpan.FromHours(1));
        var reset = new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.FromHours(2));
        Assert.True(reset - now < TimeSpan.FromDays(6));
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal("Mar 29 12:00 PM", CountdownFormatter.FormatClock(reset, now, berlin));
            // Five calendar days: still the weekday.
            Assert.Equal("Sat 12:00 PM", CountdownFormatter.FormatClock(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.FromHours(1)), now, berlin));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
