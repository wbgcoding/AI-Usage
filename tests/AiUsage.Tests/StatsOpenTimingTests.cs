using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Views;
using Xunit;
using Xunit.Abstractions;

namespace AiUsage.Tests;

/// <summary>A fact that only runs when AIUSAGE_PERF_DB names a copy of a real stats.db.</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AIUSAGE_PERF_DB")))
            Skip = "AIUSAGE_PERF_DB not set";
    }
}

/// <summary>Where the time goes when the statistics window opens, on a copy of a real index. Prints
/// one line per phase; asserts nothing about speed.</summary>
[Trait("Category", "Perf")]
[Collection(SharedStateTestsCollection.Name)]
public class StatsOpenTimingTests(ITestOutputHelper output)
{
    [PerfFact]
    public void Print_the_phases_of_opening_the_statistics_window()
    {
        var db = Environment.GetEnvironmentVariable("AIUSAGE_PERF_DB")!;
        var directory = Path.GetDirectoryName(Path.GetFullPath(db))!;
        var lines = new List<string>();
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Measure(directory, lines);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromMinutes(5)))
            throw new TimeoutException("timing run did not finish");
        foreach (var line in lines)
            output.WriteLine(line);
        File.AppendAllLines(Path.Combine(directory, "timing.txt"), lines);
        if (failure is not null)
            throw new InvalidOperationException("timing run failed.", failure);
    }

    private static double Ms(Stopwatch sw)
    {
        var ms = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        return ms;
    }

    private static void Measure(string directory, List<string> lines)
    {
        var sw = Stopwatch.StartNew();
        var store = new StatsStore(directory);
        lines.Add($"store construct      {Ms(sw),9:F1} ms");
        var records = store.LoadAll();
        lines.Add($"LoadAll (cold)       {Ms(sw),9:F1} ms  rows={records.Count}");
        records = store.LoadAll();
        lines.Add($"LoadAll (warm)       {Ms(sw),9:F1} ms");
        var sessions = store.LoadSessions();
        lines.Add($"LoadSessions         {Ms(sw),9:F1} ms  rows={sessions.Count}");
        var resolved = StatsAggregator.ResolveBareProjectNames(records);
        lines.Add($"ResolveBareProjects  {Ms(sw),9:F1} ms");

        foreach (var grouping in new[] { StatsGrouping.Weekday, StatsGrouping.WeekdayHour })
        {
            var vm = new StatsViewModel(store) { LoadRecords = () => resolved, LoadSessions = () => sessions };
            vm.RestoreSelection("All", grouping.ToString(), "Provider", null, null);
            sw.Restart();
            vm.Recompute();
            lines.Add($"RebuildFrom {grouping,-12} {Ms(sw),9:F1} ms");
        }

        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
        var app = new AiUsage.App();
        app.InitializeComponent();
        try
        {
            ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries,
                uri => new ResourceDictionary { Source = new Uri("pack://application:,,,/AI-Usage;component/" + uri.OriginalString, UriKind.Absolute) }, () => false);

            var vmWindow = new StatsViewModel(store) { TakeSnapshot = () => new StatsDataSnapshot(resolved, sessions) };
            vmWindow.RestoreSelection("All", nameof(StatsGrouping.Weekday), "Provider", null, null);
            sw.Restart();
            var window = new StatsWindow(vmWindow);
            lines.Add($"window constructor   {Ms(sw),9:F1} ms");
            var prepared = Environment.GetEnvironmentVariable("AIUSAGE_PERF_PREPARED") == "1";
            if (prepared)
            {
                sw.Restart();
                window.PrepareOffscreen();
                lines.Add($"PrepareOffscreen (idle, off the click path) {Ms(sw),9:F1} ms");
            }

            var click = Stopwatch.StartNew();
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;
            window.Top = -30000;
            var rendered = false;
            window.ContentRendered += (_, _) => rendered = true;
            var loaded = TimeSpan.Zero;
            window.Loaded += (_, _) => loaded = sw.Elapsed;
            window.Show();
            lines.Add($"Show() returned      {Ms(sw),9:F1} ms  (Loaded at {loaded.TotalMilliseconds:F1} ms after Show start incl. snapshot rebuild)");
            window.UpdateLayout();
            lines.Add($"first UpdateLayout   {Ms(sw),9:F1} ms");
            while (!rendered)
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });
            lines.Add($"until ContentRendered{Ms(sw),9:F1} ms");
            lines.Add($"CLICK to rendered ({(prepared ? "prepared window" : "ctor not included; add ctor above")}) {click.Elapsed.TotalMilliseconds,9:F1} ms");
            window.Close();
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        }
    }
}
