using System.Windows;
using System.Windows.Media;
using AiUsage.Views.Controls;

namespace AiUsage.Tests;

/// <summary>The token window at its minimum width must lose nothing: legends keep all their entries
/// and figures, the year grid fits without scrolling sideways, and no chart can be asked for a
/// negative rectangle.</summary>
public class StatsMinimumWidthTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
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
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "did not finish");
        if (failure is not null)
            throw failure;
        return result!;
    }

    // ---- legend bars ----

    [Fact]
    public void A_legend_that_wraps_to_a_second_row_makes_the_bar_taller_and_the_page_gives_it_that_room()
    {
        var height = RunOnSta(() =>
        {
            var bar = new StatsCacheShareBar
            {
                Values = [40, 30, 20, 10],
                SegmentLabels = ["New input", "Cache write", "Cache read", "Output"],
            };
            bar.Measure(new Size(220, double.PositiveInfinity));
            return bar.DesiredSize.Height;
        });

        // The bar (24), the gap (8) and at least two legend rows (18 each).
        Assert.True(height >= 24 + 8 + 2 * 18, $"height {height}");

        // The hosts must not pin that height: a fixed height clips the second row away.
        var xaml = File.ReadAllText(RepoFile("src", "AiUsage", "Views", "StatsWindow.xaml"));
        Assert.Contains("MinHeight=\"70\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"70\"/>", xaml.Replace("MinHeight=\"70\"/>", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("<Border Height=\"70\"", xaml, StringComparison.Ordinal);
    }

    // ---- year grid ----

    [Theory]
    [InlineData(378)]
    [InlineData(330)]
    public void The_year_grid_fits_a_minimum_window_without_scrolling(double usableWidth)
    {
        var fit = StatsMonthGrid.FitToWidth(53, usableWidth);

        var width = 53 * fit.CellSize + 52 * fit.Gap;
        Assert.True(width <= usableWidth + 0.01, $"{width} > {usableWidth}");
        Assert.True(fit.CellSize >= StatsMonthGrid.CompactMinCellSize);
    }

    [Fact]
    public void The_year_grid_keeps_its_regular_gap_whenever_the_minimum_cell_size_fits()
    {
        var wide = StatsMonthGrid.FitToWidth(53, 800);
        Assert.Equal(StatsMonthGrid.DefaultCellSize, wide.CellSize);

        var fit = StatsMonthGrid.FitToWidth(53, 53 * StatsMonthGrid.MinCellSize + 52 * 3 + 1);
        Assert.Equal(3, fit.Gap);
        Assert.True(fit.CellSize >= StatsMonthGrid.MinCellSize);
    }

    [Fact]
    public void A_width_too_small_even_for_the_compact_grid_still_draws_every_week()
    {
        var fit = StatsMonthGrid.FitToWidth(53, 100);

        Assert.Equal(StatsMonthGrid.CompactMinCellSize, fit.CellSize);
        Assert.Equal(53, fit.VisibleColumns);
    }

    // ---- session list ----

    [Fact]
    public void The_session_start_column_is_sized_by_its_content_in_the_header_and_every_row()
    {
        var xaml = File.ReadAllText(RepoFile("src", "AiUsage", "Views", "StatsWindow.xaml"));

        Assert.Contains("Grid.IsSharedSizeScope=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xaml, "SharedSizeGroup=\"SessionStart\"").Count);
        Assert.DoesNotContain("<ColumnDefinition Width=\"120\"/>", xaml, StringComparison.Ordinal);
    }

    // ---- ring legend ----

    [Theory]
    [InlineData(400, 132, 132, 144)]
    [InlineData(220, 132, 108, 120)]
    [InlineData(200, 132, 88, 100)]
    [InlineData(60, 132, 0, 12)]
    public void The_ring_legend_sits_right_after_the_ring_and_the_ring_shrinks_before_the_legend_does(
        double width, double height, double diameter, double legendX)
    {
        var (actualDiameter, actualLegendX) = StatsRingChart.RingLayout(width, height);

        Assert.Equal(diameter, actualDiameter, 6);
        Assert.Equal(legendX, actualLegendX, 6);
    }

    [Fact]
    public void A_click_finds_the_ring_slice_and_the_legend_row_in_the_new_layout()
    {
        StatsRingChart.Slice[] slices = [new("Claude", 60), new("Codex", 40)];

        // Ring centre (66, 66), radius about 51: the point straight above the centre is on slice 0.
        Assert.Equal(0, StatsRingChart.SliceIndexAt(new Point(66, 15), 400, 132, slices));
        Assert.Equal(-1, StatsRingChart.SliceIndexAt(new Point(66, 66), 400, 132, slices)); // the hole
        // The legend starts at x = 144, vertically centred: 2 rows of 18 from y = 48.
        Assert.Equal(0, StatsRingChart.SliceIndexAt(new Point(150, 50), 400, 132, slices));
        Assert.Equal(1, StatsRingChart.SliceIndexAt(new Point(150, 70), 400, 132, slices));
    }

    [Fact]
    public void A_long_legend_label_is_trimmed_before_its_figure_is()
    {
        var (label, figure, shortLabel) = RunOnSta(() =>
        {
            var typeface = new Typeface("Segoe UI");
            var (l, f) = StatsRingChart.LegendEntryTexts("Claude Sonnet 4.5 with an unreasonably long name", "45 %", 110, 1.0, typeface, Brushes.Black);
            var (s, _) = StatsRingChart.LegendEntryTexts("Codex", "45 %", 110, 1.0, typeface, Brushes.Black);
            return (l, f, s);
        });

        Assert.Equal(" \u00B7 45 %", figure.Text);
        Assert.True(label.Width + figure.Width <= 110.5, $"{label.Width} + {figure.Width}");
        Assert.True(label.Width < 100);
        Assert.Equal("Codex", shortLabel.Text);
    }

    // ---- heatmap ----

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(76)]
    [InlineData(0)]
    public void The_heatmap_never_asks_for_a_negative_cell_width(double width)
    {
        Assert.True(StatsHeatmap.CellWidthFor(width) >= 1);
        Assert.Equal(width > 30, StatsHeatmap.CanDrawCells(width));
    }

    [Fact]
    public void The_heatmap_cell_width_follows_the_free_width_when_there_is_room()
    {
        Assert.True(StatsHeatmap.CellWidthFor(720) > 20);
    }
}
