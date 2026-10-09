using AiUsage.Models;
using AiUsage.Stats;

namespace AiUsage.Tests;

public class NnlsTests
{
    [Fact]
    public void An_exact_non_negative_solution_is_recovered()
    {
        double[][] rows = [[1, 0], [0, 1], [1, 1], [2, 1]];
        double[] b = [3, 5, 8, 11];

        var x = Nnls.Solve(rows, b);

        Assert.Equal(3, x[0], 6);
        Assert.Equal(5, x[1], 6);
    }

    [Fact]
    public void A_coefficient_that_would_turn_negative_stays_at_zero()
    {
        // b falls as the second column grows, so the unconstrained fit would use a negative weight for it.
        double[][] rows = [[1, 0], [1, 1], [1, 2], [1, 3]];
        double[] b = [4, 3, 2, 1];

        var x = Nnls.Solve(rows, b);

        Assert.True(x.All(value => value >= 0));
        Assert.Equal(0, x[1], 9);
        Assert.Equal(2.5, x[0], 6);
    }

    [Fact]
    public void Columns_of_very_different_size_solve_alike()
    {
        double[][] rows = [[1e7, 1], [2e7, 3], [3e7, 2], [4e7, 5], [5e7, 4]];
        double[] expected = [2e-7, 0.5];
        var b = rows.Select(row => row[0] * expected[0] + row[1] * expected[1]).ToArray();

        var x = Nnls.Solve(rows, b);

        Assert.Equal(expected[0], x[0], 12);
        Assert.Equal(expected[1], x[1], 9);
    }

    [Fact]
    public void An_all_zero_column_or_target_gives_zeros()
    {
        Assert.Equal([0.0, 0.0], Nnls.Solve([[0, 0], [0, 0]], [1, 2]));
        Assert.Equal([0.0], Nnls.Solve([[1], [2]], [0, 0]));
        Assert.Empty(Nnls.Solve([], []));
    }
}

public class StatsTokensPerPercentTests
{
    private const string ModelA = "claude-opus-4-8";
    private const string ModelB = "claude-sonnet-5";

    private static IReadOnlyList<PerPercentInterval> Synthetic(int count, double noise, int seed, bool unrelated = false)
    {
        var random = new Random(seed);
        var intervals = new List<PerPercentInterval>();
        for (var i = 0; i < count; i++)
        {
            var a = random.Next(50_000, 500_000);
            var b = random.Next(50_000, 800_000);
            var cache = random.Next(0, 5_000_000);
            var delta = unrelated
                ? random.NextDouble()
                : (a / 2e6 + b / 1e6 + cache / 20e6) * (1 + (random.NextDouble() * 2 - 1) * noise);
            intervals.Add(new PerPercentInterval(
                delta, new Dictionary<string, long> { ["Opus 4.8"] = a, ["Sonnet 5"] = b }, cache));
        }

        return intervals;
    }

    private static void AssertNear(long expected, long actual, double tolerance) =>
        Assert.InRange(actual, expected * (1 - tolerance), expected * (1 + tolerance));

    [Fact]
    public void Known_weights_are_recovered_from_noisy_synthetic_hours()
    {
        var result = StatsTokensPerPercent.Estimate(Synthetic(80, noise: 0.02, seed: 7));

        Assert.Equal(PerPercentStatus.PerModel, result.Status);
        Assert.Equal(80, result.IntervalCount);
        var opus = Assert.Single(result.Items, item => item.Model == "Opus 4.8");
        var sonnet = Assert.Single(result.Items, item => item.Model == "Sonnet 5");
        AssertNear(2_000_000, opus.TokensPerPercent, 0.10);
        AssertNear(1_000_000, sonnet.TokensPerPercent, 0.10);
        AssertNear(20_000_000, result.CacheReadPerPercent, 0.15);
        Assert.True(result.TotalPerPercent > 0);
    }

    [Fact]
    public void A_fit_that_explains_little_falls_back_to_one_figure_for_all_models()
    {
        var intervals = Synthetic(60, noise: 0, seed: 3, unrelated: true);

        var result = StatsTokensPerPercent.Estimate(intervals);

        Assert.Equal(PerPercentStatus.Total, result.Status);
        Assert.Empty(result.Items);
        var tokens = intervals.Sum(interval => interval.TokensByModel.Values.Sum());
        AssertNear((long)(tokens / intervals.Sum(interval => interval.DeltaPercent)), result.TotalPerPercent, 0.001);
    }

