using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;
using AiUsage.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiUsage.ViewModels;

/// <summary>
/// The window's one view model: owns the live provider list,
/// layout/visibility/density settings, and the glue between <see cref="RefreshScheduler"/> and
/// <see cref="HistoryStore"/>. A hidden provider keeps being fetched and written - only its tile
/// disappears (a deliberate design choice) - so hiding never touches the scheduler, only
/// <see cref="ProviderTileViewModel.IsHidden"/> and the isVisible predicate passed to every tick.
/// Timing (a <see cref="System.Windows.Threading.DispatcherTimer"/>) stays in MainWindow's
/// code-behind, same split as the window/monitor glue there - this type only needs a plain
/// <see cref="Tick"/> method to stay testable without a live Dispatcher.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryStore _historyStore;
    private readonly RefreshScheduler _scheduler;
    private readonly NotificationService _notifications = new();
    private readonly Dictionary<string, ProviderTileViewModel> _tilesById;
    private readonly LogService? _logService;

    // The status each account's last snapshot carried, so a repeated one is not logged again.
    private readonly ConcurrentDictionary<string, ProviderStatus> _lastStatusById = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>The token usage index, read only for the weekly line each tile's <see
    /// cref="ProviderTileViewModel.WeekTokens"/> shows - null in every test that never passes one
    /// (see the test-seam constructor), which simply turns <see cref="RefreshWeekTokens"/> into a
    /// no-op rather than touching a real database no test asked for.</summary>
    private readonly StatsStore? _statsStore;

    /// <summary>Same cadence as a web provider's own <see cref="Services.IUsageProvider.MinRefreshInterval"/>
    /// floor - a weekly total does not need to be any fresher than that, and querying it on every
    /// one-second <see cref="Tick"/> would just be wasted SQLite work.</summary>
    private static readonly TimeSpan WeekTokensQueryInterval = TimeSpan.FromMinutes(5);

    /// <summary>Per real provider id (not per account - two Claude accounts would otherwise double
    /// the query for numbers <see cref="Stats.StatsStore"/> cannot even tell apart by account), the
    /// last time <see cref="RefreshWeekTokens"/> actually queried. Empty at construction, so the very
    /// first <see cref="Tick"/> queries every eligible provider right away instead of waiting out the
    /// first interval.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastWeekTokensQueryAt = new();

    // A fetch completing in a handful of milliseconds must still show its "working" indicator long
    // enough to be seen rather than flicker - each started fetch is timestamped here,
    // and IsFetching is only actually cleared once MinFetchingDuration has passed since. A provider
    // still short of the floor when its snapshot lands is re-checked on every later Tick instead of a
    // real timer, so this stays deterministically testable against the injected TimeProvider.
    private static readonly TimeSpan MinFetchingDuration = TimeSpan.FromMilliseconds(500);
    private readonly Dictionary<string, DateTimeOffset> _fetchStartedAt = new();
    private readonly HashSet<string> _pendingFetchClear = new();

    // Passed to every scheduler fetch and to the maintenance loop, so a shutdown actually stops a
    // running file scan or WebView request instead of leaving it going against a half-torn-down app.
    // Cancelled and disposed by DisposeAsync, never anywhere else.
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>Owned by this view model, keyed by account key, so sign-out can dispose exactly the
    /// one account's own runner before its profile folder gets deleted, without touching any other
    /// account's still-live session. Typed as the plain interface (production always hands in every
    /// <see cref="WebSessionScriptRunner"/> ProviderRegistry created) so a test can substitute a fake
    /// that records disposal order without needing a live WebView2.</summary>
    private readonly Dictionary<string, IAsyncDisposable> _webRunners;

    /// <summary>Forwarded from <see cref="NotificationService"/> - MainWindow shows it as a tray
    /// balloon. Dropped entirely during <see cref="QuietHours"/> (see <see
    /// cref="ForwardThresholdNotification"/>): the tile and tray colour still change, so the
    /// information is never lost, only the noise, and nothing queues up to arrive in a burst once
    /// quiet hours end.</summary>
    public event Action<ThresholdNotification>? NotificationRaised;

    /// <summary>Forwarded from <see cref="NotificationService"/>, same split as <see
    /// cref="NotificationRaised"/> (quiet hours included) - only ever raised at all when <see
    /// cref="Models.AppSettings.NotifyOnReset"/> is on (see <see cref="OnSnapshotReady"/>).</summary>
    public event Action<ResetNotification>? ResetRaised;

    private void ForwardThresholdNotification(ThresholdNotification notification)
    {
        if (!QuietHours.IsQuiet(_settings, DateTimeOffset.Now))
            NotificationRaised?.Invoke(notification);
    }

    private void ForwardResetNotification(ResetNotification notification)
    {
        if (!QuietHours.IsQuiet(_settings, DateTimeOffset.Now))
            ResetRaised?.Invoke(notification);
    }

    public ObservableCollection<ProviderTileViewModel> Tiles { get; } = [];

    /// <summary>Every row the widget actually lays out, in on-screen order: every <see
    /// cref="ProviderTileViewModel"/> from <see cref="Tiles"/> plus <see cref="DayGridTile"/>,
    /// interleaved by their shared <see cref="Models.ProviderSettings.Order"/> (see <see
    /// cref="RebuildDisplayRowsInitial"/>/<see cref="MoveRow"/>). MainWindow's ItemsControl and the
    /// eye popup both bind this instead of <see cref="Tiles"/> - everything else (the scheduler, the
    /// tray, the Settings window's provider list) keeps reading <see cref="Tiles"/> unchanged, since
    /// none of that is about the day-grid tile at all.</summary>
    public ObservableCollection<object> DisplayRows { get; } = [];

    /// <summary>The widget's own "Verbrauch je Tag"/"Usage per day" tile - one instance for the whole
    /// process lifetime, always present in <see cref="DisplayRows"/> (hidden or not, the same "stays
    /// in the list, only the container collapses" contract every provider tile already follows).</summary>
    public DayGridTileViewModel DayGridTile { get; }

    /// <summary>The "a newer version exists" notice and the install request behind it; read by the
    /// bar in this window and by the About page.</summary>
    public UpdateNoticeViewModel Update { get; }

    /// <summary>Forwarded from <see cref="DayGridTileViewModel.DaySelected"/> - MainWindow opens (or
    /// focuses) the statistics window and selects this day there.</summary>
    public event EventHandler<DateOnly>? DayGridDaySelected;

    /// <summary>Drives the title bar's own refresh button spinner - true while any tile is fetching,
    /// not only the one a per-tile button started. Re-raised from each tile's own PropertyChanged
    /// (wired up next to the HideRequested/RefreshRequested subscriptions below), since IsFetching
    /// lives on the tile, not here.</summary>
    public bool IsAnyFetching => Tiles.Any(t => t.IsFetching);

    /// <summary>Set by MainWindow from its own Visibility/WindowState (hidden into the tray, or
    /// minimized, both count as not visible) - feeds <see cref="RefreshScheduler.ResolveInterval"/> so
    /// every provider slows down while nobody can see a tile at all, not only the ones individually
    /// hidden. Defaults true: a view model with no window wired up yet (every test) polls at the
    /// normal cadence rather than the closed-window one.</summary>
    public bool WindowVisible { get; set; } = true;

    /// <summary>Set by MainWindow while the Windows session is locked: nobody can see a tile then,
    /// whatever the window itself says.</summary>
    public bool SessionLocked { get; set; }

    /// <summary>Whether anyone can actually be looking at the widget: its window is on screen and the
    /// session is not locked. Pulled out as a pure predicate so the decision is testable without a
    /// live window.</summary>
    internal static bool IsAttended(bool windowVisible, bool sessionLocked) =>
        windowVisible && !sessionLocked;

    public ObservableCollection<Choice<string>> DensityChoices { get; } =
    [
        new("Density.Auto", "Auto"),
        new("Density.Full", "Full"),
        new("Density.Mini", "Mini"),
    ];

    /// <summary>The eye menu's density picker is a <c>ComboBox</c> (see <c>TitleBar.xaml</c>) bound to
    /// this instead of driving <see cref="DensityChoices"/>' own <see cref="Choice{TValue}.IsSelected"/>
    /// flags through a radio group - a set forwards to the existing <see cref="SetDensity"/> and is
    /// kept in step by the same <see cref="UpdateDensitySelection"/> call every other density change
    /// already goes through, so the picker and a settings-driven change never disagree.</summary>
    public Choice<string>? SelectedDensityChoice
    {
        get => DensityChoices.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is not null && value.Value != DensityMode)
                SetDensity(value.Value);
        }
    }

    public const string TrayProviderAuto = "Auto";

    /// <summary>The tray choice that shows the plain program icon, without a number.</summary>
    public const string TrayProviderAppIcon = "AppIcon";

    /// <summary>The settings window's "tray icon shows" picker: automatic (the highest usage) first,
    /// then one row per tile in the tiles' own order, rebuilt whenever that list or a tile's name
    /// changes.</summary>
    public ObservableCollection<Choice<string>> TrayProviderChoices { get; } = [];

    /// <summary>Whose usage the tray icon shows, see <see cref="AppSettings.TrayProvider"/>.</summary>
    public string TrayProvider => _settings.TrayProvider;

    /// <summary>A stored provider that no tile carries any more (a removed account) shows as
    /// automatic here, which is also what the tray icon falls back to for it.</summary>
    public Choice<string>? SelectedTrayProviderChoice
    {
        get => TrayProviderChoices.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is null || value.Value == _settings.TrayProvider)
                return;
            _settings.TrayProvider = value.Value;
            Choice.Select(TrayProviderChoices, value.Value);
            _settingsStore.RequestSave(_settings);
            OnPropertyChanged(nameof(SelectedTrayProviderChoice));
            OnPropertyChanged(nameof(TrayProvider));
            OnPropertyChanged(nameof(TrayWindowEnabled));
            RebuildTrayWindowChoices();
        }
    }

    /// <summary>The window list only matters while the icon follows one chosen provider: on the plain
    /// program icon there is no number, and "automatic" picks the window on its own too.</summary>
    public bool TrayWindowEnabled => _settings.TrayProvider != TrayProviderAppIcon && _settings.TrayProvider != TrayProviderAuto;

    /// <summary>The window the tray icon actually follows: the stored choice only while the list is
    /// offered, otherwise null (the highest window).</summary>
    public string? EffectiveTrayWindow => TrayWindowEnabled ? _settings.TrayWindow : null;

    /// <summary>Re-reads both tray pickers from the settings after an import or a reset replaced
    /// them underneath this view model.</summary>
    internal void ReloadTraySelection()
    {
        RebuildTrayProviderChoices();
        OnPropertyChanged(nameof(TrayProvider));
        OnPropertyChanged(nameof(TrayWindowEnabled));
        OnPropertyChanged(nameof(TrayWindow));
    }

    private void RebuildTrayProviderChoices()
    {
        TrayProviderChoices.Clear();
        TrayProviderChoices.Add(new Choice<string>("Settings.TrayProvider.Auto", TrayProviderAuto));
        TrayProviderChoices.Add(new Choice<string>("Settings.TrayProvider.AppIcon", TrayProviderAppIcon));
        foreach (var tile in Tiles)
            TrayProviderChoices.Add(Choice.WithFixedLabel(tile.HeaderDisplayName, tile.ProviderId));

        var stored = TrayProviderChoices.Any(c => c.Value == _settings.TrayProvider) ? _settings.TrayProvider : TrayProviderAuto;
        Choice.Select(TrayProviderChoices, stored);
        OnPropertyChanged(nameof(SelectedTrayProviderChoice));
        RebuildTrayWindowChoices();
    }

    /// <summary>The picker right of <see cref="TrayProviderChoices"/>: automatic (the highest window)
    /// first, then every window kind the chosen provider has reported - on automatic provider, every
    /// kind any tile has reported.</summary>
    public ObservableCollection<Choice<string>> TrayWindowChoices { get; } = [];

    /// <summary>Which window the tray icon shows, see <see cref="AppSettings.TrayWindow"/>.</summary>
    public string TrayWindow => _settings.TrayWindow;

    /// <summary>A stored window the chosen provider does not report shows as automatic here, which
    /// is also what the tray icon falls back to for it; the stored value itself is kept, so it comes
    /// back once that window is reported again.</summary>
    public Choice<string>? SelectedTrayWindowChoice
    {
        get => TrayWindowChoices.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is null || value.Value == _settings.TrayWindow)
                return;
            _settings.TrayWindow = value.Value;
            Choice.Select(TrayWindowChoices, value.Value);
            _settingsStore.RequestSave(_settings);
            OnPropertyChanged(nameof(SelectedTrayWindowChoice));
            OnPropertyChanged(nameof(TrayWindow));
        }
    }

    private static readonly (WindowKind Kind, string LabelKey)[] TrayWindowKinds =
    [
        (WindowKind.FiveHour, "Window.FiveHour"),
        (WindowKind.Weekly, "Window.Weekly"),
    ];

    /// <summary>Prefix a tray window choice's own <see cref="Choice{TValue}.Value"/> carries for one
    /// specific Other row (see <see cref="RebuildTrayWindowChoices"/>) - <see
    /// cref="Views.MainWindow.TryComputeTraySummary"/> strips it back off to match rows by <see
    /// cref="ViewModels.UsageRowViewModel.Label"/> rather than by <see cref="WindowKind"/>, since
    /// every such row shares the one <see cref="WindowKind.Other"/> kind.</summary>
    internal const string TrayWindowLabelPrefix = "Label:";

    /// <summary>The picker's rows beyond Auto/FiveHour/Weekly: one per distinct Other-row label the
    /// chosen provider (or, on automatic provider, any tile) has reported, display text = that row's
    /// own resolved name - replaces the old single "Sonstiges" entry, which could not tell two
    /// different Other rows (Cursor's model breakdown, for instance) apart. A stored value of the
    /// old "Other" (from a settings file predating this) falls back to automatic, same as any other
    /// value the current tiles no longer report.</summary>
    private void RebuildTrayWindowChoices()
    {
        var pinned = Tiles.FirstOrDefault(t => t.ProviderId == _settings.TrayProvider);
        var tiles = (pinned is null ? Tiles : [pinned]).ToList();
        var reportedKinds = tiles.SelectMany(t => t.KnownWindowKinds).ToHashSet();

        TrayWindowChoices.Clear();
        TrayWindowChoices.Add(new Choice<string>("Settings.TrayWindow.Auto", TrayProviderAuto));
        foreach (var (kind, labelKey) in TrayWindowKinds)
        {
            if (reportedKinds.Contains(kind))
                TrayWindowChoices.Add(new Choice<string>(labelKey, kind.ToString()));
        }

        var otherToggles = tiles.SelectMany(t => t.WindowToggles)
            .Where(t => t.Kind == WindowKind.Other)
            .GroupBy(t => t.Label)
            .Select(g => g.First());
        foreach (var toggle in otherToggles)
            TrayWindowChoices.Add(Choice.WithFixedLabel(toggle.DisplayText, TrayWindowLabelPrefix + toggle.Label));

        var stored = _settings.TrayWindow == "Other" ? TrayProviderAuto
            : TrayWindowChoices.Any(c => c.Value == _settings.TrayWindow) ? _settings.TrayWindow : TrayProviderAuto;
        Choice.Select(TrayWindowChoices, stored);
        OnPropertyChanged(nameof(SelectedTrayWindowChoice));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHorizontal))]
    private string layout;

    [ObservableProperty]
    private string densityMode;

    [ObservableProperty]
    private bool heightAutomatic;

    [ObservableProperty]
    private bool widthAutomatic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EyeButtonAccessibleName))]
    private int hiddenCount;

    /// <summary>Screen-reader name for the title bar's eye button: the visible badge number has no
    /// automation peer of its own, so without this the hidden count is announced to nobody.
    /// Reuses the "Eye.HiddenCount" resx entry the visual badge already
    /// carries in spirit.</summary>
    public string EyeButtonAccessibleName => HiddenCount > 0
        ? $"{LocalizationService.Instance["Eye.Menu"]}, {LocalizationService.Instance.Format("Eye.HiddenCount", HiddenCount)}"
        : LocalizationService.Instance["Eye.Menu"];

    [ObservableProperty]
    private bool isEmpty;

    [ObservableProperty]
    private bool alwaysOnTop;

    /// <summary>The click-through overlay mode (persisted in <see
    /// cref="Models.AppSettings.ClickThrough"/>) - MainWindow alone applies the actual extended
    /// window style and the always-on-top/opacity implications (<see cref="Services.ClickThroughPolicy"/>),
    /// same split as <see cref="AlwaysOnTop"/>.</summary>
    [ObservableProperty]
    private bool clickThrough;

    [ObservableProperty]
    private bool hotkeyEnabled;

    [ObservableProperty]
    private string hotkeyText = "";

    /// <summary>Set by MainWindow after a registration attempt: true only while the combination in
    /// <see cref="HotkeyText"/> is both enabled and already claimed by another program.</summary>
    [ObservableProperty]
    private bool hotkeyTaken;

    /// <summary>Set by the Settings window's own capture box when the user releases a bare key with
    /// no modifier held - a transient capture-time warning, never persisted.</summary>
    [ObservableProperty]
    private bool hotkeyNeedsModifier;

    /// <summary>Set once at startup from <see cref="SettingsStore.LastLoadWasFromNewerVersion"/>.
    /// The "Settings.NewerFile" resx string existed but was never actually shown
    /// anywhere, so a downgrade silently ran on defaults with no visible explanation why.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewerVersionWarningText))]
    private bool showNewerVersionWarning;

    public string NewerVersionWarningText => ShowNewerVersionWarning ? LocalizationService.Instance["Settings.NewerFile"] : "";

    public bool IsHorizontal => string.Equals(Layout, "Horizontal", StringComparison.OrdinalIgnoreCase);

    /// <summary>The current arrangement's own remembered width/height - vertical and horizontal never
    /// share these, see <see cref="Models.WindowSettings.Vertical"/>.</summary>
    private LayoutSizes CurrentSizes => IsHorizontal ? _settings.Window.Horizontal : _settings.Window.Vertical;

    public MainViewModel(SettingsStore settingsStore, AppSettings settings, LogService? logService = null)
        : this(settingsStore, settings, ProviderRegistry.CreateAll(settings, settingsStore, logService), logService)
    {
    }

    private MainViewModel(
        SettingsStore settingsStore, AppSettings settings,
        (IReadOnlyList<IUsageProvider> Providers, IReadOnlyDictionary<string, WebSessionScriptRunner> Runners) created, LogService? logService)
        : this(settingsStore, settings, created.Providers, new HistoryStore(), claudeRunner: null, logService, statsStore: new StatsStore())
    {
        // Every account's own runner, keyed by its account key - see _webRunners. Assigned here
        // rather than threaded through the test-seam constructor below, so that constructor's single
        // optional claudeRunner stays the simple shape every existing test already uses.
        foreach (var (accountKey, runner) in created.Runners)
            _webRunners[accountKey] = runner;
    }

    /// <summary>Test seam: caller-supplied providers (no real network/file access) and history store.
    /// <paramref name="claudeRunner"/> is stored under the default install's own account key (see
    /// <see cref="AppSettings.CreateDefaultAccounts"/>) in <see cref="_webRunners"/> - the one this
    /// instance owns and disposes on sign-out (see <see cref="SignOutAsync"/>) - null in tests that
    /// never exercise that path.
    /// <paramref name="logService"/> null keeps every test silent. <paramref name="timeProvider"/>
    /// null uses the real clock - a test injects a fake one to make the IsFetching minimum-visible-
    /// duration (see <see cref="MinFetchingDuration"/>) deterministic. <paramref name="statsStore"/>
    /// null leaves the weekly token line permanently off (no real store, so <see
    /// cref="RefreshWeekTokens"/> never runs) - the shape every existing test keeps compiling with;
    /// production wires up a real one (see the two constructors above).</summary>
    internal MainViewModel(
        SettingsStore settingsStore, AppSettings settings, IReadOnlyList<IUsageProvider> providers,
        HistoryStore historyStore, IAsyncDisposable? claudeRunner = null, LogService? logService = null,
        TimeProvider? timeProvider = null, StatsStore? statsStore = null)
    {
        _logService = logService;
        _webRunners = new Dictionary<string, IAsyncDisposable>();
        if (claudeRunner is not null)
            _webRunners[AppSettings.CreateDefaultAccounts().Keys.Single()] = claudeRunner;
        _settingsStore = settingsStore;
        _settings = settings;
        // Dispatches on the account key's own base provider id ("codex#2" -> "codex"), never a literal
        // equality check against one fixed key - the same generalisation ProviderRegistry.WebSessionFor
        // itself went through, so a further Codex/Cursor/Gemini account (not only Claude's) builds
        // through this same seam. Copilot never reaches this delegate: it has no web session at all,
        // so MainViewModel.AddAccount routes it to AddCopilotAccountAsync instead.
        CreateWebAccount = accountKey => ProviderRegistry.BaseProviderId(accountKey) switch
        {
            "codex" => ProviderRegistry.CreateCodexAccount(accountKey, _settings, _settingsStore, _logService),
            "cursor" => ProviderRegistry.CreateCursorAccount(accountKey, _settings, _settingsStore, _logService),
            "gemini" => ProviderRegistry.CreateGeminiAccount(accountKey, _settings, _settingsStore, _logService),
            _ => ProviderRegistry.CreateClaudeAccount(accountKey, _settings, _settingsStore),
        };
        _historyStore = historyStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _statsStore = statsStore;
        Update = new UpdateNoticeViewModel(settings, settingsStore.RequestSave);
        layout = settings.Layout;
        densityMode = settings.TileDensity;
        heightAutomatic = CurrentSizes.Height is null;
        widthAutomatic = !CurrentSizes.WidthIsManual;
        alwaysOnTop = settings.AlwaysOnTop;
        clickThrough = settings.ClickThrough;
        hotkeyEnabled = settings.HotkeyEnabled;
        hotkeyText = settings.Hotkey;
        UpdateDensitySelection();

        _tilesById = new Dictionary<string, ProviderTileViewModel>();
        // Ascending by each provider's own ProviderSettings.Order (see MoveProvider) - Enumerable.OrderBy
        // is a stable sort, so providers with no settings entry at all (every test that never went
        // through SettingsStore.Load) keep the caller's own order instead of being shuffled by a tie.
        foreach (var provider in providers.OrderBy(GetProviderOrder))
            BuildTile(provider);
        RebuildTrayProviderChoices();
        Tiles.CollectionChanged += (_, _) => RebuildTrayProviderChoices();

        DayGridTile = new DayGridTileViewModel(_statsStore)
        {
            IsHidden = !(_settings.Providers.TryGetValue(AppSettings.DayGridTileId, out var gridSettings) && gridSettings.Visible),
        };
        DayGridTile.DaySelected += (_, day) => DayGridDaySelected?.Invoke(this, day);
        DayGridTile.HideRequested += (_, _) => ToggleHidden(AppSettings.DayGridTileId);
        RebuildDisplayRowsInitial();
        WireStatsIndexerForDayGrid();
        RefreshDayGridIfShown();

        RefreshHiddenCount();
        RefreshMoveEligibility();

        // Compaction/pruning used to run right here, synchronously, once per launch - but this
        // constructor runs on the UI thread inside MainWindow's own constructor, before the window
        // is shown, so a cold start waited for a full read-rewrite of every history file. Moved to
        // RunMaintenanceAsync, started once the window is up and repeated every 24h for the process
        // lifetime instead of only once per launch - this app runs as an autostart tray widget for
        // weeks at a time, so "once at startup" alone never enforces retention.
        _scheduler = new RefreshScheduler(providers, TimeSpan.FromSeconds(settings.RefreshSeconds),
            log: line => (_logService ?? LogService.Shared).LogError(line));
        _scheduler.SnapshotReady += OnSnapshotReady;
        _scheduler.FetchStarted += OnFetchStarted;
        _scheduler.FetchEnded += OnFetchEnded;
        _notifications.NotificationRaised += ForwardThresholdNotification;
        _notifications.ResetRaised += ForwardResetNotification;

        // MainViewModel lives for the whole process (one instance, created once in MainWindow's own
        // constructor) - never unsubscribed, same as TrayService's identical subscription.
        LocalizationService.Instance.PropertyChanged += (_, _) => RefreshLocalizedText();
    }

    /// <summary>Builds one tile for <paramref name="provider"/>, wires its events and adds it to both
    /// <see cref="_tilesById"/> (keyed by <see cref="Services.IUsageProvider.AccountKey"/>, never by
    /// <see cref="Services.IUsageProvider.Id"/> - two accounts of the same provider share an Id) and
    /// <see cref="Tiles"/>. Used by the constructor for every provider ProviderRegistry built at
    /// startup, and again by <see cref="AddAccount"/> for one built afterward.</summary>
    private void BuildTile(IUsageProvider provider)
    {
        var visible = !_settings.Providers.TryGetValue(provider.AccountKey, out var providerSettings) || providerSettings.Visible;
        var tile = new ProviderTileViewModel(provider.AccountKey, provider.DisplayName, provider.Id)
        {
            IsHidden = !visible,
            SupportsInAppSignIn = provider.SupportsInAppSignIn,
            DisconnectsLocalSignIn = provider.SupportsSignOut,
            SupportsAddAccount = provider.SupportsMultipleAccounts,
            ReadLocations = provider.ReadLocations,
            ShowFiveHour = providerSettings?.ShowFiveHour ?? true,
            ShowWeekly = providerSettings?.ShowWeekly ?? true,
            ChartHidden = providerSettings?.ChartHidden ?? false,
            AttentionDisabled = providerSettings?.AttentionDisabled ?? false,
            HiddenWindows = [.. providerSettings?.HiddenWindows ?? []],
        };
        tile.HideRequested += (_, _) => ToggleHidden(tile.ProviderId);
        tile.RefreshRequested += (_, _) => RefreshProvider(tile.ProviderId);
        tile.ShowFiveHourChanged += (_, value) => SetShowFiveHour(tile.ProviderId, value);
        tile.ShowWeeklyChanged += (_, value) => SetShowWeekly(tile.ProviderId, value);
        tile.ChartHiddenChanged += (_, value) => SetChartHidden(tile.ProviderId, value);
        tile.AttentionDisabledChanged += (_, value) => SetAttentionDisabled(tile.ProviderId, value);
        tile.OtherWindowVisibilityChanged += (_, _) => SetHiddenWindows(tile.ProviderId, tile.HiddenWindows);
        tile.WindowToggles.CollectionChanged += (_, _) =>
        {
            if (Tiles.Contains(tile))
                RebuildTrayWindowChoices();
        };
        tile.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProviderTileViewModel.IsFetching))
                OnPropertyChanged(nameof(IsAnyFetching));
            else if (e.PropertyName == nameof(ProviderTileViewModel.HeaderDisplayName) && Tiles.Contains(tile))
                RebuildTrayProviderChoices();
            else if (e.PropertyName == nameof(ProviderTileViewModel.KnownWindowKinds) && Tiles.Contains(tile))
                RebuildTrayWindowChoices();
        };
        _tilesById[provider.AccountKey] = tile;
        Tiles.Add(tile);
        // Mirrors Tiles.Add: a freshly built tile (startup, or a further account added later) always
        // joins at the very end of the on-screen order too - RebuildDisplayRowsInitial (constructor
        // only) then re-sorts everything, including this one, by each row's own persisted Order.
        DisplayRows.Add(tile);
    }

    /// <summary>Builds the widget's initial on-screen order from scratch: every row currently in <see
    /// cref="Tiles"/> plus <see cref="DayGridTile"/>, sorted ascending by each row's own persisted
    /// <see cref="Models.ProviderSettings.Order"/> - a row with no settings entry at all (the day-grid
    /// tile before it is ever shown or moved for the first time) sorts to -1, ahead of every real
    /// provider, which is exactly the "first show lands at the very top" contract the spec asks for.
    /// Called once, right after the day-grid tile is built; every later structural change (an account
    /// added or removed, a reorder) updates <see cref="DisplayRows"/> incrementally instead.</summary>
    private void RebuildDisplayRowsInitial()
    {
        var entries = new List<(int Order, object Row)>();
        foreach (var tile in Tiles)
            entries.Add((ResolveRowOrder(tile.ProviderId), tile));
        entries.Add((ResolveRowOrder(AppSettings.DayGridTileId), DayGridTile));

        DisplayRows.Clear();
        foreach (var (_, row) in entries.OrderBy(entry => entry.Order))
            DisplayRows.Add(row);
    }

    /// <summary>A row with an explicit settings entry sorts by its own <see
    /// cref="ProviderSettings.Order"/>; a row with none at all (only ever the day-grid tile, before
    /// its very first reveal - every real provider already has an entry from <see
    /// cref="AppSettings.CreateDefaultProviders"/>) sorts last, so a fresh install's widget looks
    /// exactly like it did before this tile existed. <see cref="SetHidden"/> is the one place that
    /// then moves it to the very top the first time it is shown, per spec.</summary>
    private int ResolveRowOrder(string id) => _settings.Providers.TryGetValue(id, out var settings) ? settings.Order : int.MaxValue;

    /// <summary>Starts the background walk over the local session logs (a no-op while one is already
    /// running) and reloads the day-grid tile every time it finishes, so the tile shows real numbers
    /// without the statistics window ever having to be opened first - the only other place that starts
    /// this walk today (<see cref="Views.StatsWindow"/>) only runs while that window is open.</summary>
    private void WireStatsIndexerForDayGrid()
    {
        if (StatsIndexerService.Shared is not { } indexer)
            return;

        indexer.IndexCompleted += (_, _) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(RefreshDayGridIfShown);
            else
                RefreshDayGridIfShown();
        };
        indexer.StartInBackground();
    }

    /// <summary>A hidden day-grid tile skips the reload (it reads the whole year from the index);
    /// showing it loads it then, see <see cref="SetHidden"/>.</summary>
    private void RefreshDayGridIfShown()
    {
        if (!DayGridTile.IsHidden)
            DayGridTile.Refresh();
    }

    /// <summary>A language switch must reach every tile and the density menu immediately,
    /// not only on the next refresh tick.</summary>
    private void RefreshLocalizedText()
    {
        foreach (var choice in DensityChoices)
            choice.RefreshLabel();
        foreach (var choice in TrayProviderChoices)
            choice.RefreshLabel();
        foreach (var tile in Tiles)
            tile.RefreshLocalizedText();
        DayGridTile.RefreshLocalizedText();
        // A plain per-row refresh, not RebuildTrayWindowChoices - this runs off SetLanguage, which a
        // caller can invoke from off the dispatcher thread (see LocalizationServiceTests); Clear()/Add()
        // against a WPF-bound ObservableCollection would throw there, a property write would not.
        foreach (var choice in TrayWindowChoices)
        {
            choice.RefreshLabel();
            if (!choice.Value.StartsWith(TrayWindowLabelPrefix, StringComparison.Ordinal))
                continue;
            // The Label:<label> rows carry a fixed display text captured when they were built (see
            // RebuildTrayWindowChoices), not a resource key RefreshLabel() could re-resolve - the
            // tiles' own WindowToggles just refreshed their DisplayText a few lines up, so this picks
            // up the new language for those rows too.
            var label = choice.Value[TrayWindowLabelPrefix.Length..];
            var displayText = Tiles.SelectMany(t => t.WindowToggles).FirstOrDefault(t => t.Label == label)?.DisplayText;
            if (displayText is not null)
                choice.Label = displayText;
        }
        OnPropertyChanged(nameof(EyeButtonAccessibleName));
        OnPropertyChanged(nameof(NewerVersionWarningText));
    }

    /// <summary>
    /// Called once a second by MainWindow's DispatcherTimer. A hidden provider or a closed/minimized
    /// window never skips a fetch outright - both only ever lengthen that provider's interval (see
    /// <see cref="RefreshScheduler.ResolveInterval"/>), so history stays continuous, just coarser.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var tile in Tiles)
            tile.TickCountdowns(now);
        // A provider whose snapshot landed before its minimum visible duration was up gets
        // re-checked here every second instead of on a real timer - see MinFetchingDuration.
        foreach (var providerId in _pendingFetchClear.ToList())
            TryClearFetching(providerId);
        _scheduler.Tick(IsTileHidden, IsAttended(WindowVisible, SessionLocked), IsAccountDisconnected, _lifetimeCts.Token);
        RefreshWeekTokens(now);
    }

    /// <summary>F5 / tray "refresh now" - bypasses every provider's remaining wait once.</summary>
    public void RefreshNow() => _scheduler.RefreshNow(IsTileHidden, IsAttended(WindowVisible, SessionLocked), IsAccountDisconnected, _lifetimeCts.Token);

    /// <summary>Queries <see cref="_statsStore"/> for every tile whose real provider <see
    /// cref="ProviderCoverage.HasLocalTokenData"/> says has a local token index, at most once per
    /// <see cref="WeekTokensQueryInterval"/> per real provider - a no-op entirely when no store was
    /// wired up (see the test-seam constructor). The query itself runs on the thread pool, same
    /// "read off the UI thread, only the landed result comes back to it" split as <see
    /// cref="RefreshTileHistory"/>; two accounts of the same provider share one query and one
    /// answer, since <see cref="Stats.StatsStore"/> indexes by provider id, not by account.</summary>
    private void RefreshWeekTokens(DateTimeOffset now)
    {
        if (_statsStore is not { } store)
            return;

        var localNow = now.LocalDateTime;
        var weekStart = StatsAggregator.WeekStart(DateOnly.FromDateTime(localNow));

        foreach (var providerId in Tiles.Select(t => t.RealProviderId).Distinct())
        {
            if (!ProviderCoverage.HasLocalTokenData(providerId))
                continue;
            if (_lastWeekTokensQueryAt.TryGetValue(providerId, out var last) && now - last < WeekTokensQueryInterval)
                continue;

            _lastWeekTokensQueryAt[providerId] = now;

            Task.Run(() =>
            {
                var tokens = store.SumTokensSince(providerId, weekStart);

                void Apply()
                {
                    foreach (var tile in Tiles.Where(t => t.RealProviderId == providerId))
                        tile.WeekTokens = tokens;
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is not null && !dispatcher.CheckAccess())
                    dispatcher.BeginInvoke(Apply);
                else
                    Apply();
            });
        }
    }

    /// <summary>One tile's hidden flag, for <see cref="RefreshScheduler.ResolveInterval"/> - a plain
    /// method rather than a lambda at each call site so it stays the same delegate instance either
    /// way (not that the scheduler compares them, but there is no reason to allocate a new one twice
    /// a second).</summary>
    // TryGetValue, not the plain indexer: a fetch already in flight for an account just removed via
    // RemoveAccount still asks this once more before the scheduler drops it (see
    // RefreshScheduler.RemoveProvider) - an unknown key must never throw here, "not hidden" is a
    // harmless answer for the one remaining tick it can ever be asked for.
    internal bool IsTileHidden(string providerId) => _tilesById.TryGetValue(providerId, out var tile) && tile.IsHidden;

    /// <summary>One account's disconnected flag (<see cref="ProviderSettings.Disconnected"/>), for
    /// <see cref="RefreshScheduler.RunOneCoreAsync"/> to skip its fetch outright - same "unknown key
    /// answers false" reasoning as <see cref="IsTileHidden"/>.</summary>
    internal bool IsAccountDisconnected(string accountKey) =>
        _removingAccounts.Contains(accountKey)
        || _signingOutAccounts.Contains(accountKey)
        || (_settings.Providers.TryGetValue(accountKey, out var providerSettings) && providerSettings.Disconnected);

    /// <summary>Whether an account still has a tile and is not being removed: a sign-out or sign-in
    /// that finishes after (or during) a removal must not re-create anything for it.</summary>
    private bool IsLiveAccount(string accountKey) =>
        _tilesById.ContainsKey(accountKey) && !_removingAccounts.Contains(accountKey);

    /// <summary>One tile's own refresh button - bypasses only that provider's remaining wait, every
    /// other provider's schedule stays untouched.</summary>
    [RelayCommand]
    private void RefreshProvider(string providerId) => _scheduler.RefreshNow(providerId, _lifetimeCts.Token);

    /// <summary>A sign-in window that ran its whole flow through and landed back on the provider's
    /// own site is proof of a session, whatever the next read manages to do with it - without this
    /// the tile kept offering the sign-in button to someone who had just signed in, because a read
    /// that fails for its own reasons never moves the state off "not signed in". The next read is
    /// still free to correct it: a snapshot that actually reports "not signed in" sets it straight
    /// back.</summary>
    public void MarkSignedIn(string providerId)
    {
        _scheduler.SignInCompleted(providerId);
        if (_tilesById.TryGetValue(providerId, out var tile))
            tile.MarkSignedIn(DateTimeOffset.Now);
    }

    /// <summary>Called when a sign-out could not fully clear the browser profile: overrides the
    /// NotSignedIn state <see cref="SignOutAsync"/> just set with an explicit warning instead
    /// of leaving the user thinking sign-out silently did nothing.</summary>
    internal void ShowSignOutIncomplete(string providerId)
    {
        if (!_tilesById.TryGetValue(providerId, out var tile))
            return;

        var now = DateTimeOffset.Now;
        var error = new ProviderError("Status_SignOutIncomplete_Reason");
        tile.Apply(new ProviderSnapshot(providerId, [], null, SourceKind.None, now, null, ProviderStatus.Blocked, error), now);
    }

    /// <summary>Settings window's "Aktualisierung" slider - applies from the next
    /// completed fetch per provider onward, see <see cref="RefreshScheduler.UpdateBaseInterval"/>.</summary>
    public void UpdateRefreshInterval(int seconds)
    {
        _settings.RefreshSeconds = seconds;
        _scheduler.UpdateBaseInterval(TimeSpan.FromSeconds(seconds));
        _settingsStore.RequestSave(_settings);
    }

    /// <summary>True for the one row in <see cref="DisplayRows"/> that is not a real provider tile at
    /// all - <see cref="AppSettings.DayGridTileId"/>. Every command below branches on this exactly
    /// once, right here, rather than scattering an <c>if (providerId == "daygrid")</c> through each
    /// one.</summary>
    private static bool IsDayGridRow(string id) => id == AppSettings.DayGridTileId;

    private bool ResolveRowHidden(string id) => IsDayGridRow(id) ? DayGridTile.IsHidden : _tilesById[id].IsHidden;

    [RelayCommand]
    private void ToggleHidden(string providerId) => SetHidden(providerId, !ResolveRowHidden(providerId));

    /// <summary>Eye popup's up/down arrows swap this row's own position in <see cref="DisplayRows"/>
    /// with whichever neighbour it lands on and persist - a no-op already at the relevant end,
    /// matching the arrow <see cref="RefreshMoveEligibility"/> disables there. <see cref="Tiles"/>
    /// itself only ever moves alongside this when both the row and its neighbour are real provider
    /// tiles - swapping past the day-grid tile changes a provider's position relative to that one
    /// pseudo row, never its position relative to another provider, so <see cref="Tiles"/> has nothing
    /// to reflect in that case.</summary>
    [RelayCommand]
    private void MoveProviderUp(string providerId) => MoveRow(providerId, -1);

    [RelayCommand]
    private void MoveProviderDown(string providerId) => MoveRow(providerId, 1);

    private void MoveRow(string id, int direction)
    {
        var oldIndex = IndexOfRow(id);
        if (oldIndex < 0)
            return;
        var newIndex = oldIndex + direction;
        if (newIndex < 0 || newIndex >= DisplayRows.Count)
            return;

        if (DisplayRows[oldIndex] is ProviderTileViewModel movingTile && DisplayRows[newIndex] is ProviderTileViewModel neighborTile)
            Tiles.Move(Tiles.IndexOf(movingTile), Tiles.IndexOf(neighborTile));

        DisplayRows.Move(oldIndex, newIndex);
        RenumberRowOrders();
        RefreshMoveEligibility();
        _settingsStore.RequestSave(_settings);
    }

    private int IndexOfRow(string id)
    {
        for (var i = 0; i < DisplayRows.Count; i++)
            if (RowId(DisplayRows[i]) == id)
                return i;
        return -1;
    }

    private static string RowId(object row) => row switch
    {
        ProviderTileViewModel tile => tile.ProviderId,
        DayGridTileViewModel => AppSettings.DayGridTileId,
        _ => "",
    };

    /// <summary>Writes every row's current position back as its own <see
    /// cref="ProviderSettings.Order"/>, providers and the day-grid tile alike - the one shared
    /// mechanism <see cref="Models.AppSettings.DayGridTileId"/>'s own doc comment promises. Swapping
    /// two stored values was not enough: a removed account leaves a gap and a newly created entry
    /// starts at -1, so two rows could share one value and a swap between them changed nothing on
    /// disk.</summary>
    private void RenumberRowOrders()
    {
        for (var i = 0; i < DisplayRows.Count; i++)
            GetOrCreateProviderSettings(RowId(DisplayRows[i])).Order = i;
    }

    [RelayCommand]
    private void SetDensity(string mode)
    {
        // The setting first: the window recomputes the density from it as soon as DensityMode or
        // HeightAutomatic announce a change.
        _settings.TileDensity = mode;
        // A fixed density sizes the window to its tiles; only "Auto" fits the tiles into a
        // hand-sized height instead.
        if (Enum.TryParse<TileDensity>(mode, out var fixedDensity) && Enum.IsDefined(fixedDensity))
            HeightAutomatic = true;
        DensityMode = mode;
        UpdateDensitySelection();
        _settingsStore.RequestSave(_settings);
    }

    private void UpdateDensitySelection()
    {
        Choice.Select(DensityChoices, DensityMode);
        OnPropertyChanged(nameof(SelectedDensityChoice));
    }

    public void SetHidden(string providerId, bool hidden)
    {
        // A fresh install sorts the hidden day grid last, out of every real provider's way. The very
        // first time it is shown, the spec puts it at the very top instead - done here, once, as an
        // explicit move rather than relying on sort order, since merely flipping Visible on the row
        // already sitting at the end would leave it there.
        var isFirstDayGridReveal = IsDayGridRow(providerId) && !hidden && !_settings.DayGridShownOnce;
        if (isFirstDayGridReveal)
            _settings.DayGridShownOnce = true;

        if (IsDayGridRow(providerId))
        {
            // The reload runs in the background; the grid fills in when it lands.
            if (!hidden)
                DayGridTile.Refresh();
            DayGridTile.IsHidden = hidden;
        }
        else
            _tilesById[providerId].IsHidden = hidden;

        GetOrCreateProviderSettings(providerId).Visible = !hidden;

        if (isFirstDayGridReveal)
        {
            var oldIndex = IndexOfRow(providerId);
            if (oldIndex > 0)
            {
                DisplayRows.Move(oldIndex, 0);
                RenumberRowOrders();
                RefreshMoveEligibility();
            }
        }

        RefreshHiddenCount();
        _settingsStore.RequestSave(_settings);
    }

    /// <summary>Settings window's Providers card, per-provider window-visibility check boxes - the
    /// tile itself already re-filtered its own <see cref="ProviderTileViewModel.Rows"/> immediately
    /// (see <see cref="ProviderTileViewModel.ShowFiveHourChanged"/>); this only persists the choice.</summary>
    private void SetShowFiveHour(string providerId, bool value)
    {
        GetOrCreateProviderSettings(providerId).ShowFiveHour = value;
        _settingsStore.RequestSave(_settings);
        TileLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetAttentionDisabled(string providerId, bool value)
    {
        GetOrCreateProviderSettings(providerId).AttentionDisabled = value;
        _settingsStore.RequestSave(_settings);
    }

    private void SetChartHidden(string providerId, bool value)
    {
        GetOrCreateProviderSettings(providerId).ChartHidden = value;
        _settingsStore.RequestSave(_settings);
        TileLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when a setting changed how tall a tile is without hiding it (its chart went
    /// away or came back, a bar row was hidden or shown). An automatic-height window has to fit itself again: its viewport is
    /// already as tall as the window, so a shrinking tile list never raises a size change of its
    /// own.</summary>
    public event EventHandler? TileLayoutChanged;

    private void SetShowWeekly(string providerId, bool value)
    {
        GetOrCreateProviderSettings(providerId).ShowWeekly = value;
        _settingsStore.RequestSave(_settings);
        TileLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Settings window's Providers card, per-row window-visibility switches for a row that
    /// is not FiveHour/Weekly - the tile itself already re-filtered its own <see
    /// cref="ProviderTileViewModel.Rows"/> immediately (see <see
    /// cref="ProviderTileViewModel.OtherWindowVisibilityChanged"/>); this only persists the current
    /// set of hidden labels.</summary>
    private void SetHiddenWindows(string providerId, HashSet<string> hiddenWindows)
    {
        GetOrCreateProviderSettings(providerId).HiddenWindows = [.. hiddenWindows];
        _settingsStore.RequestSave(_settings);
        TileLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The thresholds notifications actually use (global default unless the provider has its
    /// own), so the bar's marker never shows a different number than the balloon fires at.</summary>
    private ThresholdSettings ResolvedThresholds(string providerId)
    {
        var (fiveHour, fiveHourEnabled) = SettingsRanges.ResolveThreshold(_settings, providerId, WindowKind.FiveHour);
        var (weekly, weeklyEnabled) = SettingsRanges.ResolveThreshold(_settings, providerId, WindowKind.Weekly);
        var (other, otherEnabled) = SettingsRanges.ResolveThreshold(_settings, providerId, WindowKind.Other);
        return new ThresholdSettings
        {
            FiveHour = fiveHour,
            FiveHourEnabled = fiveHourEnabled,
            Weekly = weekly,
            WeeklyEnabled = weeklyEnabled,
            Other = other,
            OtherEnabled = otherEnabled,
        };
    }

    private ProviderSettings GetOrCreateProviderSettings(string providerId)
    {
        if (!_settings.Providers.TryGetValue(providerId, out var providerSettings))
        {
            // The day-grid pseudo tile defaults hidden, unlike every real provider (Visible = true by
            // default) - the one place that default actually needs stating, since every other read of
            // this dictionary either already has an entry or treats a missing one as hidden itself.
            providerSettings = providerId == AppSettings.DayGridTileId ? new ProviderSettings { Visible = false } : new ProviderSettings();
            _settings.Providers[providerId] = providerSettings;
        }
        return providerSettings;
    }

    // A provider's FetchAsync completes on its own ThreadPool thread, and RefreshScheduler.Tick
    // starts all four providers at once - so this can run concurrently for up to four providers.
    // The whole tail (tile apply, history append, notification evaluation, chart reload) is
    // marshalled as one BeginInvoke, never Invoke: a pool thread must never block waiting for the UI
    // thread, and none of this work may run partly on the pool thread and partly on the UI thread.
    internal void OnSnapshotReady(ProviderSnapshot snapshot)
    {
        // NotSignedIn joins Failed/Blocked here once a provider can name why its primary source was
        // skipped (see ProviderSnapshot.SkipReasonWord) - the word is already provider-neutral, never
        // a token or a path, so it is as safe to log as the reason key next to it.
        // A provider that stays signed out answers the same way on every refresh; only the change into
        // that state is worth a line. Failed and Blocked keep logging every time.
        var previousStatus = _lastStatusById.TryGetValue(snapshot.ProviderId, out var seen) ? seen : (ProviderStatus?)null;
        _lastStatusById[snapshot.ProviderId] = snapshot.Status;
        var repeatedNotSignedIn = snapshot.Status == ProviderStatus.NotSignedIn && previousStatus == ProviderStatus.NotSignedIn;
        if (snapshot.Status is ProviderStatus.Failed or ProviderStatus.Blocked or ProviderStatus.NotSignedIn && !repeatedNotSignedIn)
        {
            var reasonWord = snapshot.SkipReasonWord is { Length: > 0 } word ? $" ({word})" : "";
            _logService?.LogInfo($"Provider '{snapshot.ProviderId}' {snapshot.Status}: {snapshot.Error?.ReasonKey ?? "(default)"}{reasonWord}.");
        }

        void Tail()
        {
            var now = DateTimeOffset.Now;
            TryClearFetching(snapshot.ProviderId);

            // A fetch that was already running when its account was removed still lands here - it
            // must neither write history for nor raise a balloon about an account that is gone.
            if (!_tilesById.TryGetValue(snapshot.ProviderId, out var tile))
                return;

            // Same for a fetch that finished after its account was signed out: the tile keeps the
            // not-signed-in state and nothing is recorded. The scheduler's own not-signed-in answer
            // for a disconnected account still applies (it is what a restart shows).
            if (snapshot.Status != ProviderStatus.NotSignedIn && IsAccountDisconnected(snapshot.ProviderId))
                return;

            tile.Apply(
                snapshot, now, ResolvedThresholds(snapshot.ProviderId), _settings.ShowAttentionMark,
                refreshInterval: TimeSpan.FromSeconds(_settings.RefreshSeconds));
            var displayName = tile.DisplayName;

            // A tile's own master switch (ProviderSettings.NotificationsEnabled) gates both streams;
            // its own reset switch sits underneath that AND the global NotifyOnReset switch, so either
            // one turning it off is enough to silence the "free again" balloon for this one tile.
            var providerSettings = GetOrCreateProviderSettings(snapshot.ProviderId);
            var notifyOnReset = _settings.NotifyOnReset && providerSettings.NotifyOnResetEnabled;
            // A snapshot that repeats an older reading (a held-over or cached read) adds no chart point.
            var recordHistory = !snapshot.HeldOver;
            using var notificationSaves = _notifications.BatchSaves();
            foreach (var window in snapshot.Windows)
            {
                if (recordHistory)
                {
                    _historyStore.Append(
                        snapshot.ProviderId, window.Kind, window.UsedPercent, window.ResetsAt, window.Tokens?.TotalTokens, window.Label);
                }

                var (threshold, enabled) = SettingsRanges.ResolveThreshold(_settings, snapshot.ProviderId, window.Kind);
                _notifications.Evaluate(
                    snapshot.ProviderId, displayName, window, threshold, enabled && providerSettings.NotificationsEnabled,
                    notifyOnReset && providerSettings.NotificationsEnabled, now);
            }

            RefreshTileHistory(snapshot.ProviderId);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Tail);
        else
            Tail();
    }

    // Raised for whichever provider RunOneAsync just started - Tick, RefreshNow() and
    // RefreshProvider() all fetch through it, so this one handler covers every entry point. May arrive
    // off the UI thread the same way OnSnapshotReady can, hence the same dispatcher check.
    private void OnFetchStarted(string providerId)
    {
        void Mark()
        {
            _fetchStartedAt[providerId] = _timeProvider.GetUtcNow();
            _pendingFetchClear.Remove(providerId);
            if (_tilesById.TryGetValue(providerId, out var tile))
                tile.IsFetching = true;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Mark);
        else
            Mark();
    }

    // An attempt the owner cancelled raises no snapshot, so this is the only thing that ends its spinner
    // (and drops the bookkeeping of an account removed in the meantime).
    private void OnFetchEnded(string providerId)
    {
        void End() => TryClearFetching(providerId);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(End);
        else
            End();
    }

    /// <summary>Clears IsFetching once at least <see cref="MinFetchingDuration"/> has passed since the
    /// matching <see cref="OnFetchStarted"/> - otherwise leaves it set and queues the provider for
    /// another look on the next <see cref="Tick"/>, so the spinner never disappears before it was even
    /// visible. Always called on the UI thread already (from OnSnapshotReady's own marshalled tail, or
    /// from Tick itself).</summary>
    private void TryClearFetching(string providerId)
    {
        if (!_tilesById.TryGetValue(providerId, out var tile))
        {
            // The tile is gone (an account removed mid-flight) - drop the stale bookkeeping instead
            // of leaving it for every future Tick to walk forever.
            _fetchStartedAt.Remove(providerId);
            _pendingFetchClear.Remove(providerId);
            return;
        }

        var startedAt = _fetchStartedAt.TryGetValue(providerId, out var at) ? at : _timeProvider.GetUtcNow();
        if (_timeProvider.GetUtcNow() - startedAt >= MinFetchingDuration)
        {
            tile.IsFetching = false;
            _pendingFetchClear.Remove(providerId);
        }
        else
        {
            _pendingFetchClear.Add(providerId);
        }
    }

    // Keyed by provider id, so a completed read can tell whether it is still the one the view model
    // last asked for. Written only from ShouldStartHistoryRead and read only from
    // HistoryReadCompleted, both on the UI thread - the thread pool side never touches this.
    private readonly Dictionary<string, int> _historyReadTokens = new();

    // The thread pool reads started by RefreshTileHistory that have not finished yet, so a test can
    // await the ones it just triggered instead of racing the background thread. Each read removes
    // itself when it completes, so this never grows over a long run. Guarded by its own lock: the
    // removal runs on a pool thread.
    private readonly List<Task> _pendingHistoryReads = new();

    /// <summary>Awaits every history read started since the last call, then forgets them - a test
    /// seam only: production code never needs to know when the thread pool side of
    /// <see cref="RefreshTileHistory"/> has actually landed, it only reacts to the tile's own
    /// property-changed notifications once it does.</summary>
    internal Task WaitForPendingHistoryReadsAsync()
    {
        Task[] tasks;
        lock (_pendingHistoryReads)
        {
            tasks = _pendingHistoryReads.ToArray();
            _pendingHistoryReads.Clear();
        }

        return Task.WhenAll(tasks);
    }

    /// <summary>Issues the token for a new history read of <paramref name="providerId"/>, silently
    /// superseding whichever token that provider last issued - a read that later reports the older
    /// token is then recognised as stale by <see cref="HistoryReadCompleted"/> instead of racing it
    /// to apply its (now outdated) result. Always true: there is no case today where starting a read
    /// is refused, this only names the decision point so the token bookkeeping is unit testable
    /// without a live read.</summary>
    internal bool ShouldStartHistoryRead(string providerId, out int token)
    {
        token = _historyReadTokens.TryGetValue(providerId, out var previous) ? unchecked(previous + 1) : 0;
        _historyReadTokens[providerId] = token;
        return true;
    }

    /// <summary>True when <paramref name="token"/> is still the current one for
    /// <paramref name="providerId"/> - false when a later <see cref="ShouldStartHistoryRead"/> call
    /// for the same provider already superseded it, meaning this read's result belongs to a request
    /// that is no longer wanted and must be discarded rather than applied to the tile.</summary>
    internal bool HistoryReadCompleted(string providerId, int token) =>
        _historyReadTokens.TryGetValue(providerId, out var current) && current == token;

    /// <summary>What the history chart actually draws for one tile: <see cref="RangeStart"/> is the
    /// later of the requested start and the first point that actually exists, so the curve starts at
    /// the chart's left edge and uses the field's whole width. The requested range itself never
    /// changes, only what gets drawn.</summary>
    internal readonly record struct ChartRangeDecision(DateTimeOffset RangeStart);

    /// <summary>Pure range decision behind <see cref="RefreshTileHistory"/>, pulled out so a one-year
    /// range with four days of history is directly testable without a live history store: with no
    /// data, or with data reaching back at least to <paramref name="requestedRange"/>'s own start,
    /// the drawn range is exactly the requested one.</summary>
    internal static ChartRangeDecision DecideChartRange(
        DateTimeOffset now, TimeSpan requestedRange, DateTimeOffset? earliestPoint)
    {
        var requestedStart = now - requestedRange;
        if (earliestPoint is not { } earliest || earliest <= requestedStart)
            return new ChartRangeDecision(requestedStart);

        return new ChartRangeDecision(earliest);
    }

    /// <summary>Reloads one tile's history chart data for whatever range is currently selected
    /// - called after every snapshot so a fresh point shows up immediately, not just on the next
    /// full refresh. Every caller is already on the UI thread (OnSnapshotReady's own tail is
    /// marshalled there before this runs). The file reads happen on the thread pool; only the
    /// eventual assignment onto the tile comes back to the UI thread, and the chart keeps showing its
    /// previous points until that lands, so nothing blinks empty in between. A second call for the
    /// same provider before the first has landed replaces it outright (the superseded read's result
    /// is discarded when it completes) rather than the two queueing behind each other.</summary>
    private void RefreshTileHistory(string providerId)
    {
        if (!_tilesById.TryGetValue(providerId, out _))
            return;
        if (!ShouldStartHistoryRead(providerId, out var token))
            return;

        var range = SettingsRanges.ChartRangeToTimeSpan(_settings.ChartRange);
        var showPreviousWeek = _settings.ShowPreviousWeekLine;

        var read = Task.Run(() =>
        {
            var now = DateTimeOffset.Now;
            var requestedStart = now - range;
            var points = _historyStore.Load(providerId, range);
            var earliestPoint = points.Count > 0 ? points.Min(p => p.Timestamp) : (DateTimeOffset?)null;
            var decision = DecideChartRange(now, range, earliestPoint);

            IReadOnlyList<HistoryPoint> previousWeek = [];
            if (showPreviousWeek)
            {
                var shift = TimeSpan.FromDays(7);
                previousWeek = _historyStore.Load(providerId, requestedStart - shift, now - shift)
                    .Select(p => p with { Timestamp = p.Timestamp + shift })
                    .ToList();
            }

            void Apply()
            {
                if (!HistoryReadCompleted(providerId, token))
                    return;
                if (!_tilesById.TryGetValue(providerId, out var tile))
                    return;
                tile.UpdateHistory(points, previousWeek, decision.RangeStart, now);
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(Apply);
            else
                Apply();
        });
        lock (_pendingHistoryReads)
            _pendingHistoryReads.Add(read);
        _ = read.ContinueWith(
            finished =>
            {
                lock (_pendingHistoryReads)
                    _pendingHistoryReads.Remove(finished);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Settings window's own "change data folder" flow calls this once it has already
    /// copied every history file across, so the live recording this view model does on every tick
    /// (<see cref="OnSnapshotReady"/>) continues into the new location instead of the one just left
    /// behind.</summary>
    internal void RedirectHistoryStore(string dataDirectory)
    {
        _historyStore.Redirect(dataDirectory);
        _notifications.Redirect(dataDirectory);
    }

    /// <summary>Settings window's chart-range picker calls this so every tile's chart reflects the
    /// new range immediately, instead of waiting for each provider's next scheduled fetch.</summary>
    public void RefreshHistoryForAllTiles()
    {
        foreach (var tile in Tiles)
            RefreshTileHistory(tile.ProviderId);
    }

    /// <summary>Settings window's threshold sliders and Ein/Aus switches call this so every bar's
    /// marker moves at once instead of at the next fetch.</summary>
    internal void ApplyThresholdsToAllTiles()
    {
        foreach (var tile in Tiles)
            tile.ApplyThresholds(ResolvedThresholds(tile.ProviderId));
    }

    /// <summary>Settings window's "mark a waiting tile" check box calls this so every tile's marker
    /// reflects the new setting immediately, instead of waiting for each provider's next scheduled
    /// fetch.</summary>
    internal void ApplyShowAttentionMarkToAllTiles(bool value)
    {
        foreach (var tile in Tiles)
            tile.ApplyShowAttentionMark(value);
    }

    /// <summary>
    /// Compacts and prunes every provider's history once immediately, then again every <paramref
    /// name="interval"/> (24h in production - a test injects a short one instead of waiting out a
    /// real day) for as long as <paramref name="ct"/> stays uncancelled. Started once, with
    /// <c>Task.Run</c>, right after the window is shown. The awaited do/while below is itself the
    /// guarantee against overlap: the next pass is never even attempted before the previous one's
    /// compact/prune/refresh has fully finished.
    /// </summary>
    internal async Task RunMaintenanceAsync(CancellationToken ct, TimeSpan? interval = null)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromHours(24));
        do
        {
            try
            {
                // Tiles belongs to the UI thread (adding or removing an account changes it there), so the
                // ids are read on that thread and only the file work below runs here.
                var dispatcher = Application.Current?.Dispatcher;
                var providerIds = dispatcher is not null && !dispatcher.CheckAccess()
                    ? await dispatcher.InvokeAsync(() => Tiles.Select(t => t.ProviderId).ToList())
                    : Tiles.Select(t => t.ProviderId).ToList();

                foreach (var providerId in providerIds)
                {
                    _historyStore.Compact(providerId);
                    _historyStore.Prune(providerId, _settings.HistoryRetentionDays);
                }

                if (dispatcher is not null && !dispatcher.CheckAccess())
                    await dispatcher.InvokeAsync(RefreshHistoryForAllTiles);
                else
                    RefreshHistoryForAllTiles();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed pass must not end the maintenance for the rest of the session.
                (_logService ?? LogService.Shared).LogError($"History maintenance failed ({ex.GetType().Name}): {PathSanitizer.Sanitize(ex.Message)}");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>What MainWindow actually starts, once, right after the window is shown - repeats
    /// forever using this view model's own lifetime token instead of a caller-supplied one, so
    /// <see cref="DisposeAsync"/> is the only thing that can ever stop it.</summary>
    public Task RunMaintenanceForeverAsync() => RunMaintenanceAsync(_lifetimeCts.Token);

    partial void OnLayoutChanged(string value)
    {
        _settings.Layout = value;
        _settingsStore.RequestSave(_settings);
    }

    partial void OnHeightAutomaticChanged(bool value)
    {
        if (value)
            CurrentSizes.Height = null;
        HeightAutomaticChanged?.Invoke(this, value);
        _settingsStore.RequestSave(_settings);
    }

    /// <summary>MainWindow switches SizeToContent when this fires (the actual window-height mechanics
    /// already live there) - turning height-automatic back on needs the live window, not
    /// just the settings value, to snap back to content size immediately.</summary>
    public event EventHandler<bool>? HeightAutomaticChanged;

    partial void OnWidthAutomaticChanged(bool value)
    {
        CurrentSizes.WidthIsManual = !value;
        WidthAutomaticChanged?.Invoke(this, value);
        _settingsStore.RequestSave(_settings);
    }

    /// <summary>Same split as the height: the setting lives here, snapping the live window back to
    /// the width of its tiles needs the window itself.</summary>
    public event EventHandler<bool>? WidthAutomaticChanged;

    /// <summary>
    /// Single source of truth for the always-on-top option: both the tray menu checkbox and
    /// the Settings window's own checkbox write here, and both are kept in sync via
    /// <see cref="AlwaysOnTopChanged"/> - MainWindow is the only place that actually sets
    /// Window.Topmost or the tray checkbox, so neither surface can ever drift from the other.
    /// </summary>
    partial void OnAlwaysOnTopChanged(bool value)
    {
        _settings.AlwaysOnTop = value;
        _settingsStore.RequestSave(_settings);
        AlwaysOnTopChanged?.Invoke(this, value);
    }

    public event EventHandler<bool>? AlwaysOnTopChanged;

    /// <summary>Single source of truth for click-through: the tray checkbox, the Settings checkbox
    /// and the global hotkey (see MainWindow.WndProc) all write here, and MainWindow is the only
    /// place that actually flips the extended window style or resolves the always-on-top/opacity
    /// implications - same split as <see cref="OnAlwaysOnTopChanged"/>.</summary>
    partial void OnClickThroughChanged(bool value)
    {
        _settings.ClickThrough = value;
        _settingsStore.RequestSave(_settings);
        ClickThroughChanged?.Invoke(this, value);
    }

    public event EventHandler<bool>? ClickThroughChanged;

    /// <summary>Raised whenever the hotkey's enabled flag or its combination text changes -
    /// MainWindow is the only thing that ever actually registers/unregisters the native hotkey
    /// (it alone holds the window handle), so it re-applies both from here rather than from two
    /// separate partial-method events.</summary>
    public event EventHandler? HotkeySettingsChanged;

    partial void OnHotkeyEnabledChanged(bool value)
    {
        _settings.HotkeyEnabled = value;
        _settingsStore.RequestSave(_settings);
        HotkeySettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnHotkeyTextChanged(string value)
    {
        _settings.Hotkey = value;
        _settingsStore.RequestSave(_settings);
        HotkeySettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><see cref="HiddenCount"/> counts providers only, deliberately: the badge it feeds
    /// counts what a hidden provider means everywhere else in this app (nothing left to poll for it).
    /// <see cref="IsEmpty"/> drives the widget's own "everything hidden" placeholder, which shares
    /// the tile area's cell, so a shown day-grid tile keeps it away - otherwise the placeholder would
    /// draw on top of the grid. MainWindow still reacts to the day-grid tile's own show/hide
    /// separately (see MainWindow.DayGridTile_PropertyChanged) for the window resize.</summary>
    private void RefreshHiddenCount()
    {
        HiddenCount = Tiles.Count(t => t.IsHidden);
        IsEmpty = Tiles.Count > 0 && Tiles.All(t => t.IsHidden) && DayGridTile.IsHidden;
    }

    /// <summary>Re-derives every row's own CanMoveUp/CanMoveDown from its current position in <see
    /// cref="DisplayRows"/> (a real provider tile or the day-grid tile alike, via <see
    /// cref="ITileRow"/>) - called once after the initial build and again after every successful <see
    /// cref="MoveRow"/>, since only the two rows nearest either end can ever change which arrow is
    /// disabled.</summary>
    private void RefreshMoveEligibility()
    {
        for (var i = 0; i < DisplayRows.Count; i++)
        {
            if (DisplayRows[i] is not ITileRow row)
                continue;
            row.CanMoveUp = i > 0;
            row.CanMoveDown = i < DisplayRows.Count - 1;
        }
    }

    /// <summary>Tile build order - ascending by this provider's own <see cref="ProviderSettings.Order"/>,
    /// falling back to 0 for an id with no settings entry at all (only ever happens in a test that
    /// builds a <see cref="Models.AppSettings"/> by hand) so it still sorts deterministically.</summary>
    private int GetProviderOrder(IUsageProvider provider) =>
        _settings.Providers.TryGetValue(provider.AccountKey, out var providerSettings) ? providerSettings.Order : 0;

    private bool _disposed;

    /// <summary>Called once by MainWindow.Dispose() at real shutdown: cancels every in-flight and
    /// future fetch and maintenance pass, then disposes the Claude browser session so its off-screen
    /// window and WebView2 environment do not outlive the process for no reason. Idempotent - a
    /// second call is a no-op rather than throwing on the already-disposed token source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Synchronous Cancel, not CancelAsync: MainWindow blocks the UI thread on this method, and an
        // awaited CancelAsync would post its continuation back to that very thread and never finish.
        // Only cancelled, never disposed: UI and timer callbacks still read its Token for a moment
        // after this, and a disposed source throws from Token.
        _lifetimeCts.Cancel();

        foreach (var runner in _webRunners.Values)
        {
            if (runner is Web.WebSessionScriptRunner webRunner)
                await webRunner.DisposeForShutdownAsync();
            else
                await runner.DisposeAsync();
        }
        _webRunners.Clear();
    }
}
