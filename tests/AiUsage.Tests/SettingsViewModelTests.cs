using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class SettingsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-settings-viewmodel");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }

    /// <summary>Stands in for the real pack-URI load, which needs a live Application this test
    /// project deliberately has none of (same reasoning as ThemeServiceTests.FakeLoad) - reads the
    /// same real theme file ThemeTokenTests reads, copied to ThemeFixtures at build time, so a
    /// SettingsViewModel built through this loader sees exactly the colours the shipped theme
    /// actually has.</summary>
    private static ResourceDictionary LoadThemeFixture(Uri uri)
    {
        var fileName = Path.GetFileName(uri.OriginalString);
        var path = Path.Combine(AppContext.BaseDirectory, "ThemeFixtures", fileName);
        using var stream = File.OpenRead(path);
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private (SettingsViewModel Vm, AppSettings Settings) Build(AppSettings? settings = null, bool fakeAutostartEnabled = false,
        Func<string, string?>? askForSettingsExportPath = null, Func<string?>? askForSettingsImportPath = null,
        Func<CancellationToken, Task<UpdateCheck.Release?>>? fetchLatestRelease = null,
        IReadOnlyList<FakeProvider>? providers = null, Action<string>? showMessage = null, string? historyDirectory = null,
        string? dataDirectory = null, Func<string, string?>? askForBackupSavePath = null, Func<string?>? askForBackupOpenPath = null,
        Func<string, bool>? confirmRestore = null, Func<bool>? restartApp = null, Stats.StatsStore? statsStore = null)
    {
        settings ??= new AppSettings();
        providers ??= [new("codex"), new("claude"), new("gemini"), new("copilot")];
        var settingsStore = new SettingsStore(dataDirectory ?? TempDirectory());
        var historyStore = new HistoryStore(historyDirectory ?? TempDirectory(), () => Now);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);
        // Never the real AutostartService here - a test that flips Autostart must not touch the
        // actual Windows Run key on whatever machine runs the suite.
        var autostartState = fakeAutostartEnabled;
        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => autostartState,
            enableAutostart: _ => { autostartState = true; return true; },
            disableAutostart: () => { autostartState = false; return true; },
            // Never the real LocalizationService.Instance.SetLanguage here either - it is one shared
            // static instance across the whole test run, so a test flipping the language must not
            // leave every other test in the same run reading English or German by execution order.
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            askForSettingsExportPath: askForSettingsExportPath,
            askForSettingsImportPath: askForSettingsImportPath,
            // Never the real MessageBox.Show here - it would pop a real, blocking dialog with no
            // Application around to own it and hang the test run.
            showMessage: showMessage ?? (_ => { }),
            fetchLatestRelease: fetchLatestRelease,
            // Never a real dialog, a real restart or the real data folder's index here either.
            askForBackupSavePath: askForBackupSavePath ?? (_ => null),
            askForBackupOpenPath: askForBackupOpenPath ?? (() => null),
            confirmRestore: confirmRestore ?? (_ => false),
            restartApp: restartApp ?? (() => false),
            statsStore: statsStore ?? new Stats.StatsStore(dataDirectory ?? TempDirectory()));
        return (vm, settings);
    }

    /// <summary>Records the property names the view model raises. The view model's history-size load
    /// raises HistorySizeText from a pool thread once it finishes, which would append to the list
    /// while the test thread does the same; waiting for that load first leaves the test thread the
    /// only writer.</summary>
    private static List<string?> RecordChanges(SettingsViewModel vm)
    {
        vm.HistorySizeLoadTask.GetAwaiter().GetResult();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        return changed;
    }

    [Fact]
    public void PickingATheme_updates_the_Mica_option_visibility_live()
    {
        var (vm, _) = Build();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SetThemeCommand.Execute(AppTheme.Dark);

        Assert.Contains(nameof(SettingsViewModel.MicaOptionVisible), changed);
        Assert.False(vm.MicaOptionVisible);
    }

    [Fact]
    public void TheZoomPickerOffersTheFourStepsAndStoresTheChosenOne()
    {
        var (vm, settings) = Build();
        var changed = RecordChanges(vm);

        Assert.Equal(["90 %", "100 %", "125 %", "150 %"], vm.ZoomChoices.Select(c => c.Label));
        Assert.Equal(100, vm.SelectedZoomChoice?.Value);

        vm.SelectedZoomChoice = vm.ZoomChoices.Single(c => c.Value == 150);

        Assert.Equal(150, vm.ZoomPercent);
        Assert.Equal(150, settings.ZoomPercent);
        Assert.Contains(nameof(SettingsViewModel.ZoomPercent), changed);

        vm.ZoomPercent = 133;
        Assert.Equal(100, settings.ZoomPercent);
        Assert.Equal(100, vm.SelectedZoomChoice?.Value);
    }

    [Fact]
    public void TheFullscreenHideSettingIsSavedAndReportedToTheWindow()
    {
        var (vm, settings) = Build();
        var changed = RecordChanges(vm);
        Assert.True(vm.HideOnFullscreen);

        vm.HideOnFullscreen = false;

        Assert.False(settings.HideOnFullscreen);
        Assert.Contains(nameof(SettingsViewModel.HideOnFullscreen), changed);
    }

    [Fact]
    public void TheSaveEnergySettingIsOnByDefaultAndSaved()
    {
        var (vm, settings) = Build();
        Assert.True(new AppSettings().SaveEnergyOnBattery);
        Assert.True(vm.SaveEnergyOnBattery);

        vm.SaveEnergyOnBattery = false;

        Assert.False(settings.SaveEnergyOnBattery);
    }

    [Fact]
    public void TheWindowLevelPickerFollowsAndDrivesTheMainWindowLevel()
    {
        var (vm, settings) = Build();
        Assert.Equal(WindowLayers.Normal, vm.SelectedWindowLayerChoice?.Value);
        Assert.False(vm.WindowLayerIsDesktop);

        vm.SelectedWindowLayerChoice = vm.WindowLayerChoices.Single(c => c.Value == WindowLayers.Desktop);
        Assert.Equal(WindowLayers.Desktop, vm.Main.WindowLayer);
        Assert.Equal(WindowLayers.Desktop, settings.WindowLayer);
        Assert.True(vm.WindowLayerIsDesktop);

        // The tray or the title bar menu changes it while the window is open.
        vm.Main.WindowLayer = WindowLayers.OnTop;
        Assert.Equal(WindowLayers.OnTop, vm.SelectedWindowLayerChoice?.Value);
        Assert.False(vm.WindowLayerIsDesktop);
    }

    [Theory]
    [InlineData(14, 15)]
    [InlineData(16 * 60, 15 * 60)]
    public void RefreshSecondsClampsAndPersists(int input, int expected)
    {
        var (vm, settings) = Build();

        vm.RefreshSeconds = input;

        Assert.Equal(expected, vm.RefreshSeconds);
        Assert.Equal(expected, settings.RefreshSeconds);
    }

    private static ProviderSnapshot FiveHourSnapshot() => new(
        "codex", [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 40, Now.AddHours(2), 300)], null, SourceKind.LocalFile,
        Now, Now, ProviderStatus.Ok, null);

    [Fact]
    public void MovingTheDefaultThresholdMovesTheBarMarkersWithoutAnotherFetch()
    {
        var (vm, settings) = Build();
        var tile = vm.Main.Tiles.Single(t => t.ProviderId == "codex");
        tile.Apply(FiveHourSnapshot(), Now, new ThresholdSettings { FiveHour = 80, FiveHourEnabled = true });

        vm.DefaultThreshold = 70;

        Assert.Equal(70, tile.Rows.Single().ThresholdPercent);
        vm.DefaultThresholdEnabled = false;
        Assert.False(tile.Rows.Single().ShowThresholdMarker);
    }

    [Fact]
    public void MovingAProvidersOwnThresholdMovesItsBarMarkerWithoutAnotherFetch()
    {
        var (vm, _) = Build();
        var tile = vm.Main.Tiles.Single(t => t.ProviderId == "codex");
        tile.Apply(FiveHourSnapshot(), Now, new ThresholdSettings { FiveHour = 80, FiveHourEnabled = true });
        var row = vm.ThresholdRows.Single(r => r.ProviderId == "codex");

        row.UseCustom = true;
        row.FiveHour = 55;

        Assert.Equal(55, tile.Rows.Single().ThresholdPercent);
    }

    [Fact]
    public void AnExtraAccountsNameArrivingWhileTheWindowIsOpenReachesItsRows()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var (vm, _) = Build(settings, providers: [new("codex"), new("claude"), new("claude", "claude#2"), new("gemini"), new("copilot")]);
        var row = vm.NotificationRows.Single(r => r.ProviderId == "claude#2");
        var tile = vm.Main.Tiles.Single(t => t.ProviderId == "claude#2");
        Assert.Contains("(2)", row.HeaderDisplayName, StringComparison.Ordinal);

        tile.Apply(
            new ProviderSnapshot("claude#2", [], null, SourceKind.None, Now, null, ProviderStatus.NoLocalData, null) with { AccountLabel = "work" }, Now);

        Assert.Contains("work", row.HeaderDisplayName, StringComparison.Ordinal);
        Assert.Contains("work", row.Threshold.HeaderDisplayName, StringComparison.Ordinal);
        Assert.Contains("work", row.NotificationsForProviderText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(90, 60)]
    public void RemoteRefreshMinutesClampsAndPersists(int input, int expected)
    {
        var (vm, settings) = Build();

        vm.RemoteRefreshMinutes = input;

        Assert.Equal(expected, vm.RemoteRefreshMinutes);
        Assert.Equal(expected, settings.RemoteRefreshMinutes);
    }

    [Theory]
    [InlineData(6, 7)]
    [InlineData(4000, 3650)]
    public void RetentionDaysSnapsToTheNearestRungAndPersists(int input, int expected)
    {
        var (vm, settings) = Build();

        vm.HistoryRetentionDays = input;

        Assert.Equal(expected, vm.HistoryRetentionDays);
        Assert.Equal(expected, settings.HistoryRetentionDays);
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(120, "2 min")]
    [InlineData(300, "5 min")]
    [InlineData(90, "1 min 30 s")]
    [InlineData(195, "3 min 15 s")]
    public void RefreshSecondsLabelShowsSecondsUnderAMinuteOtherwiseMinutes(int seconds, string expected)
    {
        var (vm, _) = Build();

        vm.RefreshSeconds = seconds;

        Assert.Equal(expected, vm.RefreshSecondsLabel);
    }

    [Fact]
    public void RetentionLabelIsTheBareDurationWithNoSurroundingSentence()
    {
        var (vm, _) = Build();

        vm.HistoryRetentionDays = 90;

        Assert.Equal(DurationFormatter.Describe(90), vm.RetentionLabel);
        // RetentionFullLabel wraps the same value in a sentence ("Keep history for 90 days"),
        // which is strictly longer than the bare value alone.
        Assert.True(vm.RetentionFullLabel.Length > vm.RetentionLabel.Length);
        Assert.Contains(vm.RetentionLabel, vm.RetentionFullLabel);
    }

    [Fact]
    public void ThresholdRowFiveHourAndWeeklyLabelsShowTheBarePercentValue()
    {
        var (vm, _) = Build();
        var row = vm.ThresholdRows.Single(r => r.ProviderId == "claude");

        row.FiveHour = 72;
        row.Weekly = 63;

        Assert.Equal("72 %", row.FiveHourLabel);
        Assert.Equal("63 %", row.WeeklyLabel);
    }

    [Fact]
    public void NotificationRowLabelNamesItsOwnProviderNotSomeOtherRows()
    {
        var providers = new List<FakeProvider> { new("claude"), new("codex") };
        var (vm, _) = Build(providers: providers);

        var claudeRow = vm.NotificationRows.Single(r => r.ProviderId == "claude");
        var codexRow = vm.NotificationRows.Single(r => r.ProviderId == "codex");

        Assert.Contains(claudeRow.HeaderDisplayName, claudeRow.NotificationsForProviderText);
        Assert.DoesNotContain(codexRow.HeaderDisplayName, claudeRow.NotificationsForProviderText);
    }

    [Fact]
    public void LevelExplainerShowsBothLevels()
    {
        var (vm, _) = Build();

        try
        {
            LocalizationService.Instance.SetLanguage("en");
            Assert.Equal(
                "The bar turns yellow above 60% and red above 85%. The default notification matches red.",
                vm.LevelExplainerText);

            LocalizationService.Instance.SetLanguage("de");
            Assert.Equal(
                "Der Balken wird über 60 % gelb und über 85 % rot. Die Vorgabe für die Meldung entspricht Rot.",
                vm.LevelExplainerText);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void ThresholdBelow50ClampsTo50AndWritesTheSharedThresholdSettings()
    {
        var (vm, settings) = Build();
        var row = vm.ThresholdRows.Single(r => r.ProviderId == "claude");

        row.FiveHour = 40;

        Assert.Equal(50, row.FiveHour);
        Assert.Equal(50, settings.Providers["claude"].Thresholds.FiveHour);
    }

    [Fact]
    public void TwoClaudeAccountsGetTwoDifferentThresholdRowNames()
    {
        var providers = new List<FakeProvider> { new("claude"), new("claude", accountKey: "claude#2") };
        var (vm, _) = Build(providers: providers);

        var first = vm.ThresholdRows.Single(r => r.ProviderId == "claude");
        var second = vm.ThresholdRows.Single(r => r.ProviderId == "claude#2");

        Assert.NotEqual(first.HeaderDisplayName, second.HeaderDisplayName);
    }

    [Fact]
    public void NotificationRowsAreBuiltOncePerTileAndDefaultToBothSwitchesOn()
    {
        var (vm, _) = Build();

        var row = vm.NotificationRows.Single(r => r.ProviderId == "claude");

        Assert.True(row.NotificationsEnabled);
        Assert.True(row.NotifyOnResetEnabled);
    }

    [Fact]
    public void TwoClaudeAccountsGetTwoDifferentNotificationRowNames()
    {
        var providers = new List<FakeProvider> { new("claude"), new("claude", accountKey: "claude#2") };
        var (vm, _) = Build(providers: providers);

        var first = vm.NotificationRows.Single(r => r.ProviderId == "claude");
        var second = vm.NotificationRows.Single(r => r.ProviderId == "claude#2");

        Assert.NotEqual(first.HeaderDisplayName, second.HeaderDisplayName);
    }

    [Fact]
    public void TurningANotificationRowSwitchOffWritesTheSharedProviderSettings()
    {
        var (vm, settings) = Build();
        var row = vm.NotificationRows.Single(r => r.ProviderId == "claude");

        row.NotificationsEnabled = false;
        row.NotifyOnResetEnabled = false;

        Assert.False(settings.Providers["claude"].NotificationsEnabled);
        Assert.False(settings.Providers["claude"].NotifyOnResetEnabled);
    }

    [Fact]
    public void AtThirtyDaysRetentionYearDisappearsFromTheChoicesButAllStays()
    {
        var (vm, _) = Build();

        vm.HistoryRetentionDays = 30;

        Assert.DoesNotContain(vm.ChartRangeChoices, c => c.Value == "Year");
        Assert.Contains(vm.ChartRangeChoices, c => c.Value == "All");
        Assert.Contains(vm.ChartRangeChoices, c => c.Value == "Month");
    }

    [Fact]
    public void AutostartReflectsTheInjectedStateNotAppSettings()
    {
        var settings = new AppSettings { Autostart = false };
        var (vm, _) = Build(settings, fakeAutostartEnabled: true);

        Assert.True(vm.Autostart); // the fake registry says enabled, AppSettings.Autostart says false
    }

    [Fact]
    public void TogglingAutostartCallsEnableAndDisableInsteadOfTouchingAppSettingsAlone()
    {
        bool? lastCall = null;
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settings = new AppSettings();
        var settingsStore = new SettingsStore(TempDirectory());
        var main = new MainViewModel(settingsStore, settings, providers, new HistoryStore(TempDirectory(), () => Now));
        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => { lastCall = true; return true; },
            disableAutostart: () => { lastCall = false; return true; },
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture);

        vm.Autostart = true;
        Assert.True(lastCall);

        vm.Autostart = false;
        Assert.False(lastCall);
    }

    [Fact]
    public void HidingThroughSettingsHasTheSameEffectAsTheEyeMenu()
    {
        var (vm, settings) = Build();

        vm.Main.ToggleHiddenCommand.Execute("codex");

        Assert.True(vm.Main.Tiles.Single(t => t.ProviderId == "codex").IsHidden);
        Assert.False(settings.Providers["codex"].Visible);
    }

    [Theory]
    [InlineData(AppTheme.Nebula, "Nebula.xaml")]
    [InlineData(AppTheme.Terminal, "Terminal.xaml")]
    [InlineData(AppTheme.Dark, "Dark.xaml")]
    [InlineData(AppTheme.Light, "Light.xaml")]
    public void ThemeSwatchColoursMatchTheThemeFileTheyPreviewInsteadOfADuplicatedLiteral(AppTheme theme, string themeFileName)
    {
        var (vm, _) = Build();
        // Read straight from the shipped theme file, independently of whatever SettingsViewModel
        // itself did - this is the ground truth a hardcoded hex literal could silently drift from.
        var expected = LoadThemeFixture(new Uri(themeFileName, UriKind.Relative));
        var expectedBg = ((SolidColorBrush)expected["Bg.Base"]).Color;
        var expectedAccent = ((SolidColorBrush)expected["Accent"]).Color;

        var choice = vm.ThemeChoices.Single(c => c.Value == theme);

        Assert.Equal(expectedBg, ((SolidColorBrush)choice.BgPreview!).Color);
        Assert.Equal(expectedAccent, ((SolidColorBrush)choice.AccentPreview!).Color);
    }

    [Fact]
    public void SystemThemeIsTheFirstChoice()
    {
        var (vm, _) = Build();

        Assert.Equal(AppTheme.System, vm.ThemeChoices[0].Value);
    }

    [Fact]
    public void SetLanguageCommandOnTheProductionConstructorActuallyDrivesTheRealLocalizationService()
    {
        // The only test that uses the public 3-arg constructor (every other test above uses the
        // internal test-seam ctor with a no-op applyLanguage, deliberately never touching the real
        // shared LocalizationService.Instance). This one proves the wiring itself is correct - that
        // SetLanguageCommand really reaches LocalizationService.Instance.SetLanguage, not just the
        // injected fake - then restores German so no other test in the run sees English afterward.
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settings = new AppSettings();
        var settingsStore = new SettingsStore(TempDirectory());
        var main = new MainViewModel(settingsStore, settings, providers, new HistoryStore(TempDirectory(), () => Now));
        var vm = new SettingsViewModel(main, settings, settingsStore);

        try
        {
            vm.SetLanguageCommand.Execute("en");
            Assert.Equal("Display", LocalizationService.Instance["Settings.Section.Display"]);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    /// <summary>Every getter-only property this view model computes from a localized resource
    /// string must re-announce itself the instant the language changes, or it keeps showing the old
    /// language until something else happens to touch it (a slider move, a reopen). Uses the
    /// production constructor (same reasoning and the same restore-in-finally as the test above) so
    /// SetLanguageCommand really drives LocalizationService.Instance's change notification.</summary>
    [Fact]
    public async Task ChangingTheLanguageRaisesPropertyChangedForEveryLocalizedLabel()
    {
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settings = new AppSettings();
        var settingsStore = new SettingsStore(TempDirectory());
        var main = new MainViewModel(settingsStore, settings, providers, new HistoryStore(TempDirectory(), () => Now));
        var vm = new SettingsViewModel(main, settings, settingsStore);
        await vm.HistorySizeLoadTask; // so HistorySizeText already has a byte count to re-format.

        var raised = new HashSet<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                raised.Add(e.PropertyName);
        };

        try
        {
            vm.SetLanguageCommand.Execute("en");

            Assert.Contains(nameof(SettingsViewModel.RefreshSecondsLabel), raised);
            Assert.Contains(nameof(SettingsViewModel.RetentionLabel), raised);
            Assert.Contains(nameof(SettingsViewModel.RetentionFullLabel), raised);
            Assert.Contains(nameof(SettingsViewModel.RemoteIntervalLabelText), raised);
            Assert.Contains(nameof(SettingsViewModel.WindowOpacityLabel), raised);
            Assert.Contains(nameof(SettingsViewModel.DefaultThresholdLabelText), raised);
            Assert.Contains(nameof(SettingsViewModel.DefaultThresholdEnabledFullLabel), raised);
            Assert.Contains(nameof(SettingsViewModel.LevelExplainerText), raised);
            Assert.Contains(nameof(SettingsViewModel.HistorySizeText), raised);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void ResetToDefaultsRestoresEveryKnownSettingAndReappliesThemLive()
    {
        var settings = new AppSettings { RefreshSeconds = 300, HistoryRetentionDays = 30, Language = "en" };
        settings.Providers["claude"].Thresholds.FiveHour = 99;
        var (vm, _) = Build(settings);
        vm.Main.ToggleHiddenCommand.Execute("claude");
        Assert.True(vm.Main.Tiles.Single(t => t.ProviderId == "claude").IsHidden);

        vm.ResetToDefaults();

        Assert.Equal(new AppSettings().RefreshSeconds, settings.RefreshSeconds);
        Assert.Equal(new AppSettings().HistoryRetentionDays, settings.HistoryRetentionDays);
        Assert.Equal(new AppSettings().Language, settings.Language);
        Assert.Equal(85, settings.Providers["claude"].Thresholds.FiveHour);
        Assert.False(vm.Main.Tiles.Single(t => t.ProviderId == "claude").IsHidden);
    }

    // Resetting used to replace the whole provider dictionary, so a signed-out account started
    // being read again without anyone signing it back in.
    [Fact]
    public void ResetToDefaultsKeepsASignedOutAccountSignedOut()
    {
        var settings = new AppSettings();
        settings.Providers["codex"] = new ProviderSettings { Disconnected = true };
        var (vm, _) = Build(settings);

        vm.ResetToDefaults();

        Assert.True(settings.Providers["codex"].Disconnected);
        Assert.True(vm.Main.IsAccountDisconnected("codex"));
        Assert.False(vm.Main.IsAccountDisconnected("claude"));
    }

    // An account added while the window is open used to get no rows, and a reset afterwards threw
    // because the default settings carry no entry for that account.
    [Fact]
    public void AnAccountAddedWhileOpenGetsRowsAndSurvivesAReset()
    {
        var settings = new AppSettings();
        var (vm, _) = Build(settings);
        var before = vm.NotificationRows.Count;

        vm.Main.CreateWebAccount = key => (new FakeProvider("claude", key), new NoopRunner());
        vm.Main.AddAccountCommand.Execute("claude");
        Assert.Equal(before + 1, vm.NotificationRows.Count);
        Assert.Equal(before + 1, vm.ThresholdRows.Count);

        vm.ResetToDefaults();

        Assert.Equal(before + 1, vm.NotificationRows.Count);
        Assert.True(settings.Providers.ContainsKey(vm.Main.Tiles[^1].ProviderId));
        vm.Dispose();
    }

    [Fact]
    public void ResetToDefaultsAlsoResetsTheTrayChoiceAndItsPicker()
    {
        var settings = new AppSettings { TrayProvider = "claude" };
        var (vm, _) = Build(settings);
        Assert.Equal("claude", vm.Main.SelectedTrayProviderChoice?.Value);

        vm.ResetToDefaults();

        Assert.Equal("Auto", settings.TrayProvider);
        Assert.Equal("Auto", settings.TrayWindow);
        Assert.Equal("Auto", vm.Main.SelectedTrayProviderChoice?.Value);
    }

    [Fact]
    public async Task HistorySizeTextIsNonEmptyAfterTheBackgroundLoadCompletes()
    {
        var (vm, _) = Build();

        await vm.HistorySizeLoadTask;

        Assert.NotEqual("", vm.HistorySizeText);
    }

    [Fact]
    public async Task HistorySizeTextStaysEmptyAfterAFaultingRead()
    {
        var settings = new AppSettings();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new FaultingHistoryStore(TempDirectory(), () => Now);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);

        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            historyStore: historyStore);

        // The background load must complete (not fault out of the awaiting task) even though the
        // read it awaits throws.
        await vm.HistorySizeLoadTask;

        Assert.Equal("", vm.HistorySizeText);
    }

    [Fact]
    public async Task HistorySizeTextReportsTheRealSizeWhenTheReadSucceeds()
    {
        var settings = new AppSettings();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        historyStore.Append("codex", WindowKind.FiveHour, 40, resetsAt: null);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);
        var actualBytes = historyStore.TotalHistoryBytes(providers.Select(p => p.Id));
        var expectedSize = actualBytes < 1024 * 1024
            ? string.Format(CultureInfo.CurrentCulture, "{0:0} kB", actualBytes / 1024.0)
            : string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", actualBytes / (double)(1024 * 1024));

        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            historyStore: historyStore);

        await vm.HistorySizeLoadTask;

        Assert.Equal(LocalizationService.Instance.Format("Settings.HistorySize", expectedSize), vm.HistorySizeText);
    }

    /// <summary>Proves <see cref="SettingsViewModel"/>'s background history size load survives a
    /// disk read that throws instead of the best-effort zero <see cref="HistoryStore.TotalHistoryBytes"/>
    /// itself always returns.</summary>
    private sealed class FaultingHistoryStore(string dataDirectory, Func<DateTimeOffset> now) : HistoryStore(dataDirectory, now)
    {
        public override long TotalHistoryBytes(IEnumerable<string> providerIds) =>
            throw new IOException("simulated read failure");
    }

    [Fact]
    public void ExportHistoryWritesACsvFileStartingWithTheHeaderThroughTheFakePathProvider()
    {
        var settings = new AppSettings();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        historyStore.Append("codex", WindowKind.FiveHour, 40, resetsAt: null);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);
        var exportPath = Path.Combine(TempDirectory(), "export.csv");

        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            historyStore: historyStore,
            askForSavePath: _ => exportPath);

        vm.ExportHistoryCommand.Execute(null);

        Assert.True(File.Exists(exportPath));
        Assert.StartsWith("provider,window,timestamp_utc,percent,resets_at_utc,tokens\r\n", File.ReadAllText(exportPath));
    }

    [Fact]
    public void ExportHistoryDoesNothingWhenTheSaveDialogIsCancelled()
    {
        var (vm, _) = Build();
        var exportPath = Path.Combine(TempDirectory(), "never-written.csv");

        // The public seam only exposes a Func<string,string?>; simulate "cancel" the same way the
        // real SaveFileDialog does - a null path back.
        var cancelledVm = new SettingsViewModel(vm.Main, new AppSettings(), new SettingsStore(TempDirectory()),
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            askForSavePath: _ => null);

        cancelledVm.ExportHistoryCommand.Execute(null);

        Assert.False(File.Exists(exportPath));
    }

    [Fact]
    public void ExportHistoryReportsAWriteFailureThroughTheMessageSeam()
    {
        var settings = new AppSettings();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);
        // A path under a directory that does not exist makes File.WriteAllText throw
        // DirectoryNotFoundException, an IOException, without touching the real MessageBox.
        var unwritablePath = Path.Combine(TempDirectory(), "does-not-exist", "export.csv");
        string? shownMessage = null;

        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            historyStore: historyStore,
            askForSavePath: _ => unwritablePath,
            showMessage: message => shownMessage = message);

        vm.ExportHistoryCommand.Execute(null);

        Assert.False(File.Exists(unwritablePath));
        Assert.Equal(LocalizationService.Instance["Settings.ExportFailed"], shownMessage);
    }

    [Fact]
    public void ExportSettingsThenImportRoundTripsEveryNonWindowSetting()
    {
        var sourceSettings = new AppSettings { RefreshSeconds = 300, Language = "en", DefaultThreshold = 77, HistoryRetentionDays = 90 };
        sourceSettings.Providers["claude"].Thresholds.FiveHour = 60;
        var exportPath = Path.Combine(TempDirectory(), "export-settings.json");
        var (sourceVm, _) = Build(sourceSettings, askForSettingsExportPath: _ => exportPath);

        sourceVm.ExportSettingsCommand.Execute(null);
        Assert.True(File.Exists(exportPath));

        var targetSettings = new AppSettings();
        targetSettings.Window.Left = 555; // deliberately not what the exported file carries
        var (targetVm, _) = Build(targetSettings, askForSettingsImportPath: () => exportPath);

        targetVm.ImportSettingsCommand.Execute(null);

        Assert.Equal(300, targetSettings.RefreshSeconds);
        Assert.Equal("en", targetSettings.Language);
        Assert.Equal(77, targetSettings.DefaultThreshold);
        Assert.Equal(90, targetSettings.HistoryRetentionDays);
        Assert.Equal(60, targetSettings.Providers["claude"].Thresholds.FiveHour);
        Assert.Equal(555, targetSettings.Window.Left); // window block is never imported
    }

    [Fact]
    public void ResetToDefaultsClearsTheStatsWindowArrangement()
    {
        var settings = new AppSettings { StatsSectionLayout = [new Stats.StatsLayoutRow { Left = ["table"] }] };
        settings.StatsSectionsCollapsed["breakdown"] = true;
        var (vm, _) = Build(settings);

        vm.ResetToDefaults();

        Assert.Null(settings.StatsSectionLayout);
        Assert.Empty(settings.StatsSectionsCollapsed);
    }

    [Fact]
    public void ImportedStatsWindowArrangementIsApplied()
    {
        var sourceSettings = new AppSettings
        {
            StatsSectionLayout = [new Stats.StatsLayoutRow { Left = ["table"], Right = ["figures"] }],
        };
        sourceSettings.StatsSectionsCollapsed["breakdown"] = true;
        var exportPath = Path.Combine(TempDirectory(), "export-stats-layout.json");
        var (sourceVm, _) = Build(sourceSettings, askForSettingsExportPath: _ => exportPath);
        sourceVm.ExportSettingsCommand.Execute(null);

        var (targetVm, targetSettings) = Build(new AppSettings(), askForSettingsImportPath: () => exportPath);
        targetVm.ImportSettingsCommand.Execute(null);

        var row = Assert.Single(targetSettings.StatsSectionLayout!);
        Assert.Equal(["table"], row.Left);
        Assert.Equal(["figures"], row.Right);
        Assert.True(targetSettings.StatsSectionsCollapsed["breakdown"]);
    }

    [Fact]
    public void AnImportedThemeNameInAnyLetterCaseIsAppliedLive()
    {
        var sourceSettings = new AppSettings { Theme = "light" };
        var exportPath = Path.Combine(TempDirectory(), "export-theme.json");
        var (sourceVm, _) = Build(sourceSettings, askForSettingsExportPath: _ => exportPath);
        sourceVm.ExportSettingsCommand.Execute(null);

        var (targetVm, targetSettings) = Build(new AppSettings { Theme = "Dark" }, askForSettingsImportPath: () => exportPath);
        targetVm.ImportSettingsCommand.Execute(null);

        Assert.Equal(AppTheme.Light, targetVm.ThemeChoices.Single(c => c.IsSelected).Value);
        Assert.Equal("Light", targetSettings.Theme);
    }

    [Fact]
    public async Task ChooseDataFolderCopiesEveryFileKindToTheNewFolderAndKeepsTheSource()
    {
        var sourceDirectory = TempDirectory();
        File.WriteAllText(Path.Combine(sourceDirectory, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(sourceDirectory, "settings.json.bak"), "{}");
        File.WriteAllText(Path.Combine(sourceDirectory, "history-codex.jsonl"), "1\n");
        File.WriteAllText(Path.Combine(sourceDirectory, "history-claude.jsonl"), "1\n");
        var destinationDirectory = TempDirectory();
        var settings = new AppSettings();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(sourceDirectory);
        var historyStore = new HistoryStore(sourceDirectory, () => Now);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);

        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            historyStore: historyStore,
            showMessage: _ => { },
            askForDataFolder: () => destinationDirectory);

        AppPaths.SetPointerRootOverride(TempDirectory());
        try
        {
            await vm.ChooseDataFolderCommand.ExecuteAsync(null);

            Assert.True(File.Exists(Path.Combine(destinationDirectory, "settings.json")));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "settings.json.bak")));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "history-codex.jsonl")));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "history-claude.jsonl")));
            // Nothing is ever deleted from the old folder.
            Assert.True(File.Exists(Path.Combine(sourceDirectory, "settings.json")));
            Assert.True(File.Exists(Path.Combine(sourceDirectory, "history-codex.jsonl")));
        }
        finally
        {
            AppPaths.ClearOverrideForTests();
            AppPaths.ClearPointerRootOverrideForTests();
        }
    }

    [Fact]
    public async Task ChooseDataFolderReturnsBeforeASlowCopyFinishesAndIsBusyMeanwhile()
    {
        var sourceDirectory = TempDirectory();
        var destinationDirectory = TempDirectory();
        var settings = new AppSettings();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(sourceDirectory);
        var historyStore = new HistoryStore(sourceDirectory, () => Now);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);
        using var copyStarted = new ManualResetEventSlim();
        using var releaseCopy = new ManualResetEventSlim();
        var indexCopyDone = false;
        var smallFilesCalls = 0;
        var smallFilesAfterIndex = false;
        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            historyStore: historyStore,
            showMessage: _ => { },
            askForDataFolder: () => destinationDirectory,
            copyIndex: (_, _) =>
            {
                copyStarted.Set();
                releaseCopy.Wait(TimeSpan.FromSeconds(30));
                indexCopyDone = true;
            },
            copySmallFiles: (_, _) =>
            {
                smallFilesCalls++;
                smallFilesAfterIndex = indexCopyDone;
            });

        AppPaths.SetPointerRootOverride(TempDirectory());
        try
        {
            var running = vm.ChooseDataFolderCommand.ExecuteAsync(null);

            Assert.True(copyStarted.Wait(TimeSpan.FromSeconds(30)));
            Assert.False(running.IsCompleted);
            Assert.True(vm.IsMovingData);
            Assert.False(vm.ChooseDataFolderCommand.CanExecute(null));
            Assert.NotEqual(destinationDirectory, vm.DataFolderPath); // the pointer only switches after the copy
            Assert.Equal(0, smallFilesCalls); // the small files wait for the index, never copied beside it

            releaseCopy.Set();
            await running;

            Assert.Equal(1, smallFilesCalls);
            Assert.True(smallFilesAfterIndex);

            Assert.False(vm.IsMovingData);
            Assert.True(vm.ChooseDataFolderCommand.CanExecute(null));
            Assert.Equal(destinationDirectory, vm.DataFolderPath);
        }
        finally
        {
            releaseCopy.Set();
            AppPaths.ClearOverrideForTests();
            AppPaths.ClearPointerRootOverrideForTests();
        }
    }

    // The token index used to stay behind in the old folder: the next start rebuilt it from whatever
    // session files were left, losing every day their tools had already deleted.
    [Fact]
    public async Task ChooseDataFolderTakesTheTokenIndexAlongAndTheStoreFollowsIt()
    {
        var sourceDirectory = TempDirectory();
        var day = new DateOnly(2026, 9, 1);
        new Stats.StatsStore(sourceDirectory).AddDelta([new Stats.StatsRecord("codex", day, "modelA", "projA", 1234, 0, 0, 0)]);
        File.WriteAllText(Path.Combine(sourceDirectory, "project-colors.json"), "{}");
        var destinationDirectory = TempDirectory();
        var settings = new AppSettings();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(sourceDirectory);
        var historyStore = new HistoryStore(sourceDirectory, () => Now);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);
        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            historyStore: historyStore,
            showMessage: _ => { },
            askForDataFolder: () => destinationDirectory);

        AppPaths.SetOverride(sourceDirectory);
        AppPaths.SetPointerRootOverride(TempDirectory());
        try
        {
            var followingStore = new Stats.StatsStore();

            await vm.ChooseDataFolderCommand.ExecuteAsync(null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            Assert.Equal(1234, new Stats.StatsStore(destinationDirectory).LoadAll().Single().InputTokens);
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "project-colors.json")));
            followingStore.AddDelta([new Stats.StatsRecord("codex", day, "modelA", "projA", 1, 0, 0, 0)]);
            Assert.Equal(1235, new Stats.StatsStore(destinationDirectory).LoadAll().Single().InputTokens);
            Assert.Equal(1234, new Stats.StatsStore(sourceDirectory).LoadAll().Single().InputTokens);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            AppPaths.ClearOverrideForTests();
            AppPaths.ClearPointerRootOverrideForTests();
        }
    }

    [Theory]
    [InlineData("C:\\short\\path", "C:\\short\\path")] // shorter than the limit: unchanged
    [InlineData("", "")]
    public void TrimPathMiddleLeavesAShortPathUnchanged(string path, string expected) =>
        Assert.Equal(expected, SettingsViewModel.TrimPathMiddle(path));

    [Fact]
    public void TrimPathMiddleKeepsStartAndEndAroundAnEllipsis()
    {
        var longPath = "C:\\Users\\someone\\AppData\\Roaming\\AI-Usage\\history-claude.jsonl";

        var trimmed = SettingsViewModel.TrimPathMiddle(longPath, maxLength: 30);

        Assert.Equal(30, trimmed.Length);
        Assert.Contains("...", trimmed);
        Assert.StartsWith(longPath[..5], trimmed);
        Assert.EndsWith(longPath[^5..], trimmed);
    }

    [Fact]
    public void DataFolderToolTipIsNullForAShortPathAndTheFullPathWhenItIsCut()
    {
        var (vm, _) = Build();

        vm.DataFolderPath = @"C:\short\path";
        Assert.Null(vm.DataFolderToolTip);

        var longPath = @"D:\Programs\Data\Apps\AI-Usage\with\a\very\long\profile\folder\name";
        vm.DataFolderPath = longPath;
        Assert.NotEqual(longPath, vm.DataFolderDisplayText);
        Assert.Equal(longPath, vm.DataFolderToolTip);
    }

    [Fact]
    public async Task DataFolderPathExposesAppPathsDataDirectoryAndUpdatesAfterAMove()
    {
        var sourceDirectory = TempDirectory();
        var destinationDirectory = TempDirectory();
        var settings = new AppSettings();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(sourceDirectory);
        var historyStore = new HistoryStore(sourceDirectory, () => Now);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);

        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            historyStore: historyStore,
            showMessage: _ => { },
            askForDataFolder: () => destinationDirectory);

        AppPaths.SetPointerRootOverride(TempDirectory());
        try
        {
            Assert.Equal(AppPaths.DataDirectory, vm.DataFolderPath);

            await vm.ChooseDataFolderCommand.ExecuteAsync(null);

            Assert.Equal(destinationDirectory, vm.DataFolderPath);
            Assert.Equal(AppPaths.DataDirectory, vm.DataFolderPath);
        }
        finally
        {
            AppPaths.ClearOverrideForTests();
            AppPaths.ClearPointerRootOverrideForTests();
        }
    }

    [Fact]
    public async Task ChooseDataFolderWritesThePointerUnderTheFakeRootAndNeverTheRealOne()
    {
        var realPointerFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AI-Usage", "location.txt");
        var realPointerExistedBefore = File.Exists(realPointerFile);
        var realPointerWriteTimeBefore = realPointerExistedBefore ? File.GetLastWriteTimeUtc(realPointerFile) : default;
        var sourceDirectory = TempDirectory();
        File.WriteAllText(Path.Combine(sourceDirectory, "settings.json"), "{}");
        var destinationDirectory = TempDirectory();
        var fakePointerRoot = TempDirectory();
        var settings = new AppSettings();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(sourceDirectory);
        var historyStore = new HistoryStore(sourceDirectory, () => Now);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);

        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            historyStore: historyStore,
            showMessage: _ => { },
            askForDataFolder: () => destinationDirectory);

        AppPaths.SetPointerRootOverride(fakePointerRoot);
        try
        {
            await vm.ChooseDataFolderCommand.ExecuteAsync(null);

            var fakePointerFile = Path.Combine(fakePointerRoot, "AI-Usage", "location.txt");
            Assert.True(File.Exists(fakePointerFile));
            Assert.Equal(destinationDirectory, File.ReadAllText(fakePointerFile));
        }
        finally
        {
            AppPaths.ClearOverrideForTests();
            AppPaths.ClearPointerRootOverrideForTests();
        }

        // The real pointer file is exactly as before the run: either still absent, or (if some
        // earlier real move left one behind) not rewritten, proving nothing was ever written under
        // the real ApplicationData folder while the seam redirected this run.
        Assert.Equal(realPointerExistedBefore, File.Exists(realPointerFile));
        if (realPointerExistedBefore)
            Assert.Equal(realPointerWriteTimeBefore, File.GetLastWriteTimeUtc(realPointerFile));
    }

    [Fact]
    public void ImportSettingsLeavesEverythingUnchangedWhenTheFileIsNotUsable()
    {
        var targetSettings = new AppSettings { RefreshSeconds = 123 };
        var badPath = Path.Combine(TempDirectory(), "not-settings.json");
        File.WriteAllText(badPath, "{not valid json");
        var (targetVm, _) = Build(targetSettings, askForSettingsImportPath: () => badPath);

        targetVm.ImportSettingsCommand.Execute(null);

        Assert.Equal(123, targetSettings.RefreshSeconds);
    }

    [Fact]
    public void ResetToDefaultsKeepsHistoryUnlessAlsoDeleteHistoryIsTrue()
    {
        var settings = new AppSettings();
        var providers = new List<FakeProvider> { new("codex"), new("claude"), new("gemini"), new("copilot") };
        var settingsStore = new SettingsStore(TempDirectory());
        var historyStore = new HistoryStore(TempDirectory(), () => Now);
        historyStore.Append("codex", WindowKind.FiveHour, 40, resetsAt: null);
        var main = new MainViewModel(settingsStore, settings, providers, historyStore);
        var vm = new SettingsViewModel(main, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadThemeFixture,
            historyStore: historyStore);

        vm.ResetToDefaults(alsoDeleteHistory: false);
        Assert.NotEmpty(historyStore.Load("codex", TimeSpan.FromDays(1)));

        vm.ResetToDefaults(alsoDeleteHistory: true);
        Assert.Empty(historyStore.Load("codex", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void SaveSettingsWidthUpdatesAndPersistsTheSettingsWindowWidth()
    {
        var (vm, settings) = Build();

        vm.SaveSettingsWidth(600);

        Assert.Equal(600, vm.SettingsWidth);
        Assert.Equal(600, settings.Window.SettingsWidth);
    }

    [Fact]
    public void SetLayoutCommandSwitchesTheArrangementAndPersistsIt()
    {
        var settings = new AppSettings { Layout = "Vertical" };
        var (vm, _) = Build(settings);

        vm.SetLayoutCommand.Execute("Horizontal");

        Assert.Equal("Horizontal", vm.Main.Layout);
        Assert.Equal("Horizontal", settings.Layout);
    }

    // The Anzeige card's layout, chart-range and language pickers are ComboBoxes bound to these
    // three properties, never the choice collections' own IsSelected flags directly (see
    // SettingsWindow.xaml) - each proves the ComboBox drives the exact same setting its old
    // RadioButton group's command did, so the picker and a settings-driven change can never disagree.
    [Fact]
    public void SelectedLayoutChoiceAppliesTheSameChangeAsTheLayoutCommand()
    {
        var settings = new AppSettings { Layout = "Vertical" };
        var (vm, _) = Build(settings);

        vm.SelectedLayoutChoice = vm.LayoutChoices.Single(c => c.Value == "Horizontal");

        Assert.Equal("Horizontal", vm.Main.Layout);
        Assert.Equal("Horizontal", settings.Layout);
        Assert.Same(vm.LayoutChoices.Single(c => c.Value == "Horizontal"), vm.SelectedLayoutChoice);
    }

    [Fact]
    public void SelectedChartRangeChoiceAppliesTheSameChangeAsTheChartRangeCommand()
    {
        var settings = new AppSettings { HistoryRetentionDays = 3650, ChartRange = "Day" };
        var (vm, _) = Build(settings);

        vm.SelectedChartRangeChoice = vm.ChartRangeChoices.Single(c => c.Value == "Month");

        Assert.Equal("Month", settings.ChartRange);
        Assert.Same(vm.ChartRangeChoices.Single(c => c.Value == "Month"), vm.SelectedChartRangeChoice);
    }

    [Fact]
    public void SelectedLanguageChoiceAppliesTheSameChangeAsTheLanguageCommand()
    {
        var settings = new AppSettings { Language = "en" };
        var (vm, _) = Build(settings);

        vm.SelectedLanguageChoice = vm.LanguageChoices.Single(c => c.Value == "de");

        Assert.Equal("de", settings.Language);
        Assert.Same(vm.LanguageChoices.Single(c => c.Value == "de"), vm.SelectedLanguageChoice);
    }

    [Fact]
    public async Task CheckForUpdateAsyncNeverCallsTheFetcherTwiceInsideOneDay()
    {
        // CheckForUpdateAsync reads the real clock (DateTimeOffset.UtcNow), never the fixed
        // Now this file uses elsewhere - so "recently" here must be relative to that real clock too.
        var settings = new AppSettings { CheckForUpdates = true, LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(-1) };
        var callCount = 0;
        var (vm, _) = Build(settings, fetchLatestRelease: _ =>
        {
            callCount++;
            return Task.FromResult<UpdateCheck.Release?>(new UpdateCheck.Release("v99.0", "https://example.invalid/release"));
        });

        var result = await vm.CheckForUpdateAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public void DefaultCategoryIsTheFirst()
    {
        var (vm, _) = Build();

        Assert.Equal("Display", vm.SelectedCategory);
        Assert.Equal("Display", vm.Categories[0].Value);
    }

    [Fact]
    public void SwitchingCategoryChangesTheShownContentKey()
    {
        var (vm, _) = Build();

        vm.SelectedCategory = "System";

        Assert.Equal("System", vm.SelectedCategory);
    }

    [Fact]
    public void ShowAttentionMarkPersistsAndClearsAnAlreadyMarkedTileImmediately()
    {
        var (vm, settings) = Build();
        var tile = vm.Main.Tiles.Single(t => t.ProviderId == "claude");
        tile.Apply(
            new ProviderSnapshot("claude", [], null, SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null,
                IsWaitingForUser: true, WaitingSince: Now),
            Now, showAttentionMark: true);
        Assert.True(tile.IsWaitingForUser);

        vm.ShowAttentionMark = false;

        Assert.False(settings.ShowAttentionMark);
        Assert.False(tile.IsWaitingForUser);
    }

    [Fact]
    public void ChoosingOffSwitchesTheMarkOffAndAnHourCountSwitchesItBackOn()
    {
        var (vm, settings) = Build();
        var tile = vm.Main.Tiles.Single(t => t.ProviderId == "claude");
        tile.Apply(
            new ProviderSnapshot("claude", [], null, SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null,
                IsWaitingForUser: true, WaitingSince: Now),
            Now, showAttentionMark: true);
        Assert.True(tile.IsWaitingForUser);

        vm.SelectedAttentionMaxAgeChoice = vm.AttentionMaxAgeChoices.Single(c => c.Value == 0);

        Assert.False(settings.ShowAttentionMark);
        Assert.False(tile.IsWaitingForUser);
        Assert.Equal(120, settings.AttentionMaxAgeMinutes);
        Assert.Equal(0, vm.SelectedAttentionMaxAgeChoice?.Value);

        // The stored 2 hours is picked again while the mark is off: it must switch the mark back on.
        vm.SelectedAttentionMaxAgeChoice = vm.AttentionMaxAgeChoices.Single(c => c.Value == 2);

        Assert.True(settings.ShowAttentionMark);
        Assert.Equal(120, settings.AttentionMaxAgeMinutes);
        Assert.Equal(2, vm.SelectedAttentionMaxAgeChoice?.Value);

        vm.SelectedAttentionMaxAgeChoice = vm.AttentionMaxAgeChoices.Single(c => c.Value == 4);
        Assert.True(settings.ShowAttentionMark);
        Assert.Equal(240, settings.AttentionMaxAgeMinutes);
    }

    [Fact]
    public void LoadingWithTheMarkOffSelectsOff()
    {
        var (vm, _) = Build(new AppSettings { ShowAttentionMark = false });

        Assert.Equal(0, vm.SelectedAttentionMaxAgeChoice?.Value);
    }

    /// <summary>The Meldungen dropdown's own values are Off and hours (1, 2, 4, 8, 24); the persisted setting
    /// is minutes (AttentionDetector compares it against a session's own elapsed minutes) - this
    /// proves the round trip between the two, and that the clamp underneath the picker still holds
    /// for a value the dropdown itself could never produce.</summary>
    [Fact]
    public void AttentionMaxAgeChoiceRoundTripsThroughAppSettingsAndIsClamped()
    {
        var (vm, settings) = Build();

        vm.SelectedAttentionMaxAgeChoice = vm.AttentionMaxAgeChoices.Single(c => c.Value == 8);

        Assert.Equal(8 * 60, settings.AttentionMaxAgeMinutes);
        Assert.Same(vm.AttentionMaxAgeChoices.Single(c => c.Value == 8), vm.SelectedAttentionMaxAgeChoice);

        Assert.Equal(SettingsRanges.MaxAttentionMaxAgeMinutes, SettingsRanges.ClampAttentionMaxAgeMinutes(999_999));
        Assert.Equal(SettingsRanges.MinAttentionMaxAgeMinutes, SettingsRanges.ClampAttentionMaxAgeMinutes(0));
    }

    /// <summary>The class handler App.xaml.cs registers on the tooltip-opening event delegates its
    /// whole decision to this one static method (see App.OnStartup) - proving the decision itself
    /// here does not need a live Application, which the test project has none of.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ToolTipOpeningHandler_marksTheEventHandledOnlyWhileTheSettingIsOff(bool showTooltips, bool expectHandled)
    {
        var settings = new AppSettings { ShowTooltips = showTooltips };
        var args = new RoutedEventArgs(ToolTipService.ToolTipOpeningEvent);

        AiUsage.App.HandleToolTipOpening(settings, args);

        Assert.Equal(expectHandled, args.Handled);
    }

    /// <summary>Every button in the settings window must be discoverable without sight or without a
    /// mouse: either it carries a visible Content label (WPF's own ButtonAutomationPeer already reads
    /// that as the accessible name, and a visible label is its own hover-discoverable "what is
    /// this"), or, for an icon-only button, an explicit ToolTip or AutomationProperties.Name. Scans
    /// the markup as text for the same reason ProvidersSectionOffersSignInAndSignOutBoundToTileVisibility
    /// does - SettingsWindow cannot be unit-instantiated.</summary>
    [Fact]
    public void EveryButtonInSettingsWindowCarriesATooltipOrAnAutomationName()
    {
        var repoRoot = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "AiUsage", "Views", "SettingsWindow.xaml"));

        var offenders = new List<string>();
        foreach (Match match in Regex.Matches(xaml, @"<Button\b[^>]*?(?:/>|>)", RegexOptions.Singleline))
        {
            var tag = match.Value;
            var named = tag.Contains("ToolTip=", StringComparison.Ordinal)
                || tag.Contains("AutomationProperties.Name=", StringComparison.Ordinal)
                || tag.Contains("Content=", StringComparison.Ordinal);
            if (!named)
                offenders.Add(tag);
        }

        Assert.True(offenders.Count == 0, "Button with neither a tooltip, an automation name nor a Content label: " + string.Join(" | ", offenders));
    }

    /// <summary>SettingsWindow itself cannot be unit-instantiated (a live WPF Window, same reasoning
    /// as MainWindowCloseGuardTests), so this scans the markup and its code-behind as text: the
    /// Providers section's two buttons exist, their visibility comes straight from the tile's own
    /// ShowSignIn/ShowSignOut rather than a rule of the window's own, and the click handlers drive
    /// the exact same two calls MainWindow.xaml.cs's own tile wiring already uses (see
    /// SignInDescriptorTests for the equivalent proof there) - no second implementation.</summary>
    [Fact]
    public void ProvidersSectionOffersSignInAndSignOutBoundToTileVisibility()
    {
        var repoRoot = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "AiUsage", "Views", "SettingsWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(repoRoot, "src", "AiUsage", "Views", "SettingsWindow.xaml.cs"));

        Assert.Contains("Path=[Settings.SignIn]", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding ShowSignIn, Converter={StaticResource BoolToVis}}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Path=[Settings.SignOut]", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding ShowSignOut, Converter={StaticResource BoolToVis}}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SignIn_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SignOut_Click\"", xaml, StringComparison.Ordinal);

        Assert.Contains("WebSignInFlow.Open(this, ProviderRegistry.WebSessionFor(tile.ProviderId)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_viewModel.Main.TrySignOutAsync(tile.ProviderId)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_viewModel.Main.ShowSignOutIncomplete(tile.ProviderId)", codeBehind, StringComparison.Ordinal);
    }

    // A worktree's own root has a ".git" FILE (pointing at the real repo's .git/worktrees/<name>),
    // not a ".git" directory - checking only Directory.Exists (as elsewhere in this test project)
    // walks straight past a worktree root and finds the main checkout's .git directory instead,
    // silently scanning the wrong copy of the source. Checking either keeps this correct in both.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the repository root (.git) above " + AppContext.BaseDirectory);
    }

    private sealed class NoopRunner : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private (SettingsViewModel Vm, List<string> DeletedProfiles) BuildWithSecondAccount(Action<string>? showMessage = null, string? historyDirectory = null)
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var (vm, _) = Build(settings, providers: [new("codex"), new("claude"), new("claude", "claude#2"), new("gemini"), new("copilot")],
            showMessage: showMessage, historyDirectory: historyDirectory);
        var deleted = new List<string>();
        vm.Main.SignOutWebView = key => { deleted.Add(key); return Web.SignOutResult.Removed; };
        return (vm, deleted);
    }

    [Fact]
    public async Task CancellingTheRemoveAccountDialogDeletesNothing()
    {
        var (vm, deleted) = BuildWithSecondAccount();
        vm.ConfirmRemoveAccount = () => (false, true);

        await vm.RemoveAccountCommand.ExecuteAsync("claude#2");

        Assert.Empty(deleted);
        Assert.Contains(vm.Main.Tiles, t => t.ProviderId == "claude#2");
    }

    [Fact]
    public async Task WithoutAConfirmDialogNoAccountIsRemoved()
    {
        var (vm, deleted) = BuildWithSecondAccount();

        await vm.RemoveAccountCommand.ExecuteAsync("claude#2");

        Assert.Empty(deleted);
        Assert.Contains(vm.Main.Tiles, t => t.ProviderId == "claude#2");
    }

    [Fact]
    public async Task AFailedSignInFolderDeletionKeepsTheAccountAndSaysSo()
    {
        var messages = new List<string>();
        var (vm, _) = BuildWithSecondAccount(messages.Add);
        vm.Main.SignOutWebView = _ => Web.SignOutResult.StillOpen;
        vm.Main.SignOutRetryDelay = TimeSpan.Zero;
        vm.ConfirmRemoveAccount = () => (true, true);

        await vm.RemoveAccountCommand.ExecuteAsync("claude#2");

        Assert.Contains(vm.Main.Tiles, t => t.ProviderId == "claude#2");
        Assert.Equal([LocalizationService.Instance["Settings.RemoveAccount.Failed"]], messages);
    }

    [Fact]
    public async Task AnErrorWhileRemovingAnAccountKeepsItAndSaysSoInsteadOfThrowing()
    {
        var messages = new List<string>();
        var (vm, _) = BuildWithSecondAccount(messages.Add);
        vm.Main.SignOutWebView = _ => throw new InvalidOperationException("browser still shutting down");
        vm.ConfirmRemoveAccount = () => (true, true);

        await vm.RemoveAccountCommand.ExecuteAsync("claude#2");

        Assert.Contains(vm.Main.Tiles, t => t.ProviderId == "claude#2");
        Assert.Equal([LocalizationService.Instance["Settings.RemoveAccount.Failed"]], messages);
    }

    [Fact]
    public async Task AHistoryThatCouldNotBeDeletedIsReportedAfterTheRemoval()
    {
        var messages = new List<string>();
        var historyDir = TempDirectory();
        var historyFile = Path.Combine(historyDir, "history-claude-2.jsonl");
        File.WriteAllText(historyFile, "kept");
        var (vm, _) = BuildWithSecondAccount(messages.Add, historyDir);
        vm.ConfirmRemoveAccount = () => (true, true);

        using (new FileStream(historyFile, FileMode.Open, FileAccess.Read, FileShare.None))
            await vm.RemoveAccountCommand.ExecuteAsync("claude#2");

        Assert.DoesNotContain(vm.Main.Tiles, t => t.ProviderId == "claude#2");
        Assert.Equal([LocalizationService.Instance["Settings.RemoveAccount.HistoryFailed"]], messages);
    }

    [Fact]
    public async Task ConfirmingTheRemoveAccountDialogRemovesTheAccount()
    {
        var (vm, deleted) = BuildWithSecondAccount();
        vm.ConfirmRemoveAccount = () => (true, false);

        await vm.RemoveAccountCommand.ExecuteAsync("claude#2");

        Assert.Equal(["claude#2"], deleted);
        Assert.DoesNotContain(vm.Main.Tiles, t => t.ProviderId == "claude#2");
    }

    private sealed class FakeProvider(string id, string? accountKey = null) : IUsageProvider
    {
        public string Id { get; } = id;
        public string AccountKey { get; } = accountKey ?? id;
        public string DisplayName => Id;

        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) =>
            Task.FromResult(new ProviderSnapshot(Id, [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300)],
                "Plus", SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null));
    }

    [Fact]
    public void Moving_the_data_folder_carries_every_migration_backup_along()
    {
        var source = TempDirectory();
        var destination = TempDirectory();
        foreach (var name in new[] { "stats.v4.bak", "stats.v5.bak", "stats.v6.bak" })
            File.WriteAllText(Path.Combine(source, name), name);
        File.WriteAllText(Path.Combine(source, "stats.v6.bak.tmp"), "half");

        SettingsViewModel.CopyIndexFiles(source, destination);

        foreach (var name in new[] { "stats.v4.bak", "stats.v5.bak", "stats.v6.bak" })
            Assert.Equal(name, File.ReadAllText(Path.Combine(destination, name)));
        Assert.False(File.Exists(Path.Combine(destination, "stats.v6.bak.tmp")));
    }

    // ---- backup, restore and importing another PC ----------------------------------------------

    private const string OtherPc = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string OwnPc = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    private static string Loc(string key) => LocalizationService.Instance[key];

    /// <summary>A data folder with a settings file and an index holding one row of <paramref name="tokens"/>.</summary>
    private string SeededDataFolder(string machineId, long tokens, string machine = "")
    {
        var directory = TempDirectory();
        var settings = new AppSettings { MachineId = machineId };
        File.WriteAllText(Path.Combine(directory, "settings.json"), System.Text.Json.JsonSerializer.Serialize(settings, SettingsStore.JsonOptions));
        new Stats.StatsStore(directory).AddDelta([new Stats.StatsRecord("claude", new DateOnly(2026, 5, 1), "modelA", "projA", tokens, 0, 0, 0, Machine: machine)]);
        IndexPools.Release(directory);
        return directory;
    }

    [Fact]
    public async Task Creating_a_backup_saves_one_zip_and_says_so()
    {
        var data = SeededDataFolder(OtherPc, 10);
        var zip = Path.Combine(TempDirectory(), "b.zip");
        var messages = new List<string>();
        var (vm, settings) = Build(new AppSettings { MachineId = OwnPc }, dataDirectory: data,
            askForBackupSavePath: name => { Assert.Matches(@"^ai-usage-backup-\d{4}-\d{2}-\d{2}\.zip$", name); return zip; }, showMessage: messages.Add);

        await vm.CreateBackupCommand.ExecuteAsync(null);
        IndexPools.Release(data);

        var inspection = BackupService.Inspect(zip, TempDirectory());
        Assert.True(inspection.IsAccepted);
        Assert.Equal(settings.MachineId, inspection.Manifest!.MachineId);
        Assert.Equal([Loc("Settings.Backup.Saved")], messages);
        Assert.False(vm.IsBackupBusy);
    }

    [Fact]
    public async Task Cancelling_the_backup_dialogs_does_nothing()
    {
        var messages = new List<string>();
        var restarted = false;
        var (vm, _) = Build(showMessage: messages.Add, restartApp: () => restarted = true);

        await vm.CreateBackupCommand.ExecuteAsync(null);
        await vm.RestoreBackupCommand.ExecuteAsync(null);
        await vm.ImportPcCommand.ExecuteAsync(null);

        Assert.Empty(messages);
        Assert.False(restarted);
    }

    [Fact]
    public async Task Restoring_a_file_that_is_not_a_backup_is_refused_and_asks_for_nothing()
    {
        var data = SeededDataFolder(OtherPc, 10);
        var junk = Path.Combine(TempDirectory(), "notes.zip");
        File.WriteAllText(junk, "not a zip");
        var messages = new List<string>();
        var asked = false;
        var restarted = false;
        var (vm, _) = Build(dataDirectory: data, askForBackupOpenPath: () => junk, showMessage: messages.Add,
            confirmRestore: _ => asked = true, restartApp: () => restarted = true);

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal([Loc("Settings.Backup.Refused")], messages);
        Assert.False(asked);
        Assert.False(restarted);
        Assert.False(File.Exists(Path.Combine(data, "restore-pending.txt")));
    }

    [Fact]
    public async Task A_confirmed_restore_writes_the_request_and_restarts_and_a_declined_one_does_neither()
    {
        var data = SeededDataFolder(OtherPc, 10);
        var zip = Path.Combine(TempDirectory(), "b.zip");
        BackupService.Create(SeededDataFolder(OtherPc, 99), zip, "1", OtherPc, "PC", DateTimeOffset.UtcNow, TempDirectory());
        var confirm = false;
        var restarts = 0;
        string? offered = null;
        var (vm, _) = Build(dataDirectory: data, askForBackupOpenPath: () => zip,
            confirmRestore: folder => { offered = folder; return confirm; }, restartApp: () => { restarts++; return true; });

        await vm.RestoreBackupCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(data, "restore-pending.txt")));
        Assert.Equal(0, restarts);

        confirm = true;
        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal(1, restarts);
        // The dialog names the folder the previous data goes to; the request carries the same name.
        Assert.Equal(data, Path.GetDirectoryName(offered));
        Assert.StartsWith("before-restore-", Path.GetFileName(offered), StringComparison.Ordinal);
        Assert.Equal(zip + "\n" + Path.GetFileName(offered), File.ReadAllText(Path.Combine(data, "restore-pending.txt")));
        // Nothing in the data folder is touched by the running process: the next start does the swap.
        Assert.Equal(10, new Stats.StatsStore(data).LoadAll().Single().InputTokens);
        IndexPools.Release(data);
    }

    [Fact]
    public async Task A_restart_that_fails_takes_the_restore_request_back()
    {
        var data = SeededDataFolder(OtherPc, 10);
        var zip = Path.Combine(TempDirectory(), "b.zip");
        BackupService.Create(SeededDataFolder(OtherPc, 99), zip, "1", OtherPc, "PC", DateTimeOffset.UtcNow, TempDirectory());
        var messages = new List<string>();
        var (vm, _) = Build(dataDirectory: data, askForBackupOpenPath: () => zip, confirmRestore: _ => true, restartApp: () => false, showMessage: messages.Add);

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.False(File.Exists(Path.Combine(data, "restore-pending.txt")));
        Assert.Single(messages);
    }

    [Fact]
    public async Task Importing_another_PCs_backup_files_its_history_once_lists_it_and_can_remove_it()
    {
        var data = SeededDataFolder(OwnPc, 1000);
        var zip = Path.Combine(TempDirectory(), "other.zip");
        BackupService.Create(SeededDataFolder(OtherPc, 77), zip, "1", OtherPc, "Work PC", DateTimeOffset.UtcNow, TempDirectory());
        var messages = new List<string>();
        var stats = new Stats.StatsStore(data);
        var (vm, settings) = Build(new AppSettings { MachineId = OwnPc }, dataDirectory: data, askForBackupOpenPath: () => zip,
            showMessage: messages.Add, statsStore: stats);

        await vm.ImportPcCommand.ExecuteAsync(null);
        await vm.ImportPcCommand.ExecuteAsync(null); // the same PC again

        var expected = LocalizationService.Instance.Format("Settings.Backup.Imported", 1, "Work PC");
        Assert.Equal([expected, expected], messages);
        var imported = Assert.Single(settings.ImportedMachines);
        Assert.Equal((OtherPc, "Work PC"), (imported.Id, imported.Name));
        Assert.Equal(new ImportedPcRow(OtherPc, "Work PC"), Assert.Single(vm.ImportedPcs));
        Assert.True(vm.HasImportedPcs);
        Assert.Equal(77, stats.LoadAll().Where(r => r.Machine == OtherPc).Sum(r => r.TotalTokens));
        Assert.Equal(1000, stats.LoadAll().Where(r => r.Machine == "").Sum(r => r.TotalTokens));

        vm.RemoveImportedPcCommand.Execute(OtherPc);

        Assert.Empty(settings.ImportedMachines);
        Assert.Empty(vm.ImportedPcs);
        Assert.False(vm.HasImportedPcs);
        Assert.DoesNotContain(stats.LoadAll(), r => r.Machine == OtherPc);
        Assert.Equal(1000, stats.LoadAll().Where(r => r.Machine == "").Sum(r => r.TotalTokens));
        IndexPools.Release(data);
    }

    [Fact]
    public async Task A_backup_from_this_PC_is_not_imported()
    {
        var data = SeededDataFolder(OwnPc, 1000);
        var zip = Path.Combine(TempDirectory(), "mine.zip");
        BackupService.Create(data, zip, "1", OwnPc, "This PC", DateTimeOffset.UtcNow, TempDirectory());
        var messages = new List<string>();
        var stats = new Stats.StatsStore(data);
        var (vm, settings) = Build(new AppSettings { MachineId = OwnPc }, dataDirectory: data, askForBackupOpenPath: () => zip,
            showMessage: messages.Add, statsStore: stats);

        await vm.ImportPcCommand.ExecuteAsync(null);

        Assert.Equal([Loc("Settings.Backup.SamePc")], messages);
        Assert.Empty(settings.ImportedMachines);
        Assert.Equal(1000, stats.LoadAll().Single().InputTokens);
        IndexPools.Release(data);
    }

    [Fact]
    public void Every_label_in_the_index_is_listed_with_a_fallback_name_and_can_be_removed_while_an_unlisted_one_changes_nothing()
    {
        var data = SeededDataFolder(OwnPc, 1000, machine: OtherPc);
        var stats = new Stats.StatsStore(data);
        var (vm, _) = Build(dataDirectory: data, statsStore: stats);

        Assert.Equal(new ImportedPcRow(OtherPc, Loc("Settings.Machine.Unknown")), Assert.Single(vm.ImportedPcs));

        vm.RemoveImportedPcCommand.Execute("not-listed");
        vm.RemoveImportedPcCommand.Execute("");
        vm.RemoveImportedPcCommand.Execute(null);
        Assert.Equal(1000, stats.LoadAll().Single().InputTokens);

        vm.RemoveImportedPcCommand.Execute(OtherPc);

        Assert.Empty(vm.ImportedPcs);
        Assert.Empty(stats.LoadAll());
        IndexPools.Release(data);
    }

    [Fact]
    public async Task A_backup_is_refused_when_its_destination_lies_in_the_data_folder()
    {
        var data = SeededDataFolder(OwnPc, 1000);
        var messages = new List<string>();
        var (vm, _) = Build(new AppSettings { MachineId = OwnPc }, dataDirectory: data,
            askForBackupSavePath: _ => Path.Combine(data, "settings.json"), showMessage: messages.Add);

        await vm.CreateBackupCommand.ExecuteAsync(null);

        Assert.Equal([Loc("Settings.ExportFailed")], messages);
        Assert.Equal((byte)'{', File.ReadAllBytes(Path.Combine(data, "settings.json"))[0]); // still the settings, not a zip
    }

    [Fact]
    public async Task A_backup_from_this_PC_is_recognised_whatever_the_letter_case_of_its_id()
    {
        var data = SeededDataFolder(OwnPc, 1000);
        var zip = Path.Combine(TempDirectory(), "mine.zip");
        BackupService.Create(data, zip, "1", OwnPc, "This PC", DateTimeOffset.UtcNow, TempDirectory());
        var messages = new List<string>();
        var stats = new Stats.StatsStore(data);
        var (vm, settings) = Build(new AppSettings { MachineId = OwnPc.ToUpperInvariant() }, dataDirectory: data, askForBackupOpenPath: () => zip,
            showMessage: messages.Add, statsStore: stats);

        await vm.ImportPcCommand.ExecuteAsync(null);

        Assert.Equal([Loc("Settings.Backup.SamePc")], messages);
        Assert.Empty(settings.ImportedMachines);
        IndexPools.Release(data);
    }

    [Fact]
    public void A_reset_never_changes_this_PCs_id_or_the_imported_list()
    {
        var settings = new AppSettings { MachineId = OwnPc, ImportedMachines = [new() { Id = OtherPc, Name = "Work PC" }] };
        var (vm, _) = Build(settings);

        vm.ResetToDefaults();

        Assert.Equal(OwnPc, settings.MachineId);
        Assert.Equal(OtherPc, Assert.Single(settings.ImportedMachines).Id);
    }
}
