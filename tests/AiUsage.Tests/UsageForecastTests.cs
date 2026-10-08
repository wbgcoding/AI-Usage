using AiUsage.Services;
using Xunit;
using ChartPoint = AiUsage.Views.Controls.HistoryChart.ChartPoint;

namespace AiUsage.Tests;

public class UsageForecastTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ChartPoint[] RisingSeries(int count, TimeSpan step, double startPercent, double percentPerStep)
    {
        var points = new ChartPoint[count];
        for (var i = 0; i < count; i++)
            points[i] = new ChartPoint(Start + step * i, startPercent + percentPerStep * i);
        return points;
    }

    [Fact]
    public void ATrendTooSmallToBeRealReturnsNullInsteadOfOverflowing()
    {
        var points = RisingSeries(10, TimeSpan.FromMinutes(5), 33.3, 1e-13);

        var result = UsageForecast.TimeToFull(points, points[^1].At, resetsAt: null);

        Assert.Null(result);
    }

    // Points from the window before a reset used to stay in the fit: the fall from 90 % to 0 %
    // turned the slope negative and hid a new window that was filling fast.
    [Fact]
    public void PointsFromBeforeAResetDoNotHideAFastNewWindow()
    {
        var before = RisingSeries(8, TimeSpan.FromMinutes(5), 80, 1);
        var after = Enumerable.Range(0, 8)
            .Select(i => new ChartPoint(before[^1].At + TimeSpan.FromMinutes(5) * (i + 1), 10 * i))
            .ToArray();
        var points = before.Concat(after).ToArray();

        var result = UsageForecast.TimeToFull(points, points[^1].At, resetsAt: null);

        Assert.NotNull(result);
    }

    [Fact]
    public void FewerThanMinimumPointsInsideTheLookbackReturnsNull()
    {
        // 5 points, 5 minutes apart (20 min span - well over the minimum) - only the count is short.
        var points = RisingSeries(5, TimeSpan.FromMinutes(5), 32, 2);

        var result = UsageForecast.TimeToFull(points, points[^1].At, resetsAt: null);

        Assert.Null(result);
    }

    [Fact]
    public void PointsSpanningLessThanFifteenMinutesReturnsNull()
    {
        // 10 points (enough of them) but only a minute apart - 9 minutes of real time, under the floor.
        var points = RisingSeries(10, TimeSpan.FromMinutes(1), 32, 2);

        var result = UsageForecast.TimeToFull(points, points[^1].At, resetsAt: null);

        Assert.Null(result);
    }

    [Fact]
    public void FlatSlopeReturnsNull()
    {
        var points = RisingSeries(10, TimeSpan.FromMinutes(5), 42, 0);

        var result = UsageForecast.TimeToFull(points, points[^1].At, resetsAt: null);

        Assert.Null(result);
    }

    [Fact]
    public void FallingSlopeReturnsNull()
    {
        var points = RisingSeries(10, TimeSpan.FromMinutes(5), 60, -2);

        var result = UsageForecast.TimeToFull(points, points[^1].At, resetsAt: null);

        Assert.Null(result);
    }

    [Fact]
    public void AlreadyAtOrAboveAHundredReturnsZero()
    {
        var points = RisingSeries(10, TimeSpan.FromMinutes(5), 82, 2); // ends exactly at 100

        var result = UsageForecast.TimeToFull(points, points[^1].At, resetsAt: null);

        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void AProjectionLandingPastTheResetReturnsNullInstead()
    {
        var points = RisingSeries(10, TimeSpan.FromMinutes(5), 32, 2); // projects to about 2h5m
        var now = points[^1].At;

        var result = UsageForecast.TimeToFull(points, now, resetsAt: now.AddMinutes(30)); // resets long before

        Assert.Null(result);
    }

    [Fact]
    public void NoResetsAtReturnsTheRawProjection()
    {
        var points = RisingSeries(10, TimeSpan.FromMinutes(5), 32, 2);

        var result = UsageForecast.TimeToFull(points, points[^1].At, resetsAt: null);

        Assert.NotNull(result);
    }

    [Fact]
    public void StraightLineCaseMatchesTheHandCheckedValue()
    {
        // 10 points, 5 minutes apart, rising 2% every step, ending at 50% - a perfect straight line
        // whose slope is exactly 2% per 5 minutes. From 50%, reaching 100% takes (100-50)/0.4%-per-
        // minute = 125 minutes = 2h 5m.
        var points = RisingSeries(10, TimeSpan.FromMinutes(5), 32, 2);

        var result = UsageForecast.TimeToFull(points, points[^1].At, resetsAt: null);

        Assert.NotNull(result);
        Assert.True(Math.Abs((TimeSpan.FromMinutes(125) - result!.Value).TotalSeconds) < 1,
            $"expected about 2h 5m, got {result.Value}");
    }
}
