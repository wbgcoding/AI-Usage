using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

public class RefreshSchedulerNetworkTests
{
    private static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(60);

    private static ProviderSnapshot Ok(string id) =>
        new(id, [], "plus", SourceKind.LocalFile, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProviderStatus.Ok, null);

    private static ProviderSnapshot Failed(string id, FailureKind kind) =>
        new(id, [], null, SourceKind.None, DateTimeOffset.UtcNow, null, ProviderStatus.Failed,
            new ProviderError("Status_Failed_Reason", Kind: kind));

    private sealed class Provider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public ProviderSnapshot? Next { get; set; }
        public Func<Task<ProviderSnapshot>>? Fetch { get; set; }
        public int FetchCount { get; private set; }

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
        {
            FetchCount++;
            return Fetch is not null ? Fetch() : Task.FromResult(Next ?? Ok(Id));
        }
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task The_network_coming_back_reads_only_the_providers_that_failed_for_the_network()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var offline = new Provider("claude") { Next = Failed("claude", FailureKind.Network) };
        var refused = new Provider("codex") { Next = Failed("codex", FailureKind.Refused) };
        var healthy = new Provider("gemini") { Next = Ok("gemini") };
        var scheduler = new RefreshScheduler([offline, refused, healthy], BaseInterval, clock);
        await Task.WhenAll(scheduler.Tick());
        clock.Advance(TimeSpan.FromSeconds(5));

        var started = scheduler.RefreshNetworkFailed();
        await Task.WhenAll(started);

        _ = Assert.Single(started);
        Assert.Equal(2, offline.FetchCount);
        Assert.Equal(1, refused.FetchCount);
        Assert.Equal(1, healthy.FetchCount);
    }

    [Fact]
    public async Task A_network_failure_that_recovered_is_not_read_again()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new Provider("claude") { Next = Failed("claude", FailureKind.Network) };
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        await Task.WhenAll(scheduler.Tick());
        clock.Advance(TimeSpan.FromMinutes(3));
        provider.Next = Ok("claude");
        await Task.WhenAll(scheduler.Tick());

        var started = scheduler.RefreshNetworkFailed();

        Assert.Empty(started);
    }

    [Fact]
    public async Task The_backoff_of_a_network_failed_provider_is_cleared()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new Provider("claude") { Next = Failed("claude", FailureKind.Network) };
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        await Task.WhenAll(scheduler.Tick());
        clock.Advance(TimeSpan.FromMinutes(2));
        await Task.WhenAll(scheduler.Tick());

        await Task.WhenAll(scheduler.RefreshNetworkFailed());

        // Cleared, the failure after the reset waits one doubling (two minutes), not three (eight).
        clock.Advance(TimeSpan.FromMinutes(2));
        var due = scheduler.Tick();
        _ = Assert.Single(due);
        await Task.WhenAll(due);
    }

    [Fact]
    public async Task The_hidden_check_only_runs_on_the_thread_that_ticks()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new Provider("claude")
        {
            Fetch = async () =>
            {
                await Task.Delay(30);
                return Ok("claude");
            },
        };
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        var callers = new List<int>();
        bool IsHidden(string id)
        {
            lock (callers)
                callers.Add(Environment.CurrentManagedThreadId);
            return false;
        }

        var tickThread = Environment.CurrentManagedThreadId;
        await Task.WhenAll(scheduler.Tick(IsHidden));

        Assert.NotEmpty(callers);
        Assert.All(callers, id => Assert.Equal(tickThread, id));
    }

    [Fact]
    public async Task A_hidden_tile_still_waits_the_longer_interval_after_its_fetch()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var provider = new Provider("claude");
        var scheduler = new RefreshScheduler([provider], BaseInterval, clock);
        await Task.WhenAll(scheduler.Tick(isHidden: _ => true));

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Empty(scheduler.Tick(isHidden: _ => true));
        clock.Advance(TimeSpan.FromMinutes(1));
        _ = Assert.Single(scheduler.Tick(isHidden: _ => true));
    }

    [Fact]
    public void The_window_refreshes_network_failed_providers_when_a_connection_is_reported()
    {
        var source = SourceScan.Read("src", "AiUsage", "Views", "MainWindow.xaml.cs");
        var body = SourceScan.Member(source, "private async void NetworkChange_NetworkAvailabilityChanged");

        Assert.Contains("if (!e.IsAvailable)", body, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke", body, StringComparison.Ordinal);
        Assert.Contains("ViewModel.RefreshNetworkFailed()", body, StringComparison.Ordinal);
        Assert.Contains("NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_change_restarts_one_debounce_timer_that_checks_the_network_and_refreshes_the_failed_ones()
    {
        var source = SourceScan.Read("src", "AiUsage", "Views", "MainWindow.xaml.cs");
        var changed = SourceScan.Member(source, "private void NetworkChange_NetworkAddressChanged");
        var tick = SourceScan.Member(source, "private void AddressChangeTimer_Tick");

        Assert.Contains("NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged", source, StringComparison.Ordinal);
        Assert.Contains("NetworkChange.NetworkAddressChanged -= NetworkChange_NetworkAddressChanged", source, StringComparison.Ordinal);
        Assert.Contains("_addressChangeTimer.Stop();", changed, StringComparison.Ordinal);
        Assert.Contains("_addressChangeTimer.Start();", changed, StringComparison.Ordinal);
        Assert.Contains("NetworkStatus.HasInternet()", tick, StringComparison.Ordinal);
        Assert.Contains("ViewModel.RefreshNetworkFailed()", tick, StringComparison.Ordinal);
        Assert.Contains("Interval = NetworkSettleTime", source, StringComparison.Ordinal);
    }
}
