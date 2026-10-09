using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiUsage.ViewModels;

/// <summary>
/// Everything the Settings window shows. The "Anzeige" card's layout,
/// per-provider visibility and Kacheldichte controls bind straight to the already-running
/// <see cref="MainViewModel"/> passed in here - the same commands the eye-menu popup uses
/// - so there is exactly one code path behind both surfaces, never a second write path that could
/// drift from it. Every setter here saves immediately: the window
/// has no save button.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HistoryStore _historyStore;
    private readonly Func<bool> _isAutostartEnabled;
    private readonly Func<string, bool> _enableAutostart;
    private readonly Func<bool> _disableAutostart;
    private bool _revertingAutostart;
    private readonly Action<string> _applyLanguage;
    private readonly Func<string, string?> _askForSavePath;
    private readonly Func<string, string?> _askForSettingsExportPath;
    private readonly Func<string?> _askForSettingsImportPath;
    private readonly Action<string> _showMessage;
    private readonly Func<string?> _askForDataFolder;
    private readonly Action<string, string> _copyIndex;
    private readonly Action<string, string> _copySmallFiles;
    private readonly Func<Uri, ResourceDictionary> _loadThemeDictionary;

    // Same theme-to-file mapping ThemeService.ThemeUris keeps for the live app - kept here too
    // rather than shared, because reaching into ThemeService's private table would need a second
    // seam of its own for no gain: this dictionary holds Uris, not colours, so it cannot drift
    // out of sync with a theme file the way hardcoded hex strings did.
    private static readonly Dictionary<AppTheme, Uri> ThemeUris = new()
    {
        [AppTheme.Nebula] = new Uri("Themes/Nebula.xaml", UriKind.Relative),
        [AppTheme.Terminal] = new Uri("Themes/Terminal.xaml", UriKind.Relative),
        [AppTheme.Dark] = new Uri("Themes/Dark.xaml", UriKind.Relative),
        [AppTheme.Light] = new Uri("Themes/Light.xaml", UriKind.Relative),
    };

    public MainViewModel Main { get; }

    public ObservableCollection<Choice<AppTheme>> ThemeChoices { get; }

    /// <summary>The sidebar's six rows, fixed order. <see cref="SelectedCategory"/> holds the
    /// matching <see cref="Choice{TValue}.Value"/> and is session only - never read from or written
    /// to <see cref="AppSettings"/>, so the window always opens back on the first category.</summary>
    public ObservableCollection<Choice<string>> Categories { get; } =
    [
        new("Settings.Section.Display", "Display"),
        new("Settings.Section.Updates", "Updates"),
        new("Settings.Section.Notifications", "Notifications"),
        new("Settings.Section.Providers", "Providers"),
        new("Settings.Section.System", "System"),
        new("Settings.Section.About", "About"),
    ];

    [ObservableProperty]
    private string selectedCategory = "Display";

    /// <summary>Built straight from <see cref="LocalizationService.Languages"/>, which already keeps
    /// the required order (System first, English second, the rest alphabetical by their own
    /// name) - adding a language needs no second list here to stay in sync.</summary>
    public ObservableCollection<Choice<string>> LanguageChoices { get; } =
        new(LocalizationService.Languages.Select(l => new Choice<string>(l.LabelKey, l.Code)));

    public ObservableCollection<Choice<string>> ChartRangeChoices { get; } = [];

    /// <summary>The window levels offered in the Window group, in the order they are listed.</summary>
    public ObservableCollection<Choice<string>> WindowLayerChoices { get; } =
    [
        new("WindowLayer.OnTop", WindowLayers.OnTop),
        new("WindowLayer.Normal", WindowLayers.Normal),
        new("WindowLayer.Desktop", WindowLayers.Desktop),
    ];

    /// <summary>Same shape as <see cref="SelectedLayoutChoice"/>, for the window-level ComboBox.</summary>
    public Choice<string>? SelectedWindowLayerChoice
    {
        get => WindowLayerChoices.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is not null && value.Value != Main.WindowLayer)
                Main.WindowLayer = value.Value;
        }
    }

    /// <summary>Whether the "stays behind all windows" hint shows under the window-level picker.</summary>
    public bool WindowLayerIsDesktop => Main.WindowLayer == WindowLayers.Desktop;

    /// <summary>The tray, the title bar menu or click-through changed the level while this window is
    /// open: the picker follows.</summary>
    private void OnMainWindowLayerChanged(object? sender, string layer)
    {
        Choice.Select(WindowLayerChoices, layer);
        OnPropertyChanged(nameof(SelectedWindowLayerChoice));
        OnPropertyChanged(nameof(WindowLayerIsDesktop));
    }

    public ObservableCollection<Choice<string>> LayoutChoices { get; } =
    [
        new("Layout.Vertical", "Vertical"),
        new("Layout.Horizontal", "Horizontal"),
    ];

    public ObservableCollection<Choice<string>> TileOrderChoices { get; } =
    [
        new("Settings.TileOrder.Custom", "Custom"),
        new("Settings.TileOrder.ByUsage", "ByUsage"),
    ];

    /// <summary>Same shape as <see cref="SelectedLayoutChoice"/>, for the tile-order ComboBox.</summary>
    public Choice<string>? SelectedTileOrderChoice
    {
        get => TileOrderChoices.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is not null && value.Value != Main.TileOrderMode)
                SetTileOrder(value.Value);
        }
    }

    private void SetTileOrder(string mode)
    {
        Choice.Select(TileOrderChoices, mode);
        Main.TileOrderMode = mode;
        OnPropertyChanged(nameof(SelectedTileOrderChoice));
    }

    /// <summary>The Meldungen card's "wartenden Agenten markieren für" picker - Off first, then fixed
    /// rungs in hours (not a slider) because <see cref="Models.AppSettings.AttentionMaxAgeMinutes"/> is a threshold
    /// someone sets once and forgets, not a value worth fine dragging. Value is hours, converted to/
    /// from the stored minutes in <see cref="SetAttentionMaxAge"/> - the setting itself stays in
    /// minutes (see AppSettings.cs) since <see cref="Providers.Parsing.AttentionDetector"/> compares
    /// it against a session's own elapsed minutes.</summary>
    public ObservableCollection<Choice<int>> AttentionMaxAgeChoices { get; } =
    [
        new("Settings.AttentionMaxAge.Off", 0),
        new("Settings.AttentionMaxAge.1h", 1),
        new("Settings.AttentionMaxAge.2h", 2),
        new("Settings.AttentionMaxAge.4h", 4),
        new("Settings.AttentionMaxAge.8h", 8),
        new("Settings.AttentionMaxAge.24h", 24),
    ];

    /// <summary>The Anzeige card's layout picker is a <c>ComboBox</c> bound to this instead of driving
    /// <see cref="LayoutChoices"/>' own <see cref="Choice{TValue}.IsSelected"/> flags through a radio
    /// group - same shape as <see cref="MainViewModel.SelectedDensityChoice"/>: a set forwards to
    /// <see cref="SetLayout"/>, which keeps this in step through the same <see cref="Choice.Select{TValue}"/>
    /// call every other choice group already goes through.</summary>
    public Choice<string>? SelectedLayoutChoice
    {
        get => LayoutChoices.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is not null && value.Value != Main.Layout)
                SetLayout(value.Value);
        }
    }

    /// <summary>Same shape as <see cref="SelectedLayoutChoice"/>, for the waiting-mark-age ComboBox.</summary>
    public Choice<int>? SelectedAttentionMaxAgeChoice
    {
        get => AttentionMaxAgeChoices.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is null)
                return;

            // While the mark is off, a stored hour count must be pickable again to switch it back on.
            var changed = value.Value == 0
                ? _settings.ShowAttentionMark
                : value.Value * 60 != _settings.AttentionMaxAgeMinutes || !_settings.ShowAttentionMark;
            if (changed)
                SetAttentionMaxAge(value.Value);
        }
    }

    /// <summary>Same shape as <see cref="SelectedLayoutChoice"/>, for the chart-range ComboBox.</summary>
    public Choice<string>? SelectedChartRangeChoice
    {
        get => ChartRangeChoices.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is not null && value.Value != _settings.ChartRange)
                SetChartRange(value.Value);
        }
    }

    /// <summary>Same shape as <see cref="SelectedLayoutChoice"/>, for the language ComboBox.</summary>
    public Choice<string>? SelectedLanguageChoice
    {
        get => LanguageChoices.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is not null && value.Value != _settings.Language)
                SetLanguage(value.Value);
        }
    }

    public ObservableCollection<ThresholdRowViewModel> ThresholdRows { get; } = [];

    public ObservableCollection<NotificationRowViewModel> NotificationRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshSecondsLabel))]
    private int refreshSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetentionLabel))]
    [NotifyPropertyChangedFor(nameof(RetentionFullLabel))]
    [NotifyPropertyChangedFor(nameof(RetentionSummaryText))]
    private int historyRetentionDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteIntervalLabelText))]
    private int remoteRefreshMinutes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowOpacityLabel))]
    private int windowOpacityPercent;

    /// <summary>The Mica background option, saved to the same value the settings file carries.</summary>
    [ObservableProperty]
    private bool micaEnabled;

    /// <summary>Whether the Mica option is shown: only while the theme is "follow Windows" (the only
    /// theme Mica acts in) and on a Windows build that can show it. Hidden otherwise, the stored value
    /// stays as it was.</summary>
    public bool MicaOptionVisible => IsMicaOptionVisible(Environment.OSVersion.Version.Build, _settings.Theme);

    internal static bool IsMicaOptionVisible(int osBuild, string? theme) =>
        osBuild >= MicaPolicy.MinimumBuild
        && string.Equals(theme, nameof(AppTheme.System), StringComparison.OrdinalIgnoreCase);

    /// <summary>The live value of <see cref="Storage.AppPaths.DataDirectory"/> - re-read after a
    /// successful <see cref="ChooseDataFolderAsync"/> so the data folder row (Settings.DataFolder) shows the new location
    /// without needing a restart.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataFolderDisplayText))]
    [NotifyPropertyChangedFor(nameof(DataFolderToolTip))]
    [NotifyPropertyChangedFor(nameof(DataFolderKindText))]
    private string dataFolderPath = "";

    /// <summary>True while <see cref="ChooseDataFolderAsync"/> copies the data in the background; the
    /// window disables the button and shows the "moving" text meanwhile.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseDataFolderCommand))]
    private bool isMovingData;

    /// <summary><see cref="DataFolderPath"/> trimmed for the settings row, the full path always kept
    /// in its tooltip - a middle ellipsis rather than <c>TextTrimming</c>'s end-only one, since the
    /// most distinguishing part of a long profile path (the leaf folder) sits at the end.</summary>
    public string DataFolderDisplayText => TrimPathMiddle(DataFolderPath);

    /// <summary>The full path, only while <see cref="DataFolderDisplayText"/> shows it cut short;
    /// null otherwise, so a path that is fully visible gets no tooltip repeating it.</summary>
    public string? DataFolderToolTip => DataFolderDisplayText == DataFolderPath ? null : DataFolderPath;

    private const int DataFolderDisplayMaxLength = 60;

    internal static string TrimPathMiddle(string path, int maxLength = DataFolderDisplayMaxLength)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
            return path;

        var keep = maxLength - 3; // room for the "..." itself
        var head = (keep + 1) / 2;
        var tail = keep / 2;
        return string.Concat(path.AsSpan(0, head), "...", path.AsSpan(path.Length - tail));
    }

    /// <summary>The same default/chosen/fallback sentence <see cref="Views.SettingsWindow"/> already
    /// shows next to its own copy of the path - reused verbatim (same resource keys) so a redirected
    /// folder never looks like the normal one, here or there.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Bound from XAML against this instance; classifies the live AppPaths.DataDirectory, not per-instance state.")]
    public string DataFolderKindText => LocalizationService.Instance[AppPaths.ClassifyDataDirectory() switch
    {
        AppPaths.DataFolderKind.Chosen => "About.DataFolder.Chosen",
        AppPaths.DataFolderKind.Fallback => "About.DataFolder.Fallback",
        _ => "About.DataFolder.Default",
    }];

    [ObservableProperty]
    private bool autostart;

    [ObservableProperty]
    private bool showPreviousWeekLine;

    [ObservableProperty]
    private bool hideOnFullscreen;

    [ObservableProperty]
    private bool saveEnergyOnBattery;

    /// <summary>The widget zoom in percent, one of <see cref="WindowZoom.AllowedPercents"/>. MainWindow
    /// watches this property to redraw and resize the widget.</summary>
    [ObservableProperty]
    private int zoomPercent;

    /// <summary>The zoom steps for the Size picker, labelled like "125 %".</summary>
    public ObservableCollection<Choice<int>> ZoomChoices { get; } =
        new(WindowZoom.AllowedPercents.Select(percent => Choice.WithFixedLabel(percent + " %", percent)));

    /// <summary>Same shape as <see cref="SelectedLayoutChoice"/>, for the Size ComboBox.</summary>
    public Choice<int>? SelectedZoomChoice
    {
        get => ZoomChoices.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is not null && value.Value != ZoomPercent)
                ZoomPercent = value.Value;
        }
    }

    [ObservableProperty]
    private bool showTooltips;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultThresholdLabelText))]
    [NotifyPropertyChangedFor(nameof(DefaultThresholdValueText))]
    [NotifyPropertyChangedFor(nameof(DefaultThresholdEnabledFullLabel))]
    private double defaultThreshold;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultThresholdEnabledFullLabel))]
    private bool defaultThresholdEnabled;

    [ObservableProperty]
    private bool notifyOnReset;

    [ObservableProperty]
    private bool forecastAlertEnabled;

    [ObservableProperty]
    private bool limitReachedAlertEnabled;

    [ObservableProperty]
    private bool quietWeekend;

    [ObservableProperty]
    private bool showAttentionMark;

    [ObservableProperty]
    private bool checkForUpdates;

    [ObservableProperty]
    private bool quietHoursEnabled;

    [ObservableProperty]
    private string quietHoursStartText = "";

    [ObservableProperty]
    private string quietHoursEndText = "";

    [ObservableProperty]
    private bool quietHoursStartInvalid;

    [ObservableProperty]
    private bool quietHoursEndInvalid;

    /// <summary>Filled in from a background read once construction finishes - opening the window
    /// never waits on disk. A fresh instance is created every time the window opens (see the class
    /// summary), so this is naturally refreshed then, with no subscription needed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetentionSummaryText))]
    private string historySizeText = "";

    public string RefreshSecondsLabel => RefreshSeconds < 60
        ? LocalizationService.Instance.Format("Duration.Seconds", RefreshSeconds)
        : RefreshSeconds % 60 == 0
            ? LocalizationService.Instance.Format("Duration.Minutes", RefreshSeconds / 60)
            : LocalizationService.Instance.Format("Duration.MinutesSeconds", RefreshSeconds / 60, RefreshSeconds % 60);

    public string RetentionLabel => DurationFormatter.Describe(HistoryRetentionDays);

    public string RemoteIntervalLabelText => LocalizationService.Instance.Format("Settings.RemoteInterval", RemoteRefreshMinutes);

    public string RetentionFullLabel => LocalizationService.Instance.Format("Settings.Retention", RetentionLabel);

    /// <summary>The line under the retention slider: how long history is kept, and how much room it
    /// takes on disk once that figure has been read. Composed here rather than from two Runs in the
    /// window, so the size half can never be the part that falls off the end of the row.</summary>
    public string RetentionSummaryText => HistorySizeText is { Length: > 0 } size
        ? $"{RetentionLabel} · {size}"
        : RetentionLabel;

    public string WindowOpacityLabel => LocalizationService.Instance.Format("Settings.WindowOpacityValue", WindowOpacityPercent);

    public string DefaultThresholdValueText => StatusTextMap.FormatPercent(DefaultThreshold.ToString("0", CultureInfo.CurrentCulture));

    public string DefaultThresholdLabelText => LocalizationService.Instance.Format("Settings.DefaultThreshold", $"{DefaultThreshold:0}");

    /// <summary>Names what the global Ein/Aus check box actually toggles - the same reasoning as
    /// <see cref="ThresholdRowViewModel.FiveHourEnabledFullLabel"/> and its siblings.</summary>
    public string DefaultThresholdEnabledFullLabel => $"{DefaultThresholdLabelText}: {LocalizationService.Instance["Settings.On"]}";

    /// <summary>Names the two real colour boundaries <see cref="UsageRowViewModel.Classify"/> uses -
    /// shown as a grey line under the threshold slider so "yellow"/"red" is never a claim the numbers on
    /// screen could silently drift from. Reads only static/const data (the two boundaries never
    /// change at runtime) but must stay an instance member - XAML's <c>{Binding LevelExplainerText}</c>
    /// resolves against this view model's own instance, not a static member.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Bound from XAML against this instance; the underlying values are const, not per-instance.")]
    public string LevelExplainerText => LocalizationService.Instance.Format(
        "Settings.LevelExplainer", LevelPercent(UsageRowViewModel.WarnFrom), LevelPercent(UsageRowViewModel.CritFrom));

    private static string LevelPercent(double level) =>
        StatusTextMap.FormatPercent(level.ToString("0", CultureInfo.CurrentCulture));

    /// <summary>Settings window's "add another account" button, forwarded straight to <see
    /// cref="MainViewModel.AddAccountCommand"/> - this view model owns no account state of its own,
    /// <see cref="MainViewModel"/> does, same split as every other provider-card control that binds
    /// to <see cref="MainViewModel.Tiles"/> directly.</summary>
    [RelayCommand]
    private void AddAccount(string providerId) => Main.AddAccountCommand.Execute(providerId);

    /// <summary>Asks whether to remove an account and whether its recorded history goes too. The
    /// Settings window sets the real confirm dialog; without one nothing is confirmed, so a missing
    /// dialog can never delete anything.</summary>
    internal Func<(bool Confirmed, bool AlsoHistory)> ConfirmRemoveAccount { get; set; } = () => (false, false);

    /// <summary>The Settings.RemoveAccount button - after the user confirmed, removes
    /// the account through <see cref="MainViewModel.RemoveAccountAsync"/> (its sign-in folder is
    /// deleted, its history only when asked) and says so, or says that it could not, through the same
    /// <see cref="_showMessage"/> seam every other Settings-window notice uses. Cancelling the confirmation changes nothing.</summary>
    [RelayCommand]
    private async Task RemoveAccountAsync(string accountKey)
    {
        var (confirmed, alsoHistory) = ConfirmRemoveAccount();
        if (!confirmed)
            return;

        switch (await Main.TryRemoveAccountAsync(accountKey, alsoHistory))
        {
            case RemoveAccountResult.Removed:
                _showMessage(LocalizationService.Instance["Settings.AccountRemoved"]);
                break;
            case RemoveAccountResult.RemovedHistoryKept:
                _showMessage(LocalizationService.Instance["Settings.RemoveAccount.HistoryFailed"]);
                break;
            case RemoveAccountResult.SignInFilesLocked:
            case RemoveAccountResult.Failed:
                _showMessage(LocalizationService.Instance["Settings.RemoveAccount.Failed"]);
                break;
        }
    }

    public SettingsViewModel(MainViewModel main, AppSettings settings, SettingsStore store)
        : this(main, settings, store, AutostartService.IsEnabled, AutostartService.Enable, AutostartService.Disable,
            LocalizationService.Instance.SetLanguage, LoadThemeDictionary)
    {
    }

    /// <summary>Test seam: never touches the real Windows Run key - a unit test that flips
    /// <see cref="Autostart"/> (directly or via <see cref="ResetToDefaults"/>) must not write to or
    /// delete a real autostart entry on whatever machine runs the test suite. <paramref
    /// name="applyLanguage"/> is the same idea for <see cref="LocalizationService"/>'s single shared
    /// instance: a test flipping <see cref="LanguageChoices"/> must not leave every other
    /// test in the same run reading English or German depending on execution order. <paramref
    /// name="loadThemeDictionary"/> is the same <c>Func&lt;Uri, ResourceDictionary&gt;</c> seam
    /// <see cref="ThemeService"/> itself uses, so a test can hand back an in-memory dictionary
    /// instead of a real pack-URI load, which needs a live <see cref="Application"/> this test
    /// project deliberately has none of.</summary>
    internal SettingsViewModel(MainViewModel main, AppSettings settings, SettingsStore store,
        Func<bool> isAutostartEnabled, Func<string, bool> enableAutostart, Func<bool> disableAutostart,
        Action<string> applyLanguage, Func<Uri, ResourceDictionary> loadThemeDictionary,
        HistoryStore? historyStore = null, Func<string, string?>? askForSavePath = null,
        Func<string, string?>? askForSettingsExportPath = null, Func<string?>? askForSettingsImportPath = null,
        Action<string>? showMessage = null, Func<string?>? askForDataFolder = null,
        Func<CancellationToken, Task<UpdateCheck.Release?>>? fetchLatestRelease = null,
        Action<string, string>? copyIndex = null,
        Action<string, string>? copySmallFiles = null)
    {
        Main = main;
        _settings = settings;
        _store = store;
        _historyStore = historyStore ?? new HistoryStore();
        _isAutostartEnabled = isAutostartEnabled;
        _enableAutostart = enableAutostart;
        _disableAutostart = disableAutostart;
        _applyLanguage = applyLanguage;
        _askForSavePath = askForSavePath ?? ShowRealSaveDialog;
        _askForSettingsExportPath = askForSettingsExportPath ?? ShowRealSettingsSaveDialog;
        _askForSettingsImportPath = askForSettingsImportPath ?? ShowRealSettingsOpenDialog;
        _showMessage = showMessage ?? (message => Views.ConfirmWindow.ShowInfo(null, message));
        _askForDataFolder = askForDataFolder ?? ShowRealFolderDialog;
        _copyIndex = copyIndex ?? CopyIndexFiles;
        _copySmallFiles = copySmallFiles ?? CopySmallFiles;
        if (fetchLatestRelease is not null)
            main.Update.FetchLatestRelease = fetchLatestRelease;

        refreshSeconds = settings.RefreshSeconds;
        historyRetentionDays = settings.HistoryRetentionDays;
        remoteRefreshMinutes = settings.RemoteRefreshMinutes;
        windowOpacityPercent = settings.WindowOpacityPercent;
        micaEnabled = settings.MicaEnabled;
        dataFolderPath = AppPaths.DataDirectory;
        showPreviousWeekLine = settings.ShowPreviousWeekLine;
        hideOnFullscreen = settings.HideOnFullscreen;
        saveEnergyOnBattery = settings.SaveEnergyOnBattery;
        zoomPercent = WindowZoom.Normalize(settings.ZoomPercent);
        Choice.Select(ZoomChoices, zoomPercent);
        showTooltips = settings.ShowTooltips;
        defaultThreshold = settings.DefaultThreshold;
        defaultThresholdEnabled = settings.DefaultThresholdEnabled;
        notifyOnReset = settings.NotifyOnReset;
        forecastAlertEnabled = settings.ForecastAlertEnabled;
        limitReachedAlertEnabled = settings.LimitReachedAlertEnabled;
        showAttentionMark = settings.ShowAttentionMark;
        checkForUpdates = settings.CheckForUpdates;
        quietHoursEnabled = settings.QuietHoursEnabled;
        quietWeekend = settings.QuietWeekend;
        quietHoursStartText = settings.QuietHoursStart;
        quietHoursEndText = settings.QuietHoursEnd;
        // The registry is the single source of truth, never the remembered settings
        // value - a manual registry edit or a failed previous write must show up as the true state.
        autostart = _isAutostartEnabled();

        ThemeChoices =
        [
            // OrdinalIgnoreCase: App.xaml.cs parses the same settings.Theme value with
            // Enum.Parse(ignoreCase: true), so a hand-edited "nebula" must select the same choice
            // here that it actually applies at startup, instead of showing no theme selected.
            // The two preview brushes come straight out of each theme's own dictionary (Bg.Base,
            // Accent) instead of a duplicated hex literal, so a swatch cannot disagree with the
            // theme it previews.
            BuildThemeChoice("Theme.System", AppTheme.System, loadThemeDictionary, settings.Theme),
            BuildThemeChoice("Theme.Nebula", AppTheme.Nebula, loadThemeDictionary, settings.Theme),
            BuildThemeChoice("Theme.Terminal", AppTheme.Terminal, loadThemeDictionary, settings.Theme),
            BuildThemeChoice("Theme.Dark", AppTheme.Dark, loadThemeDictionary, settings.Theme),
            BuildThemeChoice("Theme.Light", AppTheme.Light, loadThemeDictionary, settings.Theme),
        ];
        _loadThemeDictionary = loadThemeDictionary;

        Choice.Select(LanguageChoices, settings.Language);
        Choice.Select(LayoutChoices, settings.Layout);
        Choice.Select(WindowLayerChoices, Main.WindowLayer);
        Main.WindowLayerChanged += OnMainWindowLayerChanged;
        Choice.Select(TileOrderChoices, settings.TileOrderMode);
        Choice.Select(AttentionMaxAgeChoices, AttentionMaxAgeSelection(settings));

        RebuildProviderRows();
        // An account added or removed while this window is open gets or loses its rows right away.
        Main.Tiles.CollectionChanged += OnTilesChanged;

        RebuildChartRangeChoices();

        // This window is short-lived (recreated on every open, MainWindow.xaml.cs) - Dispose below
        // unsubscribes so a closed-and-reopened Settings window never piles up handlers on the one
        // process-lifetime LocalizationService.Instance.
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;

        StartHistorySizeLoad();
    }

    public void Dispose()
    {
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
        Main.Tiles.CollectionChanged -= OnTilesChanged;
        Main.WindowLayerChanged -= OnMainWindowLayerChanged;
        UnwatchTileNames();
    }

    private readonly List<ProviderTileViewModel> _watchedTiles = [];

    private void UnwatchTileNames()
    {
        foreach (var tile in _watchedTiles)
            tile.PropertyChanged -= OnTilePropertyChanged;
        _watchedTiles.Clear();
    }

    /// <summary>An extra account's real name arrives with its first snapshot, possibly while this
    /// window is open: the row's caption follows it instead of keeping the placeholder.</summary>
    private void OnTilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProviderTileViewModel.HeaderDisplayName) || sender is not ProviderTileViewModel tile)
            return;
        foreach (var row in NotificationRows)
        {
            if (row.Tile == tile)
                row.SetHeaderDisplayName(tile.HeaderDisplayName);
        }
    }

    private void OnTilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RebuildProviderRows();

    /// <summary>One notification and threshold row per tile, each bound to that account's own entry
    /// in the live settings - created when an import or an older file has none for it.</summary>
    private void RebuildProviderRows()
    {
        ThresholdRows.Clear();
        NotificationRows.Clear();
        UnwatchTileNames();
        foreach (var tile in Main.Tiles)
        {
            tile.PropertyChanged += OnTilePropertyChanged;
            _watchedTiles.Add(tile);
            if (!_settings.Providers.TryGetValue(tile.ProviderId, out var providerSettings))
            {
                providerSettings = new ProviderSettings();
                _settings.Providers[tile.ProviderId] = providerSettings;
            }
            var notificationRow = new NotificationRowViewModel(tile.ProviderId, tile.HeaderDisplayName, providerSettings, SaveProviderSettings, tile);
            NotificationRows.Add(notificationRow);
            ThresholdRows.Add(notificationRow.Threshold);
        }
    }

    /// <summary>Test seam: lets a test await the background load below instead of polling for
    /// <see cref="HistorySizeText"/> to become non-empty.</summary>
    internal Task HistorySizeLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The last byte count <see cref="StartHistorySizeLoad"/> read off disk, kept so
    /// <see cref="RefreshHistorySizeText"/> can re-format <see cref="HistorySizeText"/> in the new
    /// language without re-reading every provider's history file again.</summary>
    private long? _historySizeBytes;

    /// <summary>Reads every provider's history file size off disk on a background thread and
    /// marshals the formatted result back - opening the window must never wait on disk I/O.</summary>
    private void StartHistorySizeLoad()
    {
        HistorySizeLoadTask = LoadHistorySizeAsync();
    }

    /// <summary>A failing read (a disk that goes away mid read, for instance) leaves
    /// <see cref="HistorySizeText"/> empty rather than throwing out of the continuation - this
    /// background load must never take the app down with it.</summary>
    private async Task LoadHistorySizeAsync()
    {
        var providerIds = Main.Tiles.Select(tile => tile.ProviderId).ToList();
        var text = "";
        try
        {
            var bytes = await Task.Run(() => _historyStore.TotalHistoryBytes(providerIds));
            _historySizeBytes = bytes;
            text = LocalizationService.Instance.Format("Settings.HistorySize", FormatHistorySize(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort, same reasoning as HistoryStore's own reads - the caller runs with an
            // empty size text rather than a crash.
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.BeginInvoke(() => HistorySizeText = text);
        else
            HistorySizeText = text;
    }

    /// <summary>Re-formats <see cref="HistorySizeText"/> in the new language on a live switch - its
    /// surrounding wording ("Settings.HistorySize") is localized even though the byte count itself
    /// is not. A no-op while the initial background load has not finished yet (<see
    /// cref="_historySizeBytes"/> still null): that load will format in the current language anyway.</summary>
    private void RefreshHistorySizeText()
    {
        if (_historySizeBytes is { } bytes)
            HistorySizeText = LocalizationService.Instance.Format("Settings.HistorySize", FormatHistorySize(bytes));
    }

    private static string FormatHistorySize(long bytes)
    {
        const long oneMb = 1024 * 1024;
        return bytes < oneMb
            ? LocalizationService.Instance.Format("Settings.SizeKilobytes", bytes / 1024.0)
            : LocalizationService.Instance.Format("Settings.SizeMegabytes", bytes / (double)oneMb);
    }

    /// <summary>Every label this window itself computed from a resource key must update the
    /// instant the language changes, not only the next time this window happens to be reopened.</summary>
    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var choice in ThemeChoices)
            choice.RefreshLabel();
        foreach (var choice in Categories)
            choice.RefreshLabel();
        foreach (var choice in LanguageChoices)
            choice.RefreshLabel();
        foreach (var choice in ChartRangeChoices)
            choice.RefreshLabel();
        foreach (var choice in LayoutChoices)
            choice.RefreshLabel();
        foreach (var choice in WindowLayerChoices)
            choice.RefreshLabel();
        foreach (var choice in TileOrderChoices)
            choice.RefreshLabel();
        foreach (var choice in AttentionMaxAgeChoices)
            choice.RefreshLabel();
        foreach (var row in ThresholdRows)
            row.RefreshLabels();
        foreach (var row in NotificationRows)
            row.RefreshLabels();
        OnPropertyChanged(nameof(RefreshSecondsLabel));
        OnPropertyChanged(nameof(RetentionLabel));
        OnPropertyChanged(nameof(RetentionFullLabel));
        OnPropertyChanged(nameof(RetentionSummaryText));
        OnPropertyChanged(nameof(RemoteIntervalLabelText));
        OnPropertyChanged(nameof(WindowOpacityLabel));
        OnPropertyChanged(nameof(DefaultThresholdLabelText));
        OnPropertyChanged(nameof(DefaultThresholdEnabledFullLabel));
        OnPropertyChanged(nameof(LevelExplainerText));
        OnPropertyChanged(nameof(DataFolderKindText));
        RefreshHistorySizeText();
    }

    [RelayCommand]
    private void SetTheme(AppTheme theme)
    {
        Choice.Select(ThemeChoices, theme);
        RefreshSystemThemePreview();

        _settings.Theme = theme.ToString();
        OnPropertyChanged(nameof(MicaOptionVisible));
        if (Application.Current is not null) // guards unit tests, which run with no live WPF Application
            ThemeService.Apply(theme);
        _store.RequestSave(_settings);
    }

    /// <summary>Keeps the "follow Windows" swatch showing whichever of Light/Dark is currently
    /// resolved - called on every theme pick (not only when System itself is picked) so it never sits
    /// there stale from whatever Windows was using when this window first opened.</summary>
    private void RefreshSystemThemePreview()
    {
        var systemChoice = ThemeChoices.FirstOrDefault(c => c.Value == AppTheme.System);
        if (systemChoice is null)
            return;

        var previewTheme = ThemeService.IsWindowsUsingLightTheme() ? AppTheme.Light : AppTheme.Dark;
        var dictionary = _loadThemeDictionary(ThemeUris[previewTheme]);
        ThemeService.ApplySystemAccent(dictionary, previewTheme == AppTheme.Dark, AccentColors.ReadSystemAccent);
        systemChoice.RefreshPreview(dictionary["Bg.Base"] as Brush, dictionary["Accent"] as Brush);
    }

    [RelayCommand]
    private void SetLayout(string layout)
    {
        Choice.Select(LayoutChoices, layout);
        Main.Layout = layout;
        OnPropertyChanged(nameof(SelectedLayoutChoice));
    }

    /// <summary>hours is one of <see cref="AttentionMaxAgeChoices"/>' own values (0 for off, else 1,
    /// 2, 4, 8, 24). Off only switches the mark off and keeps the stored minutes; an hour count
    /// switches it on and is converted to minutes and clamped the same way any hand-edited settings.json value would be -
    /// the dropdown can never itself produce an out-of-range value, but going through
    /// <see cref="SettingsRanges.ClampAttentionMaxAgeMinutes"/> anyway keeps this the one path a
    /// future rung list could never bypass.</summary>
    [RelayCommand]
    private void SetAttentionMaxAge(int hours)
    {
        Choice.Select(AttentionMaxAgeChoices, hours);
        OnPropertyChanged(nameof(SelectedAttentionMaxAgeChoice));

        if (hours == 0)
        {
            ShowAttentionMark = false;
            return;
        }

        _settings.AttentionMaxAgeMinutes = SettingsRanges.ClampAttentionMaxAgeMinutes(hours * 60);
        _store.RequestSave(_settings);
        ShowAttentionMark = true;
    }

    /// <summary>The rung the picker shows for the stored settings: Off while the mark is off.</summary>
    private static int AttentionMaxAgeSelection(AppSettings settings) =>
        settings.ShowAttentionMark ? AttentionMaxAgeHoursOrUnmatched(settings.AttentionMaxAgeMinutes) : 0;

    /// <summary>The stored minutes only ever came from this same dropdown or a fresh 120-minute
    /// default, both exact multiples of 60 - a value that is not (only reachable by hand-editing
    /// settings.json) selects no rung at all rather than silently rounding to the wrong one.</summary>
    private static int AttentionMaxAgeHoursOrUnmatched(int minutes) => minutes % 60 == 0 ? minutes / 60 : -1;

    [RelayCommand]
    private void SetLanguage(string language)
    {
        Choice.Select(LanguageChoices, language);
        OnPropertyChanged(nameof(SelectedLanguageChoice));

        _settings.Language = language;
        _applyLanguage(language); // raises LocalizationService's change notification - every open
                                   // window's text updates immediately
        _store.RequestSave(_settings);
    }

    [RelayCommand]
    private void SetChartRange(string range)
    {
        Choice.Select(ChartRangeChoices, range);
        OnPropertyChanged(nameof(SelectedChartRangeChoice));

        _settings.ChartRange = range;
        _store.RequestSave(_settings);
        Main.RefreshHistoryForAllTiles();
    }

    internal double SettingsWidth => _settings.Window.SettingsWidth;

    internal void SaveSettingsWidth(double width)
    {
        _settings.Window.SettingsWidth = width;
        _store.RequestSave(_settings);
    }

    partial void OnRefreshSecondsChanged(int value)
    {
        var clamped = SettingsRanges.ClampRefreshSeconds(value);
        if (clamped != value)
        {
            RefreshSeconds = clamped; // re-enters this setter once more with the clamped value
            return;
        }
        Main.UpdateRefreshInterval(clamped);
    }

    partial void OnRemoteRefreshMinutesChanged(int value)
    {
        var clamped = SettingsRanges.ClampRemoteRefreshMinutes(value);
        if (clamped != value)
        {
            RemoteRefreshMinutes = clamped; // re-enters this setter once more with the clamped value
            return;
        }
        _settings.RemoteRefreshMinutes = clamped;
        _store.RequestSave(_settings);
    }

    partial void OnHistoryRetentionDaysChanged(int value)
    {
        var snapped = SettingsRanges.SnapRetentionDays(value);
        if (snapped != value)
        {
            HistoryRetentionDays = snapped;
            return;
        }
        _settings.HistoryRetentionDays = snapped;
        _store.RequestSave(_settings);
        RebuildChartRangeChoices();
    }

    partial void OnWindowOpacityPercentChanged(int value)
    {
        var clamped = SettingsRanges.ClampWindowOpacityPercent(value);
        if (clamped != value)
        {
            WindowOpacityPercent = clamped; // re-enters this setter once more with the clamped value
            return;
        }
        _settings.WindowOpacityPercent = clamped;
        _store.RequestSave(_settings);
        if (Application.Current is not null) // guards unit tests, which run with no live WPF Application
            WindowOpacity.ApplyToAllOpenWindows(clamped);
    }

    partial void OnMicaEnabledChanged(bool value)
    {
        _settings.MicaEnabled = value;
        _store.RequestSave(_settings);
        if (Application.Current is not null) // guards unit tests, which run with no live WPF Application
            MicaBackdrop.SetEnabled(value);
    }

    partial void OnShowPreviousWeekLineChanged(bool value)
    {
        _settings.ShowPreviousWeekLine = value;
        _store.RequestSave(_settings);
        Main.RefreshHistoryForAllTiles();
    }

    /// <summary>MainWindow watches this property to start or stop the full-screen watcher.</summary>
    partial void OnHideOnFullscreenChanged(bool value)
    {
        _settings.HideOnFullscreen = value;
        _store.RequestSave(_settings);
    }

    partial void OnZoomPercentChanged(int value)
    {
        var allowed = WindowZoom.Normalize(value);
        if (allowed != value)
        {
            ZoomPercent = allowed; // re-enters this handler once more with the allowed value
            return;
        }

        _settings.ZoomPercent = value;
        _store.RequestSave(_settings);
        Choice.Select(ZoomChoices, value);
        OnPropertyChanged(nameof(SelectedZoomChoice));
    }

    partial void OnShowTooltipsChanged(bool value)
    {
        _settings.ShowTooltips = value;
        _store.RequestSave(_settings);
    }

    partial void OnDefaultThresholdChanged(double value)
    {
        var clamped = SettingsRanges.ClampThreshold(value);
        if (Math.Abs(clamped - value) > 0.001)
        {
            DefaultThreshold = clamped; // re-enters this setter once more with the clamped value
            return;
        }
        _settings.DefaultThreshold = clamped;
        _store.RequestSave(_settings);
        Main.ApplyThresholdsToAllTiles();
    }

    partial void OnDefaultThresholdEnabledChanged(bool value)
    {
        _settings.DefaultThresholdEnabled = value;
        _store.RequestSave(_settings);
        Main.ApplyThresholdsToAllTiles();
    }

    partial void OnNotifyOnResetChanged(bool value)
    {
        _settings.NotifyOnReset = value;
        _store.RequestSave(_settings);
    }

    partial void OnForecastAlertEnabledChanged(bool value)
    {
        _settings.ForecastAlertEnabled = value;
        _store.RequestSave(_settings);
    }

    partial void OnLimitReachedAlertEnabledChanged(bool value)
    {
        _settings.LimitReachedAlertEnabled = value;
        _store.RequestSave(_settings);
    }

    /// <summary>Re-applies the marker to every tile's last snapshot immediately, the same reasoning
    /// as <see cref="ProviderTileViewModel.OnShowFiveHourChanged"/> - a setting flip must not wait for
    /// the next scheduled fetch (up to a minute away) to show or hide the mark.</summary>
    partial void OnShowAttentionMarkChanged(bool value)
    {
        _settings.ShowAttentionMark = value;
        _store.RequestSave(_settings);
        Main.ApplyShowAttentionMarkToAllTiles(value);
    }

    partial void OnSaveEnergyOnBatteryChanged(bool value)
    {
        _settings.SaveEnergyOnBattery = value;
        _store.RequestSave(_settings);
        Main.UpdateSaveEnergyOnBattery(value);
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.CheckForUpdates = value;
        _store.RequestSave(_settings);
    }

    /// <summary>The About window's own seam into the daily update check, shared with the notice bar in
    /// the main window (see <see cref="UpdateNoticeViewModel"/>) so one check serves both.</summary>
    internal Task<UpdateCheck.Release?> CheckForUpdateAsync(CancellationToken ct) => Main.Update.CheckAsync(ct);

    partial void OnQuietHoursEnabledChanged(bool value)
    {
        _settings.QuietHoursEnabled = value;
        _store.RequestSave(_settings);
    }

    partial void OnQuietWeekendChanged(bool value)
    {
        _settings.QuietWeekend = value;
        _store.RequestSave(_settings);
    }

    partial void OnQuietHoursStartTextChanged(string value)
    {
        if (TryParseTime(value))
        {
            QuietHoursStartInvalid = false;
            _settings.QuietHoursStart = value;
            _store.RequestSave(_settings);
        }
        else
        {
            // A value that does not parse shows a red border; the previous stored value stays in
            // effect rather than being overwritten with something QuietHours could not use.
            QuietHoursStartInvalid = true;
        }
    }

    partial void OnQuietHoursEndTextChanged(string value)
    {
        if (TryParseTime(value))
        {
            QuietHoursEndInvalid = false;
            _settings.QuietHoursEnd = value;
            _store.RequestSave(_settings);
        }
        else
        {
            QuietHoursEndInvalid = true;
        }
    }

    private static bool TryParseTime(string value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Enable/Disable now report whether Windows actually accepted the registry write
    /// (locked-down policy, missing permission) instead of throwing - a refusal shows a message and
    /// puts the checkbox back to the registry's real state rather than a wish that never took.
    /// <see cref="_revertingAutostart"/> stops that revert from itself trying (and possibly
    /// re-refusing) the same registry write a second time: it is set only while
    /// <see cref="Autostart"/> is being written back to the already-known-real value.</summary>
    partial void OnAutostartChanged(bool value)
    {
        if (_revertingAutostart)
            return;

        var applied = true;
        if (value)
        {
            var exePath = Environment.ProcessPath;
            if (exePath is not null)
                applied = _enableAutostart(exePath);
        }
        else
        {
            applied = _disableAutostart();
        }

        if (!applied)
        {
            _showMessage(LocalizationService.Instance["Settings.AutostartRefused"]);
            var actual = _isAutostartEnabled();
            _revertingAutostart = true;
            try
            {
                Autostart = actual;
            }
            finally
            {
                _revertingAutostart = false;
            }
            _settings.Autostart = actual;
            _store.RequestSave(_settings);
            return;
        }

        // Settings.Autostart is kept only as an informational mirror - the registry decides.
        _settings.Autostart = value;
        _store.RequestSave(_settings);
    }

    [RelayCommand]
    private void RefreshNow() => Main.RefreshNow();

    [RelayCommand]
    private static void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // Nothing sensible to recover into - opening a folder is best-effort.
        }
    }

    /// <summary>Same body as <see cref="OpenLogsFolder"/>, against the data folder itself - settings,
    /// backup and every provider's history, one level up from the logs folder that already had a
    /// button of its own.</summary>
    [RelayCommand]
    private static void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // Nothing sensible to recover into - opening a folder is best-effort.
        }
    }

    /// <summary>
    /// Every provider's complete recorded history (no range limit - an export is a deliberate,
    /// occasional action, not something read on every tick) as one CSV file. Cancelling the save
    /// dialog does nothing; a write failure shows the app's own error text, never a raw exception
    /// message.
    /// </summary>
    [RelayCommand]
    private void ExportHistory()
    {
        var path = _askForSavePath($"ai-usage-history-{DateTime.Now:yyyy-MM-dd}.csv");
        if (path is null)
            return;

        var byProvider = Main.Tiles.ToDictionary(
            tile => tile.ProviderId,
            IReadOnlyList<HistoryPoint> (tile) => _historyStore.Load(tile.ProviderId, DateTimeOffset.MinValue, DateTimeOffset.MaxValue));

        try
        {
            File.WriteAllText(path, HistoryExport.ToCsv(byProvider), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _showMessage(LocalizationService.Instance["Settings.ExportFailed"]);
        }
    }

    private static string? ShowRealSaveDialog(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = "CSV (*.csv)|*.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Every current setting, written the same way the store itself would, to a path the
    /// user picks - never the window's own position/size (see <see cref="ApplyLoaded"/>'s doc), since
    /// that is remembered state, not a preference this window exposes. Cancelling the dialog does
    /// nothing; a write failure shows the app's own error text, never a raw exception message.</summary>
    [RelayCommand]
    private void ExportSettings()
    {
        var path = _askForSettingsExportPath("ai-usage-settings.json");
        if (path is null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(_settings, SettingsStore.JsonOptions);
            File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _showMessage(LocalizationService.Instance["Settings.ExportFailed"]);
        }
    }

    /// <summary>
    /// Reads a settings file the user picks through exactly the same gate <see cref="SettingsStore.Load"/>
    /// applies (<see cref="SettingsStore.Validate"/>) - a newer schema version or anything unparseable is
    /// refused with one generic message, never applied. A file that passes is applied live and saved
    /// immediately via <see cref="ApplyLoaded"/>, which never touches window position, size or monitor.
    /// </summary>
    [RelayCommand]
    private void ImportSettings()
    {
        var path = _askForSettingsImportPath();
        if (path is null)
            return;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _showMessage(LocalizationService.Instance["Settings.ImportRefused"]);
            return;
        }

        var (loaded, _) = SettingsStore.Validate(text);
        if (loaded is null)
        {
            _showMessage(LocalizationService.Instance["Settings.ImportRefused"]);
            return;
        }

        ApplyLoaded(loaded);
        _showMessage(LocalizationService.Instance["Settings.ImportDone"]);
    }

    private static string? ShowRealSettingsSaveDialog(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = LocalizationService.Instance["Dialog.Filter.Json"],
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? ShowRealSettingsOpenDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = LocalizationService.Instance["Dialog.Filter.Json"],
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? ShowRealFolderDialog()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = AppPaths.DataDirectory };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Moves every file this app itself keeps to a folder the user picked, in the order that actually
    /// matters: flush whatever debounced settings save is still pending, copy the token index on a
    /// worker thread (it can be large), then copy the settings, notification and history files on the
    /// UI thread, where no append or save can interleave, THEN write the pointer file, THEN redirect
    /// the stores this process already has open. The pointer must never point somewhere the copy did
    /// not actually reach, so a failed copy leaves the old folder in use. Nothing is ever deleted from
    /// the old folder; the user is told it can be removed by hand.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChooseDataFolder))]
    private async Task ChooseDataFolderAsync()
    {
        var destination = _askForDataFolder();
        if (destination is null)
            return;

        var source = _store.DataDirectory;
        if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            return; // already there

        _store.SaveNow(_settings); // drain a still-pending debounced save before anything else moves

        if (!AppPaths.TryEnsureWritableDirectory(destination))
        {
            _showMessage(LocalizationService.Instance["Settings.DataFolderRefused"]);
            return;
        }

        IsMovingData = true;
        try
        {
            try
            {
                await Task.Run(() => _copyIndex(source, destination));
                // Back on the UI thread: flush the settings, then copy the small files while nothing
                // else writes them.
                _store.SaveNow(_settings);
                _copySmallFiles(source, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // The copy did not fully land - the pointer below is never written, so this process (and
                // the next launch) keeps using the old folder exactly as before, whatever partial state
                // is now sitting in the destination the user picked.
                _showMessage(LocalizationService.Instance["Settings.DataFolderRefused"]);
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LocationPointerFile)!);
                File.WriteAllText(AppPaths.LocationPointerFile, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _showMessage(LocalizationService.Instance["Settings.DataFolderRefused"]);
                return;
            }

            AppPaths.SetOverride(destination);
            _store.Redirect(destination);
            Main.RedirectHistoryStore(destination);
            _historyStore.Redirect(destination);
            DataFolderPath = AppPaths.DataDirectory; // the settings row shows the new path without a restart

            _store.SaveNow(_settings); // now lands in the new folder - _store was just redirected above

            _showMessage(LocalizationService.Instance.Format("Settings.DataFolderMoved", destination));
        }
        finally
        {
            IsMovingData = false;
        }
    }

    private bool CanChooseDataFolder() => !IsMovingData;

    private static void CopyIndexFiles(string source, string destination)
    {
        // The token index is the only copy of past usage once the tools delete their own session
        // files, so it moves along; every StatsStore follows the data folder on its next open.
        Stats.StatsStore.CopyIndex(source, destination);
        CopyIfExists(Path.Combine(source, "stats.v4.bak"), Path.Combine(destination, "stats.v4.bak"));
    }

    private static void CopySmallFiles(string source, string destination)
    {
        CopyIfExists(Path.Combine(source, "settings.json"), Path.Combine(destination, "settings.json"));
        CopyIfExists(Path.Combine(source, "settings.json.bak"), Path.Combine(destination, "settings.json.bak"));
        CopyIfExists(Path.Combine(source, "notifications.json"), Path.Combine(destination, "notifications.json"));
        foreach (var historyFile in Directory.EnumerateFiles(source, "history-*.jsonl"))
            CopyIfExists(historyFile, Path.Combine(destination, Path.GetFileName(historyFile)));
        CopyIfExists(Path.Combine(source, "project-colors.json"), Path.Combine(destination, "project-colors.json"));
    }

    private static void CopyIfExists(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
            File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Reset to defaults, behind a confirmation prompt: every preference this window
    /// itself surfaces goes back to <see cref="AppSettings"/>'s own defaults and is re-applied to the
    /// live app immediately via <see cref="ApplyLoaded"/>. <paramref name="alsoDeleteHistory"/>,
    /// when true, additionally deletes every provider's recorded history - the confirmation dialog's
    /// own extra checkbox defaults this to false, so resetting preferences alone never touches it.
    /// </summary>
    public void ResetToDefaults(bool alsoDeleteHistory = false)
    {
        ApplyLoaded(new AppSettings());

        if (alsoDeleteHistory)
        {
            _historyStore.DeleteAll(Main.Tiles.Select(tile => tile.ProviderId).ToList());
            Main.RefreshHistoryForAllTiles();
        }
    }

    /// <summary>
    /// Copies every preference <paramref name="source"/> carries into the live <see cref="AppSettings"/>
    /// and re-applies each one to the running app immediately, then saves - the one code path behind
    /// both <see cref="ResetToDefaults"/> (source is a brand new <see cref="AppSettings"/>) and
    /// <see cref="ImportSettings"/> (source is whatever <see cref="SettingsStore.Validate"/> just
    /// accepted), so neither can leave the window and the settings file disagreeing with each other.
    /// <see cref="Models.AppSettings.Window"/> is deliberately never copied: a rectangle from a fresh
    /// default or from another machine is the one value that is certainly wrong for this one, so the
    /// running window always keeps its own place and size.
    /// </summary>
    private void ApplyLoaded(AppSettings source)
    {
        _settings.RefreshSeconds = source.RefreshSeconds;
        _settings.RemoteRefreshMinutes = source.RemoteRefreshMinutes;
        _settings.WindowLayer = WindowLayers.Normalize(source.WindowLayer);
        _settings.Layout = source.Layout;
        _settings.TileOrderMode = source.TileOrderMode;
        _settings.Theme = source.Theme;
        _settings.Language = source.Language;
        _settings.Autostart = source.Autostart;
        _settings.HistoryRetentionDays = source.HistoryRetentionDays;
        _settings.WindowOpacityPercent = source.WindowOpacityPercent;
        _settings.MicaEnabled = source.MicaEnabled;
        _settings.ChartRange = source.ChartRange;
        _settings.ShowPreviousWeekLine = source.ShowPreviousWeekLine;
        _settings.HideOnFullscreen = source.HideOnFullscreen;
        _settings.ZoomPercent = WindowZoom.Normalize(source.ZoomPercent);
        _settings.ShowTooltips = source.ShowTooltips;
        _settings.TileDensity = source.TileDensity;
        _settings.DayGridShownOnce = source.DayGridShownOnce;
        _settings.DefaultThreshold = source.DefaultThreshold;
        _settings.DefaultThresholdEnabled = source.DefaultThresholdEnabled;
        _settings.NotifyOnReset = source.NotifyOnReset;
        _settings.ForecastAlertEnabled = source.ForecastAlertEnabled;
        _settings.LimitReachedAlertEnabled = source.LimitReachedAlertEnabled;
        _settings.ShowAttentionMark = source.ShowAttentionMark;
        _settings.AttentionMaxAgeMinutes = source.AttentionMaxAgeMinutes;
        _settings.CheckForUpdates = source.CheckForUpdates;
        _settings.SaveEnergyOnBattery = source.SaveEnergyOnBattery;
        _settings.QuietHoursEnabled = source.QuietHoursEnabled;
        _settings.QuietWeekend = source.QuietWeekend;
        _settings.QuietHoursStart = source.QuietHoursStart;
        _settings.QuietHoursEnd = source.QuietHoursEnd;
        _settings.HotkeyEnabled = source.HotkeyEnabled;
        _settings.Hotkey = source.Hotkey;
        _settings.ClickThrough = source.ClickThrough;
        _settings.TrayProvider = source.TrayProvider;
        _settings.TrayWindow = source.TrayWindow;
        // Statistics window arrangement: only read when that window opens, so a copy here never
        // fights a live window.
        _settings.StatsSectionLayout = source.StatsSectionLayout?.Select(row => new Stats.StatsLayoutRow { Left = [.. row.Left], Right = [.. row.Right] }).ToList();
        _settings.StatsSectionsCollapsed = new Dictionary<string, bool>(source.StatsSectionsCollapsed ?? []);
        _settings.StatsPerDayView = source.StatsPerDayView;
        var previousProviders = _settings.Providers;
        _settings.Providers = source.Providers ?? AppSettings.CreateDefaultProviders();
        // A sign-out belongs to this machine (its browser profile is already gone), so neither a reset
        // nor an import may quietly resume reading an account the user disconnected here, or
        // disconnect one that is connected here.
        foreach (var providerSettings in _settings.Providers.Values)
            providerSettings.Disconnected = false;
        foreach (var (accountKey, previous) in previousProviders)
        {
            if (!previous.Disconnected)
                continue;
            if (!_settings.Providers.TryGetValue(accountKey, out var current))
                _settings.Providers[accountKey] = current = new ProviderSettings();
            current.Disconnected = true;
        }

        SetLayout(_settings.Layout);
        SetTileOrder(_settings.TileOrderMode);
        Main.WindowLayer = WindowLayers.Normalize(_settings.WindowLayer);
        Main.ClickThrough = _settings.ClickThrough;
        Main.ReloadTraySelection();
        Main.HotkeyEnabled = _settings.HotkeyEnabled;
        Main.HotkeyText = _settings.Hotkey;
        Main.UpdateRefreshInterval(_settings.RefreshSeconds);
        foreach (var tile in Main.Tiles)
        {
            var providerSettings = _settings.Providers.TryGetValue(tile.ProviderId, out var ps) ? ps : new ProviderSettings();
            Main.SetHidden(tile.ProviderId, hidden: !providerSettings.Visible);
            tile.ShowFiveHour = providerSettings.ShowFiveHour;
            tile.ShowWeekly = providerSettings.ShowWeekly;
            tile.ChartHidden = providerSettings.ChartHidden;
            tile.AccountName = providerSettings.AccountName;
            tile.AttentionDisabled = providerSettings.AttentionDisabled;
            tile.HiddenWindows = [.. providerSettings.HiddenWindows];
            tile.SyncWindowVisibility();
        }
        Main.SetHidden(AppSettings.DayGridTileId,
            hidden: !(_settings.Providers.TryGetValue(AppSettings.DayGridTileId, out var grid) && grid.Visible));
        Main.SetDensityCommand.Execute(_settings.TileDensity);

        if (Enum.TryParse<AppTheme>(_settings.Theme, ignoreCase: true, out var theme))
            SetThemeCommand.Execute(theme);

        RefreshSeconds = _settings.RefreshSeconds;
        RemoteRefreshMinutes = _settings.RemoteRefreshMinutes;
        HistoryRetentionDays = _settings.HistoryRetentionDays;
        WindowOpacityPercent = _settings.WindowOpacityPercent;
        MicaEnabled = _settings.MicaEnabled;
        Autostart = _settings.Autostart;
        ShowPreviousWeekLine = _settings.ShowPreviousWeekLine;
        HideOnFullscreen = _settings.HideOnFullscreen;
        ZoomPercent = _settings.ZoomPercent;
        ShowTooltips = _settings.ShowTooltips;
        DefaultThreshold = _settings.DefaultThreshold;
        DefaultThresholdEnabled = _settings.DefaultThresholdEnabled;
        NotifyOnReset = _settings.NotifyOnReset;
        ForecastAlertEnabled = _settings.ForecastAlertEnabled;
        LimitReachedAlertEnabled = _settings.LimitReachedAlertEnabled;
        ShowAttentionMark = _settings.ShowAttentionMark;
        CheckForUpdates = _settings.CheckForUpdates;
        SaveEnergyOnBattery = _settings.SaveEnergyOnBattery;
        QuietHoursEnabled = _settings.QuietHoursEnabled;
        QuietWeekend = _settings.QuietWeekend;
        QuietHoursStartText = _settings.QuietHoursStart;
        QuietHoursEndText = _settings.QuietHoursEnd;
        QuietHoursStartInvalid = false;
        QuietHoursEndInvalid = false;
        SetLanguageCommand.Execute(_settings.Language);
        SetChartRangeCommand.Execute(_settings.ChartRange);
        Choice.Select(AttentionMaxAgeChoices, AttentionMaxAgeSelection(_settings));
        OnPropertyChanged(nameof(SelectedAttentionMaxAgeChoice));

        RebuildProviderRows();

        _store.SaveNow(_settings);
    }

    private void SaveProviderSettings()
    {
        _store.RequestSave(_settings);
        Main.ApplyThresholdsToAllTiles();
    }

    private void RebuildChartRangeChoices()
    {
        var available = SettingsRanges.AvailableChartRanges(HistoryRetentionDays);
        var selected = available.Any(o => o.Value == _settings.ChartRange) ? _settings.ChartRange : available[0].Value;

        ChartRangeChoices.Clear();
        foreach (var option in available)
            ChartRangeChoices.Add(new Choice<string>(option.LabelKey, option.Value) { IsSelected = option.Value == selected });
        OnPropertyChanged(nameof(SelectedChartRangeChoice));

        if (selected != _settings.ChartRange)
        {
            _settings.ChartRange = selected;
            _store.RequestSave(_settings);
        }
    }

    private static Choice<AppTheme> BuildThemeChoice(string labelKey, AppTheme theme, Func<Uri, ResourceDictionary> load, string selectedTheme)
    {
        // AppTheme.System has no dictionary of its own in ThemeUris - its swatch previews whichever
        // of Light/Dark Windows is currently using, the same resolution ThemeService.Apply itself
        // would pick.
        var previewTheme = theme == AppTheme.System
            ? (ThemeService.IsWindowsUsingLightTheme() ? AppTheme.Light : AppTheme.Dark)
            : theme;
        var dictionary = load(ThemeUris[previewTheme]);
        if (theme == AppTheme.System)
            ThemeService.ApplySystemAccent(dictionary, previewTheme == AppTheme.Dark, AccentColors.ReadSystemAccent);
        return new Choice<AppTheme>(labelKey, theme, dictionary["Bg.Base"] as Brush, dictionary["Accent"] as Brush)
        {
            IsSelected = string.Equals(selectedTheme, theme.ToString(), StringComparison.OrdinalIgnoreCase)
        };
    }

    // Default production loader: the same relative pack-URI load ThemeService.Apply's own default
    // uses. Falls back to an empty dictionary (no preview colours, never an exception) when there is
    // no live Application - the one test that exercises the public constructor runs with none, and a
    // missing key simply reads back null through ResourceDictionary's own indexer.
    private static ResourceDictionary LoadThemeDictionary(Uri uri) =>
        Application.Current is not null ? new ResourceDictionary { Source = uri } : new ResourceDictionary();
}
