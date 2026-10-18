using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The background aggregation of <see cref="StatsViewModel"/> under overlap, failure and a
/// language switch, forced through the hook that runs right before each aggregation.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class StatsRebuildOverlapTests
{
    private static StatsStore StoreWithHistory(string name, out IDisposable directory)
    {
        var dataDir = TestPaths.CreateDisposableDirectory(name);
        directory = dataDir;
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        store.AddDelta([
            new StatsRecord("claude", today, "modelA", "projA", 100, 0, 0, 0),
            new StatsRecord("claude", today.AddDays(-20), "modelA", "projA", 900, 0, 0, 0),
        ]);
        return store;
    }

    [Fact]
    public async Task The_newer_request_wins_when_the_older_aggregation_finishes_last()
    {
        var store = StoreWithHistory("stats-overlap-reverse", out var directory);
        using var _ = directory;
        var viewModel = StatsVm.Create(store);
        await viewModel.SetRangeCommand.ExecuteAsync("Month");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        viewModel.BeforeAggregate = _ =>
        {
            if (Interlocked.Increment(ref calls) != 1)
                return;

            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var year = viewModel.SetRangeCommand.ExecuteAsync("Year");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        await viewModel.SetRangeCommand.ExecuteAsync("Week");
        Assert.Equal(100, viewModel.PeriodTotalRaw);

        release.Set();
        await year;

        Assert.Equal("Week", viewModel.SelectedRange);
        Assert.Equal(100, viewModel.PeriodTotalRaw);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task A_failing_aggregation_is_logged_and_leaves_the_previous_state()
    {
        var store = StoreWithHistory("stats-overlap-throw", out var directory);
        using var _ = directory;
        var viewModel = StatsVm.Create(store);
        await viewModel.SetRangeCommand.ExecuteAsync("Year");
        var before = viewModel.PeriodTotalRaw;
        Assert.Equal(1000, before);

        var marker = "boom-" + Guid.NewGuid().ToString("N");
        viewModel.BeforeAggregate = _ => throw new InvalidOperationException(marker);

        await viewModel.SetRangeCommand.ExecuteAsync("Week");

        Assert.Equal(before, viewModel.PeriodTotalRaw);
        Assert.False(viewModel.IsLoading);
        Assert.Contains(
            Directory.EnumerateFiles(AppPaths.LogsDirectory, "app*.log").SelectMany(File.ReadLines),
            line => line.Contains(marker) && line.Contains("ERROR"));
    }

    [Fact]
    public async Task A_quiet_refresh_after_a_failed_aggregation_rebuilds_the_unchanged_data()
    {
        var store = StoreWithHistory("stats-overlap-recover", out var directory);
        using var _ = directory;
        var viewModel = StatsVm.Create(store);
        await viewModel.SetRangeCommand.ExecuteAsync("Year");
        var calls = 0;
        viewModel.BeforeAggregate = _ =>
        {
            if (++calls == 1)
                throw new InvalidOperationException("first rebuild fails");
        };

        await viewModel.SetRangeCommand.ExecuteAsync("Week");
        await viewModel.RecomputeAsync(showLoading: false);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_language_switch_during_an_aggregation_keeps_the_captured_texts()
    {
        var store = StoreWithHistory("stats-overlap-language", out var directory);
        using var _ = directory;
        var loc = LocalizationService.Instance;
        var viewModel = StatsVm.Create(store);
        await viewModel.SetRangeCommand.ExecuteAsync("Month");
        var german = viewModel.CacheShareLabels.ToList();

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        viewModel.BeforeAggregate = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        try
        {
            var week = viewModel.SetRangeCommand.ExecuteAsync("Week");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            loc.SetLanguage("en");
            release.Set();
            await week;

            Assert.Equal("Week", viewModel.SelectedRange);
            Assert.Equal(100, viewModel.PeriodTotalRaw);
            Assert.Equal(german, viewModel.CacheShareLabels);
            Assert.NotEqual(loc["Stats.Cache.New"], viewModel.CacheShareLabels[0]);
        }
        finally
        {
            release.Set();
            loc.SetLanguage("de");
        }
    }
}
