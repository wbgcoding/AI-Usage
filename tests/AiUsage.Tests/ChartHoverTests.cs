using System.Windows;
using AiUsage.Stats;
using AiUsage.Views.Controls;
using ChartPoint = AiUsage.Views.Controls.HistoryChart.ChartPoint;

namespace AiUsage.Tests;

/// <summary>Hover state and hit tests of the self-drawn charts: a data change clears the hover, the
/// nearest-point search agrees with a plain scan, and the blank area below the rows hits nothing.</summary>
public class ChartHoverTests
{
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
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "did not finish");
        if (failure is not null)
            throw failure;
        return result!;
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    // ---- data change clears the hover ----

    [Fact]
    public void Replacing_the_bars_clears_the_hovered_bar()
    {
        var hover = RunOnSta(() =>
        {
            var chart = new StatsBarChart { Bars = [new StatsBarChart.Bar("a", [10]), new StatsBarChart.Bar("b", [20])] };
            chart.HoverIndex = 1;
            chart.Bars = [new StatsBarChart.Bar("c", [5])];
            return chart.HoverIndex;
        });

        Assert.Equal(-1, hover);
    }

    [Fact]
    public void Replacing_the_rows_clears_the_hovered_row()
    {
        var hover = RunOnSta(() =>
        {
            var day = new DateOnly(2026, 9, 1);
            var chart = new StatsHorizontalBarChart { Rows = [new StatsProjectRow("p", "p", 10, 10, day, day)] };
            chart.HoverIndex = 0;
            chart.Rows = [new StatsProjectRow("q", "q", 20, 20, day, day)];
            return chart.HoverIndex;
        });

        Assert.Equal(-1, hover);
    }

    [Fact]
    public void Replacing_the_grid_days_clears_the_hovered_cell()
    {
        var hasHover = RunOnSta(() =>
        {
            var grid = new StatsMonthGrid { HasHover = true };
            grid.Days = [new StatsMonthGrid.DayValue(new DateOnly(2026, 9, 5), 700)];
            return grid.HasHover;
        });

        Assert.False(hasHover);
    }

    [Fact]
    public void Replacing_the_heatmap_values_clears_the_hovered_cell()
    {
        var hasHover = RunOnSta(() =>
        {
            var heatmap = new StatsHeatmap { HasHover = true };
            heatmap.Values = new long[StatsHeatmap.Rows * StatsHeatmap.Columns];
            return heatmap.HasHover;
        });

        Assert.False(hasHover);
    }

    [Fact]
    public void The_bar_chart_does_not_redraw_on_every_bar_it_crosses()
    {
        var source = File.ReadAllText(RepoFile("src", "AiUsage", "Views", "Controls", "StatsBarChart.cs"));
        var start = source.IndexOf("protected override void OnMouseMove", StringComparison.Ordinal);
        var end = source.IndexOf("private void UpdateTooltip", StringComparison.Ordinal);

        Assert.True(start > 0 && end > start);
        Assert.DoesNotContain("InvalidateVisual()", source[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public void The_tooltip_closes_with_its_window()
    {
        var source = File.ReadAllText(RepoFile("src", "AiUsage", "Views", "Controls", "ChartTooltipPopup.cs"));

        foreach (var hook in new[] { "Deactivated", "StateChanged", "Closed", "Unloaded", "IsVisibleChanged" })
            Assert.Contains(hook + " +=", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_grid_leaves_room_for_the_focus_ring_on_every_side()
    {
        var size = RunOnSta(() =>
        {
            var grid = new StatsMonthGrid
            {
                Days = [new StatsMonthGrid.DayValue(new DateOnly(2026, 9, 1), 100)],
                RangeStart = new DateOnly(2026, 9, 1),
                RangeEnd = new DateOnly(2026, 9, 26),
                Today = new DateOnly(2026, 9, 26),
                AvailableWidth = 400,
                ShowLegend = false,
                ShowAxisLabels = false,
            };
            grid.Measure(new Size(400, double.PositiveInfinity));
            return grid.DesiredSize;
        });

        // Seven rows of 11 with 3 between, and 3 of margin above and below for the ring.
        Assert.Equal(7 * 11 + 6 * 3 + 2 * 3, size.Height, 3);
    }

    // ---- rows ----

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    [InlineData(25.9, 1)]
    [InlineData(59.9, 2)]
    [InlineData(60, -1)]
    [InlineData(500, -1)]
    public void A_pointer_below_the_last_row_hits_no_row(double y, int expected)
    {
        Assert.Equal(expected, StatsHorizontalBarChart.RowIndexAt(y, 20, 3));
    }

    [Fact]
    public void An_empty_chart_has_no_row_to_hit()
    {
        Assert.Equal(-1, StatsHorizontalBarChart.RowIndexAt(5, 20, 0));
    }

    // ---- history chart ----

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static int LinearNearest(IReadOnlyList<ChartPoint> points, double x, double width, DateTimeOffset start, DateTimeOffset end)
    {
        var span = (end - start).TotalSeconds;
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].At < start || points[i].At > end)
                continue;
            var px = HistoryChart.PlotX(span > 0 ? (points[i].At - start).TotalSeconds / span : 0, width);
            var distance = Math.Abs(px - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return bestDistance <= 12 ? best : -1;
    }

    [Fact]
    public void The_nearest_point_search_agrees_with_a_plain_scan()
    {
        var random = new Random(7);
        var end = Start.AddDays(10);
        // Samples before, inside and after the range, two of them at the same instant.
        var points = Enumerable.Range(0, 3000)
            .Select(i => new ChartPoint(Start.AddDays(-1).AddSeconds(i * 330 + random.Next(0, 300)), i % 100))
            .Concat([new ChartPoint(Start.AddDays(5), 1), new ChartPoint(Start.AddDays(5), 2)])
            .OrderBy(point => point.At)
            .ToList();

        foreach (var width in new[] { 120.0, 400.0, 1000.0 })
        {
            for (var x = -20.0; x <= width + 20; x += 3.7)
                Assert.Equal(LinearNearest(points, x, width, Start, end), HistoryChart.NearestIndex(points, x, width, Start, end));
        }
    }

    [Fact]
    public void The_nearest_point_search_handles_no_visible_point()
    {
        var end = Start.AddDays(1);
        ChartPoint[] outside = [new(Start.AddDays(-2), 10), new(Start.AddDays(3), 20)];

        Assert.Equal(-1, HistoryChart.NearestIndex(outside, 100, 200, Start, end));
        Assert.Equal(-1, HistoryChart.NearestIndex([], 100, 200, Start, end));
    }

    [Fact]
    public void The_reduction_is_reused_until_the_data_or_the_width_changes()
    {
        var end = Start.AddDays(1);
        IReadOnlyList<ChartPoint> points = [new(Start.AddHours(1), 10), new(Start.AddHours(5), 20), new(Start.AddHours(9), 30)];
        var cache = new HistoryChart.ReducedColumnsCache();

        var first = cache.Get(points, 100, Start, end);
        Assert.Same(first, cache.Get(points, 100, Start, end));
        Assert.NotSame(first, cache.Get(points, 120, Start, end));
        IReadOnlyList<ChartPoint> other = [.. points, new ChartPoint(Start.AddHours(12), 40)];
        Assert.Equal(4, cache.Get(other, 120, Start, end).Count);
    }

    // ---- mini chart ----

    [Theory]
    [InlineData(30, 14)]
    [InlineData(2, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    public void The_two_bar_height_is_never_negative(double height, double expected)
    {
        Assert.Equal(expected, StatsFigureMiniChart.TwoBarHeight(height));
    }
}