    [Fact]
    public void Fewer_than_twenty_hours_give_no_estimate()
    {
        var result = StatsTokensPerPercent.Estimate(Synthetic(19, noise: 0.02, seed: 7));

        Assert.Equal(PerPercentStatus.TooFew, result.Status);
        Assert.Equal(19, result.IntervalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Hours_without_any_quota_movement_give_no_estimate()
    {
        var flat = Synthetic(30, noise: 0, seed: 1).Select(interval => interval with { DeltaPercent = 0 }).ToList();

        Assert.Equal(PerPercentStatus.TooFew, StatsTokensPerPercent.Estimate(flat).Status);
    }

    [Fact]
    public void A_model_below_five_percent_of_the_tokens_is_left_out_of_the_line()
    {
        var random = new Random(11);
        var intervals = new List<PerPercentInterval>();
        for (var i = 0; i < 60; i++)
        {
            var big = random.Next(100_000, 600_000);
            var tiny = random.Next(0, 5_000);
            intervals.Add(new PerPercentInterval(
                big / 1e6 + tiny / 1e6, new Dictionary<string, long> { ["Opus 4.8"] = big, ["Haiku 4.5"] = tiny }, 0));
        }

        var result = StatsTokensPerPercent.Estimate(intervals);

        Assert.Equal(PerPercentStatus.PerModel, result.Status);
        Assert.Equal(["Opus 4.8"], result.Items.Select(item => item.Model));
    }

    [Fact]
    public void More_than_six_models_pool_the_rest_as_other()
    {
        var random = new Random(5);
        var intervals = new List<PerPercentInterval>();
        for (var i = 0; i < 120; i++)
        {
            var tokens = new Dictionary<string, long>();
            double delta = 0;
            for (var m = 0; m < 8; m++)
            {
                var amount = random.Next(10_000, 100_000);
                tokens["Model " + m] = amount;
                delta += amount / 1e6;
            }

            intervals.Add(new PerPercentInterval(delta, tokens, 0));
        }

        var result = StatsTokensPerPercent.Estimate(intervals);

        Assert.Equal(PerPercentStatus.PerModel, result.Status);
        Assert.Single(result.Items, item => item.IsOther);
        Assert.True(result.Items.Count <= 7);
    }

    private static StatsRecord Record(DateTime hour, string model, long tokens, long cacheRead = 0) =>
        new("claude", DateOnly.FromDateTime(hour), model, "", tokens, 0, 0, cacheRead, hour.Hour);

    [Fact]
    public void Intervals_pair_each_whole_hour_with_the_quota_movement_across_it()
    {
        var start = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Local);
        var reset = new DateTimeOffset(start).AddDays(3);
        var points = Enumerable.Range(0, 31)
            .Select(h => new HistoryPoint(1, new DateTimeOffset(start.AddHours(h)), WindowKind.Weekly, h * 0.5, reset))
            .ToList();
        var records = Enumerable.Range(0, 30).Select(h => Record(start.AddHours(h), ModelA, 1000, cacheRead: 5000)).ToList();

        var intervals = StatsTokensPerPercent.BuildIntervals(records, points);

        Assert.Equal(30, intervals.Count);
        Assert.All(intervals, interval =>
        {
            Assert.Equal(0.5, interval.DeltaPercent, 6);
            Assert.Equal(1000, interval.TokensByModel["Opus 4.8"]);
            Assert.Equal(5000, interval.CacheReadTokens);
        });
    }

    [Fact]
    public void An_hour_without_a_recent_reading_at_either_end_is_skipped()
    {
        var start = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Local);
        var reset = new DateTimeOffset(start).AddDays(3);
        // Readings at 08:00, 09:00, 09:30 and 11:00: hour 09 ends without a reading in the last 15 minutes
        // before 10:00, hour 10 starts from that same stale reading.
        var times = new[] { 0.0, 1.0, 1.5, 3.0 };
        var points = times
            .Select(h => new HistoryPoint(1, new DateTimeOffset(start).AddHours(h), WindowKind.Weekly, h, reset))
            .ToList();
        var records = Enumerable.Range(0, 3).Select(h => Record(start.AddHours(h), ModelB, 100)).ToList();

        var intervals = StatsTokensPerPercent.BuildIntervals(records, points);

        Assert.Single(intervals);
        Assert.Equal(1.0, intervals[0].DeltaPercent, 6);
    }

    [Fact]
    public void A_fall_of_the_quota_inside_an_hour_is_not_an_interval()
    {
        var start = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Local);
        var reset = new DateTimeOffset(start).AddDays(3);
        var points = new[]
        {
            new HistoryPoint(1, new DateTimeOffset(start), WindowKind.Weekly, 50, reset),
            new HistoryPoint(1, new DateTimeOffset(start).AddHours(1), WindowKind.Weekly, 40, reset),
            new HistoryPoint(1, new DateTimeOffset(start).AddHours(2), WindowKind.Weekly, 45, reset),
        };
        var records = new[] { Record(start, ModelA, 100), Record(start.AddHours(1), ModelA, 100) };

        var intervals = StatsTokensPerPercent.BuildIntervals(records, points);

        Assert.Single(intervals);
        Assert.Equal(5, intervals[0].DeltaPercent, 6);
    }
}
