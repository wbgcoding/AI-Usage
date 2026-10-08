using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiUsage.Views.Controls;
using Xunit;
using ChartPoint = AiUsage.Views.Controls.HistoryChart.ChartPoint;

namespace AiUsage.Tests;

/// <summary>
/// Renders a bare <see cref="HistoryChart"/> with its own frozen theme brushes (no application, no
/// shipped theme) and inspects the pixels, so each test pins the colours it cares about.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class HistoryChartRenderTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(24);

    private static readonly Color AccentColour = Colors.Red;
    private static readonly Color OkColour = Colors.Lime;
    private static readonly Color WarnColour = Colors.Yellow;

    private static ChartPoint[] Flat(double percent) =>
        Enumerable.Range(0, 25).Select(i => new ChartPoint(Start.AddHours(i), percent)).ToArray();

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static byte[] Render(Action<HistoryChart> configure, int width = 300, int height = 64)
    {
        byte[]? pixels = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                var chart = new HistoryChart { Width = width, Height = height, RangeStart = Start, RangeEnd = End };
                chart.Resources["Accent"] = Frozen(AccentColour);
                chart.Resources["Level.Ok"] = Frozen(OkColour);
                chart.Resources["Level.Warn"] = Frozen(WarnColour);
                chart.Resources["Level.Crit"] = Frozen(Colors.Magenta);
                chart.Resources["Text.Muted"] = Frozen(Colors.Gray);
                chart.Resources["Border"] = Frozen(Colors.Gray);
                configure(chart);

                chart.Measure(new Size(width, height));
                chart.Arrange(new Rect(0, 0, width, height));
                chart.UpdateLayout();

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(chart);
                pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("chart render did not finish");
        if (failure is not null)
            throw new InvalidOperationException("chart render failed", failure);
        return pixels!;
    }

    /// <summary>BGRA premultiplied pixel at the given position.</summary>
    private static (byte B, byte G, byte R, byte A) Pixel(byte[] pixels, int x, int y, int width = 300)
    {
        var i = (y * width + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    /// <summary>The most opaque pixel in the box (a dashed line may have a gap at any one column).</summary>
    private static (byte B, byte G, byte R, byte A) Strongest(byte[] pixels, int xFrom, int xTo, int yFrom, int yTo, int width = 300) =>
        Enumerable.Range(xFrom, xTo - xFrom)
            .SelectMany(x => Enumerable.Range(yFrom, yTo - yFrom).Select(y => Pixel(pixels, x, y, width)))
            .OrderByDescending(p => p.A)
            .First();

    private static bool IsRedDominant((byte B, byte G, byte R, byte A) p) => p.A > 100 && p.R > p.G + 60 && p.R > p.B + 60;

    private static bool IsGreenDominant((byte B, byte G, byte R, byte A) p) => p.A > 100 && p.G > p.R + 60 && p.G > p.B + 60;

    private static int CountInRows(byte[] pixels, int fromRow, int width, int height, Func<(byte B, byte G, byte R, byte A), bool> match)
    {
        var count = 0;
        for (var y = fromRow; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (match(Pixel(pixels, x, y, width)))
                    count++;
            }
        }
        return count;
    }

    [Fact]
    public void WeeklySeriesUsesTheAccentNotTheLevelColour()
    {
        // 70 % is the warning level: the line used to turn yellow there.
        var pixels = Render(chart => chart.WeeklyValues = Flat(70));

        // 70 % of a 64 px field puts the line at y = 19.2; take the strongest pixel around it.
        var line = Strongest(pixels, 140, 160, 17, 22);

        Assert.True(IsRedDominant(line), $"weekly line pixel is {line}, expected the accent (red)");
    }

    [Fact]
    public void TheFiveHourSeriesKeepsItsLevelColour()
    {
        var pixels = Render(chart => chart.FiveHourValues = Flat(70));

        var line = Strongest(pixels, 140, 160, 17, 22);

        Assert.True(line.A > 100 && line.R > 150 && line.G > 150 && line.B < 100, $"five-hour line pixel is {line}, expected the warning yellow");
    }

    [Fact]
    public void FillUnderTheWeeklyLineIsTheAccentAtSixPercent()
    {
        var pixels = Render(chart => chart.WeeklyValues = Flat(50));

        // Well below the line (y = 32) and clear of the dashed mid-gridline.
        var (_, green, red, alpha) = Pixel(pixels, 150, 50);

        Assert.InRange(alpha, 14, 17); // 6 % of 255
        Assert.InRange(red, 14, 17);   // premultiplied pure red
        Assert.Equal(0, green);
    }

    [Fact]
    public void NoCurvePixelsReachTheCaptionStrip()
    {
        // A flat 0 % line sits on the bottom edge of the plot; nothing of it may reach the strip.
        var pixels = Render(chart =>
        {
            chart.ShowAxes = true;
            chart.WeeklyValues = Flat(0);
            chart.FiveHourValues = Flat(0);
        });

        Assert.Equal(0, CountInRows(pixels, 54, 300, 64, p => IsRedDominant(p) || IsGreenDominant(p)));
        // The curve itself is drawn: its lower edge reaches the bottom of the plot (y = 52).
        Assert.True(CountInRows(pixels, 50, 300, 54, IsRedDominant) > 0);
    }

    private static void BothSeries(HistoryChart chart)
    {
        chart.ShowAxes = true;
        chart.FiveHourValues = Flat(30);
        chart.WeeklyValues = Flat(30);
        chart.FiveHourLabel = "5h";
        chart.WeeklyLabel = "Week";
    }

    [Fact]
    public void LegendShowsBothSeriesWhenItFits()
    {
        var pixels = Render(BothSeries, width: 300);

        // Under the 52 px plot: the dashed five-hour pattern (level colour) and the solid weekly one (accent).
        Assert.True(CountInRows(pixels, 54, 300, 64, IsGreenDominant) > 0, "five-hour pattern missing");
        Assert.True(CountInRows(pixels, 54, 300, 64, IsRedDominant) > 0, "weekly pattern missing");
    }

    [Fact]
    public void LegendIsOmittedWhenTheStripIsTooNarrow()
    {
        var pixels = Render(BothSeries, width: 100);

        Assert.Equal(0, CountInRows(pixels, 54, 100, 64, p => IsGreenDominant(p) || IsRedDominant(p)));
    }
}
