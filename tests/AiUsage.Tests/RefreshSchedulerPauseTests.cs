using AiUsage.Models;
using AiUsage.Services;
using Xunit;
using static AiUsage.Tests.RefreshSchedulerPowerTests;

namespace AiUsage.Tests;

/// <summary>The one-hour pause: network fetches stop, local files keep updating, and it ends by itself.</summary>
public class RefreshSchedulerPauseTests
{
    private static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public async Task A_pause_skips_network_providers_and_keeps_local_ones_running()
    {
        var clock = new FakeClock();
        var web = new Provider("web", SourceKind.WebSession);
        var file = new Provider("file", SourceKind.LocalFile);
        var scheduler = new RefreshScheduler([web, file], BaseInterval, clock);
        await Task.WhenAll(scheduler.Tick());

        scheduler.PauseFor(Hour);
        clock.Advance(TimeSpan.FromMinutes(10));
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(1, web.FetchCount);
        Assert.Equal(2, file.FetchCount);
    }

    [Fact]
    public async Task A_provider_without_a_reading_yet_is_fetched_once_during_a_pause()
    {
        var clock = new FakeClock();
        var web = new Provider("web", SourceKind.WebSession);
        var scheduler = new RefreshScheduler([web], BaseInterval, clock);
        scheduler.PauseFor(Hour);

        await Task.WhenAll(scheduler.Tick());
        clock.Advance(TimeSpan.FromMinutes(10));
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(1, web.FetchCount);
    }

    [Fact]
    public async Task The_pause_ends_by_itself_and_the_skipped_provider_fetches_on_the_next_tick()
    {
        var clock = new FakeClock();
        var web = new Provider("web", SourceKind.WebSession);
        var scheduler = new RefreshScheduler([web], BaseInterval, clock);
        await Task.WhenAll(scheduler.Tick());
        var until = scheduler.PauseFor(Hour);
        Assert.Equal(until, scheduler.PausedUntil);

        clock.Advance(Hour - TimeSpan.FromSeconds(1));
        await Task.WhenAll(scheduler.Tick());
        Assert.Equal(1, web.FetchCount);
        Assert.Equal(until, scheduler.PausedUntil);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(scheduler.Tick());

        Assert.Null(scheduler.PausedUntil);
        Assert.Equal(2, web.FetchCount);
    }

    [Fact]
    public async Task Resume_ends_the_pause_at_once()
    {
        var clock = new FakeClock();
        var web = new Provider("web", SourceKind.WebSession);
        var scheduler = new RefreshScheduler([web], BaseInterval, clock);
        await Task.WhenAll(scheduler.Tick());
        scheduler.PauseFor(Hour);
        clock.Advance(TimeSpan.FromMinutes(5));

        scheduler.Resume();
        await Task.WhenAll(scheduler.Tick());

        Assert.Null(scheduler.PausedUntil);
        Assert.Equal(2, web.FetchCount);
    }

    [Fact]
    public async Task A_manual_refresh_fetches_once_and_keeps_the_pause()
    {
        var clock = new FakeClock();
        var web = new Provider("web", SourceKind.WebSession);
        var scheduler = new RefreshScheduler([web], BaseInterval, clock);
        await Task.WhenAll(scheduler.Tick());
        var until = scheduler.PauseFor(Hour);

        await Task.WhenAll(scheduler.RefreshNow());
        Assert.Equal(2, web.FetchCount);
        Assert.Equal(until, scheduler.PausedUntil);

        clock.Advance(TimeSpan.FromMinutes(10));
        await Task.WhenAll(scheduler.Tick());
        Assert.Equal(2, web.FetchCount);
    }
}
