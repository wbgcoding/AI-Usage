using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

// The view model reads Application.Current; see MainViewModelTests for why this joins the shared collection.
[Collection(SharedStateTestsCollection.Name)]
public class NotificationForecastTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static UsageWindow FiveHour(double percent, DateTimeOffset resetsAt) =>
        new("Window_FiveHour", WindowKind.FiveHour, percent, resetsAt, 300);

    private readonly List<DisposableTestDirectory> _dirs = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-forecast");
        _dirs.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _dirs)
            directory.Dispose();
    }

    private static (NotificationService Service, List<ForecastNotification> Raised) Watch(string directory, DateTimeOffset? now = null)
    {
        var service = new NotificationService(directory, now ?? Start);
        var raised = new List<ForecastNotification>();
        service.ForecastRaised += raised.Add;
        return (service, raised);
    }

    [Fact]
    public void Fires_once_per_period_and_not_again_after_a_dip()
    {
        var (service, raised) = Watch(TempDirectory());
        var resetsAt = Start.AddHours(3);

        service.EvaluateForecast("codex", "Codex", FiveHour(80, resetsAt), TimeSpan.FromMinutes(25), true, Start);
        service.EvaluateForecast("codex", "Codex", FiveHour(82, resetsAt), TimeSpan.FromMinutes(20), true, Start.AddMinutes(1));
        service.EvaluateForecast("codex", "Codex", FiveHour(60, resetsAt), null, true, Start.AddMinutes(2)); // dip: no confident forecast
        service.EvaluateForecast("codex", "Codex", FiveHour(80, resetsAt), TimeSpan.FromMinutes(25), true, Start.AddMinutes(3));

        var only = Assert.Single(raised);
        Assert.Equal(TimeSpan.FromMinutes(25), only.Remaining);
    }

    [Fact]
    public void Fires_again_in_the_next_period()
    {
        var (service, raised) = Watch(TempDirectory());

        service.EvaluateForecast("codex", "Codex", FiveHour(80, Start.AddHours(3)), TimeSpan.FromMinutes(25), true, Start);
        var later = Start.AddHours(5);
        service.EvaluateForecast("codex", "Codex", FiveHour(80, later.AddHours(3)), TimeSpan.FromMinutes(25), true, later);

        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void A_reset_time_that_only_wobbles_is_still_the_same_period()
    {
        var (service, raised) = Watch(TempDirectory());

        service.EvaluateForecast("codex", "Codex", FiveHour(80, Start.AddHours(3)), TimeSpan.FromMinutes(25), true, Start);
        service.EvaluateForecast("codex", "Codex", FiveHour(82, Start.AddHours(3).AddMinutes(2)), TimeSpan.FromMinutes(20), true, Start.AddMinutes(1));

        Assert.Single(raised);
    }

    [Fact]
    public void The_off_switch_suppresses_it_and_does_not_use_up_the_period()
    {
        var (service, raised) = Watch(TempDirectory());
        var resetsAt = Start.AddHours(3);

        service.EvaluateForecast("codex", "Codex", FiveHour(80, resetsAt), TimeSpan.FromMinutes(25), false, Start);
        Assert.Empty(raised);

        service.EvaluateForecast("codex", "Codex", FiveHour(80, resetsAt), TimeSpan.FromMinutes(25), true, Start.AddMinutes(1));
        Assert.Single(raised);
    }

    [Theory]
    [InlineData(31, 80, 180)] // further than 30 minutes away
    [InlineData(10, 100, 180)] // already full
    [InlineData(25, 80, 20)] // the reset comes before the projected fill
    [InlineData(25, 80, 25)] // reset at the very same moment
    public void Does_not_fire_outside_its_conditions(int minutesToFull, int percent, int resetInMinutes)
    {
        var (service, raised) = Watch(TempDirectory());

        service.EvaluateForecast("codex", "Codex", FiveHour(percent, Start.AddMinutes(resetInMinutes)), TimeSpan.FromMinutes(minutesToFull), true, Start);

        Assert.Empty(raised);
    }

    [Theory]
    [InlineData(5, 3, 7, true)] // 7 min to full, lead 3 + read every 5: the next read could already be past it
    [InlineData(5, 3, 8, true)] // exactly the lead plus the interval
    [InlineData(5, 3, 9, false)] // beyond both
    public void The_read_interval_widens_the_warning_range(int intervalMinutes, int leadMinutes, int minutesToFull, bool fires)
    {
        var (service, raised) = Watch(TempDirectory());

        service.EvaluateForecast("codex", "Codex", FiveHour(80, Start.AddHours(3)), TimeSpan.FromMinutes(minutesToFull), true, Start,
            TimeSpan.FromMinutes(leadMinutes), TimeSpan.FromMinutes(intervalMinutes));

        Assert.Equal(fires, raised.Count == 1);
    }

    [Fact]
    public void A_window_without_a_reset_time_never_warns()
    {
        var (service, raised) = Watch(TempDirectory());
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 80, null, 300);

        service.EvaluateForecast("codex", "Codex", window, TimeSpan.FromMinutes(10), true, Start);

        Assert.Empty(raised);
    }

    [Fact]
    public void The_fired_period_survives_a_restart()
    {
        var directory = TempDirectory();
        var resetsAt = Start.AddHours(3);
        Watch(directory).Service.EvaluateForecast("codex", "Codex", FiveHour(80, resetsAt), TimeSpan.FromMinutes(25), true, Start);

        var (restarted, raised) = Watch(directory, Start.AddMinutes(5));
        restarted.EvaluateForecast("codex", "Codex", FiveHour(82, resetsAt), TimeSpan.FromMinutes(20), true, Start.AddMinutes(5));

        Assert.Empty(raised);
    }

    [Fact]
    public void The_forecast_arm_is_independent_of_the_threshold_alert()
    {
        var (service, raised) = Watch(TempDirectory());
        var thresholds = new List<ThresholdNotification>();
        service.NotificationRaised += thresholds.Add;
        var resetsAt = Start.AddHours(3);

        service.EvaluateForecast("codex", "Codex", FiveHour(80, resetsAt), TimeSpan.FromMinutes(25), true, Start);
        service.Evaluate("codex", "Codex", FiveHour(86, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(1));

        Assert.Single(raised);
        Assert.Single(thresholds);
    }

    [Fact]
    public void The_text_names_provider_window_time_and_percent()
    {
        var text = new ForecastNotification("codex", "Codex", WindowKind.FiveHour, 82, TimeSpan.FromMinutes(25)).Text();

        Assert.Contains("Codex", text);
        Assert.Contains("25m", text);
        Assert.Contains("82", text);
    }

    // The view model side: history points feed the forecast, the per-provider switch and quiet hours gate it.
    private sealed class SteadyProvider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public bool RunsOnUiThread => true;
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private async Task<List<ForecastNotification>> RunViewModel(Action<AppSettings, string> configure)
    {
        // The view model keeps its alert state in the real data folder, so every run uses a provider id
        // of its own instead of meeting the period a previous run already used up.
        var id = $"forecast-test-{Guid.NewGuid():N}";
        var settings = new AppSettings();
        settings.Providers[id] = new ProviderSettings { Visible = true };
        configure(settings, id);
        var clock = DateTimeOffset.Now;
        var store = new HistoryStore(TempDirectory(), () => clock);
        var now = DateTimeOffset.Now;
        var resetsAt = now.AddHours(2);
        // Eight readings over 49 minutes climbing about one percent a minute: full in roughly ten minutes.
        for (var i = 0; i < 8; i++)
        {
            clock = now.AddMinutes(-49 + (i * 7));
            store.Append(id, WindowKind.FiveHour, 40 + (i * 7), resetsAt);
        }

        clock = now;
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, [new SteadyProvider(id)], store);
        var raised = new List<ForecastNotification>();
        vm.ForecastRaised += raised.Add;

        vm.OnSnapshotReady(new ProviderSnapshot(
            id, [FiveHour(89, resetsAt)], "Plus", SourceKind.LocalFile, now, now, ProviderStatus.Ok, null));
        await vm.WaitForPendingHistoryReadsAsync();
        return raised;
    }

    [Fact]
    public async Task The_view_model_raises_it_from_the_tile_history()
    {
        var raised = await RunViewModel((_, _) => { });

        var only = Assert.Single(raised);
        Assert.StartsWith("forecast-test-", only.ProviderId);
        Assert.True(only.Remaining < TimeSpan.FromMinutes(30));
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, false)]
    [InlineData(120, 120, true)]
    [InlineData(120, 121, false)]
    [InlineData(30, 30, true)]
    [InlineData(30, 31, false)]
    public void The_chosen_lead_is_the_boundary_for_the_warning(int leadMinutes, int minutesToFull, bool fires)
    {
        var (service, raised) = Watch(TempDirectory());

        service.EvaluateForecast(
            "codex", "Codex", FiveHour(80, Start.AddHours(5)), TimeSpan.FromMinutes(minutesToFull), true, Start, TimeSpan.FromMinutes(leadMinutes));

        Assert.Equal(fires, raised.Count == 1);
    }

    [Fact]
    public void Without_a_lead_the_default_of_thirty_minutes_applies()
    {
        var (service, raised) = Watch(TempDirectory());

        service.EvaluateForecast("codex", "Codex", FiveHour(80, Start.AddHours(5)), TimeSpan.FromMinutes(31), true, Start);
        Assert.Empty(raised);
        service.EvaluateForecast("codex", "Codex", FiveHour(80, Start.AddHours(5)), TimeSpan.FromMinutes(30), true, Start);
        Assert.Single(raised);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(3, 3)]
    [InlineData(120, 120)]
    [InlineData(0, 3)]
    [InlineData(-5, 3)]
    [InlineData(500, 120)]
    [InlineData(7, 5)] // nearer to 5 than to 10
    [InlineData(8, 10)]
    [InlineData(4, 3)] // halfway: the shorter lead
    [InlineData(20, 15)] // halfway between 15 and 30 is 22.5, so 20 is nearer to 15
    [InlineData(40, 45)]
    [InlineData(75, 60)] // halfway between 60 and 90: the shorter lead
    public void A_stored_lead_is_clamped_and_snapped_to_the_nearest_choice(int stored, int expected) =>
        Assert.Equal(expected, AppSettings.NormalizeForecastLeadMinutes(stored));

    // The stored value goes through the file format as is, bypassing nothing the loader does.
    private static string ForecastLeadJson(int minutes) =>
        System.Text.Json.JsonSerializer.Serialize(new AppSettings { ForecastLeadMinutes = minutes }, SettingsStore.JsonOptions);

    [Fact]
    public void A_settings_file_loads_its_lead_snapped_and_a_missing_one_as_thirty_and_it_round_trips()
    {
        Assert.Equal(30, SettingsStore.Validate("{}").Settings!.ForecastLeadMinutes);
        Assert.Equal(5, SettingsStore.Validate(ForecastLeadJson(7)).Settings!.ForecastLeadMinutes);
        Assert.Equal(120, SettingsStore.Validate(ForecastLeadJson(9999)).Settings!.ForecastLeadMinutes);

        var settings = new AppSettings { ForecastLeadMinutes = 90 };
        var json = System.Text.Json.JsonSerializer.Serialize(settings, SettingsStore.JsonOptions);
        Assert.Equal(90, SettingsStore.Validate(json).Settings!.ForecastLeadMinutes);
    }

    [Fact]
    public async Task The_view_model_uses_the_lead_from_the_settings()
    {
        // The history climbs to full in about ten minutes: a three minute lead stays quiet, a two hour one warns.
        Assert.Empty(await RunViewModel((s, _) => s.ForecastLeadMinutes = 3));
        Assert.Single(await RunViewModel((s, _) => s.ForecastLeadMinutes = 120));
    }

    [Fact]
    public async Task The_settings_switch_the_provider_switch_and_quiet_hours_suppress_it()
    {
        Assert.Empty(await RunViewModel((s, _) => s.ForecastAlertEnabled = false));
        Assert.Empty(await RunViewModel((s, id) => s.Providers[id].NotificationsEnabled = false));
        Assert.Empty(await RunViewModel((s, _) =>
        {
            s.QuietHoursEnabled = true;
            s.QuietHoursStart = "00:00";
            s.QuietHoursEnd = "23:59";
        }));
    }
    [Fact]
    public async Task A_signed_out_account_raises_no_forecast_on_a_later_history_refresh()
    {
        var id = $"forecast-test-{Guid.NewGuid():N}";
        var settings = new AppSettings();
        settings.Providers[id] = new ProviderSettings { Visible = true };
        var clock = DateTimeOffset.Now;
        var store = new HistoryStore(TempDirectory(), () => clock);
        var now = clock;
        var resetsAt = now.AddHours(2);
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, [new SteadyProvider(id)], store);
        vm.SignOutWebView = _ => SignOutResult.Removed;
        var raised = new List<ForecastNotification>();
        vm.ForecastRaised += raised.Add;

        // No history yet, so the good read alone raises nothing.
        vm.OnSnapshotReady(new ProviderSnapshot(
            id, [FiveHour(89, resetsAt)], "Plus", SourceKind.LocalFile, now, now, ProviderStatus.Ok, null));
        await vm.WaitForPendingHistoryReadsAsync();
        await vm.SignOutAsync(id);

        // A climbing history then arrives and the charts are drawn again before the next read.
        for (var i = 0; i < 8; i++)
        {
            clock = now.AddMinutes(-49 + (i * 7));
            store.Append(id, WindowKind.FiveHour, 40 + (i * 7), resetsAt);
        }

        clock = now;
        vm.RefreshHistoryForAllTiles();
        await vm.WaitForPendingHistoryReadsAsync();

        Assert.Empty(raised);
    }
}
