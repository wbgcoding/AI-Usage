using AiUsage.Models;
using AiUsage.Views.Controls;
using Xunit;
using ChartPoint = AiUsage.Views.Controls.HistoryChart.ChartPoint;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class HistoryChartGeometryTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OneDayLater = Start.AddDays(1);

    [Fact]
    public void EmptySeriesReducesToEmpty() =>
        Assert.Empty(HistoryChart.ReduceToColumns([], 100, Start, OneDayLater));

    [Fact]
    public void SinglePointPassesThroughUnchanged()
    {
        var at = Start.AddHours(5);

        var reduced = HistoryChart.ReduceToColumns([new ChartPoint(at, 42.0)], 100, Start, OneDayLater);

        Assert.Equal([new ChartPoint(at, 42.0)], reduced);
    }

    [Fact]
    public void ThirtyThousandEvenlySpacedPointsReduceToExactlyTwoHundredColumns()
    {
        // 30000 points spread evenly across the 24h range, one every 2.88 seconds - each of the 200
        // buckets (7.2 minutes each) then holds exactly 150 of them. The +0.5 keeps every point
        // solidly inside its intended bucket instead of exactly on a boundary, where floating-point
        // rounding could otherwise nudge it into the neighbor.
        var points = Enumerable.Range(0, 30_000)
            .Select(i => new ChartPoint(Start + TimeSpan.FromTicks((long)((OneDayLater - Start).Ticks * (i + 0.5) / 30_000)), i))
            .ToArray();

        var reduced = HistoryChart.ReduceToColumns(points, 200, Start, OneDayLater);

        Assert.Equal(200, reduced.Count);
        for (var c = 0; c < 200; c++)
            Assert.Equal(c * 150 + 74.5, reduced[c].Percent);
    }

    [Fact]
    public void AllValuesEqualStaysAFlatLine()
    {
        var points = Enumerable.Range(0, 500)
            .Select(i => new ChartPoint(Start + TimeSpan.FromTicks((OneDayLater - Start).Ticks * i / 500), 37.0))
            .ToArray();

        var reduced = HistoryChart.ReduceToColumns(points, 64, Start, OneDayLater);

        Assert.All(reduced, p => Assert.Equal(37.0, p.Percent));
    }

    [Fact]
    public void PointsFarApartInTimeLandInDistantBucketsRegardlessOfSampleCount()
    {
        // Two points a minute apart plus one eight hours later - an index-based reducer would treat
        // "close in the list" as "close on screen"; a time-based one must not.
        ChartPoint[] points =
        [
            new(Start.AddMinutes(1), 10),
            new(Start.AddMinutes(2), 20),
            new(Start.AddHours(8), 90),
        ];

        var reduced = HistoryChart.ReduceToColumns(points, columns: 24, Start, OneDayLater);

        Assert.Equal(2, reduced.Count); // the two close points share hour 0's bucket, the distant one is its own
        Assert.Equal(15.0, reduced[0].Percent); // average of 10 and 20
        Assert.Equal(90.0, reduced[1].Percent);
    }

    [Fact]
    public void AnEmptyBucketProducesNoPointRatherThanRepeatingThePreviousValue()
    {
        ChartPoint[] points = [new(Start.AddHours(1), 50), new(Start.AddHours(10), 80)]; // hours 2-9 empty

        var reduced = HistoryChart.ReduceToColumns(points, columns: 24, Start, OneDayLater);

        Assert.Equal(2, reduced.Count);
        Assert.Equal(50.0, reduced[0].Percent);
        Assert.Equal(80.0, reduced[1].Percent);
    }

    [Fact]
    public void GridLinesPlacesTheHundredPercentLineAtTheTopAndTheFiftyPercentLineDashedAtHalfHeight()
    {
        // 64 px control with the caption strip: the grid spans the 52 px plot area above it.
        var lines = HistoryChart.GridLines(HistoryChart.PlotHeight(64, showAxes: true));

        Assert.Equal(2, lines.Count);
        Assert.Equal((0.5, false), lines[0]);
        Assert.Equal((26.5, true), lines[1]);
    }

    [Fact]
    public void CaptionsSitBelowThePlotArea()
    {
        Assert.Equal(52, HistoryChart.PlotHeight(64, showAxes: true));

        // The lowest a curve can go is 0 %, and that must stay above the 12 px strip.
        Assert.True(HistoryChart.MapY(0, HistoryChart.PlotHeight(64, showAxes: true)) <= 64 - HistoryChart.CaptionStripHeight);
        Assert.Equal(0, HistoryChart.MapY(100, 52));
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(47, true)]
    public void WithoutCaptionsThePlotKeepsTheWholeHeight(double height, bool showAxes) =>
        Assert.Equal(height, HistoryChart.PlotHeight(height, showAxes));

    [Fact]
    public void RangeCaptionUsesClockTimeForARangeOfADayOrLess()
    {
        var at = new DateTimeOffset(2026, 3, 15, 14, 30, 0, TimeSpan.Zero);

        var caption = HistoryChart.RangeCaption(at, TimeSpan.FromHours(24));

        Assert.Equal(at.LocalDateTime.ToString("t", System.Globalization.CultureInfo.CurrentCulture), caption);
    }

    [Theory]
    [InlineData("en-US", "Sep 25")]
    [InlineData("de-DE", "25. Sept.")] // the ICU data shortens September to "Sept."
    public void RangeCaptionUsesAShortDateAboveADay(string culture, string expected)
    {
        var at = new DateTimeOffset(new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Local));

        var caption = WithCulture(culture, () => HistoryChart.RangeCaption(at, TimeSpan.FromDays(30)));

        Assert.Equal(expected, caption);
    }

    [Fact]
    public void RangeCaptionAddsTheYearOnlyAcrossAYearBoundary()
    {
        var sameYearStart = new DateTimeOffset(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Local));
        var sameYearEnd = new DateTimeOffset(new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Local));
        var acrossStart = new DateTimeOffset(new DateTime(2025, 12, 20, 12, 0, 0, DateTimeKind.Local));
        var acrossEnd = new DateTimeOffset(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Local));

        var (sameStart, sameEnd) = WithCulture("en-US", () => HistoryChart.RangeCaptions(sameYearStart, sameYearEnd));
        var (crossStart, crossEnd) = WithCulture("en-US", () => HistoryChart.RangeCaptions(acrossStart, acrossEnd));

        Assert.Equal(("Sep 1", "Sep 25"), (sameStart, sameEnd));
        Assert.Equal(("Dec 20 2025", "Jan 15"), (crossStart, crossEnd));
    }

    [Fact]
    public void ADayLongRangeNamesTheStartDaySoTheTwoClockTimesDiffer()
    {
        var start = new DateTimeOffset(new DateTime(2026, 10, 4, 20, 13, 0, DateTimeKind.Local));
        var end = new DateTimeOffset(new DateTime(2026, 10, 5, 20, 13, 0, DateTimeKind.Local));

        var captions = WithCulture("de-DE", () => HistoryChart.RangeCaptions(start, end));

        Assert.Equal(("4. Okt. 20:13", "20:13"), captions);
    }

    [Fact]
    public void AnUnsetRangeHasNoCaptions()
    {
        Assert.Equal(("", ""), HistoryChart.RangeCaptions(default, default));
    }

    [Theory]
    [InlineData("en-US", "Sep 25 12:00 PM")]
    [InlineData("de-DE", "25. Sept. 12:00")]
    public void HoverTimeNamesTheMonthAndDayWithoutAYearAboveADay(string culture, string expected)
    {
        var at = new DateTimeOffset(new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Local));

        Assert.Equal(expected, WithCulture(culture, () => HistoryChart.HoverTime(at, TimeSpan.FromDays(7))));
    }

    private static T WithCulture<T>(string name, Func<T> action)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(name);
        try
        {
            return action();
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void NearestIndexFindsAnExactHit()
    {
        var end = Start.AddHours(24);
        ChartPoint[] points = [new(Start.AddHours(12), 50)]; // maps to x=100 at width 200

        var index = HistoryChart.NearestIndex(points, x: 100, width: 200, Start, end);

        Assert.Equal(0, index);
    }

    [Fact]
    public void NearestIndexPicksTheCloserOfTwoPoints()
    {
        var end = Start.AddHours(24);
        ChartPoint[] points = [new(Start.AddHours(6), 10), new(Start.AddHours(18), 90)]; // x=50 and x=150 at width 200

        var index = HistoryChart.NearestIndex(points, x: 140, width: 200, Start, end);

        Assert.Equal(1, index);
    }

    [Fact]
    public void NearestIndexReturnsMinusOneBeyondTheTolerance()
    {
        var end = Start.AddHours(24);
        ChartPoint[] points = [new(Start.AddHours(12), 50)]; // maps to x=100 at width 200

        var index = HistoryChart.NearestIndex(points, x: 200, width: 200, Start, end);

        Assert.Equal(-1, index);
    }

    [Fact]
    public void ASinglePreviousWeekPointReducesToTooFewPointsToDrawALine()
    {
        // DrawPreviousWeekLine skips drawing below two reduced points, exactly like a real series -
        // a lone comparison point is background noise, not a line.
        var reduced = HistoryChart.ReduceToColumns([new ChartPoint(Start.AddHours(5), 42)], 100, Start, OneDayLater);

        Assert.True(reduced.Count < 2);
    }

    [Fact]
    public void FiveHourSeriesIsDashedWithNoFill()
    {
        var style = HistoryChart.SeriesStyle(WindowKind.FiveHour);

        Assert.True(style.IsDashed);
        Assert.False(style.HasFill);
    }

    [Fact]
    public void WeeklySeriesIsSolidAndFilled()
    {
        var style = HistoryChart.SeriesStyle(WindowKind.Weekly);

        Assert.False(style.IsDashed);
        Assert.True(style.HasFill);
    }

    [Fact]
    public void OtherWindowKindDefaultsToTheSameLookAsWeekly()
    {
        var style = HistoryChart.SeriesStyle(WindowKind.Other);

        Assert.False(style.IsDashed);
        Assert.True(style.HasFill);
    }

    [Fact]
    public void HoverPointChangedIsFalseWhenBothSeriesStillMatchTheSamePoint()
    {
        var changed = HistoryChart.HoverPointChanged(previousFiveHourIndex: 3, previousWeeklyIndex: 7, newFiveHourIndex: 3, newWeeklyIndex: 7);

        Assert.False(changed);
    }

    [Fact]
    public void HoverPointChangedIsTrueWhenEitherSeriesMatchesADifferentIndex()
    {
        Assert.True(HistoryChart.HoverPointChanged(previousFiveHourIndex: 3, previousWeeklyIndex: 7, newFiveHourIndex: 4, newWeeklyIndex: 7));
        Assert.True(HistoryChart.HoverPointChanged(previousFiveHourIndex: 3, previousWeeklyIndex: 7, newFiveHourIndex: 3, newWeeklyIndex: 8));
    }

    [Fact]
    public void HoverGuideColumnChangedIsFalseWithinTheSamePixelColumn()
    {
        Assert.False(HistoryChart.HoverGuideColumnChanged(previousGuideX: 120.2, newGuideX: 120.4));
    }

    [Fact]
    public void HoverGuideColumnChangedIsTrueOnceTheRoundedPixelMoves()
    {
        Assert.True(HistoryChart.HoverGuideColumnChanged(previousGuideX: 120.4, newGuideX: 121.4));
    }

    [Fact]
    public void HoverGuideColumnChangedIsTrueForTheFirstEverHover()
    {
        Assert.True(HistoryChart.HoverGuideColumnChanged(previousGuideX: double.NaN, newGuideX: 42));
    }

    [Fact]
    public void PlotXSpansTheFieldLessOnlyTheStrokeInsetOnEachSide()
    {
        Assert.Equal(HistoryChart.StrokeInset, HistoryChart.PlotX(0, 300));
        Assert.Equal(300 - HistoryChart.StrokeInset, HistoryChart.PlotX(1, 300));
    }

    [Fact]
    public void LegendNamesBothSeriesInOrder()
    {
        var entries = HistoryChart.LegendEntries(hasFiveHour: true, hasWeekly: true, "5h", "Week");

        Assert.Equal([(WindowKind.FiveHour, "5h"), (WindowKind.Weekly, "Week")], entries);
    }

    [Fact]
    public void LegendHasOnlyTheSeriesThatIsDrawnAndNamed()
    {
        Assert.Equal([(WindowKind.Weekly, "Week")], HistoryChart.LegendEntries(false, true, "5h", "Week"));
        Assert.Equal([(WindowKind.FiveHour, "5h")], HistoryChart.LegendEntries(true, false, "5h", "Week"));
        Assert.Equal([(WindowKind.FiveHour, "5h")], HistoryChart.LegendEntries(true, true, "5h", ""));
        Assert.Empty(HistoryChart.LegendEntries(false, false, "5h", "Week"));
    }

    [Fact]
    public void LegendWidthAddsPatternGapAndNamePerEntryPlusTenBetweenEntries()
    {
        Assert.Equal(12 + 4 + 30, HistoryChart.LegendWidth([30]));
        Assert.Equal(2 * (12 + 4) + 30 + 40 + 10, HistoryChart.LegendWidth([30, 40]));
    }

    [Fact]
    public void LegendIsCentredWhenEightPixelsStayFreeOnBothSides()
    {
        // Captions 40 wide at a 4 px inset: the legend (100 wide) may start at 52 at the earliest.
        Assert.Equal(100.0, HistoryChart.LegendLeft(300, 40, 40, 100));
        Assert.Equal(52.0, HistoryChart.LegendLeft(204, 40, 40, 100));
    }

    [Fact]
    public void LegendIsDroppedWhenOneSideKeepsLessThanEightPixels()
    {
        Assert.Null(HistoryChart.LegendLeft(203, 40, 40, 100));
        Assert.Null(HistoryChart.LegendLeft(120, 40, 40, 100));
    }
}
