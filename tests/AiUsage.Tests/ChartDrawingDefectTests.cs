using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiUsage.Stats;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Small drawing defects of the self-drawn charts, pinned on pixels.</summary>
[Collection(SharedStateTestsCollection.Name)]
public sealed class ChartDrawingDefectTests
{
    [Fact]
    public void The_hover_dot_sits_on_the_line_even_at_zero_percent()
    {
        const double plotHeight = 40;

        Assert.Equal(plotHeight - HistoryChart.SeriesThickness / 2, HistoryChart.HoverDotY(0, plotHeight));
        Assert.Equal(20, HistoryChart.HoverDotY(50, plotHeight));
    }

    [Fact]
    public void A_heatmap_without_enough_values_still_draws_its_empty_text()
    {
        var painted = OnSta(() =>
        {
            var heatmap = new StatsHeatmap { Values = new long[10], EmptyText = "No data", TextBrush = Brushes.Red };
            heatmap.Measure(new Size(400, double.PositiveInfinity));
            heatmap.Arrange(new Rect(0, 0, 400, Math.Max(40, heatmap.DesiredSize.Height)));
            var bitmap = new RenderTargetBitmap(400, (int)Math.Max(40, heatmap.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(heatmap);
            return CountPixels(bitmap, pixel => pixel.R > 150 && pixel.G < 100 && pixel.A > 0);
        });

        Assert.True(painted > 0);
    }

    [Fact]
    public void The_dot_of_a_project_row_without_its_own_color_is_drawn_in_the_bar_brush()
    {
        var painted = OnSta(() =>
        {
            var chart = new StatsHorizontalBarChart
            {
                ShowProjectDetails = true,
                BarBrush = Brushes.Lime,
                TextBrush = Brushes.Black,
                Rows = [new StatsProjectRow("proj", @"C:\proj", 100, 100, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2))],
            };
            chart.Measure(new Size(400, double.PositiveInfinity));
            var height = (int)Math.Ceiling(chart.DesiredSize.Height);
            chart.Arrange(new Rect(0, 0, 400, height));
            var bitmap = new RenderTargetBitmap(400, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(chart);
            // The left 16 pixels over the text area hold only the icon or the dot.
            return CountPixels(bitmap, new Int32Rect(0, 0, 16, 20), pixel => pixel.G > 150 && pixel.R < 100 && pixel.A > 0);
        });

        Assert.True(painted > 0);
    }

    private static int CountPixels(BitmapSource bitmap, Func<(byte R, byte G, byte B, byte A), bool> match) =>
        CountPixels(bitmap, new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), match);

    private static int CountPixels(BitmapSource bitmap, Int32Rect area, Func<(byte R, byte G, byte B, byte A), bool> match)
    {
        var stride = area.Width * 4;
        var data = new byte[stride * area.Height];
        bitmap.CopyPixels(area, data, stride, 0);
        var count = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            // Pbgra32 is premultiplied; the colours here are opaque, so the channels are exact.
            if (match((data[i + 2], data[i + 1], data[i], data[i + 3])))
                count++;
        }
        return count;
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
