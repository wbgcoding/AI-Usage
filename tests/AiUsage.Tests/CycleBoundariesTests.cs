using AiUsage.Models;
using AiUsage.Stats;

namespace AiUsage.Tests;

public class CycleBoundariesTests
{
    private static HistoryPoint Weekly(string time, double percent, string reset, string? plan = null, bool? early = null) =>
        new(1, DateTimeOffset.Parse(time), WindowKind.Weekly, percent, DateTimeOffset.Parse(reset), Plan: plan, EarlyReset: early);

    private static HistoryPoint FiveHour(string time, double percent, string reset) =>
        new(1, DateTimeOffset.Parse(time), WindowKind.FiveHour, percent, DateTimeOffset.Parse(reset));

    [Fact]
    public void A_real_used_limit_reset_is_an_early_reset()
    {
        var before = Weekly("2026-10-17T14:38:00+02:00", 100, "2026-10-21T14:00:00+00:00", "Max");
        var after = Weekly("2026-10-17T14:42:00+02:00", 4, "2026-10-21T14:00:00+00:00", "Max");

        Assert.Equal(CycleBoundary.EarlyReset, CycleBoundaries.Classify(before, after));
    }

    [Fact]
    public void A_plan_change_with_the_same_reset_time_is_a_plan_change_not_an_early_reset()
    {
        var before = Weekly("2026-11-07T19:45:00+01:00", 83, "2026-11-11T14:00:00+00:00", "Max");
        var after = Weekly("2026-11-07T19:47:00+01:00", 0, "2026-11-11T14:00:00+00:00", "Ultra");

        Assert.Equal(CycleBoundary.PlanChange, CycleBoundaries.Classify(before, after));
    }

    [Fact]
    public void A_moved_reset_time_is_a_regular_reset()
    {
        var before = Weekly("2026-10-21T13:50:00+00:00", 62, "2026-10-21T14:00:00+00:00", "Max");
        var after = Weekly("2026-10-21T14:05:00+00:00", 1, "2026-10-28T14:00:00+00:00", "Max");

        Assert.Equal(CycleBoundary.Regular, CycleBoundaries.Classify(before, after));
    }

    [Fact]
    public void A_jittering_reset_time_with_a_steady_rise_is_no_boundary()
    {
        var before = FiveHour("2026-11-14T10:00:00+00:00", 30, "2026-11-14T13:00:05+00:00");
        var after = FiveHour("2026-11-14T10:20:00+00:00", 41, "2026-11-14T13:00:00+00:00");

        Assert.Equal(CycleBoundary.None, CycleBoundaries.Classify(before, after));
    }

    [Fact]
    public void A_drop_with_a_jittering_reset_time_restarts_the_cycle()
    {
        var before = FiveHour("2026-11-14T10:00:00+00:00", 88, "2026-11-14T13:00:05+00:00");
        var after = FiveHour("2026-11-14T10:20:00+00:00", 6, "2026-11-14T13:19:50+00:00");

        Assert.Equal(CycleBoundary.EarlyReset, CycleBoundaries.Classify(before, after));
    }

    [Theory]
    [InlineData(60, 50)]
    [InlineData(100, 90)]
    [InlineData(30, 16)]
    [InlineData(19, 0)]
    public void A_slow_decline_or_a_low_start_is_no_reset(double was, double now)
    {
        var before = Weekly("2026-11-10T10:00:00+00:00", was, "2026-11-14T14:00:00+00:00", "Max");
        var after = Weekly("2026-11-10T10:30:00+00:00", now, "2026-11-14T14:00:00+00:00", "Max");

        Assert.Equal(CycleBoundary.None, CycleBoundaries.Classify(before, after));
    }

    [Fact]
    public void A_stored_flag_decides_without_the_values()
    {
        var before = Weekly("2026-11-10T10:00:00+00:00", 40, "2026-11-14T14:00:00+00:00");
        var after = Weekly("2026-11-10T10:30:00+00:00", 30, "2026-11-14T14:00:00+00:00", early: true);

        Assert.Equal(CycleBoundary.EarlyReset, CycleBoundaries.Classify(before, after));
    }

    [Fact]
    public void An_unknown_plan_is_never_a_plan_change()
    {
        var before = Weekly("2026-11-10T10:00:00+00:00", 40, "2026-11-14T14:00:00+00:00");
        var after = Weekly("2026-11-10T10:30:00+00:00", 45, "2026-11-14T14:00:00+00:00", "Ultra");

        Assert.Equal(CycleBoundary.None, CycleBoundaries.Classify(before, after));
    }

    [Fact]
    public void Kinds_marks_each_reading_against_the_one_before_it()
    {
        var series = new[]
        {
            Weekly("2026-10-17T14:30:00+02:00", 90, "2026-10-21T14:00:00+00:00"),
            Weekly("2026-10-17T14:38:00+02:00", 100, "2026-10-21T14:00:00+00:00"),
            Weekly("2026-10-17T14:42:00+02:00", 4, "2026-10-21T14:00:00+00:00"),
            Weekly("2026-10-21T14:10:00+00:00", 2, "2026-10-28T14:00:00+00:00"),
        };

        Assert.Equal(
            [CycleBoundary.None, CycleBoundary.None, CycleBoundary.EarlyReset, CycleBoundary.Regular],
            CycleBoundaries.Kinds(series));
    }
}
