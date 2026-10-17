using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Services;

namespace AiUsage.Tests;

public class CarryWeeklyLimitTests
{
    private static readonly DateTimeOffset FiveHourReset = DateTimeOffset.Parse("2026-10-01T15:00:00Z");
    private static readonly DateTimeOffset WeekReset = DateTimeOffset.Parse("2026-10-05T09:00:00Z");

    private static UsageWindow FiveHour(double percent, UsageAllowance? allowance = null) =>
        new("window.fiveHour", WindowKind.FiveHour, percent, FiveHourReset, 300, null, allowance);

    private static UsageWindow Weekly(double percent, DateTimeOffset? resetsAt = null) =>
        new("window.weekly", WindowKind.Weekly, percent, resetsAt ?? WeekReset, 10080);

    private static ProviderSnapshot Snapshot(params UsageWindow[] windows) => new(
        "codex", windows, null, SourceKind.LocalFile, DateTimeOffset.Parse("2026-10-01T12:00:00Z"), null,
        ProviderStatus.Ok, null);

    [Fact]
    public void A_used_up_weekly_limit_fills_the_five_hour_window_with_the_weekly_reset()
    {
        var carried = ProviderSnapshots.CarryWeeklyLimit(Snapshot(FiveHour(0), Weekly(100)));

        var five = Assert.Single(carried.Windows, w => w.Kind == WindowKind.FiveHour);
        Assert.Equal(100, five.UsedPercent);
        Assert.Equal(WeekReset, five.ResetsAt);
        Assert.Equal(300, five.WindowMinutes);
        Assert.Equal(100, Assert.Single(carried.Windows, w => w.Kind == WindowKind.Weekly).UsedPercent);
    }

    [Fact]
    public void The_allowance_of_the_five_hour_window_is_used_up_too()
    {
        var allowance = new UsageAllowance(3, 50, "unit.requests");

        var carried = ProviderSnapshots.CarryWeeklyLimit(Snapshot(FiveHour(6, allowance), Weekly(100)));

        var five = Assert.Single(carried.Windows, w => w.Kind == WindowKind.FiveHour);
        Assert.Equal(50, five.Allowance!.Used);
        Assert.Equal(50, five.Allowance.Total);
    }

    [Fact]
    public void Several_full_weekly_windows_use_the_latest_reset()
    {
        var later = WeekReset.AddDays(1);

        var carried = ProviderSnapshots.CarryWeeklyLimit(Snapshot(FiveHour(0), Weekly(100), Weekly(100, later)));

        Assert.Equal(later, Assert.Single(carried.Windows, w => w.Kind == WindowKind.FiveHour).ResetsAt);
    }

    [Fact]
    public void A_weekly_limit_below_full_leaves_the_snapshot_unchanged()
    {
        var snapshot = Snapshot(FiveHour(0), Weekly(99));

        Assert.Same(snapshot, ProviderSnapshots.CarryWeeklyLimit(snapshot));
    }

    [Fact]
    public void Without_a_weekly_window_the_snapshot_is_unchanged()
    {
        var snapshot = Snapshot(FiveHour(0));

        Assert.Same(snapshot, ProviderSnapshots.CarryWeeklyLimit(snapshot));
    }

    [Fact]
    public void A_full_per_model_weekly_window_never_triggers_it()
    {
        var snapshot = Snapshot(
            FiveHour(0), new UsageWindow("window.model", WindowKind.Other, 100, WeekReset, 10080));

        Assert.Same(snapshot, ProviderSnapshots.CarryWeeklyLimit(snapshot));
    }

    [Fact]
    public async Task A_snapshot_passing_the_scheduler_comes_out_carried()
    {
        var provider = new StubProvider(Snapshot(FiveHour(0), Weekly(100)));
        var scheduler = new RefreshScheduler([provider], TimeSpan.FromSeconds(60));
        var reported = new List<ProviderSnapshot>();
        scheduler.SnapshotReady += reported.Add;

        await Task.WhenAll(scheduler.Tick());

        var five = Assert.Single(Assert.Single(reported).Windows, w => w.Kind == WindowKind.FiveHour);
        Assert.Equal(100, five.UsedPercent);
        Assert.Equal(WeekReset, five.ResetsAt);
    }

    private sealed class StubProvider(ProviderSnapshot snapshot) : IUsageProvider
    {
        public string Id => snapshot.ProviderId;
        public string DisplayName => Id;
        public TimeSpan? MinRefreshInterval => null;
        public bool RunsOnUiThread => false;
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }
}
