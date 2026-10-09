using System.Reflection;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

// RunMaintenanceAsync reads Application.Current itself (to marshal onto the UI thread when there
// is one) - while a real one is alive on another thread, courtesy of one of the STA classes in
// SharedStateTestsCollection, CheckAccess() here comes back false and the maintenance loop tries
// to marshal onto that foreign dispatcher instead of running inline, which can starve or fault
// before a single Compact() call lands. Joining the same collection keeps that window from ever
// overlapping this class's own tests.
[Collection(SharedStateTestsCollection.Name)]
public class MainViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-main-viewmodel");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }

    private static AppSettings SettingsWithVisibility(params (string Id, bool Visible)[] providers)
    {
        var settings = new AppSettings();
        foreach (var (id, visible) in providers)
            settings.Providers[id] = new ProviderSettings { Visible = visible };
        return settings;
    }

    private (MainViewModel Vm, string HistoryDir, List<FakeProvider> Providers) Build(
        AppSettings settings, IReadOnlyDictionary<string, double>? percents = null, IAsyncDisposable? claudeRunner = null)
    {
        double PercentFor(string id) => percents is not null && percents.TryGetValue(id, out var percent) ? percent : 42;
        var providers = new List<FakeProvider>
        {
            new("codex", PercentFor("codex")), new("claude", PercentFor("claude")),
            new("gemini", PercentFor("gemini")), new("copilot", PercentFor("copilot")),
        };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyDir = TempDirectory();
        var historyStore = new HistoryStore(historyDir, () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore, claudeRunner);
        return (vm, historyDir, providers);
    }

    [Fact]
    public async Task Copilot_add_account_never_offers_the_active_login_the_primary_tile_already_shows()
    {
        var (vm, _, _) = Build(new AppSettings());
        vm.GetGitHubAccountsAsync = _ => Task.FromResult<IReadOnlyList<AiUsage.Providers.LocalLogin.GitHubAccount>>(
        [
            new("github.com", "main-user", Active: true),
            new("github.com", "second-user", Active: false),
            new("ghe.example.com", "main-user-enterprise", Active: true),
        ]);

        var candidates = await vm.ListAvailableCopilotLoginsAsync();

        // The enterprise user is left out too: its usage is never asked on its own host.
        Assert.Equal(["second-user"], candidates);
    }

    [Fact]
    public void TwoOfFourVisibleListsOnlyThoseTwoTiles()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", false), ("copilot", false));
        var (vm, _, _) = Build(settings);

        Assert.Equal(4, vm.Tiles.Count);
        Assert.Equal(2, vm.Tiles.Count(t => !t.IsHidden));
        Assert.Equal(2, vm.HiddenCount);
    }

    [Fact]
    public void TrayTooltipNamesTheTileWithTheHighestPercent()
    {
        var settings = new AppSettings();
        var (vm, _, _) = Build(settings, new Dictionary<string, double> { ["codex"] = 30, ["claude"] = 91, ["gemini"] = 42, ["copilot"] = 55 });

        vm.Tick(Now);
        var changed = MainWindow.TryComputeTraySummary(vm.Tiles, new TrayTooltipMemo(), out var tooltipText, out _, out var highestPercent);

        Assert.True(changed);
        Assert.Contains("claude", tooltipText);
        Assert.Equal(91, highestPercent);
    }

    [Fact]
    public void TrayTooltipIgnoresAHiddenTileEvenWhenItsPercentIsHighest()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", false), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings, new Dictionary<string, double> { ["codex"] = 30, ["claude"] = 90, ["gemini"] = 42, ["copilot"] = 42 });

        vm.Tick(Now);
        MainWindow.TryComputeTraySummary(vm.Tiles, new TrayTooltipMemo(), out var tooltipText, out _, out _);

        Assert.DoesNotContain("claude", tooltipText);
        Assert.Contains("gemini", tooltipText);
    }

    [Fact]
    public void HiddenProvidersAreStillFetchedAndWrittenToHistory()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", false), ("gemini", false), ("copilot", true));
        var (vm, historyDir, providers) = Build(settings);

        vm.Tick(Now);

        foreach (var provider in providers)
            Assert.True(provider.WasFetched, $"{provider.Id} should have been fetched even while hidden");

        var historyStore = new HistoryStore(historyDir, () => Now);
        Assert.NotEmpty(historyStore.Load("claude", TimeSpan.FromDays(1)));
        Assert.NotEmpty(historyStore.Load("gemini", TimeSpan.FromDays(1)));
    }

    // A snapshot that only repeats an hour-old reading (a held-over expired token, a cached web read)
    // still refreshes the tile but is not a new data point for the chart.
    [Fact]
    public void A_held_over_snapshot_updates_the_tile_but_adds_no_history_row()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var providers = new List<IUsageProvider>
        {
            new FakeProvider("codex"), new HeldOverProvider("claude"), new FakeProvider("gemini"), new FakeProvider("copilot"),
        };
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, historyStore);

        vm.Tick(Now);

        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");
        Assert.Equal(ProviderStatus.Ok, tile.Status);
        Assert.NotEmpty(tile.Rows);
        Assert.Empty(historyStore.Load("claude", TimeSpan.FromDays(1)));
        Assert.NotEmpty(historyStore.Load("codex", TimeSpan.FromDays(1)));
    }

    // An idle local read carries the time of its last session event, hours back, yet it is still a
    // fresh answer: the history line has to keep running flat to now.
    [Fact]
    public void An_old_local_reading_that_is_not_held_over_still_adds_a_history_row()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var providers = new List<IUsageProvider>
        {
            new IdleLocalProvider("codex"), new FakeProvider("claude"), new FakeProvider("gemini"), new FakeProvider("copilot"),
        };
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, historyStore);

        vm.Tick(Now);

        Assert.NotEmpty(historyStore.Load("codex", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void AProviderWithItsChartSwitchedOffShowsNoDiagramWhileTheOtherKeepsIt()
    {
        var settings = new AppSettings();
        settings.Providers["claude"].ChartHidden = true;
        var providers = new List<IUsageProvider> { new TwoWindowFakeProvider("codex"), new TwoWindowFakeProvider("claude") };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, new HistoryStore(TempDirectory(), () => Now));

        vm.Tick(Now);

        var claude = vm.Tiles.Single(t => t.ProviderId == "claude");
        var codex = vm.Tiles.Single(t => t.ProviderId == "codex");
        Assert.True(claude.HasNumbers);
        Assert.False(claude.ShowDiagram);
        Assert.Single(claude.Rows, r => r.Kind == WindowKind.Weekly);
        Assert.True(codex.ShowDiagram);
    }

    [Fact]
    public void SwitchingAChartOffOrOnTellsTheWindowToFitItsHeightAgain()
    {
        var providers = new List<IUsageProvider> { new TwoWindowFakeProvider("codex"), new TwoWindowFakeProvider("claude") };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), new AppSettings(), providers, new HistoryStore(TempDirectory(), () => Now));
        vm.Tick(Now);
        var refits = 0;
        vm.TileLayoutChanged += (_, _) => refits++;
        var claude = vm.Tiles.Single(t => t.ProviderId == "claude");

        claude.ChartShown = false;
        Assert.Equal(1, refits);

        claude.ChartShown = true;
        Assert.Equal(2, refits);
    }

    /// <summary>Hiding a bar row shrinks the tile just like hiding its chart does, so the same refit
    /// has to follow, or an automatic-height window keeps the old height.</summary>
    [Fact]
    public void HidingOrShowingABarRowTellsTheWindowToFitItsHeightAgain()
    {
        var providers = new List<IUsageProvider> { new TwoWindowFakeProvider("codex"), new TwoWindowFakeProvider("claude") };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), new AppSettings(), providers, new HistoryStore(TempDirectory(), () => Now));
        vm.Tick(Now);
        var refits = 0;
        vm.TileLayoutChanged += (_, _) => refits++;
        var claude = vm.Tiles.Single(t => t.ProviderId == "claude");

        claude.ShowFiveHour = false;
        Assert.Equal(1, refits);

        claude.ShowWeekly = false;
        Assert.Equal(2, refits);
    }

    [Fact]
    public void TheTileMenuChartEntryFlipsTheSameSwitchTheSettingsCheckBoxUses()
    {
        var settings = new AppSettings();
        var providers = new List<IUsageProvider> { new TwoWindowFakeProvider("codex"), new TwoWindowFakeProvider("claude") };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, new HistoryStore(TempDirectory(), () => Now));
        vm.Tick(Now);
        var claude = vm.Tiles.Single(t => t.ProviderId == "claude");
        var loc = LocalizationService.Instance;
        Assert.Equal(loc["Tile.Menu.HideChart"], claude.ChartMenuHeader);

        claude.ToggleChartCommand.Execute(null);

        Assert.True(claude.ChartHidden);
        Assert.False(claude.ChartShown); // what the settings check box shows
        Assert.True(settings.Providers["claude"].ChartHidden);
        Assert.Equal(loc["Tile.Menu.ShowChart"], claude.ChartMenuHeader);

        claude.ToggleChartCommand.Execute(null);

        Assert.False(claude.ChartHidden);
        Assert.True(claude.ChartShown);
        Assert.False(settings.Providers["claude"].ChartHidden);
    }

    [Fact]
    public void TheTileMenuChartEntryIsOnlyOfferedInFullDensity()
    {
        var providers = new List<IUsageProvider> { new TwoWindowFakeProvider("claude") };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), new AppSettings(), providers, new HistoryStore(TempDirectory(), () => Now));
        var tile = vm.Tiles.Single();

        tile.Density = TileDensity.Full;
        Assert.True(tile.ShowChartMenuItem);
        tile.Density = TileDensity.Mini;
        Assert.False(tile.ShowChartMenuItem);
    }

    [Fact]
    public void SwitchingAChartOffIsSavedAndStillOffAfterTheSettingsAreLoadedAgain()
    {
        var directory = TempDirectory();
        var settings = new AppSettings();
        using (var store = new SettingsStore(directory))
        {
            var providers = new List<IUsageProvider> { new TwoWindowFakeProvider("codex"), new TwoWindowFakeProvider("claude") };
            var vm = new MainViewModel(store, settings, providers, new HistoryStore(TempDirectory(), () => Now));
            vm.Tick(Now);

            vm.Tiles.Single(t => t.ProviderId == "claude").ChartShown = false;
            store.SaveNow(settings);
        }

        using var reloadStore = new SettingsStore(directory);
        var reloaded = reloadStore.Load();
        var reloadedVm = new MainViewModel(reloadStore, reloaded,
            new List<IUsageProvider> { new TwoWindowFakeProvider("codex"), new TwoWindowFakeProvider("claude") },
            new HistoryStore(TempDirectory(), () => Now));
        reloadedVm.Tick(Now);

        Assert.False(reloadedVm.Tiles.Single(t => t.ProviderId == "claude").ShowDiagram);
        Assert.True(reloadedVm.Tiles.Single(t => t.ProviderId == "codex").ShowDiagram);
    }

    [Fact]
    public void SwitchingAnAttentionMarkOffIsSavedAndStillOffAfterTheSettingsAreLoadedAgain()
    {
        var directory = TempDirectory();
        var settings = new AppSettings();
        using (var store = new SettingsStore(directory))
        {
            var vm = new MainViewModel(store, settings, new List<IUsageProvider> { new TwoWindowFakeProvider("claude") },
                new HistoryStore(TempDirectory(), () => Now));

            vm.Tiles.Single().AttentionEnabled = false;
            store.SaveNow(settings);
        }

        using var reloadStore = new SettingsStore(directory);
        var reloaded = new MainViewModel(reloadStore, reloadStore.Load(), new List<IUsageProvider> { new TwoWindowFakeProvider("claude") },
            new HistoryStore(TempDirectory(), () => Now));

        Assert.False(reloaded.Tiles.Single().AttentionEnabled);
    }

    [Fact]
    public void HidingAWindowFromTheTileDoesNotStopItFromBeingRecordedInHistory()
    {
        var settings = new AppSettings();
        settings.Providers["codex"].ShowFiveHour = false;
        var providers = new List<IUsageProvider> { new TwoWindowFakeProvider("codex") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyDir = TempDirectory();
        var historyStore = new HistoryStore(historyDir, () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore);

        vm.Tick(Now);

        var tile = vm.Tiles.Single(t => t.ProviderId == "codex");
        Assert.Single(tile.Rows); // only the weekly row shows...
        Assert.Equal(WindowKind.Weekly, tile.Rows[0].Kind);

        // A fresh read off disk proves the hidden five-hour window was recorded anyway.
        var points = new HistoryStore(historyDir, () => Now).Load("codex", TimeSpan.FromDays(1));
        Assert.Contains(points, p => p.Window == WindowKind.FiveHour);
        Assert.Contains(points, p => p.Window == WindowKind.Weekly);
    }

    [Fact]
    public void NoBalloonIsForwardedWhileQuietHoursAreActive()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        settings.DefaultThreshold = 10; // well under the fake providers' default 42%, so it crosses
        settings.QuietHoursEnabled = true;
        settings.QuietHoursStart = "00:00";
        settings.QuietHoursEnd = "23:59"; // covers the whole day regardless of when the test runs
        var (vm, _, _) = Build(settings);
        var raised = new List<ThresholdNotification>();
        vm.NotificationRaised += n => raised.Add(n);

        vm.Tick(Now);

        Assert.Empty(raised);
    }

    [Fact]
    public void NoBalloonIsForwardedForATileWhoseOwnNotificationsSwitchIsOff()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        settings.DefaultThreshold = 10; // well under the fake providers' default 42%, so it crosses
        settings.Providers["claude"].NotificationsEnabled = false;
        var (vm, _, _) = Build(settings);
        var raised = new List<ThresholdNotification>();
        vm.NotificationRaised += n => raised.Add(n);

        vm.Tick(Now);

        Assert.DoesNotContain(raised, n => n.ProviderId == "claude");
        Assert.Contains(raised, n => n.ProviderId == "codex"); // its own switch is still on
    }

    [Fact]
    public async Task ATileHoldsItsHistoryChartValuesRightAfterASnapshot()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);

        vm.Tick(Now);
        await vm.WaitForPendingHistoryReadsAsync();

        var tile = vm.Tiles.Single(t => t.ProviderId == "codex");
        Assert.Equal([42.0], tile.FiveHourValues.Select(p => p.Percent));
    }

    [Fact]
    public async Task FinishedHistoryReadsDoNotAccumulateInTheTrackingList()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);
        var pending = (List<Task>)ReadPrivateField(vm, "_pendingHistoryReads");

        for (var i = 0; i < 5; i++)
            vm.RefreshHistoryForAllTiles();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (pending)
            {
                if (pending.Count == 0)
                    break;
            }

            await Task.Delay(20);
        }

        lock (pending)
            Assert.Empty(pending);
    }

    [Fact]
    public async Task PreviousWeekValuesComeBackShiftedBySevenDaysAndEmptyWhenTheSettingIsOff()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, historyDir, _) = Build(settings);

        // A point safely inside the previous-week slice of the default "Day" chart range
        // (window [now-8d, now-7d]) - a fresh HistoryStore instance timestamps it via its own clock,
        // independently of whatever clock the view model's own history store carries.
        var previousWeekAt = DateTimeOffset.Now - TimeSpan.FromDays(7) - TimeSpan.FromHours(1);
        var seedStore = new HistoryStore(historyDir, () => previousWeekAt);
        seedStore.Append("codex", WindowKind.FiveHour, 33, resetsAt: null);

        vm.RefreshHistoryForAllTiles();
        await vm.WaitForPendingHistoryReadsAsync();
        var tile = vm.Tiles.Single(t => t.ProviderId == "codex");

        Assert.Single(tile.PreviousWeekValues);
        Assert.Equal(33, tile.PreviousWeekValues[0].Percent);
        Assert.Equal(previousWeekAt + TimeSpan.FromDays(7), tile.PreviousWeekValues[0].At);

        settings.ShowPreviousWeekLine = false;
        vm.RefreshHistoryForAllTiles();
        await vm.WaitForPendingHistoryReadsAsync();

        Assert.Empty(tile.PreviousWeekValues);
    }

    [Fact]
    public void NoDataAtAllDrawsTheFullRequestedRange()
    {
        var range = TimeSpan.FromDays(30);

        var decision = MainViewModel.DecideChartRange(Now, range, earliestPoint: null);

        Assert.Equal(Now - range, decision.RangeStart);
    }

    [Fact]
    public void DataCoveringTheFullRequestedRangeDrawsItUnchanged()
    {
        var range = TimeSpan.FromDays(30);
        var earliestPoint = Now - TimeSpan.FromDays(40); // older than the requested start

        var decision = MainViewModel.DecideChartRange(Now, range, earliestPoint);

        Assert.Equal(Now - range, decision.RangeStart);
    }

    [Fact]
    public void DataUnderATenthOfTheRequestedRangeStartsTheDrawnRangeAtTheFirstPoint()
    {
        var range = TimeSpan.FromDays(365);
        var earliestPoint = Now - TimeSpan.FromDays(4); // well under a tenth of 365 days

        var decision = MainViewModel.DecideChartRange(Now, range, earliestPoint);

        // The drawn range starts exactly at the first point, so the curve reaches the field's left edge.
        Assert.Equal(earliestPoint, decision.RangeStart);
    }

    [Fact]
    public void HidingAProviderExtendsTheEyeButtonsAccessibleNameWithTheCount()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);
        var before = vm.EyeButtonAccessibleName;

        vm.Tiles.Single(t => t.ProviderId == "codex").HideCommand.Execute(null);

        Assert.NotEqual(before, vm.EyeButtonAccessibleName);
        Assert.Contains("1", vm.EyeButtonAccessibleName);
    }

    [Fact]
    public void NewerVersionWarningTextIsOnlyPresentWhenTheFlagIsSet()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);

        Assert.Equal("", vm.NewerVersionWarningText);

        vm.ShowNewerVersionWarning = true;

        Assert.NotEqual("", vm.NewerVersionWarningText);
    }

    [Fact]
    public void AllHiddenSetsIsEmpty()
    {
        var settings = SettingsWithVisibility(("codex", false), ("claude", false), ("gemini", false), ("copilot", false));
        var (vm, _, _) = Build(settings);

        Assert.True(vm.IsEmpty);
        Assert.Equal(4, vm.HiddenCount);
    }

    [Fact]
    public void TileHideEventAndMenuToggleWriteTheSameValue()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);
        var codexTile = vm.Tiles.Single(t => t.ProviderId == "codex");

        codexTile.HideCommand.Execute(null);
        Assert.True(codexTile.IsHidden);
        Assert.False(settings.Providers["codex"].Visible);
        Assert.Equal(1, vm.HiddenCount);

        vm.ToggleHiddenCommand.Execute("codex");
        Assert.False(codexTile.IsHidden);
        Assert.True(settings.Providers["codex"].Visible);
        Assert.Equal(0, vm.HiddenCount);
    }

    [Fact]
    public void HiddenCountMatchesBadgeSource()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", false), ("gemini", true), ("copilot", false));
        var (vm, _, _) = Build(settings);

        Assert.Equal(vm.Tiles.Count(t => t.IsHidden), vm.HiddenCount);
    }

    [Fact]
    public void RaisingATilesRefreshRequestedFetchesOnlyThatProvider()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, providers) = Build(settings);
        var codexTile = vm.Tiles.Single(t => t.ProviderId == "codex");

        codexTile.RefreshCommand.Execute(null);

        Assert.True(providers.Single(p => p.Id == "codex").WasFetched);
        Assert.All(providers.Where(p => p.Id != "codex"), p => Assert.False(p.WasFetched));
    }

    private (MainViewModel Vm, FakeTimeProvider Clock) BuildWithClock()
    {
        var clock = new FakeTimeProvider(Now);
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, new HistoryStore(TempDirectory(), () => Now), timeProvider: clock);
        return (vm, clock);
    }

    [Fact]
    public void AUserStartedRefreshShowsTheSpinnerForAtLeastSixHundredMilliseconds()
    {
        var (vm, clock) = BuildWithClock();
        var tile = vm.Tiles.Single(t => t.ProviderId == "codex");

        vm.RefreshNow(userStarted: true);

        Assert.True(tile.ShowRefreshSpinner);
        clock.Advance(TimeSpan.FromMilliseconds(599));
        vm.Tick(Now);
        Assert.True(tile.ShowRefreshSpinner);
        Assert.True(tile.IsFetching);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        vm.Tick(Now);
        Assert.False(tile.ShowRefreshSpinner);
        Assert.False(tile.IsFetching);
    }

    [Fact]
    public void ARefreshFromTheTileMenuShowsTheSpinnerOnThatTileOnly()
    {
        var (vm, _) = BuildWithClock();

        vm.Tiles.Single(t => t.ProviderId == "codex").RefreshCommand.Execute(null);

        Assert.True(vm.Tiles.Single(t => t.ProviderId == "codex").ShowRefreshSpinner);
        Assert.All(vm.Tiles.Where(t => t.ProviderId != "codex"), t => Assert.False(t.ShowRefreshSpinner));
    }

    [Fact]
    public void AScheduledOrStartupFetchNeverShowsTheSpinner()
    {
        var (vm, clock) = BuildWithClock();
        var tile = vm.Tiles.Single(t => t.ProviderId == "codex");

        vm.Tick(Now);
        Assert.True(tile.IsFetching);
        Assert.False(tile.ShowRefreshSpinner);

        clock.Advance(TimeSpan.FromMinutes(10));
        vm.RefreshNow();
        Assert.All(vm.Tiles, t => Assert.False(t.ShowRefreshSpinner));
    }

    [Fact]
    public void IsFetchingStaysTrueForAtLeastFiveHundredMillisecondsAfterTheSnapshotArrives()
    {
        var clock = new FakeTimeProvider(Now);
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore, timeProvider: clock);
        var codexTile = vm.Tiles.Single(t => t.ProviderId == "codex");

        vm.Tick(Now); // starts, and - with these synchronous fakes - immediately completes every fetch

        Assert.True(codexTile.IsFetching); // the snapshot already landed, but under 500ms have passed

        clock.Advance(TimeSpan.FromMilliseconds(499));
        vm.Tick(Now);
        Assert.True(codexTile.IsFetching); // still short of the floor

        clock.Advance(TimeSpan.FromMilliseconds(1));
        vm.Tick(Now);
        Assert.False(codexTile.IsFetching);
    }

    [Fact]
    public void SwitchingLayoutTwiceKeepsEachLayoutsOwnWidth()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        settings.Window.Vertical.Width = 300;
        settings.Window.Horizontal.Width = 900;
        var (vm, _, _) = Build(settings);

        vm.Layout = "Horizontal";
        vm.Layout = "Vertical";

        Assert.Equal(300, settings.Window.Vertical.Width);
        Assert.Equal(900, settings.Window.Horizontal.Width);
    }

    [Fact]
    public void MovingTheMiddleTileUpSwapsExactlyTwoOrders()
    {
        var settings = new AppSettings();
        settings.Providers["claude"].Order = 0;
        settings.Providers["codex"].Order = 1;
        settings.Providers["gemini"].Order = 2;
        settings.Providers["copilot"].Order = 3;
        var (vm, _, _) = Build(settings);
        Assert.Equal(["claude", "codex", "gemini", "copilot"], vm.Tiles.Select(t => t.ProviderId));

        vm.MoveProviderUpCommand.Execute("gemini");

        Assert.Equal(0, settings.Providers["claude"].Order);
        Assert.Equal(2, settings.Providers["codex"].Order);
        Assert.Equal(1, settings.Providers["gemini"].Order);
        Assert.Equal(3, settings.Providers["copilot"].Order);
        Assert.Equal(["claude", "gemini", "codex", "copilot"], vm.Tiles.Select(t => t.ProviderId));
    }

    [Fact]
    public void MovingTheFirstTileUpDoesNothing()
    {
        var settings = new AppSettings();
        settings.Providers["claude"].Order = 0;
        settings.Providers["codex"].Order = 1;
        settings.Providers["gemini"].Order = 2;
        settings.Providers["copilot"].Order = 3;
        var (vm, _, _) = Build(settings);
        Assert.False(vm.Tiles.Single(t => t.ProviderId == "claude").CanMoveUp);

        vm.MoveProviderUpCommand.Execute("claude");

        Assert.Equal(0, settings.Providers["claude"].Order);
        Assert.Equal(1, settings.Providers["codex"].Order);
        Assert.Equal(["claude", "codex", "gemini", "copilot"], vm.Tiles.Select(t => t.ProviderId));
    }

    [Fact]
    public void TheOrderSurvivesAReloadThroughARealSettingsStore()
    {
        var dir = TempDirectory();
        var store = new SettingsStore(dir);
        // A fresh directory - SettingsStore.Load assigns claude=0, codex=1, gemini=2, copilot=3
        // from AppSettings.KnownProviderIds, exactly as a first-ever launch would.
        var settings = store.Load();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(store, settings, providers, historyStore);

        vm.MoveProviderUpCommand.Execute("gemini"); // swaps codex(1)/gemini(2)
        store.SaveNow(settings); // force the write instead of waiting out the debounce timer

        var reloaded = new SettingsStore(dir).Load();

        Assert.Equal(0, reloaded.Providers["claude"].Order);
        Assert.Equal(2, reloaded.Providers["codex"].Order);
        Assert.Equal(1, reloaded.Providers["gemini"].Order);
        Assert.Equal(3, reloaded.Providers["copilot"].Order);
    }

    [Fact]
    public void DayGridTileDefaultsToHiddenAndOutOfEveryProvidersWay()
    {
        var settings = new AppSettings();
        settings.Providers["claude"].Order = 0;
        settings.Providers["codex"].Order = 1;
        settings.Providers["gemini"].Order = 2;
        settings.Providers["copilot"].Order = 3;
        var (vm, _, _) = Build(settings);

        Assert.True(vm.DayGridTile.IsHidden);
        Assert.Equal(4, vm.DisplayRows.IndexOf(vm.DayGridTile));
        Assert.False(settings.Providers.ContainsKey(AppSettings.DayGridTileId));
    }

    [Fact]
    public void ShowingTheDayGridTileForTheFirstTimePutsItOnTop()
    {
        var settings = new AppSettings();
        settings.Providers["claude"].Order = 0;
        settings.Providers["codex"].Order = 1;
        settings.Providers["gemini"].Order = 2;
        settings.Providers["copilot"].Order = 3;
        var (vm, _, _) = Build(settings);

        vm.ToggleHiddenCommand.Execute(AppSettings.DayGridTileId);

        Assert.False(vm.DayGridTile.IsHidden);
        Assert.Equal(0, vm.DisplayRows.IndexOf(vm.DayGridTile));
        Assert.Equal(0, settings.Providers[AppSettings.DayGridTileId].Order);
        Assert.Equal(1, settings.Providers["claude"].Order);
        Assert.Equal(4, settings.Providers["copilot"].Order);
    }

    [Fact]
    public void ShowingTheDayGridTileForTheFirstTimeAfterAReorderStillPutsItOnTop()
    {
        var settings = new AppSettings();
        settings.Providers["claude"].Order = 0;
        settings.Providers["codex"].Order = 1;
        settings.Providers["gemini"].Order = 2;
        settings.Providers["copilot"].Order = 3;
        var (vm, _, _) = Build(settings);
        // What any earlier reorder leaves behind: an entry for every row, the hidden day grid included.
        settings.Providers[AppSettings.DayGridTileId] = new ProviderSettings { Visible = false, Order = 4 };

        vm.ToggleHiddenCommand.Execute(AppSettings.DayGridTileId);

        Assert.Equal(0, vm.DisplayRows.IndexOf(vm.DayGridTile));
        Assert.True(settings.DayGridShownOnce);
    }

    [Fact]
    public void AShownDayGridTileKeepsTheEverythingHiddenPlaceholderAway()
    {
        var settings = SettingsWithVisibility(("codex", false), ("claude", false), ("gemini", false), ("copilot", false));
        var (vm, _, _) = Build(settings);
        Assert.True(vm.IsEmpty);

        vm.ToggleHiddenCommand.Execute(AppSettings.DayGridTileId);

        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public void MovingTheDayGridTileDownAndBackUpSwapsWithItsNeighbor()
    {
        var settings = new AppSettings();
        settings.Providers["claude"].Order = 0;
        settings.Providers["codex"].Order = 1;
        settings.Providers["gemini"].Order = 2;
        settings.Providers["copilot"].Order = 3;
        var (vm, _, _) = Build(settings);
        vm.ToggleHiddenCommand.Execute(AppSettings.DayGridTileId);

        vm.MoveProviderDownCommand.Execute(AppSettings.DayGridTileId);

        Assert.Equal(1, vm.DisplayRows.IndexOf(vm.DayGridTile));
        Assert.Equal(0, settings.Providers["claude"].Order);
        Assert.Equal(1, settings.Providers[AppSettings.DayGridTileId].Order);
        Assert.Equal(2, settings.Providers["codex"].Order);

        vm.MoveProviderUpCommand.Execute(AppSettings.DayGridTileId);

        Assert.Equal(0, vm.DisplayRows.IndexOf(vm.DayGridTile));
        Assert.Equal(0, settings.Providers[AppSettings.DayGridTileId].Order);
        Assert.Equal(1, settings.Providers["claude"].Order);
    }

    [Fact]
    public void TheDayGridTilesOrderAndVisibilitySurviveAReloadThroughARealSettingsStore()
    {
        var dir = TempDirectory();
        var store = new SettingsStore(dir);
        var settings = store.Load();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(store, settings, providers, historyStore);

        vm.ToggleHiddenCommand.Execute(AppSettings.DayGridTileId); // first reveal lands on top
        vm.MoveProviderDownCommand.Execute(AppSettings.DayGridTileId); // swaps with claude
        store.SaveNow(settings);

        var reloaded = new SettingsStore(dir).Load();

        Assert.True(reloaded.Providers[AppSettings.DayGridTileId].Visible);
        Assert.Equal(1, reloaded.Providers[AppSettings.DayGridTileId].Order);
        Assert.Equal(0, reloaded.Providers["claude"].Order);
    }

    [Fact]
    public void SetDensityUpdatesSelectionAndPersists()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);

        vm.SetDensityCommand.Execute("Mini");

        Assert.Equal("Mini", vm.DensityMode);
        Assert.Equal("Mini", settings.TileDensity);
        Assert.True(vm.DensityChoices.Single(c => c.Value == "Mini").IsSelected);
        Assert.False(vm.DensityChoices.Single(c => c.Value == "Full").IsSelected);
    }

    // The eye menu's density ComboBox binds SelectedDensityChoice, never DensityChoices' own
    // IsSelected flags directly (see TitleBar.xaml) - this proves that property drives the exact
    // same setting SetDensityCommand does, so the two pickers can never disagree.
    [Fact]
    public void SelectedDensityChoiceAppliesTheSameChangeAsTheDensityCommand()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);

        vm.SelectedDensityChoice = vm.DensityChoices.Single(c => c.Value == "Mini");

        Assert.Equal("Mini", vm.DensityMode);
        Assert.Equal("Mini", settings.TileDensity);
        Assert.Same(vm.DensityChoices.Single(c => c.Value == "Mini"), vm.SelectedDensityChoice);
    }

    // The window recomputes the density from the stored setting the moment DensityMode changes, so
    // the setting must already hold the new value by then - otherwise the first pick is applied one
    // change late.
    [Fact]
    public void TheStoredDensityIsCurrentWhenDensityModeAnnouncesTheChange()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);
        string? storedAtNotification = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.DensityMode))
                storedAtNotification = settings.TileDensity;
        };

        vm.SetDensityCommand.Execute("Mini");

        Assert.Equal("Mini", storedAtNotification);
    }

    // A fixed density sizes the window to its tiles, so picking one hands the height back to
    // automatic; "Auto" keeps a hand-sized height and fits the tiles into it instead.
    [Theory]
    [InlineData("Mini", true)]
    [InlineData("Auto", false)]
    public void PickingAFixedDensityHandsTheHeightBackToAutomatic(string mode, bool expectedAutomatic)
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);
        vm.HeightAutomatic = false;

        vm.SetDensityCommand.Execute(mode);

        Assert.Equal(expectedAutomatic, vm.HeightAutomatic);
    }

    [Fact]
    public void TheTrayChoicesOfferAutomaticFirstThenEveryTile()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);

        Assert.Equal("Auto", vm.TrayProviderChoices[0].Value);
        Assert.Equal("AppIcon", vm.TrayProviderChoices[1].Value);
        Assert.Equal(vm.Tiles.Select(t => t.ProviderId), vm.TrayProviderChoices.Skip(2).Select(c => c.Value));
        Assert.Equal(vm.Tiles.Select(t => t.HeaderDisplayName), vm.TrayProviderChoices.Skip(2).Select(c => c.Label));
        Assert.Same(vm.TrayProviderChoices[0], vm.SelectedTrayProviderChoice);
    }

    [Fact]
    public void TheTrayWindowChoicesOfferOnlyAutomaticUntilAWindowIsReported()
    {
        var settings = new AppSettings();
        var (vm, _, _) = Build(settings, new Dictionary<string, double> { ["codex"] = 30, ["claude"] = 91, ["gemini"] = 42, ["copilot"] = 55 });

        Assert.Equal(["Auto"], vm.TrayWindowChoices.Select(c => c.Value));

        vm.Tick(Now);

        Assert.Equal(["Auto", "FiveHour"], vm.TrayWindowChoices.Select(c => c.Value));
        Assert.Same(vm.TrayWindowChoices[0], vm.SelectedTrayWindowChoice);
    }

    [Fact]
    public void PickingATrayWindowStoresItAndAnnouncesTheChange()
    {
        var settings = new AppSettings();
        var (vm, _, _) = Build(settings, new Dictionary<string, double> { ["codex"] = 30, ["claude"] = 91, ["gemini"] = 42, ["copilot"] = 55 });
        vm.Tick(Now);
        var announced = false;
        vm.PropertyChanged += (_, e) => announced |= e.PropertyName == nameof(MainViewModel.TrayWindow);

        vm.SelectedTrayWindowChoice = vm.TrayWindowChoices.Single(c => c.Value == "FiveHour");

        Assert.Equal("FiveHour", settings.TrayWindow);
        Assert.Equal("FiveHour", vm.TrayWindow);
        Assert.True(announced);
    }

    [Fact]
    public void TheTrayWindowChoicesOfferOneEntryPerDistinctOtherRowLabel()
    {
        var settings = new AppSettings();
        var providers = new List<IUsageProvider> { new TwoOtherWindowFakeProvider("cursor") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore);

        vm.Tick(Now);

        Assert.Equal(
            ["Auto", "Label:Window_CursorModels", "Label:Window_OtherModels"],
            vm.TrayWindowChoices.Select(c => c.Value));
    }

    [Fact]
    public void UntickingAnOtherRowsToggleInTheSettingsWindowPersistsTheHiddenLabel()
    {
        var settings = new AppSettings();
        var providers = new List<IUsageProvider> { new TwoOtherWindowFakeProvider("cursor") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore);
        vm.Tick(Now);
        var tile = vm.Tiles.Single(t => t.ProviderId == "cursor");

        tile.WindowToggles.Single(t => t.Label == "Window_CursorModels").IsVisible = false;

        Assert.Contains("Window_CursorModels", settings.Providers["cursor"].HiddenWindows);
    }

    private sealed class TwoOtherWindowFakeProvider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public bool RunsOnUiThread => true;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(new ProviderSnapshot(
            Id,
            [
                new UsageWindow("Window_CursorModels", WindowKind.Other, 20, Now.AddDays(3), 300),
                new UsageWindow("Window_OtherModels", WindowKind.Other, 30, Now.AddDays(3), 300),
            ],
            "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null));
    }

    [Fact]
    public void PickingATrayProviderStoresItAndAnnouncesTheChange()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);
        var announced = false;
        vm.PropertyChanged += (_, e) => announced |= e.PropertyName == nameof(MainViewModel.TrayProvider);
        var target = vm.TrayProviderChoices[2];

        vm.SelectedTrayProviderChoice = target;

        Assert.Equal(target.Value, settings.TrayProvider);
        Assert.Equal(target.Value, vm.TrayProvider);
        Assert.Same(target, vm.SelectedTrayProviderChoice);
        Assert.True(announced);
    }

    [Fact]
    public void TheWindowListIsOfferedOnlyForAChosenProvider()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);
        Assert.Equal(MainViewModel.TrayProviderAuto, vm.TrayProvider);
        Assert.False(vm.TrayWindowEnabled);
        Assert.Null(vm.EffectiveTrayWindow);

        vm.SelectedTrayProviderChoice = vm.TrayProviderChoices.First(c => c.Value == MainViewModel.TrayProviderAppIcon);
        Assert.False(vm.TrayWindowEnabled);
        Assert.Equal(MainViewModel.TrayProviderAppIcon, vm.TrayProvider);

        vm.SelectedTrayProviderChoice = vm.TrayProviderChoices.First(c =>
            c.Value != MainViewModel.TrayProviderAuto && c.Value != MainViewModel.TrayProviderAppIcon);
        Assert.True(vm.TrayWindowEnabled);
        Assert.Equal(settings.TrayWindow, vm.EffectiveTrayWindow);

        vm.SelectedTrayProviderChoice = vm.TrayProviderChoices[0];
        Assert.False(vm.TrayWindowEnabled);
        Assert.Null(vm.EffectiveTrayWindow);
    }

    [Fact]
    public void SwitchingTheTrayChoiceRedrawsEveryTime()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);
        var memo = new TrayTooltipMemo();

        Assert.True(MainWindow.TryComputeTraySummary(vm.Tiles, memo, out _, out _, out _, "Auto"));
        Assert.True(MainWindow.TryComputeTraySummary(vm.Tiles, memo, out _, out _, out _, "AppIcon"));
        Assert.True(MainWindow.TryComputeTraySummary(vm.Tiles, memo, out _, out _, out _, "Auto"));
        Assert.False(MainWindow.TryComputeTraySummary(vm.Tiles, memo, out _, out _, out _, "Auto"));
    }

    // MainWindow.ResetHeightToAutomaticOnDoubleClick is the pure action a double click on the top or
    // bottom resize grip performs - extracted so the effect (HeightAutomatic becoming true) is
    // provable without constructing the live window, the same reason TitleBarTests proves
    // TitleBar.ShouldOpenEyePopup this way instead.
    [Fact]
    public void DoubleClickingAHeightGripSwitchesHeightBackToAutomatic()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);
        vm.HeightAutomatic = false;

        MainWindow.ResetHeightToAutomaticOnDoubleClick(vm, clickCount: 2);

        Assert.True(vm.HeightAutomatic);
    }

    [Fact]
    public void ASingleClickOnAHeightGripLeavesTheHeightModeAlone()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);
        vm.HeightAutomatic = false;

        MainWindow.ResetHeightToAutomaticOnDoubleClick(vm, clickCount: 1);

        Assert.False(vm.HeightAutomatic);
    }

    [Fact]
    public void DoubleClickingAWidthGripSwitchesWidthBackToAutomatic()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);
        vm.WidthAutomatic = false;

        MainWindow.ResetWidthToAutomaticOnDoubleClick(vm, clickCount: 2);

        Assert.True(vm.WidthAutomatic);
    }

    [Fact]
    public void ASingleClickOnAWidthGripLeavesTheWidthModeAlone()
    {
        var settings = SettingsWithVisibility();
        var (vm, _, _) = Build(settings);
        vm.WidthAutomatic = false;

        MainWindow.ResetWidthToAutomaticOnDoubleClick(vm, clickCount: 1);

        Assert.False(vm.WidthAutomatic);
    }

    [Fact]
    public async Task SignOutAsync_disposes_the_runner_before_deleting_the_profile()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var events = new List<string>();
        var runner = new RecordingAsyncDisposable(events);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore, runner);
        vm.SignOutWebView = _ => { events.Add("delete"); return SignOutResult.Removed; };

        var result = await vm.SignOutAsync("claude");

        Assert.True(result);
        Assert.Equal(["dispose", "delete"], events);
    }

    [Fact]
    public async Task SignOutAsync_forces_the_named_tile_to_not_signed_in_and_clears_the_cached_path()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        settings.WebUsagePaths["claude"] = "/api/organizations/2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc/usage";
        var (vm, _, _) = Build(settings);
        vm.SignOutWebView = _ => SignOutResult.Removed;

        var result = await vm.SignOutAsync("claude");

        Assert.True(result);
        Assert.False(settings.WebUsagePaths.ContainsKey("claude"));
        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");
        Assert.Equal(ProviderStatus.NotSignedIn, tile.Status);
    }

    // Sign-out deletes the app's own web profile, but a local-file or local-login provider would keep
    // being read regardless - this proves the persisted flag stops the read outright, on the freshly
    // rebuilt provider SignOutAsync swaps in, and that a further tick still never touches it.
    [Fact]
    public async Task SignOutAsync_sets_the_persisted_disconnected_flag_and_a_further_tick_never_fetches_it()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings, claudeRunner: new SignOutAwareRunner());
        vm.SignOutWebView = _ => SignOutResult.Removed;
        FakeProvider? freshClaude = null;
        vm.CreateWebAccount = key => { freshClaude = new FakeProvider(key); return (freshClaude, new SignOutAwareRunner()); };

        await vm.SignOutAsync("claude");

        Assert.True(settings.Providers["claude"].Disconnected);

        vm.RefreshNow();
        await Task.Delay(20);

        Assert.NotNull(freshClaude);
        Assert.False(freshClaude!.WasFetched);
    }

    // Copilot (and the primary Claude account) read a sign-in another tool holds and have no web
    // runner: signing out used to swap their provider for a freshly built web-only Claude account.
    [Fact]
    public async Task SignOutAsync_keeps_the_provider_of_an_account_without_a_web_session()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, providers) = Build(settings);
        vm.SignOutWebView = _ => SignOutResult.Removed;
        var rebuilt = 0;
        vm.CreateWebAccount = key => { rebuilt++; return (new FakeProvider(key), new SignOutAwareRunner()); };

        var result = await vm.SignOutAsync("copilot");
        vm.Reconnect("copilot");
        await Task.Delay(20);

        Assert.True(result);
        Assert.Equal(0, rebuilt);
        Assert.True(providers.Single(p => p.Id == "copilot").WasFetched);
    }

    // Settings.SignIn: the opposite of the flag above - clears it and resumes reading right away rather
    // than waiting for the next scheduled tick.
    [Fact]
    public async Task Reconnect_clears_the_disconnected_flag_and_refreshes_immediately()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings, claudeRunner: new SignOutAwareRunner());
        vm.SignOutWebView = _ => SignOutResult.Removed;
        FakeProvider? freshClaude = null;
        vm.CreateWebAccount = key => { freshClaude = new FakeProvider(key); return (freshClaude, new SignOutAwareRunner()); };
        await vm.SignOutAsync("claude");

        vm.Reconnect("claude");
        await Task.Delay(20);

        Assert.False(settings.Providers["claude"].Disconnected);
        Assert.NotNull(freshClaude);
        Assert.True(freshClaude!.WasFetched);
    }

    // A sign-in that is still running is not a sign-in: a signed-out account stays unread (no numbers,
    // no chart point, still offering the button) until the window reports that the sign-in finished,
    // and only that resumes its reads. Resuming at the start of the flow let a local source answer
    // while the user was still typing the password.
    [Fact]
    public async Task A_signed_out_account_stays_unread_until_the_sign_in_finishes()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings, claudeRunner: new SignOutAwareRunner());
        vm.SignOutWebView = _ => SignOutResult.Removed;
        FakeProvider? freshClaude = null;
        vm.CreateWebAccount = key => { freshClaude = new FakeProvider(key); return (freshClaude, new SignOutAwareRunner()); };
        await vm.SignOutAsync("claude");
        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");

        // Signed out: nothing shown, button offered.
        Assert.Equal(ProviderStatus.NotSignedIn, tile.Status);
        Assert.Empty(tile.Rows);

        // The sign-in window is open and unfinished (closed part-way counts the same): ticks and
        // manual refreshes in between read nothing and show nothing.
        vm.RefreshNow();
        await Task.Delay(30);
        vm.CompleteSignIn("claude", signedIn: false);
        await Task.Delay(30);
        Assert.False(freshClaude!.WasFetched);
        Assert.True(settings.Providers["claude"].Disconnected);
        Assert.Equal(ProviderStatus.NotSignedIn, tile.Status);
        Assert.Empty(tile.Rows);
        Assert.Equal(SignInState.SignedOut, tile.SignInState);

        // The sign-in finished: the first read after it fills the tile.
        vm.CompleteSignIn("claude", signedIn: true);
        await Task.Delay(50);
        Assert.False(settings.Providers["claude"].Disconnected);
        Assert.True(freshClaude.WasFetched);
        Assert.Equal(ProviderStatus.Ok, tile.Status);
        Assert.NotEmpty(tile.Rows);
        Assert.Equal(SignInState.SignedIn, tile.SignInState);
    }

    // A tick that fires while the sign-out is still tearing the session down must not start a fetch
    // whose numbers land on the tile (and in the history) after the account is signed out.
    [Fact]
    public async Task A_fetch_started_while_sign_out_tears_down_does_not_fill_the_signed_out_tile()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var gate = new TaskCompletionSource();
        var claude = new GatedProvider("claude", gate.Task);
        var providers = new List<IUsageProvider> { new FakeProvider("codex"), claude, new FakeProvider("gemini"), new FakeProvider("copilot") };
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, historyStore, new SignOutAwareRunner());
        vm.CreateWebAccount = key => (new AlwaysNotSignedInProvider(key), new SignOutAwareRunner());
        vm.SignOutWebView = _ =>
        {
            vm.RefreshNow();
            return SignOutResult.Removed;
        };

        await vm.SignOutAsync("claude");
        gate.SetResult();
        await Task.Delay(50);

        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");
        Assert.Equal(ProviderStatus.NotSignedIn, tile.Status);
        Assert.Empty(historyStore.Load("claude", TimeSpan.FromDays(1)));
    }

    // The cancelled fetch raises no snapshot, so nothing else would ever turn the tile's spinner off.
    [Fact]
    public async Task A_fetch_cancelled_by_sign_out_does_not_leave_the_tile_spinner_on()
    {
        var clock = new FakeTimeProvider(Now);
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var providers = new List<IUsageProvider>
        {
            new FakeProvider("codex"), new CancelAwareProvider("claude"), new FakeProvider("gemini"), new FakeProvider("copilot"),
        };
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, historyStore,
            new SignOutAwareRunner(), timeProvider: clock);
        vm.CreateWebAccount = key => (new AlwaysNotSignedInProvider(key), new SignOutAwareRunner());
        vm.SignOutWebView = _ => SignOutResult.Removed;
        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");

        vm.Tick(Now);
        Assert.True(tile.IsFetching);
        clock.Advance(TimeSpan.FromSeconds(1)); // past the minimum spinner time, so the clear is not deferred

        await vm.SignOutAsync("claude");

        Assert.False(tile.IsFetching);

        // The other tiles' own minimum spinner time has passed too: nothing is left spinning.
        vm.Tick(Now);
        Assert.False(vm.IsAnyFetching);
    }

    // A fetch already running when the sign-out starts, from a provider that never looks at its
    // token, finishes while the sign-out is still tearing down: its numbers must not reach the tile
    // or the history.
    [Fact]
    public async Task A_fetch_running_before_sign_out_that_finishes_during_it_fills_nothing()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var gate = new TaskCompletionSource();
        var providers = new List<IUsageProvider>
        {
            new FakeProvider("codex"), new GatedProvider("claude", gate.Task), new FakeProvider("gemini"), new FakeProvider("copilot"),
        };
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, historyStore, new SignOutAwareRunner());
        vm.CreateWebAccount = key => (new AlwaysNotSignedInProvider(key), new SignOutAwareRunner());
        vm.SignOutWebView = _ => SignOutResult.Removed;

        vm.Tick(Now); // the claude fetch is now running and parked on the gate
        var signingOut = vm.SignOutAsync("claude"); // cancels it and waits for it to end
        gate.SetResult(); // the fetch ends anyway, without having looked at its token
        await signingOut;
        await Task.Delay(30);

        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");
        Assert.Equal(ProviderStatus.NotSignedIn, tile.Status);
        Assert.Empty(tile.Rows);
        Assert.Empty(historyStore.Load("claude", TimeSpan.FromDays(1)));
    }

    // The reading of a fetch whose result is only handled after the sign-out finished (the handling
    // is queued on the UI thread) is dropped as well.
    [Fact]
    public async Task A_reading_handled_after_sign_out_is_dropped()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var runner = new SignOutAwareRunner();
        var providers = new List<IUsageProvider>
        {
            new FakeProvider("codex"), new SignOutAwareProvider("claude", runner), new FakeProvider("gemini"), new FakeProvider("copilot"),
        };
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, historyStore, runner);
        vm.CreateWebAccount = key => (new AlwaysNotSignedInProvider(key), new SignOutAwareRunner());
        vm.SignOutWebView = _ => SignOutResult.Removed;
        await vm.SignOutAsync("claude");

        vm.OnSnapshotReady(new ProviderSnapshot("claude", [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300)],
            "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null));

        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");
        Assert.Equal(ProviderStatus.NotSignedIn, tile.Status);
        Assert.Empty(tile.Rows);
        Assert.Empty(historyStore.Load("claude", TimeSpan.FromDays(1)));
    }

    // A tick while the sign-out is still deleting the profile must not read the old provider again.
    [Fact]
    public async Task A_tick_during_sign_out_teardown_does_not_fetch_the_account()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var claude = new FakeProvider("claude");
        var providers = new List<IUsageProvider> { new FakeProvider("codex"), claude, new FakeProvider("gemini"), new FakeProvider("copilot") };
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, historyStore, new SignOutAwareRunner());
        vm.CreateWebAccount = key => (new AlwaysNotSignedInProvider(key), new SignOutAwareRunner());
        var fetchedDuringTeardown = true;
        vm.SignOutWebView = _ =>
        {
            vm.RefreshNow();
            fetchedDuringTeardown = claude.WasFetched;
            return SignOutResult.Removed;
        };

        await vm.SignOutAsync("claude");

        Assert.False(fetchedDuringTeardown);
    }

    [Fact]
    public async Task After_sign_out_a_further_scheduler_tick_reports_not_signed_in_and_the_runner_is_disposed_only_once()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var runner = new SignOutAwareRunner();
        var providers = new List<IUsageProvider>
        {
            new FakeProvider("codex"), new SignOutAwareProvider("claude", runner), new FakeProvider("gemini"), new FakeProvider("copilot"),
        };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore, runner);
        vm.SignOutWebView = _ => SignOutResult.Removed;
        vm.CreateWebAccount = key => (new AlwaysNotSignedInProvider(key), new SignOutAwareRunner());

        var result = await vm.SignOutAsync("claude");
        vm.RefreshNow();
        await Task.Delay(20);

        Assert.True(result);
        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");
        Assert.Equal(ProviderStatus.NotSignedIn, tile.Status);
        Assert.Equal(1, runner.DisposeCount);
    }

    /// <summary>The bug this guards against: the old runner is permanently disabled once disposed
    /// (see <see cref="WebSessionScriptRunner"/>), so if sign-out left the disposed runner's provider
    /// sitting in the scheduler, a later successful sign-in on the same tile would stay stuck showing
    /// "not signed in" forever, because the provider's fetch still went through the dead runner.
    /// Sign-out must swap in a fresh provider (same as <see cref="AddAccount"/> builds for a brand
    /// new one), so the very next tick after a fresh sign-in reports real data again.</summary>
    [Fact]
    public async Task SignOutAsync_rebuilds_a_fresh_provider_so_a_later_sign_in_is_not_stuck_behind_the_disposed_runner()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var oldRunner = new SignOutAwareRunner();
        var providers = new List<IUsageProvider>
        {
            new FakeProvider("codex"), new SignOutAwareProvider("claude", oldRunner), new FakeProvider("gemini"), new FakeProvider("copilot"),
        };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore, oldRunner);
        vm.SignOutWebView = _ => SignOutResult.Removed;
        vm.CreateWebAccount = key => (new FakeProvider(key), new SignOutAwareRunner());

        await vm.SignOutAsync("claude");
        // Signing back in (not a bare RefreshNow - SignOutAsync's own Disconnected flag would
        // otherwise correctly keep skipping the fetch, see the flag's own tests) is what proves the
        // swapped-in provider, not the old disposed runner's, actually answers.
        vm.Reconnect("claude");
        await Task.Delay(20);

        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");
        Assert.Equal(ProviderStatus.Ok, tile.Status);
    }

    [Fact]
    public async Task A_failed_sign_out_is_reported_back_instead_of_claiming_success()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);
        vm.SignOutWebView = _ => SignOutResult.Refused;

        var result = await vm.SignOutAsync("claude");

        Assert.False(result);
    }

    [Fact]
    public void ShowSignOutIncomplete_overrides_the_named_tile_with_a_blocked_warning()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);

        vm.ShowSignOutIncomplete("claude");

        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");
        Assert.Equal(ProviderStatus.Blocked, tile.Status);
        Assert.NotEqual("", tile.ReasonText);
    }

    [Fact]
    public void Repeated_not_signed_in_snapshots_are_logged_once_until_the_status_changes()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var providers = new List<IUsageProvider> { new FakeProvider("codex"), new FakeProvider("claude"), new FakeProvider("gemini"), new FakeProvider("copilot") };
        var logService = new LogService(TempDirectory(), now: () => Now);
        var vm = new MainViewModel(
            new SettingsStore(TempDirectory()), settings, providers, new HistoryStore(TempDirectory(), () => Now), claudeRunner: null, logService: logService);

        ProviderSnapshot WithStatus(ProviderStatus status) =>
            new("claude", [], null, SourceKind.LocalFile, Now, Now, status, null);
        int Lines(string word) => File.Exists(logService.CurrentFile)
            ? File.ReadAllLines(logService.CurrentFile).Count(line => line.Contains("'claude'") && line.Contains(word))
            : 0;

        vm.OnSnapshotReady(WithStatus(ProviderStatus.NotSignedIn));
        vm.OnSnapshotReady(WithStatus(ProviderStatus.NotSignedIn));
        vm.OnSnapshotReady(WithStatus(ProviderStatus.NotSignedIn));
        Assert.Equal(1, Lines("NotSignedIn"));

        vm.OnSnapshotReady(WithStatus(ProviderStatus.Ok));
        vm.OnSnapshotReady(WithStatus(ProviderStatus.NotSignedIn));
        Assert.Equal(2, Lines("NotSignedIn"));

        vm.OnSnapshotReady(WithStatus(ProviderStatus.Failed));
        vm.OnSnapshotReady(WithStatus(ProviderStatus.Failed));
        Assert.Equal(2, Lines("Failed"));
    }

    [Fact]
    public void A_failed_snapshot_writes_one_INFO_line_naming_the_provider_and_no_file_path()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var providers = new List<IUsageProvider> { new FailingFakeProvider("codex"), new FakeProvider("claude"), new FakeProvider("gemini"), new FakeProvider("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var logDir = TempDirectory();
        var logService = new LogService(logDir, now: () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore, claudeRunner: null, logService: logService);

        vm.Tick(Now);

        var logText = File.ReadAllText(logService.CurrentFile);
        var infoLines = logText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("[INFO]") && line.Contains("codex"))
            .ToList();
        Assert.Single(infoLines);
        Assert.Contains("codex", infoLines[0]);
        Assert.Contains("Failed", infoLines[0]);
        Assert.DoesNotContain(@"\", infoLines[0]);
        Assert.DoesNotContain(logDir, infoLines[0]);
    }

    [Fact]
    public void The_constructor_performs_no_Compact_or_Prune_call()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new CountingHistoryStore(TempDirectory(), () => Now);

        _ = new MainViewModel(settingsStore, settings, providers, historyStore);

        Assert.Equal(0, historyStore.CompactCalls);
        Assert.Equal(0, historyStore.PruneCalls);
    }

    [Fact]
    public async Task RunMaintenanceAsync_compacts_and_prunes_every_provider_once_before_its_first_wait()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new CountingHistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore);

        using var cts = new CancellationTokenSource();
        var run = vm.RunMaintenanceAsync(cts.Token, TimeSpan.FromHours(1)); // long interval - only the first, immediate pass matters here
        // Polls rather than sleeping a fixed guess: a fixed delay was long enough while this suite ran
        // one class at a time, but not once every class runs beside every other one - a generous
        // timeout here only ever costs real time on a genuinely stuck run, never a flake on a busy one.
        await WaitUntilAsync(() => historyStore.CompactCalls >= providers.Count && historyStore.PruneCalls >= providers.Count);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal(providers.Count, historyStore.CompactCalls);
        Assert.Equal(providers.Count, historyStore.PruneCalls);
    }

    [Fact]
    public async Task RunMaintenanceAsync_repeats_after_the_interval_and_never_overlaps_itself()
    {
        var settings = SettingsWithVisibility(("codex", true));
        var providers = new List<FakeProvider> { new("codex") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new CountingHistoryStore(TempDirectory(), () => Now) { CompactDelay = TimeSpan.FromMilliseconds(10) };
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore);

        using var cts = new CancellationTokenSource();
        var run = vm.RunMaintenanceAsync(cts.Token, TimeSpan.FromMilliseconds(15));
        // Same reasoning as the poll above: waits for two real passes instead of guessing how long
        // several 15ms-interval passes take on whatever else this thread pool is doing right now.
        await WaitUntilAsync(() => historyStore.CompactCalls >= 2);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.True(historyStore.CompactCalls >= 2, $"expected at least 2 passes, got {historyStore.CompactCalls}");
        Assert.Equal(1, historyStore.MaxObservedConcurrentCompacts);
    }

    // A busy machine running every test class at once can genuinely take longer than a fixed sleep
    // ever assumed - this polls for the real condition instead, so a slow-but-working run just takes
    // longer rather than reading as a failure. Ten seconds is generous even under heavy load; a run
    // that still has not met the condition by then really is stuck, not just slow.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("condition was never met");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task DisposeAsync_cancels_the_token_a_providers_fetch_receives()
    {
        var settings = SettingsWithVisibility(("codex", true));
        CancellationToken? capturedToken = null;
        var gate = new TaskCompletionSource();
        var provider = new TokenCapturingProvider("codex", async ct =>
        {
            capturedToken = ct;
            await gate.Task;
            return new ProviderSnapshot("codex", [], null, SourceKind.None, Now, null, ProviderStatus.Ok, null);
        });
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, new List<IUsageProvider> { provider }, historyStore);

        vm.RefreshNow(); // starts the fetch, capturing the token it was handed
        Assert.NotNull(capturedToken);
        Assert.False(capturedToken!.Value.IsCancellationRequested);

        await vm.DisposeAsync();

        Assert.True(capturedToken!.Value.IsCancellationRequested);
        gate.SetResult(); // release the still-pending fake fetch so it does not outlive the test
    }

    [Fact]
    public async Task Reconnect_a_timer_tick_and_a_manual_refresh_after_DisposeAsync_do_not_throw()
    {
        var settings = SettingsWithVisibility(("codex", true));
        var providers = new List<FakeProvider> { new("codex") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore);

        await vm.DisposeAsync();

        var thrown = Record.Exception(() =>
        {
            vm.Reconnect("codex");
            vm.Tick(Now);
            vm.RefreshNow();
        });
        Assert.Null(thrown);
    }

    [Fact]
    public async Task DisposeAsync_disposes_the_claude_runner_exactly_once_even_if_called_twice()
    {
        var settings = SettingsWithVisibility(("codex", true));
        var providers = new List<FakeProvider> { new("codex") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var events = new List<string>();
        var runner = new RecordingAsyncDisposable(events);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore, runner);

        await vm.DisposeAsync();
        await vm.DisposeAsync(); // must stay a no-op, not a second disposal

        Assert.Equal(["dispose"], events);
    }

    [Fact]
    public async Task DisposeAsync_stops_RunMaintenanceForeverAsync()
    {
        var settings = SettingsWithVisibility(("codex", true));
        var providers = new List<FakeProvider> { new("codex") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore);

        var run = vm.RunMaintenanceForeverAsync(); // default 24h interval - only DisposeAsync can end this in time
        await Task.Delay(20); // let the immediate first pass happen

        await vm.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private sealed class TokenCapturingProvider(string id, Func<CancellationToken, Task<ProviderSnapshot>> fetch) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        // This test file exercises MainViewModel's own tile/history/notification logic, never
        // RefreshScheduler's thread-pool scheduling (that is RefreshSchedulerTests' job) - opting out
        // keeps every fetch here starting on the calling thread, exactly as these synchronous
        // assertions (right after Tick/RefreshNow, with no await in between) already assume.
        public bool RunsOnUiThread => true;
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => fetch(ct);
    }

    private sealed class CountingHistoryStore(string dataDirectory, Func<DateTimeOffset> now) : HistoryStore(dataDirectory, now)
    {
        private int _inFlight;
        public int CompactCalls;
        public int PruneCalls;
        public int MaxObservedConcurrentCompacts;
        public TimeSpan CompactDelay;

        public override void Compact(string providerId)
        {
            var concurrent = Interlocked.Increment(ref _inFlight);
            MaxObservedConcurrentCompacts = Math.Max(MaxObservedConcurrentCompacts, concurrent);
            Interlocked.Increment(ref CompactCalls);
            if (CompactDelay > TimeSpan.Zero)
                Thread.Sleep(CompactDelay);
            base.Compact(providerId);
            Interlocked.Decrement(ref _inFlight);
        }

        public override void Prune(string providerId, int retentionDays)
        {
            Interlocked.Increment(ref PruneCalls);
            base.Prune(providerId, retentionDays);
        }
    }

    private sealed class FailingFakeProvider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        // See TokenCapturingProvider's own remark above - same reason on every fake in this file.
        public bool RunsOnUiThread => true;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(
            new ProviderSnapshot(Id, [], null, SourceKind.None, Now, null, ProviderStatus.Failed, new ProviderError("Status_Failed_Reason")));
    }

    private sealed class RecordingAsyncDisposable(List<string> events) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            events.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Stands in for a disposed <see cref="AiUsage.Web.WebSessionScriptRunner"/> - counts
    /// disposals so a test can prove sign-out never disposes the same runner twice.</summary>
    private sealed class SignOutAwareRunner : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public bool Disposed => DisposeCount > 0;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Models what a real web-backed provider does once its own runner is disposed (see
    /// <see cref="SignOutAwareRunner"/>): report NotSignedIn instead of the runner quietly starting a
    /// fresh session of its own.</summary>
    private sealed class SignOutAwareProvider(string id, SignOutAwareRunner runner) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        // See TokenCapturingProvider's own remark above - same reason on every fake in this file.
        public bool RunsOnUiThread => true;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(runner.Disposed
            ? new ProviderSnapshot(Id, [], null, SourceKind.None, Now, null, ProviderStatus.NotSignedIn, null)
            : new ProviderSnapshot(Id, [], "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null));
    }

    /// <summary>A freshly (re)built provider that has no session yet - the honest state right after
    /// <see cref="MainViewModel.SignOutAsync"/> rebuilds an account, before any sign-in happens.</summary>
    private sealed class AlwaysNotSignedInProvider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        // See TokenCapturingProvider's own remark above - same reason on every fake in this file.
        public bool RunsOnUiThread => true;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) =>
            Task.FromResult(new ProviderSnapshot(Id, [], null, SourceKind.None, Now, null, ProviderStatus.NotSignedIn, null));
    }

    private sealed class GatedProvider(string id, Task gate) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public bool RunsOnUiThread => true;

        public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
        {
            await gate;
            return new ProviderSnapshot(Id, [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300)],
                "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null);
        }
    }

    private sealed class CancelAwareProvider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public bool RunsOnUiThread => true;

        public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ProviderSnapshot(Id, [], null, SourceKind.None, Now, null, ProviderStatus.Failed, null);
        }
    }

    private sealed class IdleLocalProvider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public bool RunsOnUiThread => true;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(new ProviderSnapshot(
            Id, [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300)],
            "Plus", SourceKind.LocalFile, Now, Now.AddHours(-3), ProviderStatus.Ok, null));
    }

    private sealed class HeldOverProvider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public bool RunsOnUiThread => true;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(new ProviderSnapshot(
            Id, [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300)],
            "Plus", SourceKind.LocalLogin, Now, Now.AddHours(-1), ProviderStatus.Ok, null, HeldOver: true));
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeProvider(string id, double percent = 42) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        // See TokenCapturingProvider's own remark above - same reason on every fake in this file.
        public bool RunsOnUiThread => true;
        public bool WasFetched { get; private set; }
        public Task<ProviderSnapshot>? LastFetch { get; private set; }

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
        {
            WasFetched = true;
            var snapshot = new ProviderSnapshot(Id, [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, percent, Now.AddHours(2), 300)],
                "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null);
            LastFetch = Task.FromResult(snapshot);
            return LastFetch;
        }
    }

    private sealed class TwoWindowFakeProvider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        // See TokenCapturingProvider's own remark above - same reason on every fake in this file.
        public bool RunsOnUiThread => true;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(new ProviderSnapshot(
            Id,
            [
                new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300),
                new UsageWindow("Window_Weekly", WindowKind.Weekly, 55, Now.AddDays(3), 300),
            ],
            "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null));
    }

    /// <summary>Two instances sharing one <see cref="Id"/> but each with its own <see
    /// cref="AccountKey"/> - what a second Claude account looks like from MainViewModel's own point of
    /// view, without needing a real ProviderRegistry/WebView2 session.</summary>
    private sealed class FakeAccountProvider(string accountKey, string id = "claude") : IUsageProvider
    {
        public string Id { get; } = id;
        public string AccountKey { get; } = accountKey;
        public string DisplayName => Id;
        // See TokenCapturingProvider's own remark above - same reason on every fake in this file.
        public bool RunsOnUiThread => true;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(
            new ProviderSnapshot(AccountKey, [], null, SourceKind.None, Now, null, ProviderStatus.NoLocalData, null));
    }

    [Fact]
    public void TwoAccountsOfTheSameProviderGetTwoTilesWithIndependentHiddenSettings()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        settings.Providers["claude"] = new ProviderSettings { Visible = true };
        settings.Providers["claude#2"] = new ProviderSettings { Visible = false };
        var providers = new List<IUsageProvider> { new FakeAccountProvider("claude"), new FakeAccountProvider("claude#2") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);

        var vm = new MainViewModel(settingsStore, settings, providers, historyStore);

        Assert.Equal(2, vm.Tiles.Count);
        var first = vm.Tiles.Single(t => t.ProviderId == "claude");
        var second = vm.Tiles.Single(t => t.ProviderId == "claude#2");
        Assert.Equal("claude", first.RealProviderId);
        Assert.Equal("claude", second.RealProviderId);
        Assert.False(first.IsExtraAccount);
        Assert.True(second.IsExtraAccount);
        Assert.False(first.IsHidden);
        Assert.True(second.IsHidden);
    }

    private static object ReadPrivateField(MainViewModel vm, string fieldName) =>
        typeof(MainViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;

    [Fact]
    public async Task RemovingAnAccountWithAPendingFetchClearsBothTrackingDictionaries()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var providers = new List<IUsageProvider> { new FakeAccountProvider("claude"), new FakeAccountProvider("claude#2") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var vm = new MainViewModel(settingsStore, settings, providers, historyStore);
        vm.SignOutWebView = _ => SignOutResult.Removed;

        vm.Tick(Now); // starts and (synchronously) completes both fetches, leaving "claude#2" pending

        var fetchStartedAt = (Dictionary<string, DateTimeOffset>)ReadPrivateField(vm, "_fetchStartedAt");
        var pendingFetchClear = (HashSet<string>)ReadPrivateField(vm, "_pendingFetchClear");
        Assert.Contains("claude#2", fetchStartedAt.Keys);
        Assert.Contains("claude#2", pendingFetchClear);

        await vm.RemoveAccountAsync("claude#2");

        Assert.DoesNotContain("claude#2", fetchStartedAt.Keys);
        Assert.DoesNotContain("claude#2", pendingFetchClear);
    }

    [Theory]
    [InlineData("claude", "codex", "Codex")]
    [InlineData("claude", "claude", "claude#a.b", "claude#a-b")]
    public async Task AnAccountWhoseFolderNameCollidesWithAnotherIsNotRemoved(string primary, string realProvider, params string[] extraKeys)
    {
        var settings = new AppSettings();
        var providers = new List<IUsageProvider> { new FakeAccountProvider(primary) };
        foreach (var key in extraKeys)
        {
            settings.Accounts[key] = realProvider;
            providers.Add(new FakeAccountProvider(key, realProvider));
        }
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, new HistoryStore(TempDirectory(), () => Now));
        var deleted = new List<string>();
        vm.SignOutWebView = key => { deleted.Add(key); return SignOutResult.Removed; };

        foreach (var key in extraKeys)
            Assert.Equal(RemoveAccountResult.NotRemoved, await vm.RemoveAccountAsync(key, alsoHistory: true));

        Assert.Empty(deleted);
        Assert.Equal(1 + extraKeys.Length, vm.Tiles.Count);
        Assert.All(extraKeys, key => Assert.True(settings.Accounts.ContainsKey(key)));
        Assert.All(extraKeys, key => Assert.False(vm.IsAccountDisconnected(key)));
    }

    private (MainViewModel Vm, AppSettings Settings) BuildTwoAccounts()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var providers = new List<IUsageProvider> { new FakeAccountProvider("claude"), new FakeAccountProvider("claude#2") };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, new HistoryStore(TempDirectory(), () => Now));
        vm.SignOutWebView = _ => SignOutResult.Removed;
        return (vm, settings);
    }

    [Fact]
    public async Task RemovingAnAccountDropsItsCachedUsagePath()
    {
        var (vm, settings) = BuildTwoAccounts();
        settings.WebUsagePaths["claude#2"] = "/api/usage";
        settings.WebUsagePaths["claude"] = "/api/other";

        Assert.Equal(RemoveAccountResult.Removed, await vm.RemoveAccountAsync("claude#2"));

        Assert.False(settings.WebUsagePaths.ContainsKey("claude#2"));
        Assert.True(settings.WebUsagePaths.ContainsKey("claude"));
    }

    [Fact]
    public void ANewAccountSkipsAKeyWhoseHistoryIsStillOnDisk()
    {
        var historyDir = TempDirectory();
        File.WriteAllText(Path.Combine(historyDir, "history-claude-2.jsonl"), "kept");
        var settings = new AppSettings();
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings,
            [new FakeAccountProvider("claude")], new HistoryStore(historyDir, () => Now));
        vm.CreateWebAccount = key => (new FakeAccountProvider(key), new GatedRunner());

        vm.AddAccountCommand.Execute("claude");

        Assert.True(settings.Accounts.ContainsKey("claude#3"));
        Assert.False(settings.Accounts.ContainsKey("claude#2"));
    }

    [Fact]
    public void ANewAccountSkipsAKeyWhoseSignInFolderIsStillOnDisk()
    {
        var root = TempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "claude-2"));
        var settings = new AppSettings();
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings,
            [new FakeAccountProvider("claude")], new HistoryStore(TempDirectory(), () => Now));
        vm.CreateWebAccount = key => (new FakeAccountProvider(key), new GatedRunner());

        WebViewHost.SetRootOverride(root);
        try
        {
            vm.AddAccountCommand.Execute("claude");
        }
        finally
        {
            WebViewHost.ClearRootOverrideForTests();
        }

        Assert.True(settings.Accounts.ContainsKey("claude#3"));
        Assert.False(settings.Accounts.ContainsKey("claude#2"));
    }

    [Fact]
    public async Task AnAccountWhoseRequestedHistoryCannotBeDeletedIsStillRemoved()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var providers = new List<IUsageProvider> { new FakeAccountProvider("claude"), new FakeAccountProvider("claude#2") };
        var historyDir = TempDirectory();
        var historyFile = Path.Combine(historyDir, "history-claude-2.jsonl");
        File.WriteAllText(historyFile, "kept");
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, new HistoryStore(historyDir, () => Now));
        vm.SignOutWebView = _ => SignOutResult.Removed;

        RemoveAccountResult result;
        using (new FileStream(historyFile, FileMode.Open, FileAccess.Read, FileShare.None))
            result = await vm.RemoveAccountAsync("claude#2", alsoHistory: true);

        Assert.Equal(RemoveAccountResult.RemovedHistoryKept, result);
        Assert.DoesNotContain(vm.Tiles, t => t.ProviderId == "claude#2");
        Assert.False(settings.Accounts.ContainsKey("claude#2"));
        Assert.True(File.Exists(historyFile));
    }

    [Fact]
    public async Task RemovingAnAccountSavesTheSettingsAtOnceAndDiscardsAStaleQueuedSave()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var storeDir = TempDirectory();
        var store = new SettingsStore(storeDir, debounceDelay: TimeSpan.FromMilliseconds(200));
        var providers = new List<IUsageProvider> { new FakeAccountProvider("claude"), new FakeAccountProvider("claude#2") };
        var vm = new MainViewModel(store, settings, providers, new HistoryStore(TempDirectory(), () => Now));
        vm.SignOutWebView = _ => SignOutResult.Removed;
        store.RequestSave(settings); // queued while the account still exists
        var file = Path.Combine(storeDir, "settings.json");

        Assert.Equal(RemoveAccountResult.Removed, await vm.RemoveAccountAsync("claude#2"));

        Assert.DoesNotContain("claude#2", File.ReadAllText(file));
        await Task.Delay(700);
        Assert.DoesNotContain("claude#2", File.ReadAllText(file));
        store.Dispose();
    }

    [Fact]
    public async Task ARemovalWhoseSettingsCannotBeSavedIsLoggedAndStillCompletes()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var blocker = Path.Combine(TempDirectory(), "not-a-folder");
        File.WriteAllText(blocker, "x");
        var logService = new LogService(TempDirectory(), now: () => Now);
        var providers = new List<IUsageProvider> { new FakeAccountProvider("claude"), new FakeAccountProvider("claude#2") };
        var vm = new MainViewModel(new SettingsStore(blocker), settings, providers,
            new HistoryStore(TempDirectory(), () => Now), claudeRunner: null, logService: logService);
        vm.SignOutWebView = _ => SignOutResult.Removed;

        Assert.Equal(RemoveAccountResult.Removed, await vm.RemoveAccountAsync("claude#2"));

        Assert.Contains("Removing claude#2: the settings could not be saved", File.ReadAllText(logService.CurrentFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedRemovalOrSignOutLogsNoUserPath(bool signOut)
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var logService = new LogService(TempDirectory(), now: () => Now);
        var providers = new List<IUsageProvider> { new FakeAccountProvider("claude"), new FakeAccountProvider("claude#2") };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers,
            new HistoryStore(TempDirectory(), () => Now), claudeRunner: null, logService: logService);
        vm.SignOutWebView = _ => throw new IOException($"cannot open {Path.Combine(profile, "AppData", "x")}");

        if (signOut)
            Assert.False(await vm.TrySignOutAsync("claude#2"));
        else
            Assert.Equal(RemoveAccountResult.Failed, await vm.TryRemoveAccountAsync("claude#2"));

        var log = File.ReadAllText(logService.CurrentFile);
        Assert.Contains("IOException", log);
        Assert.DoesNotContain(profile, log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovingAnAccountClearsItsNotificationState()
    {
        var (vm, _) = BuildTwoAccounts();
        var notifications = (NotificationService)ReadPrivateField(vm, "_notifications");
        var raised = new List<ThresholdNotification>();
        notifications.NotificationRaised += raised.Add;
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 90, Now.AddDays(3), windowMinutes: null);
        notifications.Evaluate("claude#2", "Claude", window, threshold: 85, enabled: true, notifyOnReset: false, Now);
        notifications.Evaluate("claude#2", "Claude", window, threshold: 85, enabled: true, notifyOnReset: false, Now.AddMinutes(1));
        Assert.Single(raised);

        Assert.Equal(RemoveAccountResult.Removed, await vm.RemoveAccountAsync("claude#2"));

        notifications.Evaluate("claude#2", "Claude", window, threshold: 85, enabled: true, notifyOnReset: false, Now.AddMinutes(2));
        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public async Task SigningOutAnAccountThatWasRemovedChangesNothing()
    {
        var (vm, settings) = BuildTwoAccounts();
        var deleted = new List<string>();
        var rebuilt = new List<string>();
        vm.CreateWebAccount = key => { rebuilt.Add(key); return (new FakeAccountProvider(key), new GatedRunner()); };
        Assert.Equal(RemoveAccountResult.Removed, await vm.RemoveAccountAsync("claude#2"));
        vm.SignOutWebView = key => { deleted.Add(key); return SignOutResult.Removed; };

        Assert.False(await vm.SignOutAsync("claude#2"));

        Assert.Empty(deleted);
        Assert.Empty(rebuilt);
        Assert.False(settings.Providers.ContainsKey("claude#2"));
        Assert.False(((Dictionary<string, IAsyncDisposable>)ReadPrivateField(vm, "_webRunners")).ContainsKey("claude#2"));
    }

    [Fact]
    public async Task SigningOutAnAccountWhileItIsBeingRemovedChangesNothing()
    {
        var (vm, settings) = BuildTwoAccounts();
        var rebuilt = new List<string>();
        vm.CreateWebAccount = key => { rebuilt.Add(key); return (new FakeAccountProvider(key), new GatedRunner()); };
        var runner = new GatedRunner();
        ((Dictionary<string, IAsyncDisposable>)ReadPrivateField(vm, "_webRunners"))["claude#2"] = runner;
        var removal = vm.RemoveAccountAsync("claude#2");

        Assert.False(await vm.SignOutAsync("claude#2"));
        runner.Release.SetResult();
        Assert.Equal(RemoveAccountResult.Removed, await removal);

        Assert.Empty(rebuilt);
        Assert.False(settings.Providers.ContainsKey("claude#2"));
        Assert.False(((Dictionary<string, IAsyncDisposable>)ReadPrivateField(vm, "_webRunners")).ContainsKey("claude#2"));
    }

    [Fact]
    public async Task ASignOutThatIsOvertakenByARemovalDoesNotRebuildTheAccount()
    {
        var (vm, settings) = BuildTwoAccounts();
        var rebuilt = new List<string>();
        vm.CreateWebAccount = key => { rebuilt.Add(key); return (new FakeAccountProvider(key), new GatedRunner()); };
        var runner = new GatedRunner();
        var runners = (Dictionary<string, IAsyncDisposable>)ReadPrivateField(vm, "_webRunners");
        runners["claude#2"] = runner;
        var signOut = vm.SignOutAsync("claude#2");
        Assert.False(signOut.IsCompleted);

        Assert.Equal(RemoveAccountResult.Removed, await vm.RemoveAccountAsync("claude#2"));
        runner.Release.SetResult();
        Assert.False(await signOut);

        Assert.Empty(rebuilt);
        Assert.False(runners.ContainsKey("claude#2"));
        Assert.False(settings.Providers.ContainsKey("claude#2"));
        Assert.DoesNotContain(vm.Tiles, t => t.ProviderId == "claude#2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASignInThatFinishesAfterTheAccountWasRemovedDoesNotBringItBack(bool viaCompleteSignIn)
    {
        var (vm, settings) = BuildTwoAccounts();
        Assert.Equal(RemoveAccountResult.Removed, await vm.RemoveAccountAsync("claude#2"));

        if (viaCompleteSignIn)
            vm.CompleteSignIn("claude#2", signedIn: true);
        else
            vm.Reconnect("claude#2");

        Assert.False(settings.Providers.ContainsKey("claude#2"));
        Assert.DoesNotContain(vm.Tiles, t => t.ProviderId == "claude#2");
    }

    [Fact]
    public async Task ASignInThatFinishesWhileTheAccountIsBeingRemovedDoesNotReconnectIt()
    {
        var (vm, settings) = BuildTwoAccounts();
        var runner = new GatedRunner();
        ((Dictionary<string, IAsyncDisposable>)ReadPrivateField(vm, "_webRunners"))["claude#2"] = runner;
        var removal = vm.RemoveAccountAsync("claude#2");

        vm.CompleteSignIn("claude#2", signedIn: true);
        vm.Reconnect("claude#2");
        Assert.False(settings.Providers.ContainsKey("claude#2"));
        runner.Release.SetResult();
        await removal;

        Assert.False(settings.Providers.ContainsKey("claude#2"));
    }

    private (MainViewModel Vm, GatedRunner Old, Dictionary<string, IAsyncDisposable> Runners, List<string> Rebuilt) BuildTwoAccountsWithThrowingDelete()
    {
        var (vm, _) = BuildTwoAccounts();
        vm.SignOutWebView = _ => throw new InvalidOperationException("browser still shutting down");
        var rebuilt = new List<string>();
        vm.CreateWebAccount = key => { rebuilt.Add(key); return (new FakeAccountProvider(key), new GatedRunner()); };
        var runner = new GatedRunner();
        runner.Release.SetResult();
        var runners = (Dictionary<string, IAsyncDisposable>)ReadPrivateField(vm, "_webRunners");
        runners["claude#2"] = runner;
        return (vm, runner, runners, rebuilt);
    }

    [Fact]
    public async Task AFailureWhileRemovingAnAccountStillLeavesItWithALiveBrowserSession()
    {
        var (vm, old, runners, rebuilt) = BuildTwoAccountsWithThrowingDelete();

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RemoveAccountAsync("claude#2"));

        Assert.True(old.Disposed);
        Assert.Equal(["claude#2"], rebuilt);
        Assert.NotSame(old, runners["claude#2"]);
        Assert.Equal(2, vm.Tiles.Count);
        Assert.False(vm.IsAccountDisconnected("claude#2"));
    }

    [Fact]
    public async Task TryRemovingAnAccountReportsAFailureInsteadOfThrowing()
    {
        var (vm, old, runners, rebuilt) = BuildTwoAccountsWithThrowingDelete();

        var result = await vm.TryRemoveAccountAsync("claude#2");

        Assert.Equal(RemoveAccountResult.Failed, result);
        Assert.Equal(["claude#2"], rebuilt);
        Assert.NotSame(old, runners["claude#2"]);
        Assert.Contains(vm.Tiles, t => t.ProviderId == "claude#2");
    }

    private sealed class GatedRunner : IAsyncDisposable
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Release.Task;
            Disposed = true;
        }
    }

    [Fact]
    public async Task RemovingAnAccountWaitsForItsBrowserSessionToCloseBeforeDroppingIt()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var providers = new List<IUsageProvider> { new FakeAccountProvider("claude"), new FakeAccountProvider("claude#2") };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, new HistoryStore(TempDirectory(), () => Now));
        vm.SignOutWebView = _ => SignOutResult.Removed;
        var runner = new GatedRunner();
        ((Dictionary<string, IAsyncDisposable>)ReadPrivateField(vm, "_webRunners"))["claude#2"] = runner;

        var removal = vm.RemoveAccountAsync("claude#2");

        Assert.False(removal.IsCompleted);
        Assert.Equal(2, vm.Tiles.Count);
        Assert.True(vm.IsAccountDisconnected("claude#2"));

        runner.Release.SetResult();
        var removed = await removal;

        Assert.Equal(RemoveAccountResult.Removed, removed);
        Assert.True(runner.Disposed);
        Assert.Single(vm.Tiles);
        Assert.False(settings.Accounts.ContainsKey("claude#2"));
    }

    [Fact]
    public async Task ABrowserSessionThatFailsToCloseStillLetsTheAccountBeRemoved()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var providers = new List<IUsageProvider> { new FakeAccountProvider("claude"), new FakeAccountProvider("claude#2") };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, new HistoryStore(TempDirectory(), () => Now));
        vm.SignOutWebView = _ => SignOutResult.Removed;
        var runner = new GatedRunner();
        runner.Release.SetException(new InvalidOperationException("teardown failed"));
        ((Dictionary<string, IAsyncDisposable>)ReadPrivateField(vm, "_webRunners"))["claude#2"] = runner;

        var removed = await vm.RemoveAccountAsync("claude#2");

        Assert.Equal(RemoveAccountResult.Removed, removed);
        Assert.Single(vm.Tiles);
    }

    /// <summary>A view model over the primary Claude account and a second one, with a fake profile
    /// root and a fake history folder: the second account's sign-in folder and history file, plus the
    /// primary account's and a third account's, so a test can prove only the removed account's own
    /// files go. Never the real profile root.</summary>
    private (MainViewModel Vm, string Root, string HistoryDir) BuildTwoAccountsWithFiles()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var providers = new List<IUsageProvider> { new FakeAccountProvider("claude"), new FakeAccountProvider("claude#2") };
        var historyDir = TempDirectory();
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, providers, new HistoryStore(historyDir, () => Now));

        var root = TempDirectory();
        foreach (var name in new[] { "claude", "claude-2", "claude-3" })
        {
            Directory.CreateDirectory(Path.Combine(root, name, "EBWebView"));
            File.WriteAllText(Path.Combine(root, name, "EBWebView", "Cookies"), name);
        }
        foreach (var name in new[] { "claude", "claude-2", "claude-3" })
            File.WriteAllText(Path.Combine(historyDir, $"history-{name}.jsonl"), name);
        return (vm, root, historyDir);
    }

    [Fact]
    public async Task RemovingAnAccountDeletesItsSignInFolder()
    {
        var (vm, root, _) = BuildTwoAccountsWithFiles();
        WebViewHost.SetRootOverride(root);
        try
        {
            Assert.Equal(RemoveAccountResult.Removed, await vm.RemoveAccountAsync("claude#2"));
        }
        finally
        {
            WebViewHost.ClearRootOverrideForTests();
        }

        Assert.False(Directory.Exists(Path.Combine(root, "claude-2")));
        // Every other folder, the shared root included, is untouched.
        Assert.True(File.Exists(Path.Combine(root, "claude", "EBWebView", "Cookies")));
        Assert.True(File.Exists(Path.Combine(root, "claude-3", "EBWebView", "Cookies")));
    }

    [Fact]
    public async Task RemovingAnAccountKeepsTheHistoryUnlessAsked()
    {
        var (vm, root, historyDir) = BuildTwoAccountsWithFiles();
        WebViewHost.SetRootOverride(root);
        try
        {
            Assert.Equal(RemoveAccountResult.Removed, await vm.RemoveAccountAsync("claude#2"));
        }
        finally
        {
            WebViewHost.ClearRootOverrideForTests();
        }

        Assert.True(File.Exists(Path.Combine(historyDir, "history-claude-2.jsonl")));
    }

    [Fact]
    public async Task RemovingAnAccountDeletesTheHistoryWhenAsked()
    {
        var (vm, root, historyDir) = BuildTwoAccountsWithFiles();
        WebViewHost.SetRootOverride(root);
        try
        {
            Assert.Equal(RemoveAccountResult.Removed, await vm.RemoveAccountAsync("claude#2", alsoHistory: true));
        }
        finally
        {
            WebViewHost.ClearRootOverrideForTests();
        }

        Assert.False(File.Exists(Path.Combine(historyDir, "history-claude-2.jsonl")));
        Assert.True(File.Exists(Path.Combine(historyDir, "history-claude.jsonl")));
        Assert.True(File.Exists(Path.Combine(historyDir, "history-claude-3.jsonl")));
    }

    [Theory]
    [InlineData(SignOutResult.StillOpen)]
    [InlineData(SignOutResult.Refused)]
    public async Task AnAccountWhoseSignInFolderCannotBeDeletedIsNotRemoved(SignOutResult failure)
    {
        var (vm, _, historyDir) = BuildTwoAccountsWithFiles();
        vm.SignOutWebView = _ => failure;
        vm.SignOutRetryDelay = TimeSpan.Zero;
        var rebuilt = new List<string>();
        vm.CreateWebAccount = key => { rebuilt.Add(key); return (new FakeAccountProvider(key), new GatedRunner()); };
        var runner = new GatedRunner();
        runner.Release.SetResult();
        var runners = (Dictionary<string, IAsyncDisposable>)ReadPrivateField(vm, "_webRunners");
        runners["claude#2"] = runner;

        var result = await vm.RemoveAccountAsync("claude#2", alsoHistory: true);

        Assert.Equal(RemoveAccountResult.SignInFilesLocked, result);
        Assert.Equal(2, vm.Tiles.Count);
        Assert.Contains(vm.Tiles, t => t.ProviderId == "claude#2");
        Assert.True(File.Exists(Path.Combine(historyDir, "history-claude-2.jsonl")));
        Assert.False(vm.IsAccountDisconnected("claude#2"));
        // The browser session was already closed, so the account gets a fresh one to read with.
        Assert.Equal(["claude#2"], rebuilt);
        Assert.NotSame(runner, runners["claude#2"]);
    }

    [Fact]
    public async Task RemovingThePrimaryAccountDeletesNothing()
    {
        var (vm, root, historyDir) = BuildTwoAccountsWithFiles();
        WebViewHost.SetRootOverride(root);
        try
        {
            Assert.Equal(RemoveAccountResult.NotRemoved, await vm.RemoveAccountAsync("claude", alsoHistory: true));
        }
        finally
        {
            WebViewHost.ClearRootOverrideForTests();
        }

        Assert.True(File.Exists(Path.Combine(root, "claude", "EBWebView", "Cookies")));
        Assert.True(File.Exists(Path.Combine(historyDir, "history-claude.jsonl")));
        Assert.Equal(2, vm.Tiles.Count);
    }

    [Fact]
    public void ATickOverAStalePendingEntryWithNoTileRemovesItInsteadOfWalkingItForever()
    {
        var (vm, _, _) = Build(new AppSettings());
        var pendingFetchClear = (HashSet<string>)ReadPrivateField(vm, "_pendingFetchClear");
        pendingFetchClear.Add("removed-account");

        vm.Tick(Now);

        Assert.DoesNotContain("removed-account", pendingFetchClear);
    }

    [Fact]
    public void ASecondHistoryReadRequestSupersedesAPendingOne()
    {
        var (vm, _, _) = Build(new AppSettings());

        Assert.True(vm.ShouldStartHistoryRead("codex", out var first));
        Assert.True(vm.ShouldStartHistoryRead("codex", out var second));

        Assert.NotEqual(first, second);
        Assert.False(vm.HistoryReadCompleted("codex", first));
    }

    [Fact]
    public void ACompletedReadWhoseTokenIsNoLongerCurrentDoesNotCountAsCompleted()
    {
        var (vm, _, _) = Build(new AppSettings());

        vm.ShouldStartHistoryRead("codex", out var stale);
        vm.ShouldStartHistoryRead("codex", out var current);

        Assert.False(vm.HistoryReadCompleted("codex", stale));
        Assert.True(vm.HistoryReadCompleted("codex", current));
    }

    [Fact]
    public void AHistoryReadForAnotherProviderIsUnaffectedByOnesToken()
    {
        var (vm, _, _) = Build(new AppSettings());

        vm.ShouldStartHistoryRead("codex", out var codexToken);
        vm.ShouldStartHistoryRead("claude", out var claudeToken);

        Assert.True(vm.HistoryReadCompleted("codex", codexToken));
        Assert.True(vm.HistoryReadCompleted("claude", claudeToken));
    }
    /// <summary>The browser process does not disappear the moment the session that held it is
    /// released, so the first delete after a sign-out regularly still finds one of its own files
    /// open. Giving up there left the session on disk and the account signed in.</summary>
    // The sign-out buttons await this from async event handlers, where an escaping exception ends
    // the whole app.
    [Fact]
    public async Task AFailingSignOutReportsIncompleteInsteadOfThrowing()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);
        vm.SignOutWebView = _ => throw new InvalidOperationException("browser still shutting down");

        var result = await vm.TrySignOutAsync("claude");

        Assert.False(result);
    }

    // A teardown that failed after the runner was already disposed left the account on a provider
    // that could never read again, and a retry no longer rebuilt it.
    [Fact]
    public async Task AFailedSignOutStillLeavesAFreshWebProviderBehind()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings, claudeRunner: new SignOutAwareRunner());
        vm.SignOutWebView = _ => throw new InvalidOperationException("browser still shutting down");
        FakeProvider? fresh = null;
        vm.CreateWebAccount = key => { fresh = new FakeProvider(key); return (fresh, new SignOutAwareRunner()); };

        Assert.False(await vm.TrySignOutAsync("claude"));
        vm.RefreshNow();
        await Task.Delay(20);

        Assert.NotNull(fresh);
        Assert.True(fresh!.WasFetched);
    }

    [Fact]
    public async Task ASignOutTriesAgainWhileTheBrowserStillHasTheFolderOpen()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);
        var attempts = 0;
        vm.SignOutRetryDelay = TimeSpan.Zero;
        vm.SignOutWebView = _ => ++attempts < 3 ? SignOutResult.StillOpen : SignOutResult.Removed;

        var result = await vm.SignOutAsync("claude");

        Assert.True(result);
        Assert.Equal(3, attempts);
    }

    /// <summary>A refusal says the folder must never be deleted at all - asking again only repeats
    /// it, so it is asked exactly once.</summary>
    [Fact]
    public async Task ARefusedSignOutIsNotAskedASecondTime()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);
        var attempts = 0;
        vm.SignOutRetryDelay = TimeSpan.Zero;
        vm.SignOutWebView = _ => { attempts++; return SignOutResult.Refused; };

        var result = await vm.SignOutAsync("claude");

        Assert.False(result);
        Assert.Equal(1, attempts);
    }

    /// <summary>A sign-in window that ran its whole flow through is proof of a session even when the
    /// read that follows fails for its own reasons - the tile used to keep offering the sign-in
    /// button to someone who had just signed in.</summary>
    [Fact]
    public void AFinishedSignInWindowTakesTheSignInButtonOffTheTile()
    {
        var settings = SettingsWithVisibility(("codex", true), ("claude", true), ("gemini", true), ("copilot", true));
        var (vm, _, _) = Build(settings);

        vm.MarkSignedIn("claude");

        var tile = vm.Tiles.Single(t => t.ProviderId == "claude");
        Assert.Equal(SignInState.SignedIn, tile.SignInState);
        Assert.False(tile.ShowSignIn);
    }
}
