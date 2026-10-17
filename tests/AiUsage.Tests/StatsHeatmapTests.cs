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

    private static StatsRecord Tokens(string provider, string model, DateOnly day, int hour, long tokens, bool subagent = false) =>
        new(provider, day, model, "projA", tokens, 0, 0, 0, Hour: hour, Subagent: subagent);

    private static readonly DateOnly Tuesday = new(2026, 3, 17);

    private static int CellIndex(DayOfWeek first, DateOnly day, int hour) =>
        (((int)day.DayOfWeek - (int)first + 7) % 7) * 24 + hour;

    [Fact]
    public void A_cell_with_two_providers_names_the_larger_one_as_its_leader()
    {
        var cells = StatsAggregator.WeekdayHourLeaders(
            [Tokens("claude", "m1", Tuesday, 14, 70), Tokens("codex", "m2", Tuesday, 14, 30)], record => record.Provider, DayOfWeek.Monday);

        Assert.Equal(168, cells.Count);
        var cell = cells[CellIndex(DayOfWeek.Monday, Tuesday, 14)];
        Assert.Equal(new StatsHeatmapCell(100, "claude", 70), cell);
        Assert.Equal(new StatsHeatmapCell(0, "", 0), cells[0]);
        Assert.Equal(100, cells.Sum(c => c.Total));
    }

    [Fact]
    public void The_cells_follow_the_row_order_of_the_flattened_grid()
    {
        StatsRecord[] records = [Tokens("claude", "m1", Tuesday, 14, 100), Tokens("codex", "m2", Tuesday.AddDays(1), 3, 7)];
        foreach (var first in new[] { DayOfWeek.Monday, DayOfWeek.Sunday, DayOfWeek.Tuesday })
        {
            var flat = StatsAggregator.FlattenWeekdayHour(StatsAggregator.GroupByWeekdayHour(records), first);
            var cells = StatsAggregator.WeekdayHourLeaders(records, record => record.Provider, first);

            Assert.Equal(flat, cells.Select(c => c.Total).ToList());
        }
    }

    [Fact]
    public void Model_mode_pools_beyond_the_same_top_six_as_the_stacked_columns_into_other()
    {
        var records = new List<StatsRecord>();
        for (var i = 1; i <= 6; i++)
            records.Add(Tokens("claude", "model" + i, Tuesday.AddDays(-i), 9, 1000 * (7 - i)));
        records.Add(Tokens("claude", "model7", Tuesday, 10, 70));
        records.Add(Tokens("codex", "model8", Tuesday, 10, 30));

        var ranking = StatsAggregator.RankModels(records, 6, "Other");
        var cells = StatsAggregator.WeekdayHourLeaders(records, ranking.KeyFor, DayOfWeek.Monday);
        var stacked = StatsAggregator.GroupStackedByModel(records, StatsGrouping.Day, Tuesday.AddDays(-7), Tuesday, 6, "Other");

        Assert.Equal(stacked.Series, ranking.Series);
        Assert.True(ranking.HasOther);
        Assert.Equal(new StatsHeatmapCell(100, "other", 100), cells[CellIndex(DayOfWeek.Monday, Tuesday, 10)]);
        Assert.Equal("model:claude:0", cells[CellIndex(DayOfWeek.Monday, Tuesday.AddDays(-1), 9)].LeaderKey);
        Assert.Equal("model:claude:5", cells[CellIndex(DayOfWeek.Monday, Tuesday.AddDays(-6), 9)].LeaderKey);
    }

    [Fact]
    public void Agent_mode_uses_the_main_and_subagent_split_of_the_stacked_columns()
    {
        var cells = StatsAggregator.WeekdayHourLeaders(
            [Tokens("claude", "m1", Tuesday, 8, 20), Tokens("claude", "m1", Tuesday, 8, 80, subagent: true), Tokens("claude", "m1", Tuesday, 9, 50)],
            record => record.Subagent ? "cat:1" : "cat:0", DayOfWeek.Monday);

        Assert.Equal(new StatsHeatmapCell(100, "cat:1", 80), cells[CellIndex(DayOfWeek.Monday, Tuesday, 8)]);
        Assert.Equal(new StatsHeatmapCell(50, "cat:0", 50), cells[CellIndex(DayOfWeek.Monday, Tuesday, 9)]);
    }

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
    [InlineData("de-DE", "Dienstag, 14:00-15:00: 1.234 Token")]
    public void The_hover_text_names_weekday_hours_and_tokens(string cultureName, string expected)
    {
        var culture = new CultureInfo(cultureName);
        var format = cultureName == "en-US" ? "{0}, {1}-{2}: {3} tokens" : "{0}, {1}-{2}: {3} Token";

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

    [Fact]
    public void The_focus_ring_of_the_last_cell_stays_inside_the_control()
    {
        var (bounds, ring) = RunOnSta(() =>
        {
            var heatmap = new StatsHeatmap { Values = new long[168] };
            heatmap.Measure(new Size(600, double.PositiveInfinity));
            var cell = StatsHeatmap.CellRect(StatsHeatmap.Rows - 1, StatsHeatmap.Columns - 1, heatmap.DesiredSize.Width);
            return (new Rect(heatmap.DesiredSize), StatsHeatmap.FocusRingBounds(cell));
        });

        Assert.True(bounds.Contains(ring), $"{ring} outside {bounds}");
    }

    [Fact]
    public void A_cell_is_shaded_in_its_own_color_and_the_rest_keep_the_accent()
    {
        var (own, accent) = RunOnSta(() =>
        {
            (byte R, byte B) Pixel(bool withColor)
            {
                var values = new long[168];
                values[24 + 14] = 500;
                var colors = new Color[168];
                if (withColor)
                    colors[24 + 14] = Colors.Red;
                var heatmap = new StatsHeatmap { Values = values, CellColors = colors, LeaderTexts = Enumerable.Repeat("", 168).ToList() };
                heatmap.Measure(new Size(600, double.PositiveInfinity));
                heatmap.Arrange(new Rect(heatmap.DesiredSize));
                var bitmap = new RenderTargetBitmap(600, (int)heatmap.DesiredSize.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(heatmap);
                var cellWidth = StatsHeatmap.CellWidthFor(600);
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)(30 + 14 * (cellWidth + 2) + cellWidth / 2), (int)(16 + (cellWidth + 2) + cellWidth / 2), 1, 1), pixel, 4, 0);
                return (pixel[2], pixel[0]);
            }

            return (Pixel(withColor: true), Pixel(withColor: false));
        });

        Assert.True(own.R > own.B, $"{own}");
        Assert.True(accent.B > accent.R, $"{accent}");
    }

    [Fact]
    public void The_window_colors_each_cell_with_the_color_of_its_leading_series()
    {
        var series = new[] { new ChartSeriesInfo("Claude", "claude"), new ChartSeriesInfo("Other", "other") };
        var muted = new SolidColorBrush(Colors.Gray);

        var colors = AiUsage.Views.StatsWindow.HeatmapCellColors(series, ["claude", "", "other"], muted);

        Assert.Equal(3, colors.Count);
        Assert.Equal(ChartPalette.ForProvider("claude"), colors[0]);
        Assert.Equal(default, colors[1]);
        Assert.Equal(Colors.Gray, colors[2]);
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
