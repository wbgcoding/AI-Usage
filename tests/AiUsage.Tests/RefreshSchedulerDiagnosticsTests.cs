using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Services;
using Xunit;
using static AiUsage.Tests.RefreshSchedulerPowerTests;

namespace AiUsage.Tests;

/// <summary>The log lines that explain a refresh which seems to stand still: a long gap between
/// ticks, a read that outlives its timeout, and a held network.</summary>
public class RefreshSchedulerDiagnosticsTests
{
    private static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(60);

    private sealed class NeverEndingProvider(string id) : IUsageProvider
    {
        public TaskCompletionSource<ProviderSnapshot> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id { get; } = id;
        public string DisplayName => Id;
        public TimeSpan? MinRefreshInterval => null;
        public bool RunsOnUiThread => false;
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Release.Task;
    }

    [Fact]
    public async Task A_gap_of_minutes_between_ticks_is_logged_once_and_normal_ticks_are_silent()
    {
        var clock = new FakeClock();
        List<string> lines = [];
        var scheduler = new RefreshScheduler([new Provider("web", SourceKind.WebSession)], BaseInterval, clock, logInfo: lines.Add);

        await Task.WhenAll(scheduler.Tick());
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(scheduler.Tick());
        Assert.Empty(lines);

        clock.Advance(TimeSpan.FromMinutes(20));
        await Task.WhenAll(scheduler.Tick());
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(["Refresh tick: 20 min without a tick, the timer resumed"], lines);
    }

    [Fact]
    public async Task A_read_running_far_past_its_timeout_is_logged_once()
    {
        var clock = new FakeClock();
        List<string> lines = [];
        var stuck = new NeverEndingProvider("web");
        var scheduler = new RefreshScheduler([stuck], BaseInterval, clock, logInfo: lines.Add);

        scheduler.Tick();
        for (var i = 0; i < 8; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            scheduler.Tick();
        }

        Assert.Single(lines, l => l.StartsWith("Provider 'web': a read has been running for", StringComparison.Ordinal));
        stuck.Release.TrySetCanceled();
    }

    [Fact]
    public async Task Held_network_reads_log_once_per_change_of_the_reason()
    {
        var clock = new FakeClock();
        List<string> lines = [];
        var scheduler = new RefreshScheduler([new Provider("web", SourceKind.WebSession)], BaseInterval, clock, logInfo: lines.Add);
        await Task.WhenAll(scheduler.Tick());

        scheduler.PauseFor(TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromMinutes(2));
        scheduler.Tick();
        clock.Advance(TimeSpan.FromSeconds(30));
        scheduler.Tick();
        Assert.Equal(["Refresh: network reads are held while due (paused)"], lines);

        scheduler.Resume();
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal("Refresh: network reads are no longer held", lines[^1]);
        Assert.Equal(2, lines.Count);
    }
}
