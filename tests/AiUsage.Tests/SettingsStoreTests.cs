using System.Text.Json;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;

namespace AiUsage.Tests;

public class SettingsStoreTests : IDisposable
{
    [Fact]
    public void Load_renames_the_old_Copilot_window_labels_in_hidden_windows_and_the_tray_choice()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);
        var original = new AppSettings { TrayWindow = "Label:Premium" };
        original.Providers["copilot"].HiddenWindows = ["Chat", "Completions", "Window_CopilotPremium"];
        original.Providers["codex"].HiddenWindows = ["Chat"];
        store.SaveNow(original);

        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.Equal(["Window_CopilotChat", "Window_CopilotCompletions", "Window_CopilotPremium"], loaded.Providers["copilot"].HiddenWindows);
        Assert.Equal("Label:Window_CopilotPremium", loaded.TrayWindow);
        // Only Copilot's own labels are renamed.
        Assert.Equal(["Chat"], loaded.Providers["codex"].HiddenWindows);
    }

    [Theory]
    [InlineData("codex", "Label:Chat", "Label:Chat")]
    [InlineData("claude#2", "Label:Premium", "Label:Premium")]
    [InlineData("copilot", "Label:Chat", "Label:Window_CopilotChat")]
    [InlineData("copilot#2", "Label:Chat", "Label:Window_CopilotChat")]
    [InlineData("Auto", "Label:Chat", "Label:Window_CopilotChat")]
    public void Load_renames_a_legacy_tray_label_only_for_Copilot_or_Auto(string trayProvider, string stored, string expected)
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);
        store.SaveNow(new AppSettings { TrayProvider = trayProvider, TrayWindow = stored });

        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.Equal(expected, loaded.TrayWindow);
    }

    [Fact]
    public void Load_returns_defaults_when_no_file_exists()
    {
        using var store = new SettingsStore(TempDirectory());

        var settings = store.Load();

        Assert.Equal(60, settings.RefreshSeconds);
        Assert.Equal(5, settings.Providers.Count);
        // Fresh settings.json, no file to inherit an order from - the five providers land in
        // KnownProviderIds' own order, Cursor behind Codex and ahead of Gemini.
        Assert.Equal(0, settings.Providers["claude"].Order);
        Assert.Equal(1, settings.Providers["codex"].Order);
        Assert.Equal(2, settings.Providers["cursor"].Order);
        Assert.Equal(3, settings.Providers["gemini"].Order);
        Assert.Equal(4, settings.Providers["copilot"].Order);
    }

    [Fact]
    public void SaveNow_then_Load_round_trips_every_field()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);

        var original = new AppSettings
        {
            RefreshSeconds = 30,
            WindowLayer = WindowLayers.Desktop,
            Layout = "Horizontal",
            Theme = "Terminal",
            Language = "de",
            Autostart = true,
            HistoryRetentionDays = 90,
            ChartRange = "Week",
            TileDensity = "Mini",
            WelcomeShown = true,
        };
        original.Window.Left = 250;
        original.Window.Vertical.Height = 480;
        original.Window.Collapsed = true;
        original.Providers["codex"].Visible = false;
        original.Providers["codex"].Thresholds.FiveHour = 70;

        store.SaveNow(original);
        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.Equal(30, loaded.RefreshSeconds);
        Assert.Equal(WindowLayers.Desktop, loaded.WindowLayer);
        Assert.Equal("Horizontal", loaded.Layout);
        Assert.Equal("Terminal", loaded.Theme);
        Assert.Equal("de", loaded.Language);
        Assert.True(loaded.Autostart);
        Assert.Equal(90, loaded.HistoryRetentionDays);
        Assert.Equal("Week", loaded.ChartRange);
        Assert.Equal("Mini", loaded.TileDensity);
        Assert.True(loaded.WelcomeShown);
        Assert.Equal(250, loaded.Window.Left);
        Assert.Equal(480, loaded.Window.Vertical.Height);
        Assert.True(loaded.Window.Collapsed);
        Assert.False(loaded.Providers["codex"].Visible);
        Assert.Equal(70, loaded.Providers["codex"].Thresholds.FiveHour);
    }

    // Settings.SignOut sets this per account; it must survive a restart the same way every other
    // per-provider setting does, or a disconnected account would silently reconnect itself on the
    // next launch.
    [Fact]
    public void SaveNow_then_Load_round_trips_the_disconnected_flag_per_account()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);

        var original = new AppSettings();
        original.Providers["codex"].Disconnected = true;

        store.SaveNow(original);
        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.True(loaded.Providers["codex"].Disconnected);
        Assert.False(loaded.Providers["claude"].Disconnected);
    }

    // The Compact stage was dropped after 1.0.0 shipped, so a settings file that still names it must
    // load as the automatic choice rather than as a stage the window no longer has.
    [Fact]
    public void StoredCompactDensityLoadsAsAutomatic()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);
        store.SaveNow(new AppSettings { TileDensity = "Compact" });

        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.Equal("Auto", loaded.TileDensity);
    }

    [Theory]
    [InlineData("ByUsage", "ByUsage")]
    [InlineData("byusage", "ByUsage")]
    [InlineData("Custom", "Custom")]
    [InlineData("Sideways", "Custom")]
    public void TheTileOrderModeLoadsAsOneOfItsTwoValues(string stored, string expected)
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);
        store.SaveNow(new AppSettings { TileOrderMode = stored });

        using var reloadStore = new SettingsStore(directory);

        Assert.Equal(expected, reloadStore.Load().TileOrderMode);
    }

    [Fact]
    public void AnAccountNameLoadsTrimmedAndCutToTwentyFourCharacters()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);
        var settings = new AppSettings();
        settings.Providers["claude"].AccountName = " " + new string('y', 30);
        store.SaveNow(settings);

        using var reloadStore = new SettingsStore(directory);

        Assert.Equal(new string('y', 24), reloadStore.Load().Providers["claude"].AccountName);
    }

    [Fact]
    public void SaveNow_then_Load_round_trips_StatsSectionsCollapsed()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);

        var original = new AppSettings();
        original.StatsSectionsCollapsed["perday"] = true;
        original.StatsSectionsCollapsed["provider"] = false;

        store.SaveNow(original);
        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.Equal(2, loaded.StatsSectionsCollapsed.Count);
        Assert.True(loaded.StatsSectionsCollapsed["perday"]);
        Assert.False(loaded.StatsSectionsCollapsed["provider"]);
    }

    [Fact]
    public void Load_treats_a_file_written_before_StatsSectionsCollapsed_existed_as_an_empty_dictionary()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"schemaVersion":1,"refreshSeconds":45}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.NotNull(settings.StatsSectionsCollapsed);
        Assert.Empty(settings.StatsSectionsCollapsed);
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("5")]
    [InlineData("{\"left\":[\"figures\"]}")]
    [InlineData("[{\"left\":[7],\"right\":[]}]")]
    [InlineData("[{\"left\":\"figures\"}]")]
    public void Load_reads_a_wrong_shaped_StatsSectionLayout_as_the_default_and_keeps_every_other_setting(string layoutJson)
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            "{\"schemaVersion\":1,\"refreshSeconds\":45,\"statsSectionLayout\":" + layoutJson + "}");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(45, settings.RefreshSeconds);
        Assert.Null(settings.StatsSectionLayout);
        Assert.False(store.LastLoadWasFromNewerVersion);
        Assert.Empty(Directory.GetFiles(directory, "*.corrupt*"));
    }

    [Fact]
    public void SaveNow_then_Load_round_trips_StatsSectionLayout()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);
        var original = new AppSettings { StatsSectionLayout = [new StatsLayoutRow { Left = ["table"], Right = ["figures"] }] };

        store.SaveNow(original);
        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        var row = Assert.Single(loaded.StatsSectionLayout!);
        Assert.Equal(["table"], row.Left);
        Assert.Equal(["figures"], row.Right);
    }

    [Fact]
    public void SaveNow_then_Load_round_trips_separate_vertical_and_horizontal_window_sizes()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);

        var original = new AppSettings();
        original.Window.Vertical.Width = 340;
        original.Window.Vertical.Height = 500;
        original.Window.Vertical.WidthIsManual = true;
        original.Window.Horizontal.Width = 900;
        original.Window.Horizontal.Height = 260;
        original.Window.Horizontal.WidthIsManual = false;

        store.SaveNow(original);
        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.Equal(340, loaded.Window.Vertical.Width);
        Assert.Equal(500, loaded.Window.Vertical.Height);
        Assert.True(loaded.Window.Vertical.WidthIsManual);
        Assert.Equal(900, loaded.Window.Horizontal.Width);
        Assert.Equal(260, loaded.Window.Horizontal.Height);
        Assert.False(loaded.Window.Horizontal.WidthIsManual);
    }

    [Fact]
    public void SaveNow_writes_UTF8_without_a_byte_order_mark()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);

        store.SaveNow(new AppSettings());

        var bytes = File.ReadAllBytes(Path.Combine(directory, "settings.json"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void Load_ignores_a_file_from_a_newer_schema_version_and_sets_the_flag()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"schemaVersion":99,"refreshSeconds":5}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.True(store.LastLoadWasFromNewerVersion);
        Assert.Equal(60, settings.RefreshSeconds); // defaults, the newer file is left untouched
        Assert.True(File.Exists(Path.Combine(directory, "settings.json"))); // not deleted, not overwritten
    }

    [Fact]
    public void Load_of_a_newer_schema_file_yields_the_same_provider_entries_as_the_defaults()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"schemaVersion":99}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(AppSettings.KnownProviderIds.OrderBy(id => id), settings.Providers.Keys.OrderBy(id => id));
        Assert.All(settings.Providers.Values, provider => Assert.True(provider.Order >= 0));
    }

    [Fact]
    public void SaveNow_replaces_a_queued_older_save_so_Dispose_cannot_write_it_back()
    {
        var directory = TempDirectory();
        var store = new SettingsStore(directory, debounceDelay: TimeSpan.FromHours(1));
        var older = new AppSettings { RefreshSeconds = 30 };
        var final = new AppSettings { RefreshSeconds = 120 };

        store.RequestSave(older);
        Assert.True(store.SaveNow(final));
        store.Dispose();

        using var reloadStore = new SettingsStore(directory);
        Assert.Equal(120, reloadStore.Load().RefreshSeconds);
    }

    [Fact]
    public async Task RequestSave_does_not_wait_for_a_file_write_that_is_in_progress()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory, debounceDelay: TimeSpan.FromHours(1));
        using var writeStarted = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        store.RequestSave(new AppSettings()); // warms up the snapshot path so only the gate is measured below
        store.BeforeWrite = () =>
        {
            writeStarted.Set();
            releaseWrite.Wait(TimeSpan.FromSeconds(30));
        };

        var slowWrite = Task.Run(() => store.SaveNow(new AppSettings { RefreshSeconds = 30 }));
        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(10)));

        var request = Task.Run(() => store.RequestSave(new AppSettings { RefreshSeconds = 45 }));
        var finishedInTime = request.Wait(TimeSpan.FromMilliseconds(100));

        releaseWrite.Set();
        Assert.True(await slowWrite);
        Assert.True(finishedInTime, "RequestSave blocked behind the file write");
    }

    [Fact]
    public void Load_ignores_a_backup_from_a_newer_schema_version_when_there_is_no_primary_file_and_sets_the_flag()
    {
        var directory = TempDirectory();
        var primaryPath = Path.Combine(directory, "settings.json");
        var backupPath = Path.Combine(directory, "settings.json.bak");
        File.WriteAllText(backupPath, """{"schemaVersion":99,"refreshSeconds":5}""");
        var backupBefore = File.ReadAllBytes(backupPath);
        var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.True(store.LastLoadWasFromNewerVersion);
        Assert.Equal(60, settings.RefreshSeconds); // defaults, the newer backup is left untouched

        settings.RefreshSeconds = 42;
        store.SaveNow(settings);
        store.Dispose();

        Assert.False(File.Exists(primaryPath)); // the read-only refusal still holds after a save attempt
        Assert.Equal(backupBefore, File.ReadAllBytes(backupPath));
    }

    [Fact]
    public void SaveNow_and_Dispose_never_touch_a_settings_file_from_a_newer_version()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "settings.json");
        var original = """{"schemaVersion":99,"refreshSeconds":5}""";
        File.WriteAllText(path, original);
        var before = File.ReadAllBytes(path);
        var store = new SettingsStore(directory);

        var settings = store.Load();
        settings.RefreshSeconds = 42;
        store.SaveNow(settings);
        store.Dispose();

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void SaveNow_still_saves_a_settings_file_from_the_current_version()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"schemaVersion":1,"refreshSeconds":5}""");
        var store = new SettingsStore(directory);

        var settings = store.Load();
        settings.RefreshSeconds = 42;
        store.SaveNow(settings);
        store.Dispose();

        using var reloadStore = new SettingsStore(directory);
        Assert.Equal(42, reloadStore.Load().RefreshSeconds);
    }

    // A zero or negative wait time used to reach the providers unchanged and mark every snapshot
    // as waiting for the user.
    [Fact]
    public void Load_clamps_the_attention_age_and_the_remote_refresh_into_their_ranges()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"),
            """{"schemaVersion":1,"attentionMaxAgeMinutes":0,"remoteRefreshMinutes":-5}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(SettingsRanges.MinAttentionMaxAgeMinutes, settings.AttentionMaxAgeMinutes);
        Assert.Equal(SettingsRanges.MinRemoteRefreshMinutes, settings.RemoteRefreshMinutes);
    }

    [Theory]
    [InlineData("dark", "Dark")]
    [InlineData("LIGHT", "Light")]
    [InlineData("nonsense", "Nebula")]
    public void Validate_reads_a_theme_name_in_any_letter_case(string stored, string expected)
    {
        var (settings, refusal) = SettingsStore.Validate($$"""{"theme": "{{stored}}"}""");

        Assert.Null(refusal);
        Assert.Equal(expected, settings!.Theme);
    }

    [Fact]
    public void Load_keeps_a_file_it_could_not_read_and_never_saves_over_it()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new AppSettings { RefreshSeconds = 30 }, SettingsStore.JsonOptions));
        var before = File.ReadAllText(path);
        using var store = new SettingsStore(directory);

        AppSettings loaded;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            loaded = store.Load();

        Assert.Equal(60, loaded.RefreshSeconds);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(directory, "settings.json.corrupt-*"));
        Assert.False(store.SaveNow(loaded));
        Assert.Equal(before, File.ReadAllText(path));

        // A later read that succeeds brings the real settings back and saving again.
        var again = store.Load();
        Assert.Equal(30, again.RefreshSeconds);
        Assert.True(store.SaveNow(again));
    }

    [Fact]
    public void A_queued_save_is_a_deep_copy_taken_at_the_time_of_the_request()
    {
        var directory = TempDirectory();
        var settings = new AppSettings();
        settings.Providers["codex"].Visible = true;
        settings.WebUsagePaths["codex"] = "before";
        settings.Window.Vertical.Height = 400;
        settings.Providers["codex"].HiddenWindows.Add("first");
        var store = new SettingsStore(directory, debounceDelay: TimeSpan.FromHours(1));

        store.RequestSave(settings);
        settings.Providers["codex"].Visible = false;
        settings.WebUsagePaths["codex"] = "after";
        settings.WebUsagePaths["extra"] = "after";
        settings.Window.Vertical.Height = 999;
        settings.Providers["codex"].HiddenWindows.Add("second");
        store.Dispose();

        var saved = new SettingsStore(directory).Load();
        Assert.True(saved.Providers["codex"].Visible);
        Assert.Equal("before", saved.WebUsagePaths["codex"]);
        Assert.False(saved.WebUsagePaths.ContainsKey("extra"));
        Assert.Equal(400, saved.Window.Vertical.Height);
        Assert.Equal(["first"], saved.Providers["codex"].HiddenWindows);
    }

    [Fact]
    public void Load_quarantines_a_corrupt_file_and_runs_on_defaults()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{not valid json");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(60, settings.RefreshSeconds);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(directory, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Load_falls_back_to_the_documented_provider_order_when_the_primary_is_corrupt_and_no_backup_exists()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{not valid json");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(0, settings.Providers["claude"].Order);
        Assert.Equal(1, settings.Providers["codex"].Order);
        Assert.Equal(2, settings.Providers["cursor"].Order);
        Assert.Equal(3, settings.Providers["gemini"].Order);
        Assert.Equal(4, settings.Providers["copilot"].Order);
    }

    [Fact]
    public void Load_falls_back_to_the_documented_provider_order_when_both_primary_and_backup_are_corrupt()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{not valid json");
        File.WriteAllText(Path.Combine(directory, "settings.json.bak"), "{also not valid json");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(0, settings.Providers["claude"].Order);
        Assert.Equal(1, settings.Providers["codex"].Order);
        Assert.Equal(2, settings.Providers["cursor"].Order);
        Assert.Equal(3, settings.Providers["gemini"].Order);
        Assert.Equal(4, settings.Providers["copilot"].Order);
    }

    [Fact]
    public void Load_tolerates_a_file_saved_with_a_byte_order_mark()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "settings.json");
        var json = """{"schemaVersion":1,"refreshSeconds":45}""";
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        File.WriteAllBytes(path, [.. bom, .. System.Text.Encoding.UTF8.GetBytes(json)]);
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(45, settings.RefreshSeconds);
        Assert.False(store.LastLoadWasFromNewerVersion); // proves it parsed, not fell back to defaults
    }

    [Fact]
    public void Load_clamps_out_of_range_values_from_a_hand_edited_file()
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            """{"schemaVersion":1,"refreshSeconds":0,"historyRetentionDays":3,"chartRange":"Year"}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(SettingsRanges.MinRefreshSeconds, settings.RefreshSeconds);
        Assert.Equal(7, settings.HistoryRetentionDays); // snapped to the nearest rung
        Assert.Equal("Day", settings.ChartRange); // "Year" is no longer available at 7 days retention
    }

    [Fact]
    public void Load_clamps_a_hand_edited_third_threshold_to_the_minimum()
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            """{"schemaVersion":1,"providers":{"codex":{"visible":true,"thresholds":{"other":0}}}}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(SettingsRanges.MinThresholdPercent, settings.Providers["codex"].Thresholds.Other);
    }

    [Fact]
    public void Load_clamps_a_hand_edited_default_threshold_below_the_minimum()
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            """{"schemaVersion":1,"defaultThreshold":-1}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(SettingsRanges.MinThresholdPercent, settings.DefaultThreshold);
    }

    [Fact]
    public void Load_clamps_a_hand_edited_default_threshold_above_the_maximum()
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            """{"schemaVersion":1,"defaultThreshold":500}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(SettingsRanges.MaxThresholdPercent, settings.DefaultThreshold);
    }

    [Fact]
    public void Load_replaces_an_explicit_null_window_with_defaults()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"schemaVersion":1,"window":null}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.NotNull(settings.Window);
        Assert.Equal(100, settings.Window.Left);
    }

    [Fact]
    public void Load_replaces_explicit_null_collections_with_empty_ones()
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            """{"schemaVersion":1,"copilotAccountLogins":null,"statsSectionsCollapsed":null}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.NotNull(settings.CopilotAccountLogins);
        Assert.Empty(settings.CopilotAccountLogins);
        Assert.NotNull(settings.StatsSectionsCollapsed);
        Assert.Empty(settings.StatsSectionsCollapsed);
    }

    private const string MixedAccountsJson =
        """{"schemaVersion":1,"accounts":{"claude":"claude","codex":"claude","claude#2":"claude","codex#3":"codex","gemini#1":"gemini","claude#x":"claude","ghost":"nope"}}""";

    [Fact]
    public void Validate_drops_account_entries_whose_key_does_not_belong_to_their_provider()
    {
        var dropped = new List<string>();

        var (settings, _) = SettingsStore.Validate(MixedAccountsJson, dropped.Add);

        Assert.Equal(["claude", "claude#2", "codex#3"], settings!.Accounts.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(["claude#x", "codex", "gemini#1", "ghost"], dropped.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void A_colliding_account_key_does_not_give_the_registry_two_providers_with_one_key()
    {
        var (settings, _) = SettingsStore.Validate(MixedAccountsJson);

        var (providers, _) = ProviderRegistry.CreateAll(settings!, new SettingsStore(TempDirectory()));

        var keys = providers.Select(p => p.AccountKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Load_replaces_an_explicit_null_thresholds_with_defaults()
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            """{"schemaVersion":1,"providers":{"codex":{"visible":true,"thresholds":null}}}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.NotNull(settings.Providers["codex"].Thresholds);
        Assert.Equal(85, settings.Providers["codex"].Thresholds.FiveHour);
    }

    [Fact]
    public void Load_replaces_an_explicit_null_provider_entry_instead_of_crashing()
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            """{"schemaVersion":1,"providers":{"codex":null}}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.NotNull(settings.Providers["codex"]);
        Assert.True(settings.Providers["codex"].Visible);
    }

    [Fact]
    public void Load_snaps_an_undefined_theme_value_to_the_default()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"schemaVersion":1,"theme":"999"}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal("Nebula", settings.Theme);
    }

    [Fact]
    public void Load_snaps_an_undefined_tile_density_value_to_auto()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"schemaVersion":1,"tileDensity":"-1"}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal("Auto", settings.TileDensity);
    }

    [Fact]
    public void Load_ignores_the_removed_chart_color_mode_field_of_an_old_settings_file()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"schemaVersion":1,"chartColorMode":"Theme","layout":"Horizontal"}""");
        using var store = new SettingsStore(directory);

        Assert.Equal("Horizontal", store.Load().Layout);
    }

    [Fact]
    public void Load_clamps_a_negative_window_width_to_the_minimum()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"schemaVersion":1,"window":{"vertical":{"width":-5}}}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(WindowPlacementService.MinWidthFor(WindowZoom.MinFactor), settings.Window.Vertical.Width);
    }

    [Fact]
    public void SaveNow_then_Load_round_trips_the_settings_window_width()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);

        var original = new AppSettings();
        original.Window.SettingsWidth = 720;
        store.SaveNow(original);

        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.Equal(720, loaded.Window.SettingsWidth);
    }

    [Fact]
    public void Load_clamps_a_settings_window_width_below_the_minimum()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"schemaVersion":1,"window":{"settingsWidth":100}}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(480, settings.Window.SettingsWidth);
    }

    [Fact]
    public void Load_quarantines_a_window_width_given_as_a_non_numeric_string_and_runs_on_defaults()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """{"schemaVersion":1,"window":{"vertical":{"width":"NaN"}}}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(340, settings.Window.Vertical.Width); // defaults, the type-mismatched file is quarantined
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Load_fills_in_a_provider_missing_from_an_older_file()
    {
        var directory = TempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "settings.json"),
            """{"schemaVersion":1,"providers":{"codex":{"visible":true,"thresholds":{"fiveHour":85,"weekly":85}}}}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(5, settings.Providers.Count);
        Assert.True(settings.Providers.ContainsKey("copilot"));
    }

    [Fact]
    public void SaveNow_never_throws_when_the_target_cannot_be_written()
    {
        // A file used as a "directory" makes every Directory/File call underneath it fail reliably,
        // regardless of the account's actual filesystem permissions.
        var blockingFile = TestPaths.GetPath("ai-usage-not-a-dir");
        File.WriteAllText(blockingFile, "not a directory");
        try
        {
            using var store = new SettingsStore(Path.Combine(blockingFile, "settings-subdir"));

            var exception = Record.Exception(() => store.SaveNow(new AppSettings()));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public void SaveNow_logs_one_line_naming_the_failure_without_the_path_bearing_message()
    {
        // Same "file blocking a directory" trick as SaveNow_never_throws_when_the_target_cannot_be_written
        // above - it reliably throws an IOException whose own message embeds the full failing path
        // (in production, the user's real profile path), the exact case the second catch in SaveNow
        // must log about without repeating that message verbatim.
        var blockingFile = TestPaths.GetPath("ai-usage-not-a-dir");
        File.WriteAllText(blockingFile, "not a directory");
        var dataDirectory = Path.Combine(blockingFile, "settings-subdir");
        var logDirectory = TempDirectory();
        var logService = new LogService(logDirectory, fileName: "test.log");
        try
        {
            using var store = new SettingsStore(dataDirectory, logService: logService);

            var saved = store.SaveNow(new AppSettings());

            Assert.False(saved);
            var logText = File.ReadAllText(logService.CurrentFile);
            var lines = logText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
            Assert.Contains("save did not land", lines[0]);
            Assert.DoesNotContain(blockingFile, lines[0]);
            Assert.DoesNotContain(dataDirectory, lines[0]);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
                Assert.DoesNotContain(userProfile, lines[0], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName, lines[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(blockingFile))
                File.Delete(blockingFile);
        }
    }

    [Fact]
    public void A_failed_flush_keeps_the_pending_snapshot_and_retries_until_the_disk_is_writable_again()
    {
        // A file used as a "directory" makes every Directory/File call underneath it fail reliably,
        // regardless of the account's actual filesystem permissions - same trick as the SaveNow test
        // above, "removable" here so the retry has something to eventually succeed against.
        var blockingFile = TestPaths.GetPath("ai-usage-not-a-dir");
        File.WriteAllText(blockingFile, "not a directory");
        var dataDirectory = Path.Combine(blockingFile, "settings-subdir");
        try
        {
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
            using var store = new SettingsStore(dataDirectory, debounceDelay: TimeSpan.FromMilliseconds(5),
                failedSaveRetryDelay: TimeSpan.FromMilliseconds(30), timeProvider: clock);

            Assert.False(store.SaveNow(new AppSettings()));

            store.RequestSave(new AppSettings { RefreshSeconds = 77 });
            clock.Advance(TimeSpan.FromMilliseconds(10)); // the first flush attempt has failed by now

            // Unblock the directory - if the failed flush above had dropped the pending snapshot
            // (the previous behaviour), nothing would ever land here no matter how long this waits.
            File.Delete(blockingFile);
            clock.Advance(TimeSpan.FromMilliseconds(40)); // the retry interval has passed

            var settingsPath = Path.Combine(dataDirectory, "settings.json");
            Assert.True(File.Exists(settingsPath));
            var landed = SettingsStore.Validate(File.ReadAllText(settingsPath)).Settings;
            Assert.Equal(77, landed!.RefreshSeconds);
        }
        finally
        {
            if (File.Exists(blockingFile))
                File.Delete(blockingFile);
        }
    }

    // The backup is a courtesy copy: a locked or read-only .bak must not cost the real save.
    [Fact]
    public void SaveNow_still_saves_when_the_backup_file_is_locked()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);
        Assert.True(store.SaveNow(new AppSettings { RefreshSeconds = 70 }));
        var settingsPath = Path.Combine(directory, "settings.json");
        var backupPath = settingsPath + ".bak";
        File.WriteAllText(backupPath, "old backup");

        bool saved;
        using (new FileStream(backupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            saved = store.SaveNow(new AppSettings { RefreshSeconds = 80 });

        Assert.True(saved);
        using var reloadStore = new SettingsStore(directory);
        Assert.Equal(80, reloadStore.Load().RefreshSeconds);
    }

    [Fact]
    public void SaveNow_writes_through_a_temp_file_that_carries_this_processs_id()
    {
        var directory = TempDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");
        // The final path is itself a directory, so writing the temp file still succeeds but the
        // closing File.Move fails - leaving the process-id-qualified temp file behind to inspect,
        // the same abandoned-file shape a process killed at that exact moment would leave.
        Directory.CreateDirectory(settingsPath);
        var store = new SettingsStore(directory);

        var saved = store.SaveNow(new AppSettings());

        Assert.False(saved);
        Assert.True(File.Exists($"{settingsPath}.{Environment.ProcessId}.tmp"));
    }

    [Fact]
    public void CleanUpLeftoverTempFiles_deletes_a_tmp_file_older_than_an_hour_but_leaves_a_fresh_one()
    {
        var directory = TempDirectory();
        var oldTemp = Path.Combine(directory, "settings.json.1234.tmp");
        var freshTemp = Path.Combine(directory, "settings.json.5678.tmp");
        File.WriteAllText(oldTemp, "stale");
        File.WriteAllText(freshTemp, "fresh");
        File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow - TimeSpan.FromHours(2));

        AppPaths.CleanUpLeftoverTempFiles(directory);

        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(freshTemp));
    }

    [Fact]
    public void CleanUpLeftoverTempFiles_keeps_an_old_tmp_file_that_is_not_the_apps_own()
    {
        var directory = TempDirectory();
        var foreign = Path.Combine(directory, "report.tmp");
        var ownSettings = Path.Combine(directory, "settings.json.123.tmp");
        var ownHistory = Path.Combine(directory, "history-claude.jsonl.456.tmp");
        var ownNotifications = Path.Combine(directory, "notifications.json.tmp");
        string[] all = [foreign, ownSettings, ownHistory, ownNotifications];
        foreach (var path in all)
        {
            File.WriteAllText(path, "old");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(2));
        }

        AppPaths.CleanUpLeftoverTempFiles(directory);

        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(ownSettings));
        Assert.False(File.Exists(ownHistory));
        Assert.False(File.Exists(ownNotifications));
    }

    [Fact]
    public void RequestSave_with_a_non_finite_number_does_not_throw_and_leaves_valid_json_on_disk()
    {
        var directory = TempDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        using var store = new SettingsStore(directory, debounceDelay: TimeSpan.FromMilliseconds(5), timeProvider: clock);
        Assert.True(store.SaveNow(new AppSettings { RefreshSeconds = 70 }));

        var bad = new AppSettings { RefreshSeconds = 80 };
        bad.Window.Vertical.Width = double.NaN;
        bad.Window.Vertical.Height = double.PositiveInfinity;
        bad.Window.Left = double.NegativeInfinity;

        Assert.Null(Record.Exception(() => store.RequestSave(bad)));
        clock.Advance(TimeSpan.FromMilliseconds(10));

        var json = File.ReadAllText(Path.Combine(directory, "settings.json"));
        using (JsonDocument.Parse(json)) { }
        var landed = SettingsStore.Validate(json).Settings;
        Assert.NotNull(landed);
        Assert.Equal(80, landed!.RefreshSeconds);
        Assert.Equal(new LayoutSizes().Width, landed.Window.Vertical.Width);
        Assert.Null(landed.Window.Vertical.Height);
        Assert.Equal(new WindowSettings().Left, landed.Window.Left);
    }

    [Fact]
    public void RequestSave_debounces_a_burst_of_changes_into_one_write()
    {
        var directory = TempDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        using var store = new SettingsStore(directory, debounceDelay: TimeSpan.FromMilliseconds(30), timeProvider: clock);

        for (var i = 0; i < 5; i++)
            store.RequestSave(new AppSettings { RefreshSeconds = 60 + i });

        Assert.False(File.Exists(Path.Combine(directory, "settings.json")));

        clock.Advance(TimeSpan.FromMilliseconds(40));

        using var readerStore = new SettingsStore(directory);
        var settings = readerStore.Load();
        Assert.Equal(64, settings.RefreshSeconds); // the last of the five requests wins
    }

    [Fact]
    public void RequestSave_snapshots_Accounts_so_a_later_change_neither_lands_nor_throws()
    {
        var directory = TempDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        using var store = new SettingsStore(directory, debounceDelay: TimeSpan.FromMilliseconds(30), timeProvider: clock);
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";

        var exception = Record.Exception(() =>
        {
            store.RequestSave(settings);
            // Mutated right after handing settings to RequestSave, before the debounce timer has had
            // any chance to fire - the exact window the old, unsnapshotted _pendingSave raced.
            settings.Accounts["claude#3"] = "claude";
        });

        Assert.Null(exception);
        clock.Advance(TimeSpan.FromMilliseconds(40));

        var landed = SettingsStore.Validate(File.ReadAllText(Path.Combine(directory, "settings.json"))).Settings;
        Assert.Equal(["claude", "claude#2"], landed!.Accounts.Keys.OrderBy(key => key));
    }

    [Fact]
    public async Task SaveNow_survives_Accounts_changing_concurrently_instead_of_throwing()
    {
        // A direct SaveNow call (every use outside RequestSave's own snapshot) still serializes
        // whatever live AppSettings it is handed - this proves a concurrent change to Accounts while
        // that serialization is in flight never escapes as an unhandled InvalidOperationException.
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);
        var settings = new AppSettings();

        // 300 iterations, not the 3000 this once used: SaveNow's own file I/O, not the race window
        // itself, dominated the run time, and the mutator already races far ahead of the saver's
        // disk-bound loop either way, so a smaller count exercises the same window just as reliably.
        var mutator = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                settings.Accounts[$"claude#{i}"] = "claude";
                settings.Accounts.Remove($"claude#{i}");
            }
        });
        var saver = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
                store.SaveNow(settings);
        });

        var exception = await Record.ExceptionAsync(() => Task.WhenAll(mutator, saver));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Two_threads_saving_at_once_leave_one_valid_file_and_no_temporary_one()
    {
        // Asserted by result, not by timing: whatever order the two tasks and the debounce timer
        // interleave in, the settings file has to be readable at the end and the temporary file it
        // is written through must not be left lying around.
        var directory = TempDirectory();
        var store = new SettingsStore(directory, debounceDelay: TimeSpan.FromMilliseconds(1));

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                store.RequestSave(new AppSettings { RefreshSeconds = 60 });
        });
        var immediate = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                store.SaveNow(new AppSettings { RefreshSeconds = 90 });
        });

        await Task.WhenAll(writer, immediate);
        // No wait for the debounce timer here: Dispose flushes whatever is still pending itself,
        // under the same gate a not-yet-fired timer callback would also have to wait for.
        store.Dispose();

        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

        // Parsed here rather than through Load(), which would quietly hand back defaults for a
        // half-written file and hide exactly the damage this test is looking for.
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")));
        Assert.Contains(document.RootElement.GetProperty("refreshSeconds").GetInt32(), new[] { 60, 90 });
    }

    [Fact]
    public void SaveNow_twice_leaves_a_backup_holding_the_first_state()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);

        store.SaveNow(new AppSettings { RefreshSeconds = 45 });
        store.SaveNow(new AppSettings { RefreshSeconds = 90 });

        var backupPath = Path.Combine(directory, "settings.json.bak");
        Assert.True(File.Exists(backupPath));
        using var document = JsonDocument.Parse(File.ReadAllText(backupPath));
        Assert.Equal(45, document.RootElement.GetProperty("refreshSeconds").GetInt32());

        // The primary itself already carries the second (current) state.
        using var primary = new SettingsStore(directory);
        Assert.Equal(90, primary.Load().RefreshSeconds);
    }

    [Fact]
    public void SaveNow_once_leaves_no_backup_yet()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);

        store.SaveNow(new AppSettings { RefreshSeconds = 45 });

        Assert.False(File.Exists(Path.Combine(directory, "settings.json.bak")));
    }

    [Fact]
    public void Load_falls_back_to_the_backup_when_the_primary_is_corrupt()
    {
        var directory = TempDirectory();
        using (var seedStore = new SettingsStore(directory))
        {
            seedStore.SaveNow(new AppSettings { RefreshSeconds = 45 });
            seedStore.SaveNow(new AppSettings { RefreshSeconds = 90 }); // now settings.json.bak holds 45
        }

        File.WriteAllText(Path.Combine(directory, "settings.json"), "{not valid json");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(45, settings.RefreshSeconds);
    }

    [Fact]
    public void Load_falls_back_to_defaults_when_the_primary_is_corrupt_and_no_backup_exists()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{not valid json");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(60, settings.RefreshSeconds); // the untouched default
    }

    [Fact]
    public void Load_falls_back_to_the_backup_when_the_primary_is_missing()
    {
        var directory = TempDirectory();
        using (var seedStore = new SettingsStore(directory))
        {
            seedStore.SaveNow(new AppSettings { RefreshSeconds = 45 });
            seedStore.SaveNow(new AppSettings { RefreshSeconds = 90 }); // now settings.json.bak holds 45
        }

        File.Delete(Path.Combine(directory, "settings.json"));
        using var store = new SettingsStore(directory);

        Assert.Equal(45, store.Load().RefreshSeconds);
    }

    [Fact]
    public void Load_logs_one_info_line_when_it_recovers_from_the_backup()
    {
        var directory = TempDirectory();
        using (var seedStore = new SettingsStore(directory))
            seedStore.SaveNow(new AppSettings { RefreshSeconds = 45 });
        File.Copy(Path.Combine(directory, "settings.json"), Path.Combine(directory, "settings.json.bak"));
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{not valid json");

        var logDirectory = TempDirectory();
        var logService = new LogService(logDirectory, fileName: "test.log");
        using var store = new SettingsStore(directory, logService: logService);

        store.Load();

        var logText = File.ReadAllText(logService.CurrentFile);
        Assert.Contains("settings.json.bak", logText);
    }

    // Validate is the one gate both Load and SettingsViewModel's own settings import go through -
    // exercised directly here rather than only indirectly through Load, so an import failure mode is
    // proven without needing a file on disk at all.
    [Fact]
    public void Validate_accepts_a_good_file()
    {
        var (settings, refusal) = SettingsStore.Validate("""{"schemaVersion":1,"refreshSeconds":45}""");

        Assert.NotNull(settings);
        Assert.Null(refusal);
        Assert.Equal(45, settings.RefreshSeconds);
    }

    [Fact]
    public void Validate_refuses_a_file_from_a_newer_schema_version()
    {
        var (settings, refusal) = SettingsStore.Validate("""{"schemaVersion":99,"refreshSeconds":5}""");

        Assert.Null(settings);
        Assert.Equal(SettingsStore.ImportRefusal.NewerVersion, refusal);
    }

    [Fact]
    public void Validate_replaces_a_null_provider_entry_with_defaults_instead_of_crashing()
    {
        var (settings, refusal) = SettingsStore.Validate("""{"schemaVersion":1,"providers":{"codex":null}}""");

        Assert.NotNull(settings);
        Assert.Null(refusal);
        Assert.NotNull(settings.Providers["codex"]);
        Assert.True(settings.Providers["codex"].Visible);
    }

    [Fact]
    public void Validate_refuses_garbage()
    {
        var (settings, refusal) = SettingsStore.Validate("{not valid json");

        Assert.Null(settings);
        Assert.Equal(SettingsStore.ImportRefusal.Unparseable, refusal);
    }

    [Fact]
    public void A_non_default_statistics_layout_round_trips_through_save_and_load()
    {
        var directory = TempDirectory();
        using var store = new SettingsStore(directory);
        var layout = StatsLayout.Move(StatsLayout.Default(), "table", "figures", StatsDropPosition.Right);
        store.SaveNow(new AppSettings { StatsSectionLayout = layout });

        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.NotNull(loaded.StatsSectionLayout);
        Assert.False(StatsLayout.IsDefault(layout));
        Assert.True(StatsLayout.AreEqual(layout, loaded.StatsSectionLayout!));
    }

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-settings");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }

    /// <summary>A clock the debounce and retry timers can be advanced through virtually, instead of
    /// a test actually waiting out the real delay - same pattern as RefreshSchedulerTests' own fake
    /// clock, extended with <see cref="ITimer"/> support since SettingsStore's debounce genuinely
    /// needs a timer to fire on its own, not just a clock a scheduler polls.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }

        /// <summary>Moves the clock forward and fires every timer whose due time this reaches -
        /// synchronously, on the calling thread, so the test does not need to wait or poll for a
        /// background callback to catch up.</summary>
        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.Where(timer => timer.IsDue(_now)).ToList())
                timer.Fire(_now);
        }

        private void Forget(FakeTimer timer) => _timers.Remove(timer);

        private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;

            public bool IsDue(DateTimeOffset now) => _dueAt is { } dueAt && now >= dueAt;

            public void Fire(DateTimeOffset now)
            {
                _dueAt = null; // one-shot, same as SettingsStore's own Change(delay, InfiniteTimeSpan) usage
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Dispose() => owner.Forget(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
