using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The display choices of the "Token usage" settings page: the stored values, the week start,
/// the period the window opens on and the sections that can be switched off.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class StatsDisplayChoicesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly List<DisposableTestDirectory> _directories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("stats-display-choices");
        _directories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
            directory.Dispose();
    }

    [Theory]
    [InlineData("Week", "Week")]
    [InlineData("ThisMonth", "ThisMonth")]
    [InlineData("All", "All")]
    [InlineData("Custom", "")]
    [InlineData("nonsense", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_open_period_keeps_a_known_value_and_falls_back_to_last_used(string? stored, string expected) =>
        Assert.Equal(expected, StatsDisplayChoices.NormalizeOpenRange(stored));

    [Theory]
    [InlineData("Monday", "Monday")]
    [InlineData("Sunday", "Sunday")]
    [InlineData("Friday", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_week_start_keeps_Monday_and_Sunday_and_falls_back_to_the_system(string? stored, string expected) =>
        Assert.Equal(expected, StatsDisplayChoices.NormalizeWeekStart(stored));

    [Fact]
    public void The_first_weekday_follows_the_choice_and_the_system_one_the_culture()
    {
        var us = CultureInfo.GetCultureInfo("en-US");
        var de = CultureInfo.GetCultureInfo("de-DE");

        Assert.Equal(DayOfWeek.Monday, StatsDisplayChoices.ResolveFirstDay("Monday", us));
        Assert.Equal(DayOfWeek.Sunday, StatsDisplayChoices.ResolveFirstDay("Sunday", de));
        Assert.Equal(DayOfWeek.Sunday, StatsDisplayChoices.ResolveFirstDay("", us));
        Assert.Equal(DayOfWeek.Monday, StatsDisplayChoices.ResolveFirstDay(null, de));
    }

    [Fact]
    public void Hidden_sections_lose_unknown_and_repeated_keys_and_keep_their_order()
    {
        var cleaned = StatsDisplayChoices.NormalizeHiddenSections(["limits", "bogus", "figures", "limits", ""]);

        Assert.Equal(["limits", "figures"], cleaned);
        Assert.Empty(StatsDisplayChoices.NormalizeHiddenSections(null));
    }

    [Fact]
    public void The_switchable_sections_are_exactly_the_windows_sections_in_their_default_order() =>
        Assert.Equal(StatsLayout.SectionKeys, StatsDisplayChoices.Sections.Select(section => section.Key).ToArray());

    [Theory]
    [InlineData(2026, 11, 15, DayOfWeek.Monday, 2026, 11, 9)]
    [InlineData(2026, 11, 15, DayOfWeek.Sunday, 2026, 11, 15)]
    [InlineData(2026, 11, 14, DayOfWeek.Sunday, 2026, 11, 8)]
    [InlineData(2026, 11, 9, DayOfWeek.Monday, 2026, 11, 9)]
    public void The_week_start_of_a_day_is_the_latest_first_weekday_on_or_before_it(
        int year, int month, int day, DayOfWeek first, int expectedYear, int expectedMonth, int expectedDay)
    {
        var date = new DateOnly(year, month, day);

        Assert.Equal(new DateOnly(expectedYear, expectedMonth, expectedDay), StatsAggregator.WeekStart(date, first));
    }

    [Fact]
    public void The_plain_week_start_stays_Monday()
    {
        for (var offset = 0; offset < 14; offset++)
        {
            var date = new DateOnly(2026, 11, 2).AddDays(offset);
            Assert.Equal(StatsAggregator.WeekStart(date, DayOfWeek.Monday), StatsAggregator.WeekStart(date));
        }
    }

    [Fact]
    public void The_new_settings_survive_a_save_and_a_load()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);
        var original = new AppSettings
        {
            StatsOpenRange = "ThisMonth",
            StatsWeekStart = "Sunday",
            StatsHiddenSections = ["limits", "projects"],
        };

        store.SaveNow(original);
        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.Equal("ThisMonth", loaded.StatsOpenRange);
        Assert.Equal("Sunday", loaded.StatsWeekStart);
        Assert.Equal(["limits", "projects"], loaded.StatsHiddenSections);
    }

    [Fact]
    public void A_fresh_install_opens_on_the_last_used_period_shows_every_section_and_follows_the_system_week()
    {
        var defaults = new AppSettings();

        Assert.Equal("", defaults.StatsOpenRange);
        Assert.Equal("", defaults.StatsWeekStart);
        Assert.Empty(defaults.StatsHiddenSections);
    }

    [Fact]
    public void Opening_on_a_chosen_period_replaces_the_one_left_on_and_last_used_keeps_it()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-display-open-range");
        var viewModel = StatsVm.Create(new StatsStore(dataDir), vm => vm.SelectedRange = "Month");

        viewModel.ApplyOpenRange("");
        Assert.Equal("Month", viewModel.SelectedRange);

        viewModel.ApplyOpenRange("Custom");
        Assert.Equal("Month", viewModel.SelectedRange);

        viewModel.ApplyOpenRange("LastMonth");
        Assert.Equal("LastMonth", viewModel.SelectedRange);
        Assert.Equal("LastMonth", viewModel.RangeChoices.Single(choice => choice.IsSelected).Value);
    }

    [Fact]
    public void A_section_switched_off_is_reported_and_the_plan_forecast_needs_it_on_as_well()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-display-hidden");
        var viewModel = StatsVm.Create(new StatsStore(dataDir));
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(viewModel.IsSectionHidden("limits"));

        viewModel.SetHiddenSections(["limits", "planforecast"]);

        Assert.True(viewModel.IsSectionHidden("limits"));
        Assert.True(viewModel.IsSectionHidden("planforecast"));
        Assert.False(viewModel.IsSectionHidden("figures"));
        Assert.Contains(nameof(StatsViewModel.ShowPlanForecast), raised);
        Assert.False(viewModel.ShowPlanForecast);
    }

    [Fact]
    public void The_month_grid_starts_on_the_first_weekday_the_window_resolves()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-display-first-day");
        var viewModel = StatsVm.Create(new StatsStore(dataDir), vm => vm.FirstDayOfWeek = () => DayOfWeek.Sunday);
        viewModel.Recompute();

        Assert.Equal(DayOfWeek.Sunday, viewModel.MonthGridFirstDay);
    }

    [Fact]
    public void The_index_info_counts_the_stored_rows_and_reports_the_file_size()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-display-index-info");
        var store = new StatsStore(dataDir);
        store.AddDelta([
            new StatsRecord("claude", new DateOnly(2026, 11, 1), "modelA", "projA", 10, 5, 0, 0),
            new StatsRecord("codex", new DateOnly(2026, 11, 2), "modelB", "projB", 20, 5, 0, 0),
        ]);

        var info = store.IndexInfo();

        Assert.NotNull(info);
        Assert.Equal(2, info!.Value.Entries);
        Assert.True(info.Value.Bytes > 0);
    }

    [Fact]
    public void The_page_offers_the_stored_choices_and_a_pick_is_saved_and_announced()
    {
        var settings = new AppSettings { StatsOpenRange = "Week", StatsWeekStart = "Monday", StatsHiddenSections = ["limits"] };
        var vm = BuildViewModelWithDialogs(settings);
        var announced = 0;
        vm.StatsDisplayChanged += (_, _) => announced++;

        Assert.Equal("Week", vm.SelectedStatsOpenRangeChoice!.Value);
        Assert.Equal("Monday", vm.SelectedStatsWeekStartChoice!.Value);
        Assert.False(vm.StatsSections.Single(section => section.Key == "limits").IsShown);
        Assert.True(vm.StatsSections.Single(section => section.Key == "figures").IsShown);
        Assert.Equal(0, announced);

        vm.SelectedStatsOpenRangeChoice = vm.StatsOpenRangeChoices.Single(choice => choice.Value == "All");
        Assert.Equal("All", settings.StatsOpenRange);

        vm.SelectedStatsWeekStartChoice = vm.StatsWeekStartChoices.Single(choice => choice.Value == "Sunday");
        Assert.Equal("Sunday", settings.StatsWeekStart);
        Assert.Equal(1, announced);

        vm.StatsSections.Single(section => section.Key == "projects").IsShown = false;
        Assert.Equal(["limits", "projects"], settings.StatsHiddenSections);
        Assert.Equal(2, announced);
    }

    [Fact]
    public void Reset_puts_the_defaults_back_and_the_page_follows()
    {
        var settings = new AppSettings { StatsOpenRange = "Year", StatsWeekStart = "Sunday", StatsHiddenSections = ["figures"] };
        var vm = BuildViewModelWithDialogs(settings);

        vm.ResetToDefaults();

        Assert.Equal("", settings.StatsOpenRange);
        Assert.Equal("", settings.StatsWeekStart);
        Assert.Empty(settings.StatsHiddenSections);
        Assert.Equal("", vm.SelectedStatsOpenRangeChoice!.Value);
        Assert.Equal("", vm.SelectedStatsWeekStartChoice!.Value);
        Assert.All(vm.StatsSections, section => Assert.True(section.IsShown));
    }

    [Fact]
    public async Task An_import_takes_the_three_choices_over_and_cleans_unknown_values()
    {
        var exportPath = Path.Combine(TempDirectory(), "stats-display-export.json");
        var source = new AppSettings { StatsOpenRange = "Month", StatsWeekStart = "Sunday", StatsHiddenSections = ["model", "bogus"] };
        var sourceVm = BuildViewModelWithDialogs(source, askExport: _ => exportPath);
        sourceVm.ExportSettingsCommand.Execute(null);

        var targetVm = BuildViewModelWithDialogs(new AppSettings(), askImport: () => exportPath);
        await targetVm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Month", targetVm.SelectedStatsOpenRangeChoice!.Value);
        Assert.Equal("Sunday", targetVm.SelectedStatsWeekStartChoice!.Value);
        Assert.False(targetVm.StatsSections.Single(section => section.Key == "model").IsShown);
    }

    [Fact]
    public void Added_session_folders_are_kept_in_the_settings_listed_once_and_removable()
    {
        var settings = new AppSettings();
        var picks = new Queue<string?>([@"D:\Old PC\projects", @"d:\old pc\projects\", null, @"E:\Backup\projects"]);
        var vm = BuildViewModelWithDialogs(settings, askFolder: () => picks.Dequeue());

        for (var click = 0; click < 4; click++)
            vm.AddStatsFolderCommand.Execute(null);

        Assert.Equal([@"D:\Old PC\projects", @"E:\Backup\projects"], vm.StatsExtraFolders);
        Assert.Equal(vm.StatsExtraFolders, settings.StatsExtraFolders);
        Assert.True(vm.HasStatsExtraFolders);

        vm.RemoveStatsFolderCommand.Execute(@"D:\Old PC\projects");

        Assert.Equal([@"E:\Backup\projects"], settings.StatsExtraFolders);
    }

    [Fact]
    public void The_added_session_folders_of_the_settings_are_listed_when_the_page_opens()
    {
        var settings = new AppSettings { StatsExtraFolders = [@"D:\Old PC\projects"] };
        var vm = BuildViewModelWithDialogs(settings);

        Assert.Equal([@"D:\Old PC\projects"], vm.StatsExtraFolders);
    }

    [Fact]
    public async Task Reading_a_folder_once_runs_in_the_background_and_shows_the_result()
    {
        var gate = new TaskCompletionSource<(int, int)>();
        string? readFolder = null;
        var vm = BuildViewModelWithDialogs(new AppSettings(), askFolder: () => @"D:\Old PC\projects",
            importFolder: (folder, _) => { readFolder = folder; return gate.Task; });

        var running = vm.ImportStatsFolderCommand.ExecuteAsync(null);

        Assert.True(vm.IsStatsFolderImportRunning);
        Assert.False(vm.ImportStatsFolderCommand.CanExecute(null));
        Assert.Empty(vm.StatsExtraFolders);

        gate.SetResult((12, 3));
        await running;

        Assert.Equal(@"D:\Old PC\projects", readFolder);
        Assert.False(vm.IsStatsFolderImportRunning);
        Assert.True(vm.ImportStatsFolderCommand.CanExecute(null));
        Assert.Equal(LocalizationService.Instance.Format("Settings.Stats.ImportFolderDone", 12, 3), vm.StatsFolderImportResult);
        Assert.Empty(vm.StatsExtraFolders);
    }

    [Fact]
    public async Task Cancelling_the_folder_picker_reads_nothing()
    {
        var called = false;
        var vm = BuildViewModelWithDialogs(new AppSettings(), askFolder: () => null,
            importFolder: (_, _) => { called = true; return Task.FromResult((0, 0)); });

        await vm.ImportStatsFolderCommand.ExecuteAsync(null);

        Assert.False(called);
        Assert.False(vm.HasStatsFolderImportResult);
    }

    [Fact]
    public void Switching_the_weekly_csv_on_without_a_folder_asks_for_one_and_stays_off_when_cancelled()
    {
        var settings = new AppSettings();
        var asked = 0;
        var vm = BuildViewModelWithDialogs(settings, askFolder: () => { asked++; return null; });

        vm.StatsWeeklyCsv = true;

        Assert.Equal(1, asked);
        Assert.False(vm.StatsWeeklyCsv);
        Assert.False(settings.StatsWeeklyCsv);
        Assert.Equal("", settings.StatsWeeklyCsvFolder);
    }

    [Fact]
    public void Switching_the_weekly_csv_on_with_a_picked_folder_keeps_both_and_the_folder_stays_for_later()
    {
        var settings = new AppSettings();
        var vm = BuildViewModelWithDialogs(settings, askFolder: () => @"E:\Reports");

        vm.StatsWeeklyCsv = true;

        Assert.True(settings.StatsWeeklyCsv);
        Assert.Equal(@"E:\Reports", settings.StatsWeeklyCsvFolder);
        Assert.True(vm.HasStatsWeeklyCsvFolder);

        var asked = 0;
        var again = BuildViewModelWithDialogs(settings, askFolder: () => { asked++; return null; });
        again.StatsWeeklyCsv = false;
        again.StatsWeeklyCsv = true;

        Assert.Equal(0, asked);
        Assert.True(settings.StatsWeeklyCsv);
    }

    [Fact]
    public void The_weekly_csv_folder_button_works_with_the_switch_off_and_cancelling_changes_nothing()
    {
        var settings = new AppSettings();
        var picks = new Queue<string?>([@"E:\Reports", null]);
        var vm = BuildViewModelWithDialogs(settings, askFolder: () => picks.Dequeue());

        Assert.True(vm.ChooseStatsWeeklyCsvFolderCommand.CanExecute(null));
        vm.ChooseStatsWeeklyCsvFolderCommand.Execute(null);
        vm.ChooseStatsWeeklyCsvFolderCommand.Execute(null);

        Assert.Equal(@"E:\Reports", settings.StatsWeeklyCsvFolder);
        Assert.False(settings.StatsWeeklyCsv);
        Assert.Equal(@"E:\Reports", vm.StatsWeeklyCsvFolder);
    }

    private SettingsViewModel BuildViewModelWithDialogs(AppSettings settings, Func<string, string?>? askExport = null, Func<string?>? askImport = null,
        Func<string?>? askFolder = null, Func<string, CancellationToken, Task<(int FilesRead, int FilesAdded)>>? importFolder = null)
    {
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);
        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: uri =>
            {
                var path = Path.Combine(AppContext.BaseDirectory, "ThemeFixtures", Path.GetFileName(uri.OriginalString));
                using var stream = File.OpenRead(path);
                return (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
            },
            askForSettingsExportPath: askExport,
            askForSettingsImportPath: askImport,
            showMessage: _ => { },
            statsStore: new StatsStore(TempDirectory()),
            askForExtraFolder: askFolder ?? (() => null),
            importFolderOnce: importFolder ?? ((_, _) => Task.FromResult((0, 0))));
        vm.ImportedPcsLoadTask.GetAwaiter().GetResult();
        vm.StatsIndexInfoLoadTask.GetAwaiter().GetResult();
        return vm;
    }

    private sealed class FakeProvider(string id) : IUsageProvider
    {
        public string Id { get; } = id;
        public string AccountKey => Id;
        public string DisplayName => Id;
        public bool CanSkipWebSession => false;
        public bool RunsOnUiThread => true;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) =>
            Task.FromResult(new ProviderSnapshot(Id, [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300)],
                "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null));
    }
}
