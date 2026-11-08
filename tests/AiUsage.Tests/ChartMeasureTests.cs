using System.Threading;
using System.Windows;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>A chart whose measured size depends on a property is measured again when that property changes.</summary>
[Collection(SharedStateTestsCollection.Name)]
public sealed class ChartMeasureTests
{
    [Fact]
    public void Changing_the_slice_count_changes_the_desired_height_of_the_ring_chart()
    {
        var (before, after) = OnSta(() =>
        {
            var ring = new StatsRingChart();
            var available = new Size(300, double.PositiveInfinity);
            ring.Measure(available);
            var first = ring.DesiredSize.Height;
            ring.Slices = Enumerable.Range(0, 30).Select(i => new StatsRingChart.Slice("S" + i, 100.0 / 30)).ToArray();
            ring.Measure(available);
            return (first, ring.DesiredSize.Height);
        });

        Assert.True(after > before, $"{before} -> {after}");
    }

    [Fact]
    public void Changing_the_display_names_changes_the_desired_height_of_the_month_grid()
    {
        var (before, after) = OnSta(() =>
        {
            var grid = new StatsMonthGrid
            {
                Days =
                [
                    new StatsMonthGrid.DayValue(new DateOnly(2026, 1, 5), 400, "claude"),
                    new StatsMonthGrid.DayValue(new DateOnly(2026, 1, 6), 300, "codex"),
                    new StatsMonthGrid.DayValue(new DateOnly(2026, 1, 7), 200, "gemini"),
                    new StatsMonthGrid.DayValue(new DateOnly(2026, 1, 8), 100, "opencode"),
                ],
                RangeStart = new DateOnly(2026, 1, 1),
                RangeEnd = new DateOnly(2026, 9, 26),
                Today = new DateOnly(2026, 9, 26),
                AvailableWidth = 600,
                ShowLegend = true,
            };
            var available = new Size(600, double.PositiveInfinity);
            grid.Measure(available);
            var first = grid.DesiredSize.Height;
            var longName = new string('W', 40);
            grid.ProviderDisplayNames = new Dictionary<string, string>
            {
                ["claude"] = longName, ["codex"] = longName, ["gemini"] = longName, ["opencode"] = longName,
            };
            grid.Measure(available);
            return (first, grid.DesiredSize.Height);
        });

        Assert.True(after > before, $"{before} -> {after}");
    }

    private static T OnSta<T>(Func<T> action)
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
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "work did not finish");
        if (failure is not null)
            throw new InvalidOperationException("STA work failed.", failure);
        return result!;
    }
}
