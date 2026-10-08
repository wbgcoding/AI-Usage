using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Measures the tile's real rendered history chart: the drawn curve has to run from the chart
/// field's left edge to its right edge, never starting inside the field and never clipped at the
/// edge. The measurement renders the real <see cref="ProviderTile"/> twice (with and without data)
/// and compares the pixels inside the chart's own bounds, so the grid lines and captions cancel out.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class TileChartBoundsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly record struct Bounds(double FieldLeft, double FieldRight, int DrawnLeft, int DrawnRight);

    [Theory]
    [InlineData(340)]
    [InlineData(620)]
    public void CurveSpansTheWholeChartField(double tileWidth)
    {
        var bounds = RunOnSta(() => Measure(tileWidth, historyDays: 40, "Day"));

        Assert.True(bounds.DrawnLeft - bounds.FieldLeft <= 1.5,
            $"curve starts at {bounds.DrawnLeft}, field starts at {bounds.FieldLeft}");
        Assert.True(bounds.FieldRight - (bounds.DrawnRight + 1) <= 1.5,
            $"curve ends at {bounds.DrawnRight + 1}, field ends at {bounds.FieldRight}");
    }

    [Fact]
    public void CurveSpansTheWholeFieldWhenHistoryIsShorterThanTheRange()
    {
        var bounds = RunOnSta(() => Measure(340, historyDays: 3, "Month"));

        Assert.True(bounds.DrawnLeft - bounds.FieldLeft <= 1.5,
            $"curve starts at {bounds.DrawnLeft}, field starts at {bounds.FieldLeft}");
    }


    private static T RunOnSta<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("tile chart measure did not finish");
        if (failure is not null)
            throw new InvalidOperationException("tile chart measure failed", failure);
        return result!;
    }

    private static Bounds Measure(double tileWidth, int historyDays, string rangeName)
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
        var app = new AiUsage.App();
        app.InitializeComponent();
        try
        {
            ThemeService.Apply(AppTheme.Dark, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

            var withData = Render(tileWidth, historyDays, rangeName, withHistory: true, out var chartRect);
            var without = Render(tileWidth, historyDays, rangeName, withHistory: false, out _);

            var left = int.MaxValue;
            var right = -1;
            for (var y = (int)chartRect.Top + 1; y < (int)chartRect.Bottom - 1; y++)
            {
                for (var x = (int)chartRect.Left; x < (int)Math.Ceiling(chartRect.Right); x++)
                {
                    if (!PixelsDiffer(withData, without, x, y))
                        continue;
                    // The two captions are part of both renders; only the series changes pixels.
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }

            return new Bounds(chartRect.Left, chartRect.Right, left, right);
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        }
    }

    private static BitmapSource Render(double tileWidth, int historyDays, string rangeName, bool withHistory, out Rect chartRect)
    {
        var viewModel = new ProviderTileViewModel("probe", "Probe");
        viewModel.Apply(new ProviderSnapshot(
            ProviderId: "probe",
            Windows:
            [
                new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300),
                new UsageWindow("Window_Weekly", WindowKind.Weekly, 63, Now.AddDays(3), 10080),
            ],
            PlanType: null,
            SourceKind: SourceKind.LocalFile,
            FetchedAt: Now,
            DataTimestamp: Now,
            Status: ProviderStatus.Ok,
            Error: null,
            Diagnostics: []), Now);
        viewModel.Density = TileDensity.Full;

        var range = SettingsRanges.ChartRangeToTimeSpan(rangeName);
        var points = new List<HistoryPoint>();
        if (withHistory)
        {
            // One sample every 5 minutes over the history, a slow sawtooth so the curve has body.
            var first = Now - TimeSpan.FromDays(historyDays);
            var requestedStart = Now - range;
            for (var at = first > requestedStart ? first : requestedStart + TimeSpan.FromMinutes(3); at <= Now; at += TimeSpan.FromMinutes(5))
            {
                var phase = (at - first).TotalHours % 9 / 9.0;
                points.Add(new HistoryPoint(1, at, WindowKind.FiveHour, 20 + phase * 60, null));
                points.Add(new HistoryPoint(1, at, WindowKind.Weekly, 10 + (at - first).TotalDays % 7 / 7.0 * 70, null));
            }
        }

        var earliest = points.Count > 0 ? points.Min(p => p.Timestamp) : (DateTimeOffset?)null;
        var decision = MainViewModel.DecideChartRange(Now, range, earliest);
        viewModel.UpdateHistory(points, [], decision.RangeStart, Now);

        var tile = new ProviderTile { DataContext = viewModel, Width = tileWidth };
        var host = new System.Windows.Controls.Border
        {
            Background = (Brush)Application.Current!.Resources["Bg.Surface"],
            Child = tile,
        };
        host.Measure(new Size(tileWidth, 800));
        host.Arrange(new Rect(0, 0, tileWidth, host.DesiredSize.Height));
        host.UpdateLayout();

        var chart = FindChart(tile)!;
        var origin = chart.TransformToAncestor(host).Transform(new Point(0, 0));
        chartRect = new Rect(origin, new Size(chart.ActualWidth, chart.ActualHeight));

        var bitmap = new RenderTargetBitmap((int)tileWidth, (int)Math.Ceiling(host.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        return bitmap;
    }

    private static HistoryChart? FindChart(DependencyObject root)
    {
        if (root is HistoryChart chart)
            return chart;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindChart(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }
        return null;
    }

    private static bool PixelsDiffer(BitmapSource a, BitmapSource b, int x, int y)
    {
        var pa = new byte[4];
        var pb = new byte[4];
        var rect = new Int32Rect(x, y, 1, 1);
        a.CopyPixels(rect, pa, 4, 0);
        b.CopyPixels(rect, pb, 4, 0);
        return Math.Abs(pa[0] - pb[0]) + Math.Abs(pa[1] - pb[1]) + Math.Abs(pa[2] - pb[2]) > 6;
    }

    private static ResourceDictionary LoadFromProductionAssembly(Uri relativeUri) =>
        new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relativeUri.OriginalString, UriKind.Absolute) };
}
