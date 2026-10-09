using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>Battery and energy-saver rules of the scheduler, driven by a plain power value instead
/// of the real system.</summary>
public class RefreshSchedulerPowerTests
{
    private static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(60);
    private static readonly PowerState OnBattery = new(OnBattery: true, EnergySaver: false);
    private static readonly PowerState EnergySaver = new(OnBattery: true, EnergySaver: true);

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, false, false)]
    [InlineData(255, false, false)]
    [InlineData(1, true, false)]
    public void The_system_values_read_as_battery_and_energy_saver(byte acLine, bool saver, bool onBattery)
    {
        var state = PowerStatus.Interpret(acLine, (byte)(saver ? 1 : 0));

        Assert.Equal(onBattery, state.OnBattery);
        Assert.Equal(saver, state.EnergySaver);
    }

    [Fact]
    public void The_energy_saver_flag_is_read_even_on_mains_power()
    {
        Assert.True(PowerStatus.Interpret(1, 1).EnergySaver);
    }

    [Fact]
    public void ResolveInterval_doubles_the_result_on_battery()
    {
        Assert.Equal(TimeSpan.FromSeconds(120),
            RefreshScheduler.ResolveInterval(BaseInterval, null, isHidden: false, windowVisible: true, onBattery: true));
    }

    [Fact]
    public void ResolveInterval_doubles_after_the_floors_were_applied()
    {
        // 15 min for a hidden tile, then doubled.
        Assert.Equal(TimeSpan.FromMinutes(30),
            RefreshScheduler.ResolveInterval(BaseInterval, TimeSpan.FromMinutes(5), isHidden: true, windowVisible: false, onBattery: true));
    }

    [Fact]
    public void ResolveInterval_is_unchanged_off_battery()
    {
        Assert.Equal(BaseInterval, RefreshScheduler.ResolveInterval(BaseInterval, null, isHidden: false, windowVisible: true));
    }

    [Fact]
    public async Task On_battery_the_next_fetch_comes_after_twice_the_interval()
    {
        var clock = new FakeClock();
        var provider = new Provider("a", SourceKind.LocalFile);
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        scheduler.SetSaveEnergyOnBattery(true);
        scheduler.SetPower(OnBattery);

        await Task.WhenAll(scheduler.Tick());
        clock.Advance(BaseInterval);
        Assert.Empty(scheduler.Tick());
        clock.Advance(BaseInterval);
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(2, provider.FetchCount);
    }

    [Fact]
    public async Task With_the_setting_off_the_interval_stays_even_on_battery()
    {
        var clock = new FakeClock();
        var provider = new Provider("a", SourceKind.LocalFile);
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        scheduler.SetSaveEnergyOnBattery(false);
        scheduler.SetPower(EnergySaver);

        await Task.WhenAll(scheduler.Tick());
        clock.Advance(BaseInterval);
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(2, provider.FetchCount);
    }

    [Fact]
    public async Task The_energy_saver_skips_a_provider_whose_last_reading_was_not_local()
    {
        var clock = new FakeClock();
        var web = new Provider("web", SourceKind.WebSession);
        var login = new Provider("login", SourceKind.LocalLogin);
        var file = new Provider("file", SourceKind.LocalFile);
        var database = new Provider("db", SourceKind.LocalDatabase);
        var scheduler = new RefreshScheduler([web, login, file, database], BaseInterval, clock);
        scheduler.SetSaveEnergyOnBattery(true);

        // The first round runs without the saver: every provider has fetched once and has a source.
        await Task.WhenAll(scheduler.Tick());
        scheduler.SetPower(EnergySaver);
        clock.Advance(TimeSpan.FromMinutes(10));
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(1, web.FetchCount);
        Assert.Equal(1, login.FetchCount);
        Assert.Equal(2, file.FetchCount);
        Assert.Equal(2, database.FetchCount);
    }

    [Fact]
    public async Task A_provider_without_any_reading_yet_is_fetched_once_on_the_energy_saver()
    {
        var clock = new FakeClock();
        var web = new Provider("web", SourceKind.WebSession);
        var scheduler = new RefreshScheduler([web], BaseInterval, clock);
        scheduler.SetSaveEnergyOnBattery(true);
        scheduler.SetPower(EnergySaver);

        await Task.WhenAll(scheduler.Tick());
        clock.Advance(TimeSpan.FromMinutes(10));
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(1, web.FetchCount);
    }

    [Fact]
    public async Task A_failed_attempt_does_not_make_a_provider_local_or_forget_its_source()
    {
        var clock = new FakeClock();
        var web = new Provider("web", SourceKind.WebSession);
        var scheduler = new RefreshScheduler([web], BaseInterval, clock);
        scheduler.SetSaveEnergyOnBattery(true);

        await Task.WhenAll(scheduler.Tick());
        web.Fail = true;
        clock.Advance(BaseInterval);
        await Task.WhenAll(scheduler.Tick());
        scheduler.SetPower(EnergySaver);
        clock.Advance(TimeSpan.FromMinutes(10));
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(2, web.FetchCount);
    }

    [Fact]
    public async Task Leaving_the_energy_saver_fetches_the_skipped_provider_on_the_next_tick()
    {
        var clock = new FakeClock();
        var web = new Provider("web", SourceKind.WebSession);
        var scheduler = new RefreshScheduler([web], BaseInterval, clock);
        scheduler.SetSaveEnergyOnBattery(true);

        await Task.WhenAll(scheduler.Tick());
        scheduler.SetPower(EnergySaver);
        clock.Advance(TimeSpan.FromMinutes(10));
        await Task.WhenAll(scheduler.Tick());
        scheduler.SetPower(default);
        await Task.WhenAll(scheduler.Tick());

        Assert.Equal(2, web.FetchCount);
    }

    [Fact]
    public async Task A_manual_refresh_reads_every_source_even_on_the_energy_saver()
    {
        var clock = new FakeClock();
        var web = new Provider("web", SourceKind.WebSession);
        var scheduler = new RefreshScheduler([web], BaseInterval, clock);
        scheduler.SetSaveEnergyOnBattery(true);

        await Task.WhenAll(scheduler.Tick());
        scheduler.SetPower(EnergySaver);
        await Task.WhenAll(scheduler.RefreshNow());

        Assert.Equal(2, web.FetchCount);
    }

    internal sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-09T12:00:00Z");

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    internal sealed class Provider(string id, SourceKind source) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public TimeSpan? MinRefreshInterval => null;
        public bool RunsOnUiThread => false;
        public bool Fail { get; set; }
        public int FetchCount { get; private set; }

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
        {
            FetchCount++;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(Fail
                ? new ProviderSnapshot(Id, [], null, SourceKind.None, now, null, ProviderStatus.Failed, new ProviderError("Status_Failed_Reason"))
                : new ProviderSnapshot(Id, [], "plus", source, now, now, ProviderStatus.Ok, null));
        }
    }
}
