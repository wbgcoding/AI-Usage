using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;

namespace AiUsage.Tests;

public class StatsLimitsAggregatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static HistoryPoint Point(WindowKind window, double percent, DateTimeOffset? resets, int hour = 0, string? label = null) =>
        new(1, Start.AddHours(hour), window, percent, resets, Label: label);

    private static IReadOnlyList<StatsLimitRow> Build(params (string, IReadOnlyList<HistoryPoint>)[] series) =>
        StatsLimitsAggregator.Build(series);

    [Fact]
    public void Two_periods_one_at_the_limit_count_one_reach_and_average_the_peaks()
    {
        var firstReset = Start.AddDays(7);
        var secondReset = Start.AddDays(14);
        var rows = Build(("claude", new[]
        {
            Point(WindowKind.Weekly, 40, firstReset, 0),
            Point(WindowKind.Weekly, 100, firstReset, 10),
            Point(WindowKind.Weekly, 90, firstReset, 20),
            Point(WindowKind.Weekly, 20, secondReset, 200),
            Point(WindowKind.Weekly, 60, secondReset, 210),
        }));

        var row = Assert.Single(rows);
        Assert.Equal("claude", row.ProviderId);
        Assert.Equal(2, row.PeriodCount);
        Assert.Equal(1, row.ReachedCount);
        Assert.Equal(100, row.Highest);
        Assert.Equal(80, row.AveragePeak);
        Assert.Equal("Window_Weekly", row.WindowLabelKey);
    }

    [Fact]
    public void A_wobbling_reset_time_stays_one_period()
    {
        var reset = Start.AddHours(5);
        var rows = Build(("codex", new[]
        {
            Point(WindowKind.FiveHour, 10, reset, 0),
            Point(WindowKind.FiveHour, 30, reset.AddSeconds(4), 1),
            Point(WindowKind.FiveHour, 50, reset.AddSeconds(-3), 2),
        }));

        var row = Assert.Single(rows);
        Assert.Equal(1, row.PeriodCount);
        Assert.Equal(0, row.ReachedCount);
        Assert.Equal(50, row.AveragePeak);
    }

    [Fact]
    public void Points_without_a_reset_time_form_one_period()
    {
        var rows = Build(("cursor", new[]
        {
            Point(WindowKind.Other, 100, null, 0, "Window_Month"),
            Point(WindowKind.Other, 100, null, 5, "Window_Month"),
        }));

        var row = Assert.Single(rows);
        Assert.Equal(1, row.PeriodCount);
        Assert.Equal(1, row.ReachedCount);
        Assert.Equal("Window_Month", row.WindowLabelKey);
    }

    [Fact]
    public void Windows_of_one_provider_are_separate_rows_ordered_five_hour_weekly_other()
    {
        var reset = Start.AddDays(1);
        var rows = Build(
            ("claude", new[]
            {
                Point(WindowKind.Weekly, 30, reset),
                Point(WindowKind.FiveHour, 10, reset),
            }),
            ("codex", new[] { Point(WindowKind.Other, 5, reset, label: "Window_CursorModels") }));

        Assert.Equal(
            [("claude", WindowKind.FiveHour), ("claude", WindowKind.Weekly), ("codex", WindowKind.Other)],
            rows.Select(row => (row.ProviderId, row.Window)));
    }

    [Fact]
    public void A_provider_without_points_gives_no_row()
    {
        Assert.Empty(Build(("claude", Array.Empty<HistoryPoint>())));
    }
}

public class StatsViewModelLimitsTests
{
    private static HistoryPoint Weekly(double percent, DateTimeOffset reset) =>
        new(1, DateTimeOffset.Now.AddHours(-3), WindowKind.Weekly, percent, reset);

    private static StatsViewModel Create(Func<string, DateTimeOffset, DateTimeOffset, IReadOnlyList<HistoryPoint>> history)
    {
        using var directory = TestPaths.CreateDisposableDirectory("stats-limits-vm");
        var viewModel = new StatsViewModel(new StatsStore(directory)) { LoadHistory = history };
        viewModel.Recompute();
        return viewModel;
    }

    [Fact]
    public void Quota_history_shows_one_row_per_window_with_the_formatted_figures()
    {
        var reset = DateTimeOffset.Now.AddDays(2);
        var viewModel = Create((id, _, _) => id == "claude" ? [Weekly(100, reset), Weekly(40, reset)] : []);

        var row = Assert.Single(viewModel.LimitRows);
        Assert.StartsWith("Claude", row.Label, StringComparison.Ordinal);
        Assert.Contains(" · ", row.Label, StringComparison.Ordinal);
        Assert.Equal("1×", row.ReachedText);
        Assert.Equal(StatusTextMap.FormatPercent("100"), row.HighestText);
        Assert.Equal(StatusTextMap.FormatPercent("100"), row.AveragePeakText);
        Assert.True(viewModel.HasLimitRows);
        Assert.False(viewModel.HasNoLimitRows);
    }

    [Fact]
    public void No_history_leaves_the_section_empty()
    {
        var viewModel = Create((_, _, _) => []);

        Assert.Empty(viewModel.LimitRows);
        Assert.True(viewModel.HasNoLimitRows);
    }

    [Fact]
    public void The_provider_filter_limits_the_rows_to_that_provider()
    {
        var reset = DateTimeOffset.Now.AddDays(2);
        var viewModel = Create((id, _, _) => id is "claude" or "codex" ? [Weekly(50, reset)] : []);
        Assert.Equal(2, viewModel.LimitRows.Count);

        viewModel.SetProviderCommand.Execute("codex");

        var row = Assert.Single(viewModel.LimitRows);
        Assert.StartsWith(StatsViewModel.ProviderDisplayNames["codex"], row.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void The_range_bounds_are_passed_to_the_history_read()
    {
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        Create((_, start, end) =>
        {
            from = start;
            to = end;
            return [];
        });

        Assert.NotNull(from);
        Assert.True(to > from);
        Assert.True(to >= DateTimeOffset.Now);
    }
}
