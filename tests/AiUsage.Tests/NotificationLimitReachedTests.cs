using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

// The view model reads Application.Current; see MainViewModelTests for why this joins the shared collection.
[Collection(SharedStateTestsCollection.Name)]
public class NotificationLimitReachedTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static UsageWindow FiveHour(double percent, DateTimeOffset? resetsAt) =>
        new("Window_FiveHour", WindowKind.FiveHour, percent, resetsAt, 300);

    private readonly List<DisposableTestDirectory> _dirs = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-limit");
        _dirs.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _dirs)
            directory.Dispose();
    }

    private static (NotificationService Service, List<LimitReachedNotification> Limits, List<ThresholdNotification> Thresholds) Watch(
        string directory, DateTimeOffset? now = null)
    {
        var service = new NotificationService(directory, now ?? Start);
        var limits = new List<LimitReachedNotification>();
        var thresholds = new List<ThresholdNotification>();
        service.LimitReachedRaised += limits.Add;
        service.NotificationRaised += thresholds.Add;
        return (service, limits, thresholds);
    }

    private static void Evaluate(
        NotificationService service, double percent, DateTimeOffset? resetsAt, DateTimeOffset now,
        double threshold = 85, bool enabled = true, bool limitEnabled = true) =>
        service.Evaluate("codex", "Codex", FiveHour(percent, resetsAt), threshold, enabled, notifyOnReset: false, now, limitEnabled);

    [Fact]
    public void Fires_once_at_100_and_not_again_inside_the_period()
    {
        var (service, limits, _) = Watch(TempDirectory());
        var resetsAt = Start.AddHours(2);

        Evaluate(service, 99, resetsAt, Start);
        Evaluate(service, 100, resetsAt, Start.AddMinutes(1));
        Evaluate(service, 100, resetsAt, Start.AddMinutes(2));
        Evaluate(service, 96, resetsAt, Start.AddMinutes(3)); // dip inside the period
        Evaluate(service, 100, resetsAt, Start.AddMinutes(4));

        Assert.Single(limits);
    }

    [Fact]
    public void Fires_again_after_the_reset()
    {
        var (service, limits, _) = Watch(TempDirectory());

        Evaluate(service, 100, Start.AddHours(2), Start);
        var later = Start.AddHours(3);
        Evaluate(service, 20, later.AddHours(5), later);
        Evaluate(service, 100, later.AddHours(5), later.AddHours(1));

        Assert.Equal(2, limits.Count);
    }

    [Fact]
    public void Fires_on_the_first_sight_of_a_full_window()
    {
        var (service, limits, _) = Watch(TempDirectory());

        Evaluate(service, 100, Start.AddHours(2), Start);

        Assert.Single(limits);
    }

    [Fact]
    public void Its_own_switch_suppresses_it_but_not_the_threshold_alert()
    {
        var (service, limits, thresholds) = Watch(TempDirectory());

        Evaluate(service, 100, Start.AddHours(2), Start, limitEnabled: false);

        Assert.Empty(limits);
        Assert.Single(thresholds);
    }

    [Fact]
    public void The_threshold_switch_off_suppresses_it_like_the_threshold_alert()
    {
        var (service, limits, thresholds) = Watch(TempDirectory());

        Evaluate(service, 100, Start.AddHours(2), Start, enabled: false);

        Assert.Empty(limits);
        Assert.Empty(thresholds);
    }

    [Fact]
    public void A_threshold_of_100_gives_both_alerts_once_each()
    {
        var (service, limits, thresholds) = Watch(TempDirectory());
        var resetsAt = Start.AddHours(2);

        Evaluate(service, 100, resetsAt, Start, threshold: 100);
        Evaluate(service, 100, resetsAt, Start.AddMinutes(1), threshold: 100);

        Assert.Single(limits);
        Assert.Single(thresholds);
    }

    [Fact]
    public void Both_alerts_fire_in_one_period_when_usage_climbs_past_the_threshold_to_full()
    {
        var (service, limits, thresholds) = Watch(TempDirectory());
        var resetsAt = Start.AddHours(2);

        Evaluate(service, 90, resetsAt, Start);
        Evaluate(service, 100, resetsAt, Start.AddMinutes(5));

        Assert.Single(thresholds);
        Assert.Single(limits);
    }

    [Fact]
    public void A_window_without_a_reset_time_fires_again_only_after_it_fell_below_100()
    {
        var (service, limits, _) = Watch(TempDirectory());

        Evaluate(service, 100, null, Start);
        Evaluate(service, 100, null, Start.AddMinutes(1));
        Evaluate(service, 50, null, Start.AddMinutes(2));
        Evaluate(service, 100, null, Start.AddMinutes(3));

        Assert.Equal(2, limits.Count);
    }

    [Fact]
    public void The_fired_period_survives_a_restart()
    {
        var directory = TempDirectory();
        var resetsAt = Start.AddHours(2);
        Evaluate(Watch(directory).Service, 100, resetsAt, Start);

        var (restarted, limits, _) = Watch(directory, Start.AddMinutes(5));
        Evaluate(restarted, 100, resetsAt, Start.AddMinutes(5));

        Assert.Empty(limits);
    }

    [Fact]
    public void The_text_names_the_reset_countdown_or_leaves_it_out()
    {
        var withReset = new LimitReachedNotification("codex", "Codex", WindowKind.FiveHour, Start.AddHours(2)).Text(Start);
        var withoutReset = new LimitReachedNotification("codex", "Codex", WindowKind.FiveHour, null).Text(Start);

        Assert.Contains("Codex", withReset);
        Assert.NotEqual(withoutReset, withReset);
        Assert.Contains("Codex", withoutReset);
        Assert.DoesNotContain("2h", withoutReset);
    }

    private sealed class SteadyProvider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public bool RunsOnUiThread => true;
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private List<LimitReachedNotification> RunViewModel(Action<AppSettings, string> configure)
    {
        // The view model keeps its alert state in the real data folder, so every run uses a provider id
        // of its own instead of meeting the period a previous run already used up.
        var id = $"limit-test-{Guid.NewGuid():N}";
        var settings = new AppSettings();
        settings.Providers[id] = new ProviderSettings { Visible = true };
        configure(settings, id);
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, [new SteadyProvider(id)], new HistoryStore(TempDirectory(), () => Start));
        var raised = new List<LimitReachedNotification>();
        vm.LimitReachedRaised += raised.Add;
        var now = DateTimeOffset.Now;
        vm.OnSnapshotReady(new ProviderSnapshot(
            id, [FiveHour(100, now.AddHours(2))], "Plus", SourceKind.LocalFile, now, now, ProviderStatus.Ok, null));
        return raised;
    }

    [Fact]
    public void The_view_model_forwards_it()
    {
        Assert.Single(RunViewModel((_, _) => { }));
    }

    [Fact]
    public void The_settings_switch_the_provider_switch_and_quiet_hours_suppress_it()
    {
        Assert.Empty(RunViewModel((s, _) => s.LimitReachedAlertEnabled = false));
        Assert.Empty(RunViewModel((s, id) => s.Providers[id].NotificationsEnabled = false));
        Assert.Empty(RunViewModel((s, _) => s.DefaultThresholdEnabled = false));
        Assert.Empty(RunViewModel((s, _) =>
        {
            s.QuietHoursEnabled = true;
            s.QuietHoursStart = "00:00";
            s.QuietHoursEnd = "23:59";
        }));
    }
}
