using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiUsage.Stats;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The weekday by hour grid: its aggregation, row order, shading, hover text, keyboard walk
/// and one real render.</summary>
public class StatsHeatmapTests
{
    private static StatsRecord Record(DateOnly day, int hour, long tokens) =>
        new("claude", day, "modelA", "projA", tokens, 0, 0, 0, Hour: hour);

    [Fact]
    public void A_record_lands_in_the_cell_of_its_weekday_and_hour()
    {
        var tuesday = new DateOnly(2026, 3, 17);
        Assert.Equal(DayOfWeek.Tuesday, tuesday.DayOfWeek);

        var grid = StatsAggregator.GroupByWeekdayHour([Record(tuesday, 14, 100), Record(tuesday, 14, 25), Record(tuesday.AddDays(1), 3, 7)]);

        Assert.Equal(125, grid[(int)DayOfWeek.Tuesday, 14]);
        Assert.Equal(7, grid[(int)DayOfWeek.Wednesday, 3]);
        Assert.Equal(132, grid.Cast<long>().Sum());
        Assert.Equal(7, grid.GetLength(0));
        Assert.Equal(24, grid.GetLength(1));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, 1)]
    [InlineData(DayOfWeek.Sunday, 2)]
    [InlineData(DayOfWeek.Tuesday, 0)]
    public void The_flattened_grid_starts_with_the_first_day_of_the_week(DayOfWeek first, int expectedRow)
    {
        var date = new DateOnly(2026, 3, 17); // a Tuesday
        var grid = StatsAggregator.GroupByWeekdayHour([Record(date, 14, 100)]);

        var values = StatsAggregator.FlattenWeekdayHour(grid, first);

        Assert.Equal(168, values.Count);
        Assert.Equal(100, values[expectedRow * 24 + 14]);
        Assert.Equal(DayOfWeek.Tuesday, StatsHeatmap.DayOfRow(first, expectedRow));
    }

    [Fact]
    public void Shading_ranks_the_cells_among_the_non_empty_ones()
    {
        var sorted = StatsHeatmap.SortedNonZero([0, 5, 100, 0, 20, 1000]);

        Assert.Equal(0, StatsHeatmap.Step(0, sorted));
        Assert.Equal(1, StatsHeatmap.Step(5, sorted));
        Assert.Equal(4, StatsHeatmap.Step(1000, sorted));
        Assert.True(StatsHeatmap.Step(20, sorted) < StatsHeatmap.Step(100, sorted));
    }

    [Theory]
    [InlineData("en-US", "Tuesday, 2:00 PM-3:00 PM: 1,234 tokens")]
    [InlineData("de-DE", "Dienstag, 14:00-15:00 Uhr: 1.234 Token")]
    public void The_hover_text_names_weekday_hours_and_tokens(string cultureName, string expected)
    {
        var culture = new CultureInfo(cultureName);
        var format = cultureName == "en-US" ? "{0}, {1}-{2}: {3} tokens" : "{0}, {1}-{2} Uhr: {3} Token";

        Assert.Equal(expected, StatsHeatmap.BuildTooltip(format, DayOfWeek.Tuesday, 14, 1234, culture));
    }

    [Fact]
    public void The_last_hour_of_the_day_ends_at_midnight()
    {
        var culture = new CultureInfo("de-DE");

        Assert.Equal("23:00", StatsHeatmap.HourLabel(23, culture));
        Assert.Equal("00:00", StatsHeatmap.HourLabel(24, culture));
    }

    [Fact]
    public void Only_every_third_hour_carries_a_column_label()
    {
        var labelled = Enumerable.Range(0, 24).Where(StatsHeatmap.HasColumnLabel).ToArray();

        Assert.Equal([0, 3, 6, 9, 12, 15, 18, 21], labelled);
    }

    [Fact]
    public void Arrow_keys_walk_the_grid_and_stop_at_its_edges()
    {
        Assert.Equal((0, 0), StatsHeatmap.MoveFocus(null, Key.Down));
        Assert.Equal((0, 1), StatsHeatmap.MoveFocus((0, 0), Key.Right));
        Assert.Equal((0, 0), StatsHeatmap.MoveFocus((0, 0), Key.Left));
        Assert.Equal((0, 0), StatsHeatmap.MoveFocus((0, 0), Key.Up));
        Assert.Equal((1, 5), StatsHeatmap.MoveFocus((0, 5), Key.Down));
        Assert.Equal((6, 5), StatsHeatmap.MoveFocus((6, 5), Key.Down));
        Assert.Equal((6, 23), StatsHeatmap.MoveFocus((6, 23), Key.Right));
        Assert.Equal((3, 0), StatsHeatmap.MoveFocus((3, 9), Key.Home));
        Assert.Equal((3, 23), StatsHeatmap.MoveFocus((3, 9), Key.End));
    }

    [Fact]
    public void The_grid_measures_to_the_given_width_and_draws_with_and_without_data()
    {
        var (width, height, rendered) = RunOnSta(() =>
        {
            var values = new long[168];
            values[24 + 14] = 500;
            values[3 * 24 + 9] = 50;
            var heatmap = new StatsHeatmap { Values = values, TooltipFormat = "{0}, {1}-{2}: {3} tokens", EmptyText = "No data" };
            heatmap.Measure(new Size(600, double.PositiveInfinity));
            heatmap.Arrange(new Rect(heatmap.DesiredSize));
            var bitmap = new RenderTargetBitmap(600, (int)heatmap.DesiredSize.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(heatmap);

            heatmap.Values = new long[168];
            heatmap.Measure(new Size(600, double.PositiveInfinity));
            heatmap.Arrange(new Rect(heatmap.DesiredSize));
            bitmap.Render(heatmap);
            return (heatmap.DesiredSize.Width, heatmap.DesiredSize.Height, true);
        });

        Assert.Equal(600, width);
        Assert.InRange(height, 100, 250);
        Assert.True(rendered);
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(20)), "render did not finish");
        if (failure is not null)
            throw failure;
        return result!;
    }
}
