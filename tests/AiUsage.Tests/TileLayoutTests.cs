using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Measures the real, laid-out <see cref="ProviderTile"/>: where its text and bars sit relative to
/// each other and how tall a Full tile comes out. Same harness as <see cref="TileChartBoundsTests"/>:
/// one STA thread this test owns, a bootstrapped Application, a theme applied from the production
/// assembly.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class TileLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private const double TileWidth = 340;

    [Fact]
    public void NameTextLinesUpWithTheBarEdge()
    {
        var (nameLeft, barLeft) = RunOnSta(() => WithTile(Full, (host, tile) =>
        {
            var name = FindAll<TextBlock>(tile).First(t => t.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == "Probe"));
            var bar = FindAll<ProgressBar>(tile).First();
            return (
                name.TransformToAncestor(host).Transform(new Point(0, 0)).X,
                bar.TransformToAncestor(host).Transform(new Point(0, 0)).X);
        }));

        Assert.True(Math.Abs(nameLeft - barLeft) <= 0.5, $"name starts at {nameLeft}, bar starts at {barLeft}");
    }

    // A Full tile with two bar rows and the history chart measured 209.83 px before the tile padding
    // dropped from 12 to 8 and the row and body gaps tightened. The denser layout has to save at least
    // 12 px of that (about 6 percent).
    private const double HeightBeforeTighterSpacing = 209.83;

    [Fact]
    public void AFullTileWithTwoRowsAndAChartIsAtLeastTwelvePixelsShorterThanBefore()
    {
        var (height, hasChart) = RunOnSta(() => WithTile(Full, (host, tile) =>
            (host.DesiredSize.Height, FindAll<HistoryChart>(tile).Any(chart => chart.ActualHeight > 0))));

        Assert.True(hasChart, "the measured tile must show its history chart");
        Assert.True(height <= HeightBeforeTighterSpacing - 12, $"tile is {height} px tall, was {HeightBeforeTighterSpacing} px");
    }

    private static ProviderTileViewModel Full()
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

        var range = SettingsRanges.ChartRangeToTimeSpan("Day");
        var points = new List<HistoryPoint>();
        for (var at = Now - range + TimeSpan.FromMinutes(3); at <= Now; at += TimeSpan.FromMinutes(5))
        {
            var phase = (at - (Now - range)).TotalHours % 9 / 9.0;
            points.Add(new HistoryPoint(1, at, WindowKind.FiveHour, 20 + phase * 60, null));
            points.Add(new HistoryPoint(1, at, WindowKind.Weekly, 10 + phase * 70, null));
        }
        var decision = MainViewModel.DecideChartRange(Now, range, points.Min(p => p.Timestamp));
        viewModel.UpdateHistory(points, [], decision.RangeStart, Now);
        return viewModel;
    }

    private static T WithTile<T>(Func<ProviderTileViewModel> build, Func<Border, ProviderTile, T> measure)
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
        var app = new AiUsage.App();
        app.InitializeComponent();
        try
        {
            ThemeService.Apply(AppTheme.Dark, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

            var tile = new ProviderTile { DataContext = build(), Width = TileWidth };
            var host = new Border { Background = (Brush)Application.Current!.Resources["Bg.Surface"], Child = tile };
            host.Measure(new Size(TileWidth, 2000));
            host.Arrange(new Rect(0, 0, TileWidth, host.DesiredSize.Height));
            host.UpdateLayout();
            return measure(host, tile);
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        }
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
            yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (var found in FindAll<T>(VisualTreeHelper.GetChild(root, i)))
                yield return found;
        }
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
            throw new TimeoutException("tile layout measure did not finish");
        if (failure is not null)
            throw new InvalidOperationException("tile layout measure failed", failure);
        return result!;
    }

    private static ResourceDictionary LoadFromProductionAssembly(Uri relativeUri) =>
        new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relativeUri.OriginalString, UriKind.Absolute) };
}
