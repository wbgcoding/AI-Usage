using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Providers.LocalLogin;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;
using AiUsage.ViewModels;
using Microsoft.Win32;

namespace AiUsage.Views;

/// <summary>
/// The frameless main widget window. Owns only the glue between the live
/// window/monitors/settings and the already-tested pure logic in WindowPlacementService - no
/// placement or capping decision is made here directly.
/// </summary>
public partial class MainWindow : Window, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tickTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly TrayService _tray;
    private readonly ToastNotifier _toasts;
    private readonly MonitorAreaCache _monitorAreas = new(() => NativeMonitors.WorkAreas());
    private readonly TrayTooltipMemo _trayTooltipMemo = new();
    private readonly ClickThroughPolicy _clickThroughPolicy = new();
    private double _uncollapsedHeight;

    // The one place that now carries the "is the height automatic" meaning: SizeToContent itself no
    // longer stays on Height once a density has been resolved (see UpdateDensity()), since that let
    // WPF's own layout pass silently overwrite a height this program had just computed. Set at every
    // point that used to switch SizeToContent between Height and Manual for that reason.
    private bool _heightIsAutomatic;

    // Set for the duration of UpdateDensity()'s own height assignment: setting Height there raises
    // SizeChanged, and MainWindow_SizeChanged() must not send that same height through UpdateDensity()
    // a second time.
    private bool _applyingDensityHeight;

    // A density pass is already waiting for the current layout pass to finish (see
    // ScrollContent_SizeChanged); further content size changes before it runs need no second one.
    private bool _densityUpdateQueued;

    // How many follow-up passes UpdateDensity asked for since the last ordinary request; one is the
    // limit, so a content height that never settles cannot loop.
    private int _densityFollowUps;

    // Only ShutdownFromTray (the tray menu's exit item, Tray.Exit) sets this - every other way to close the
    // window (Alt+F4, the taskbar, the title bar's own close button) hides into the tray instead,
    // so the process and its tray icon stay alive until the user really asks to quit.
    private bool _reallyClosing;

    // The work area the automatic height was last measured against. Only read while the height is
    // automatic; a hand-dragged height is the user's own business.
    private MonitorArea? _autoHeightArea;

    // Set for the duration of ApplyStoredSizeForCurrentLayout: tells ViewModel_HeightAutomaticChanged
    // that MainWindow itself is already applying the new layout's remembered size, so it must not
    // also capture the (still stale, pre-layout-switch) ActualHeight as if the user had just toggled
    // the checkbox by hand.
    private bool _restoringLayoutSize;

    // Created once SourceInitialized hands over a real HWND; the tray double click already reaches
    // ToggleVisibility without needing this, so a hidden widget is never unreachable even while the
    // hotkey itself is off or fails to register.
    private GlobalHotkey? _globalHotkey;

    public MainViewModel ViewModel { get; }

    /// <summary>The live window's own remembered width/height for whichever arrangement is showing
    /// right now, so no call site has to branch between <see cref="WindowSettings.Vertical"/> and
    /// <see cref="WindowSettings.Horizontal"/> itself.</summary>
    private LayoutSizes Current => ViewModel.IsHorizontal ? _settings.Window.Horizontal : _settings.Window.Vertical;

    public MainWindow() : this(new SettingsStore(), null) { }

    internal MainWindow(SettingsStore settingsStore) : this(settingsStore, null) { }

    /// <summary>
    /// <paramref name="settings"/> lets the caller pass its own already-loaded settings (App.xaml.cs
    /// does, right after applying language/theme from that same instance) so startup parses
    /// settings.json exactly once instead of twice from two separate, silently-diverging
    /// <see cref="AppSettings"/> instances. Null re-loads from <paramref name="settingsStore"/>, for
    /// the parameterless/designer constructors that have nothing already loaded. <paramref
    /// name="logService"/> is null in every test and design-time path, which keeps them silent.
    /// </summary>
    internal MainWindow(SettingsStore settingsStore, AppSettings? settings, LogService? logService = null)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _settings = settings ?? _settingsStore.Load();
        ViewModel = new MainViewModel(_settingsStore, _settings, logService)
        {
            ShowNewerVersionWarning = _settingsStore.LastLoadWasFromNewerVersion,
        };
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.HeightAutomaticChanged += ViewModel_HeightAutomaticChanged;
        ViewModel.WidthAutomaticChanged += (_, _) => ApplyContentSize();
        // A tile that shrinks while the height is automatic raises no size change on the scroll
        // content (it is stretched to the viewport), so the refit has to be asked for directly.
        ViewModel.TileLayoutChanged += (_, _) =>
        {
            UpdateDensity();
            QueueDensityUpdate();
        };
        // The visible tile count is the one density input ViewModel_PropertyChanged does not already
        // cover (Layout/HiddenCount/DensityMode do) - adding or removing an account changes Tiles
        // itself, not one of those properties, so the tick no longer picking this up (see below)
        // would otherwise leave the density stale until something else happened to touch it.
        ViewModel.Tiles.CollectionChanged += ViewModel_Tiles_CollectionChanged;
        // The day-grid tile's own show/hide never touches HiddenCount (see MainViewModel.RefreshHiddenCount)
        // - that badge and IsEmpty stay providers-only - so its visibility change needs this one direct
        // hook instead, the same pair of calls a provider's own toggle reaches through HiddenCount.
        ViewModel.DayGridTile.PropertyChanged += DayGridTile_PropertyChanged;
        ViewModel.Update.RequestExit = ShutdownFromTray;
        StartUpdateChecks();
        // Opens (or focuses) the statistics window on the clicked day - reuses the exact same window
        // TitleBarControl_StatsRequested already opens, never a second one of its own.
        ViewModel.DayGridDaySelected += (_, day) =>
        {
            TitleBarControl_StatsRequested(this, EventArgs.Empty);
            _statsWindow?.SelectDay(day);
        };

        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = WindowPlacementService.MinWindowWidth;
        // XAML no longer carries a fixed MinHeight (Collapse/Restore own it from here on) - a window
        // that starts collapsed needs the floor at 0 from its very first layout pass, not just from
        // the next Collapse() call.
        MinHeight = WindowPlacementService.MinHeightFor(_settings.Window.Collapsed);
        Topmost = _settings.AlwaysOnTop;

        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged += SystemParameters_HighContrastChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

        ApplyRememberedPlacement();
        ApplyContentSize();

        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        // Windows asking the app to close - an update installing over the running copy asks through
        // the system's own restart manager, and a shutdown or sign-out asks the same way. The title
        // bar's close button answers that request with "no" and hides into the tray, which leaves an
        // installer nothing but terminating the process. This is therefore the one close route
        // besides the tray menu's own Exit that really ends it, with everything written first.
        if (Application.Current is { } application)
            application.SessionEnding += Application_SessionEnding;
        SizeChanged += MainWindow_SizeChanged;
        if (ContentScroll.Content is FrameworkElement scrollContent)
            scrollContent.SizeChanged += ScrollContent_SizeChanged;
        ThemeService.Applied += ThemeService_Applied;
        LocationChanged += MainWindow_LocationChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        _tray = new TrayService(_settings.AlwaysOnTop, _settings.ClickThrough);
        _toasts = new ToastNotifier(
            new WinRtToastBackend(), line => (logService ?? LogService.Shared).LogInfo(line),
            () => LocalizationService.Instance["Toast.ShowWidget"]);
        // A click arrives on a platform thread; the window is brought forward on the UI thread.
        _toasts.Activated += () => Dispatcher.BeginInvoke(new Action(ShowAndActivate));
        _tray.ShowHideRequested += (_, _) => ToggleVisibility();
        _tray.RefreshRequested += (_, _) => ViewModel.RefreshNow(userStarted: true);
        _tray.SettingsRequested += (_, _) => TitleBarControl_SettingsRequested(this, EventArgs.Empty);
        _tray.StatsRequested += (_, _) => TitleBarControl_StatsRequested(this, EventArgs.Empty);
        _tray.ResetPositionRequested += (_, _) => ResetPosition();
        _tray.ExitRequested += (_, _) => ShutdownFromTray();
        _tray.AlwaysOnTopChanged += (_, value) => ViewModel.AlwaysOnTop = value;
        ViewModel.AlwaysOnTopChanged += (_, value) =>
        {
            Topmost = value;
            _tray.UpdateAlwaysOnTop(value);
            TitleBarControl.SetAlwaysOnTop(value);
        };
        TitleBarControl.SetAlwaysOnTop(_settings.AlwaysOnTop);
        _tray.ClickThroughChanged += (_, value) => ViewModel.ClickThrough = value;
        ViewModel.ClickThroughChanged += (_, value) => ApplyClickThrough(value);
        ViewModel.NotificationRaised += ViewModel_NotificationRaised;
        ViewModel.ResetRaised += ViewModel_ResetRaised;
        ViewModel.ForecastRaised += ViewModel_ForecastRaised;
        ViewModel.LimitReachedRaised += ViewModel_LimitReachedRaised;
        ViewModel.LevelAnnounced += AnnounceToScreenReader;

        // Startup tiles here; an account added later is wired the moment it joins the collection
        // (ViewModel_Tiles_CollectionChanged), or its sign-in and sign-out buttons would do nothing.
        foreach (var tile in ViewModel.Tiles)
            WireSignIn(tile);

        if (_settings.Window.Collapsed)
            Collapse();

        UpdateDensity();
        ViewModel.RefreshNow();
        UpdateTray();
        // UpdateDensity() no longer runs from here - its own inputs (Layout/HiddenCount/DensityMode
        // via ViewModel_PropertyChanged, the tile count via the CollectionChanged subscription above,
        // and a live resize via MainWindow_SizeChanged) already call it directly, so a second's worth
        // of countdown ticks in between never had anything new to recompute.
        _tickTimer.Tick += (_, _) =>
        {
            ViewModel.Tick(DateTimeOffset.Now);
            UpdateTray();
            _tickTimer.Interval = ResolveTickInterval(IsAttendedForTick());
        };
        _tickTimer.Start();

        // History compaction/pruning used to run synchronously in MainViewModel's own constructor,
        // blocking cold start on a full read-rewrite of every history file - moved off the UI thread
        // and here, so the window can show first. Runs forever on the view model's own lifetime
        // token; Dispose() below is the only thing that ever stops it.
        _ = Task.Run(() => ViewModel.RunMaintenanceForeverAsync());
    }

    /// <summary>Raised from the snapshot tail, which always runs on the UI thread, the one
    /// <c>ShowBalloonTip</c> needs.</summary>
    private void ViewModel_NotificationRaised(ThresholdNotification notification) =>
        ShowAlert(notification.ProviderDisplayName, notification.Text(DateTimeOffset.Now));

    /// <summary>One alert as a Windows toast with a button that brings the widget forward; the tray
    /// balloon takes over for this and every later alert once toasts are unavailable.</summary>
    private void ShowAlert(string title, string text)
    {
        if (!_toasts.TryShow(title, text))
            _tray.ShowBalloon(text);
    }

    /// <summary>Same thread as <see cref="ViewModel_NotificationRaised"/>.</summary>
    private void ViewModel_ResetRaised(ResetNotification notification) =>
        ShowAlert(notification.ProviderDisplayName, notification.Text());

    /// <summary>Same thread as <see cref="ViewModel_NotificationRaised"/>.</summary>
    private void ViewModel_ForecastRaised(ForecastNotification notification) =>
        ShowAlert(notification.ProviderDisplayName, notification.Text());

    /// <summary>Same thread as <see cref="ViewModel_NotificationRaised"/>.</summary>
    private void ViewModel_LimitReachedRaised(LimitReachedNotification notification) =>
        ShowAlert(notification.ProviderDisplayName, notification.Text(DateTimeOffset.Now));

    /// <summary>Tells a running screen reader that a window changed color level. Only while the
    /// window is on screen: a hidden or minimized widget has nobody looking at it, and the balloon
    /// alerts already cover that case.</summary>
    private void AnnounceToScreenReader(string text)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
            return;

        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(this)
            ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseNotificationEvent(
            System.Windows.Automation.AutomationNotificationKind.Other,
            System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent,
            text, "AiUsage.Level");
    }

    /// <summary>Tray double-click / "Anzeigen/Verstecken" - also restores a
    /// minimized window, the usual Windows convention for a tray toggle.</summary>
    private void ToggleVisibility()
    {
        if (Visibility != Visibility.Visible || WindowState == WindowState.Minimized)
        {
            ShowAndActivate();
        }
        else
        {
            Hide();
            ViewModel.WindowVisible = false;
        }
    }

    /// <summary>Also the target of a second start's wake-up signal (<see cref="SingleInstanceService"/>),
    /// which always wants the window forward regardless of its current state. Coming back from not
    /// visible refreshes right away, so the first number on screen again is never minutes stale from
    /// the slower closed-window cadence (<see cref="RefreshScheduler.ResolveInterval"/>).</summary>
    public void ShowAndActivate()
    {
        var wasHidden = !ViewModel.WindowVisible;

        _showingAndActivating = true;
        try
        {
            Show();
            WindowState = WindowState.Normal;
        }
        finally
        {
            _showingAndActivating = false;
        }
        // The tick handler only re-evaluates this on its own next tick - without setting it here too,
        // a window that was just hidden or minimized keeps the slow cadence for up to 29 more seconds.
        _tickTimer.Interval = ResolveTickInterval(windowVisible: true);
        // Activate once; a refused activation (Windows' own foreground-lock rule) is left exactly
        // where it is - see WindowActivation for why nothing here ever escalates a refusal.
        WindowActivation.TryActivate(Activate);
        ViewModel.WindowVisible = true;

        if (wasHidden)
            ViewModel.RefreshNow();
    }

    /// <summary>The tray menu's recovery entry for a window that ended up somewhere it should not have
    /// - genuinely off screen, or just lost track of by the user. Works regardless of why the window
    /// is wherever it is, so it never itself needs to detect the off-screen case.</summary>
    private void ResetPosition()
    {
        var (cursorX, cursorY) = NativeMonitors.CursorPositionInWorkAreaUnits();
        var monitors = _monitorAreas.Areas;
        var primary = FirstOrDefault(monitors);

        var resolved = WindowPlacementService.CenterOnCursorMonitor(
            cursorX, cursorY, Width, Math.Max(ActualHeight, MinHeight), monitors, primary);
        Left = resolved.Left;
        Top = resolved.Top;

        ShowAndActivate();
        SaveWindowSettings();
    }

    /// <summary>Mirrors the tile list into the tray icon's colour and tooltip - only
    /// tiles that are both visible and actually showing numbers contribute. A tick with nothing new
    /// to say (every provider's numbers still read the same as last time) touches neither the
    /// tooltip nor the icon: <see cref="TryComputeTraySummary"/> already carries the memo check that
    /// used to run separately, so there is no second, redundant comparison here.</summary>
    private void UpdateTray()
    {
        if (TryComputeTraySummary(ViewModel.Tiles, _trayTooltipMemo, out var tooltipText, out var highestLevel, out var highestPercent, ViewModel.TrayProvider, ViewModel.EffectiveTrayWindow))
        {
            _tray.UpdateTooltip(tooltipText);
            if (ViewModel.TrayProvider == MainViewModel.TrayProviderAppIcon)
                _tray.ShowAppIcon();
            else
                _tray.UpdateIcon(highestLevel, highestPercent);
        }
    }

    /// <summary>The real work behind <see cref="UpdateTray"/>, pulled out so it is unit-testable
    /// without a live window: builds the tooltip text once (previously built once here for the memo
    /// comparison and a second time inside <see cref="TrayService.UpdateTooltip"/>) and reports
    /// whether it actually changed since the last call - false means neither the tooltip nor the
    /// icon need touching, since both are derived from the very same tile data and the chosen tray
    /// provider. The tooltip always lists every tile; the icon shows <paramref name="trayProvider"/>'s
    /// tile alone when that one is visible with numbers, else the highest usage across all of them;
    /// of those rows only the <paramref name="trayWindow"/> kind while any exist, else all of them.</summary>
    internal static bool TryComputeTraySummary(
        IEnumerable<ProviderTileViewModel> tiles, TrayTooltipMemo memo,
        out string tooltipText, out UsageLevel highestLevel, out int? highestPercent,
        string? trayProvider = null, string? trayWindow = null)
    {
        var lines = BuildTrayLines(tiles);
        tooltipText = TrayTooltipBuilder.Build(lines);

        var shown = tiles.Where(t => !t.IsHidden && t.HasNumbers).ToList();
        var pinned = shown.FirstOrDefault(t => t.ProviderId == trayProvider);

        var visibleRows = (pinned is null ? shown : [pinned]).SelectMany(t => t.Rows).ToList();
        if (trayWindow is not null && trayWindow.StartsWith(MainViewModel.TrayWindowLabelPrefix, StringComparison.Ordinal))
        {
            var label = trayWindow[MainViewModel.TrayWindowLabelPrefix.Length..];
            if (visibleRows.Any(r => r.Label == label))
                visibleRows = visibleRows.Where(r => r.Label == label).ToList();
        }
        else if (Enum.TryParse<WindowKind>(trayWindow, out var kind) && visibleRows.Any(r => r.Kind == kind))
        {
            visibleRows = visibleRows.Where(r => r.Kind == kind).ToList();
        }
        var highest = visibleRows.Select(r => r.Level).DefaultIfEmpty(UsageLevel.Ok).Max();
        highestLevel = highest;
        highestPercent = visibleRows.Where(r => r.Level == highest)
            .Select(r => (int?)StatusTextMap.UsagePercent(r.UsedPercent)).Max();

        // The icon's own number and colour are part of the key: the tooltip names only the
        // five-hour and weekly windows, so a change in any other window the icon shows (a Cursor
        // model row, a window picked by its label) left the icon at its old value.
        return memo.HasChanged(tooltipText + "\n" + trayProvider + "\n" + pinned?.ProviderId + "\n" + trayWindow
            + "\n" + highestLevel + "\n" + highestPercent);
    }

    /// <summary>Pulled out of <see cref="TryComputeTraySummary"/> so it is unit-testable without a
    /// live window: a Stale tile still holds and shows its last real numbers (dimmed) on the main
    /// page, so it counts toward the tray tooltip and icon just like an Ok one - only a hidden tile,
    /// or one that has never shown any numbers at all, drops out.</summary>
    internal static IReadOnlyList<TrayTooltipBuilder.ProviderLine> BuildTrayLines(IEnumerable<ProviderTileViewModel> tiles) =>
        tiles.Where(t => !t.IsHidden && t.HasNumbers)
            .Select(t => new TrayTooltipBuilder.ProviderLine(
                t.TrayName,
                t.Rows.FirstOrDefault(r => r.Kind == WindowKind.FiveHour)?.UsedPercent,
                t.Rows.FirstOrDefault(r => r.Kind == WindowKind.Weekly)?.UsedPercent))
            .ToList();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Layout))
            ApplyStoredSizeForCurrentLayout();

        if (e.PropertyName is nameof(MainViewModel.Layout) or nameof(MainViewModel.HiddenCount))
            ApplyContentSize();

        if (e.PropertyName is nameof(MainViewModel.Layout) or nameof(MainViewModel.HiddenCount) or nameof(MainViewModel.DensityMode))
            UpdateDensity();

        if (e.PropertyName is nameof(MainViewModel.TrayProvider) or nameof(MainViewModel.TrayWindow))
            UpdateTray();
    }

    /// <summary>Adding or removing an account changes <see cref="MainViewModel.Tiles"/> itself, not
    /// one of the properties <see cref="ViewModel_PropertyChanged"/> already watches - the density
    /// depends on the visible tile count, so this is the last of its inputs that still needs its own
    /// subscription.</summary>
    private void ViewModel_Tiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateDensity();
        foreach (var tile in e.NewItems?.OfType<ProviderTileViewModel>() ?? [])
            WireSignIn(tile);
    }

    /// <summary>Mirrors what a provider's own hide/show reaches through <see
    /// cref="MainViewModel.HiddenCount"/> (see <see cref="ViewModel_PropertyChanged"/>) - the window
    /// has to fit itself again either way, but the day-grid tile's own visibility never changes that
    /// providers-only count.</summary>
    private void DayGridTile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DayGridTileViewModel.IsHidden))
            return;

        ApplyContentSize();
        UpdateDensity();
        QueueDensityUpdate();
    }

    /// <summary>Connects one tile's sign-in and sign-out requests to the app's own sign-in window
    /// and sign-out path; a tile without either has neither button (<see
    /// cref="ProviderTileViewModel.SupportsSignOut"/>).</summary>
    private void WireSignIn(ProviderTileViewModel tile)
    {
        if (!tile.SupportsSignOut)
            return;
        var accountKey = tile.ProviderId;

        if (tile.SupportsInAppSignIn)
        {
            // Every in-app sign-in goes through the app's own WebView2 sign-in window. The account
            // is reconnected only once that window reports a finished sign-in (CompleteSignIn):
            // reconnecting at the start let a local source fill the tile while the user was still
            // signing in.
            tile.ActionRequested += (_, _) =>
            {
                WebSignInFlow.Open(this, ProviderRegistry.WebSessionFor(accountKey), signedIn =>
                {
                    ViewModel.CompleteSignIn(accountKey, signedIn);
                    ShowAndActivate();
                });
            };
        }
        else
        {
            // Copilot and the primary Claude account: no web session of their own to sign into -
            // "sign in" only ever resumes reading the sign-in the GitHub CLI or Claude Code already
            // holds, Copilot offering the CLI's own download page when it is missing (same themed offer WebSignInFlow.Open makes for a missing WebView2 runtime).
            // Never runs "gh auth login" itself - the CLI missing tool's own sign-in stays the user's.
            tile.ActionRequested += (_, _) =>
            {
                if (tile.RealProviderId == "copilot" && !GitHubCliUsage.IsCliInstalled())
                {
                    ShowGitHubCliMissingNotice();
                    return;
                }
                ViewModel.Reconnect(accountKey);
            };
        }

        tile.SignOutRequested += async (_, _) =>
        {
            var signedOut = await ViewModel.TrySignOutAsync(accountKey);
            if (!signedOut)
                ViewModel.ShowSignOutIncomplete(accountKey);
        };
    }

    /// <summary>Same themed offer <see cref="WebSignInFlow"/> makes for a missing WebView2 runtime,
    /// for the GitHub CLI Copilot reads through instead.</summary>
    private void ShowGitHubCliMissingNotice()
    {
        var loc = LocalizationService.Instance;
        var openDownloadPage = ConfirmWindow.Show(
            this,
            loc["State.GitHubCliMissing.Head"],
            loc["State.GitHubCliMissing.Reason"],
            loc["State.GitHubCliMissing.Action"],
            loc["Action.Cancel"]);

        if (!openDownloadPage)
            return;

        try
        {
            Process.Start(new ProcessStartInfo("https://cli.github.com/") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser registered - nothing sensible to recover into.
        }
    }

    /// <summary>1 s keeps the countdowns and the tray tooltip current while the window is on screen;
    /// hidden or minimised, nothing on screen needs that cadence and 30 s is still fast enough for
    /// the tray tooltip and the threshold logic <see cref="MainViewModel.Tick"/> runs. Pulled out as
    /// a pure predicate so the switch is testable without a live window.</summary>
    internal static TimeSpan ResolveTickInterval(bool windowVisible) =>
        windowVisible ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30);

    /// <summary>Same condition <see cref="ToggleVisibility"/> already uses (inverted) to decide
    /// whether showing the window needs to restore it first.</summary>
    private bool IsVisibleForTick() => Visibility == Visibility.Visible && WindowState != WindowState.Minimized;

    /// <summary>On screen and not behind a locked session - nothing on screen needs the one-second
    /// cadence otherwise.</summary>
    private bool IsAttendedForTick() => MainViewModel.IsAttended(IsVisibleForTick(), ViewModel.SessionLocked);

    // The system raises both events on its own thread.
    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                Dispatcher.BeginInvoke(() => SetSessionLocked(true));
                break;
            case SessionSwitchReason.SessionUnlock:
                Dispatcher.BeginInvoke(() => SetSessionLocked(false));
                break;
        }
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Only a trigger: nothing runs while the machine sleeps, so there is no state to keep.
        if (e.Mode == PowerModes.Resume)
            Dispatcher.BeginInvoke(CatchUpIfAttended);
    }

    /// <summary>Records the lock state. When the widget becomes watchable again it gets the normal
    /// cadence back and a due-check right away.</summary>
    private void SetSessionLocked(bool locked)
    {
        var wasAttended = IsAttendedForTick();
        ViewModel.SessionLocked = locked;
        _tickTimer.Interval = ResolveTickInterval(IsAttendedForTick());
        if (!wasAttended)
            CatchUpIfAttended();
    }

    /// <summary>The ordinary due-check, not a forced refresh: a provider whose time came up while the
    /// machine was away fetches on its own, and every provider's own minimum interval still holds.</summary>
    private void CatchUpIfAttended()
    {
        if (!IsAttendedForTick())
            return;

        _tickTimer.Interval = ResolveTickInterval(true);
        ViewModel.Tick(DateTimeOffset.Now);
    }

    /// <summary>Restores the size (and automatic/manual flag) that the layout being switched TO had
    /// last time, before <see cref="ApplyContentSize"/> runs for it - otherwise a row of tiles and a
    /// column of tiles would keep fighting over the other arrangement's leftover width.</summary>
    private void ApplyStoredSizeForCurrentLayout()
    {
        _restoringLayoutSize = true;
        try
        {
            var sizes = Current;
            _heightIsAutomatic = sizes.Height is null;

            if (sizes.Height is { } height)
            {
                SizeToContent = SizeToContent.Manual;
                Height = height;
            }
            else
            {
                SizeToContent = SizeToContent.Height;
            }

            if (sizes.WidthIsManual)
                Width = sizes.Width;

            ViewModel.HeightAutomatic = sizes.Height is null;
            ViewModel.WidthAutomatic = !sizes.WidthIsManual;
        }
        finally
        {
            _restoringLayoutSize = false;
        }
    }

    /// <summary>
    /// Gives the window exactly the width its visible tiles need - one tile wide when they are
    /// stacked, the whole row wide when they stand side by side - unless the user has dragged the
    /// width themselves, which always wins until they switch automatic width back on. The height
    /// keeps following the content through SizeToContent, so this only ever touches the width.
    /// </summary>
    private void ApplyContentSize()
    {
        if (Current.WidthIsManual || _settings.Window.Collapsed)
            return;

        var current = CurrentArea();
        var visibleCount = VisibleRows().Count;

        Width = WindowPlacementService.ResolveContentWidth(visibleCount, ViewModel.IsHorizontal, current.Width);

        var placed = WindowPlacementService.ResolvePosition(
            new WindowRect(Left, Top, Width, Math.Max(ActualHeight, MinHeight)), _monitorAreas.Areas, current);
        Left = placed.Left;
        Top = placed.Top;
    }

    private void ViewModel_HeightAutomaticChanged(object? sender, bool automatic)
    {
        // ApplyStoredSizeForCurrentLayout already applied the new layout's own remembered height (or
        // lack of one) - this handler's job here is only for a real checkbox toggle by the user.
        if (_restoringLayoutSize)
            return;

        _heightIsAutomatic = automatic;
        if (automatic)
        {
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            Current.Height = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            Height = ActualHeight;
        }
        UpdateDensity();
        SaveWindowSettings();
    }

    /// <summary>
    /// Picks the tile density for the current window mode from the tiles' real measured height per
    /// stage, never a fixed per-tile estimate (a tile with an error, a details disclosure or an extra
    /// row is taller than any model assumes). Automatic height: the largest stage that fits the
    /// work area, and the window takes exactly that content's height. Manual height (the resize
    /// thumbs): the largest stage that fits the height the user dragged. A hand-chosen stage wins in
    /// both.
    /// </summary>
    private void UpdateDensity()
    {
        if (_settings.Window.Collapsed)
            return;

        var visibleTiles = VisibleRows();
        var manualOverride = ManualDensityOverride();

        var measured = new Dictionary<TileDensity, double>();
        double ContentHeight(TileDensity stage)
        {
            if (!measured.TryGetValue(stage, out var height))
            {
                height = MeasureContentAt(stage, visibleTiles);
                measured[stage] = height;
            }
            return height;
        }

        TileDensity density;
        _applyingDensityHeight = true;
        try
        {
            if (_heightIsAutomatic)
            {
                var current = CurrentArea();

                // One concrete height, assigned with SizeToContent on Manual so WPF's own layout pass
                // never overwrites it again: the ScrollViewer's own desired height is not trustworthy
                // while its content is changing stage, the measurement above is.
                var resolved = WindowPlacementService.ResolveMeasuredDensityHeight(
                    ContentHeight, NonContentHeight(), manualOverride, current.Height);
                density = resolved.Density;
                SizeToContent = SizeToContent.Manual;
                Height = resolved.Height;

                // Content taller than the whole work area scrolls instead of running off screen, and
                // a window that grew past the bottom of the work area slides up. The SizeChanged this
                // assignment raises skips its own FitVertically (see _applyingDensityHeight), so it
                // happens here.
                MaxHeight = current.Height;
                Top = WindowPlacementService.FitVertically(Top, resolved.Height, current).Top;
                _autoHeightArea = current;
            }
            else
            {
                density = TileDensitySelector.SelectFitting(ContentHeight, ContentScroll.ActualHeight, manualOverride);
            }
            ContentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        finally
        {
            _applyingDensityHeight = false;
        }

        foreach (var tile in visibleTiles)
            tile.Density = density;

        // A tile that only just got its real size (the day grid right after it is shown) makes the
        // height measured above stale; one more pass settles it.
        if (measured.TryGetValue(density, out var usedHeight))
        {
            double settledHeight;
            _applyingDensityHeight = true;
            try
            {
                settledHeight = MeasureContentAt(density, visibleTiles);
            }
            finally
            {
                _applyingDensityHeight = false;
            }

            if (Math.Abs(settledHeight - usedHeight) > 1 && _densityFollowUps < 1)
            {
                _densityFollowUps++;
                QueueDensityUpdate(followUp: true);
            }
        }
    }

    private TileDensity? ManualDensityOverride() =>
        Enum.TryParse<TileDensity>(_settings.TileDensity, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    /// <summary>Every row the widget actually shows, provider tiles and the one day-grid tile alike,
    /// in <see cref="MainViewModel.DisplayRows"/>'s own order - the shared source every width/height/
    /// density calculation below reads instead of <see cref="MainViewModel.Tiles"/> alone, so the
    /// day-grid tile's own size counts toward the window fitting itself exactly as a provider's does.</summary>
    private List<ITileRow> VisibleRows() =>
        ViewModel.DisplayRows.Cast<ITileRow>().Where(row => !row.IsHidden).ToList();

    /// <summary>The tile list's natural height with every visible tile at <paramref name="stage"/>.
    /// Leaves the tiles at that stage; the caller sets the stage it actually wants afterwards.</summary>
    private double MeasureContentAt(TileDensity stage, IReadOnlyList<ITileRow> visibleTiles)
    {
        foreach (var tile in visibleTiles)
            tile.Density = stage;
        // A stage switch only invalidates the elements deep inside each tile; the tile list itself
        // still counts as measured and would hand back its old size. The layout pass settles those
        // first, so the measurement below sees the new stage.
        ContentScroll.UpdateLayout();
        return MeasureContentDesiredHeight();
    }

    /// <summary>How tall the tile list can get: at the hand-chosen stage as it stands, and on
    /// automatic at the largest stage, since dragging the window taller is exactly how the tiles get
    /// back to it. Every tile keeps the stage it had.</summary>
    private double MeasureTallestContentHeight()
    {
        if (ManualDensityOverride() is not null)
            return MeasureContentDesiredHeight();

        var visibleTiles = VisibleRows();
        var stages = visibleTiles.Select(t => t.Density).ToList();
        _applyingDensityHeight = true;
        try
        {
            return MeasureContentAt(TileDensity.Full, visibleTiles);
        }
        finally
        {
            for (var i = 0; i < visibleTiles.Count; i++)
                visibleTiles[i].Density = stages[i];
            _applyingDensityHeight = false;
        }
    }

    /// <summary>A tile grows or shrinks on its own when its content changes (a first answer, a
    /// sign-in hint, an error text), and the first layout is the first moment the tiles have a real
    /// width to wrap their text against. Either way the density and the automatic height were
    /// resolved against a content height that no longer holds, so they are resolved again - once,
    /// after the layout pass that caused it, never from inside it.</summary>
    private void ScrollContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The stage-by-stage measurement inside UpdateDensity resizes the content on purpose.
        if (!e.HeightChanged || _applyingDensityHeight)
            return;

        QueueDensityUpdate();
    }

    /// <summary>A theme swap can change the title bar's height without touching the tile list's, so
    /// the content-size hook above never fires for it; the window still has to fit again.</summary>
    private void ThemeService_Applied(object? sender, EventArgs e) => QueueDensityUpdate();

    private void QueueDensityUpdate(bool followUp = false)
    {
        if (!followUp)
            _densityFollowUps = 0;

        if (_densityUpdateQueued)
            return;

        _densityUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _densityUpdateQueued = false;
            UpdateDensity();
        });
    }

    /// <summary>Everything around the tile list (title bar, chrome, shadow margin, any banner): the
    /// window's height minus the scroll viewer's, once both have been laid out; the fixed title bar
    /// plus chrome before that.</summary>
    private double NonContentHeight() =>
        ActualHeight > 0 && ContentScroll.ActualHeight > 0
            ? ActualHeight - ContentScroll.ActualHeight
            : WindowPlacementService.DefaultChromeHeight;

    // Sizing to content happens after the density decision, so this is the only place that knows the
    // height the window actually took. A window that grew past the bottom of the work area slides up.
    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // While collapsed, the content is hidden and its ActualHeight is not the real content
        // height - computing a density from it here would leave a stale density in force until
        // Restore() runs. UpdateDensity() already re-checks this same flag, but the early return
        // also skips the FitVertically adjustment below, which has nothing meaningful to do while
        // the window sits collapsed at title-bar height.
        // While UpdateDensity() is assigning the height it just resolved, the SizeChanged that
        // assignment itself raises must not send the same height through UpdateDensity() a second time.
        if (_settings.Window.Collapsed || _applyingDensityHeight)
            return;

        UpdateDensity();

        if (!_heightIsAutomatic || _autoHeightArea is not { } area)
            return;

        var (top, maxHeight) = WindowPlacementService.FitVertically(Top, ActualHeight, area);
        Top = top;
        MaxHeight = maxHeight;
    }

    private void ApplyRememberedPlacement()
    {
        var monitors = _monitorAreas.Areas;
        var primary = monitors.FirstOrDefault(m => m.DeviceName == _settings.Window.MonitorDeviceName, FirstOrDefault(monitors));
        var desired = new WindowRect(_settings.Window.Left, _settings.Window.Top, Current.Width, Current.Height ?? MinHeight);

        var resolved = WindowPlacementService.ResolvePosition(desired, monitors, primary);
        Left = resolved.Left;
        Top = resolved.Top;
        Width = resolved.Width;

        // A remembered rectangle that no longer sits on any monitor (a screen unplugged, the
        // displays rearranged) has just been replaced by a resolved one. Writing that straight back
        // keeps the stored position the one the window actually has, so the next start opens where
        // this one stands instead of resolving the same dead rectangle all over again.
        if (resolved.Left != desired.Left || resolved.Top != desired.Top)
            SaveWindowSettings();

        _heightIsAutomatic = Current.Height is null;
        if (Current.Height is { } manualHeight)
        {
            var workAreaHeight = monitors.FirstOrDefault(m => Contains(m, resolved), primary).Height;
            SizeToContent = SizeToContent.Manual;
            Height = WindowPlacementService.ResolveManualHeight(manualHeight, workAreaHeight);
        }
        else
        {
            SizeToContent = SizeToContent.Height;
        }
    }

    private static MonitorArea FirstOrDefault(IReadOnlyList<MonitorArea> monitors) => monitors.Count > 0 ? monitors[0] : default;

    private static bool Contains(MonitorArea monitor, WindowRect rect) =>
        rect.Left >= monitor.Left && rect.Left < monitor.Right && rect.Top >= monitor.Top && rect.Top < monitor.Bottom;

    /// <summary>The monitor the window currently sits on, from the cached enumeration - shared by
    /// every call site that used to re-enumerate the native monitor list on its own.</summary>
    private MonitorArea CurrentArea() => PickArea(
        _monitorAreas.Areas,
        NativeMonitors.DeviceNameOfWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle),
        new WindowRect(Left, Top, Width, Math.Max(ActualHeight, MinHeight)));

    /// <summary>The monitor Windows names for the window wins; the position only decides when no
    /// name is known yet (no handle) or the name is missing from the cached list. With mixed scaling
    /// the per-monitor areas overlap, and the first area containing the position could be the wrong
    /// monitor.</summary>
    internal static MonitorArea PickArea(IReadOnlyList<MonitorArea> monitors, string? deviceName, WindowRect rect)
    {
        if (deviceName is not null)
        {
            foreach (var monitor in monitors)
            {
                if (monitor.DeviceName == deviceName)
                    return monitor;
            }
        }

        return monitors.FirstOrDefault(m => Contains(m, rect), FirstOrDefault(monitors));
    }

    private void SystemParameters_HighContrastChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast))
            return;

        ThemeService.Apply(ThemeService.ParseTheme(_settings.Theme));
        // A translucent window is exactly wrong once the user is relying on system colours for
        // readability - forced fully opaque while high contrast is on, restored the instant it is not.
        WindowOpacity.ApplyToAllOpenWindows(SystemParameters.HighContrast ? 100 : _settings.WindowOpacityPercent);
    }

    /// <summary>Windows raises "General" for a light/dark theme switch (there is no more specific
    /// category for it) - only relevant while the app itself is set to follow Windows
    /// (<see cref="AppTheme.System"/>), otherwise Windows switching its own theme changes nothing here.</summary>
    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && ThemeService.ParseTheme(_settings.Theme) == AppTheme.System)
            ThemeService.Apply(AppTheme.System);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _monitorAreas.Refresh();
        var hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        hwndSource.AddHook(WndProc);

        WindowChromeNative.Bootstrap(this, followsOpacity: true);

        _globalHotkey = new GlobalHotkey(hwndSource.Handle);
        ApplyHotkeySettings();
        ViewModel.HotkeySettingsChanged += (_, _) => ApplyHotkeySettings();

        // Fixed, never user-configurable, and never colliding with the one hotkey above - registered
        // once and for the lifetime of the window rather than re-applied like ApplyHotkeySettings.
        _globalHotkey.RegisterSnapHotkeys();

        // The extended window style itself only exists on the real HWND SourceInitialized just
        // handed over - a fresh window otherwise starts fully clickable even when the setting says
        // otherwise. Idempotent: re-running the always-on-top/opacity implications against values a
        // previous session already resolved never changes anything.
        ApplyClickThrough(_settings.ClickThrough);
    }

    /// <summary>(Re-)applies the hotkey to the live OS registration from whatever
    /// <see cref="MainViewModel.HotkeyEnabled"/>/<see cref="MainViewModel.HotkeyText"/> say right now
    /// - called once at startup and again every time either changes. Failing to register (another
    /// program already owns the combination) is reported back through <see
    /// cref="MainViewModel.HotkeyTaken"/> rather than thrown; the feature simply stays inactive.</summary>
    private void ApplyHotkeySettings()
    {
        if (_globalHotkey is null)
            return;

        _globalHotkey.Unregister();

        if (ViewModel.HotkeyEnabled && GlobalHotkey.TryParse(ViewModel.HotkeyText, out var modifiers, out var key))
        {
            var registered = _globalHotkey.Register(modifiers, key);
            ViewModel.HotkeyTaken = !registered;
            _tray.UpdateHotkeyShortcut(registered ? ViewModel.HotkeyText : null);
        }
        else
        {
            ViewModel.HotkeyTaken = false;
            _tray.UpdateHotkeyShortcut(null);
        }
    }

    /// <summary>Applies click-through both ways: the extended window style itself, and the
    /// always-on-top/opacity implications <see cref="ClickThroughPolicy"/> resolves purely. Called
    /// once at startup and again every time <see cref="MainViewModel.ClickThrough"/> changes, so the
    /// live window is never out of sync with the setting.</summary>
    private void ApplyClickThrough(bool clickThrough)
    {
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
            NativeWindowStyle.SetClickThrough(hwndSource.Handle, clickThrough);

        var resolution = _clickThroughPolicy.Resolve(clickThrough, ViewModel.AlwaysOnTop, _settings.WindowOpacityPercent);
        if (resolution.AlwaysOnTop != ViewModel.AlwaysOnTop)
            ViewModel.AlwaysOnTop = resolution.AlwaysOnTop;

        var resolvedOpacity = (int)resolution.WindowOpacityPercent;
        if (resolvedOpacity != _settings.WindowOpacityPercent)
        {
            _settings.WindowOpacityPercent = resolvedOpacity;
            _settingsStore.RequestSave(_settings);
            WindowOpacity.ApplyToAllOpenWindows(resolvedOpacity);
        }

        _tray.UpdateClickThrough(clickThrough);

        // The one-shot warning: nobody has to have read the Settings text to learn how to get the
        // mouse back, same "shown once, ever" contract as HideToTrayFlow's own tray balloon.
        if (clickThrough && TrayHintPolicy.ShouldShowHint(_settings.ClickThroughHintShown))
        {
            _settings.ClickThroughHintShown = true;
            _settingsStore.SaveNow(_settings);
            _tray.ShowBalloon(LocalizationService.Instance["Settings.ClickThroughHint"]);
        }
    }

    // Only the title bar's native DragMove() (a plain window MOVE) still goes through the Win32
    // size/move loop, so this hook only ever needs to persist position now - see the resize Thumbs
    // below for why height changes are handled entirely in managed code instead.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_globalHotkey is not null && msg == GlobalHotkey.WM_HOTKEY && GlobalHotkey.Matches(wParam))
        {
            // One of click-through's three ways out: the window is already visible and ignoring the
            // mouse, so the usual show/hide toggle would only hide a window nobody could have clicked
            // to begin with. Switching click-through off instead is the actually useful outcome.
            if (ViewModel.ClickThrough)
                ViewModel.ClickThrough = false;
            else
                ToggleVisibility();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == GlobalHotkey.WM_HOTKEY && GlobalHotkey.TryMatchSnapDirection(wParam, out var direction))
        {
            SnapToHalf(direction);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_ENTERSIZEMOVE)
            _replaceAfterDrag.DragStarted();

        if (msg == WM_EXITSIZEMOVE)
        {
            // Alt held at the end of the drag is the escape hatch for fine placement right next to
            // (but not flush with) an edge.
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                var snapped = WindowPlacementService.SnapToEdges(
                    new WindowRect(Left, Top, Width, Math.Max(ActualHeight, MinHeight)), CurrentArea());
                Left = snapped.Left;
                Top = snapped.Top;
            }
            if (_replaceAfterDrag.DragEnded())
                ReplaceOnScreen();
            SaveWindowSettings();
        }

        return IntPtr.Zero;
    }

    // AllowsTransparency=True (needed for the real rounded corners + soft shadow) disables Win32's
    // native non-client resize-border loop entirely for a layered window - confirmed by testing on
    // the VM (self-test): dragging inside WindowChrome's ResizeBorderThickness never even
    // started a resize. These eight Thumb grips (WPF's own drag-delta control) replace it.
    private void GrowRight(double dx)
    {
        MarkWidthManual();
        Width = Math.Max(MinWidth, Width + dx);
    }

    private void GrowLeft(double dx)
    {
        MarkWidthManual();
        var newWidth = Math.Max(MinWidth, Width - dx);
        Left += Width - newWidth;
        Width = newWidth;
    }

    /// <summary>A dragged width beats the automatic one until the eye menu switches it back on -
    /// same bargain the height already makes when a vertical grip is dragged.</summary>
    private void MarkWidthManual()
    {
        if (!Current.WidthIsManual)
            ViewModel.WidthAutomatic = false;
    }

    private void GrowDown(double dy)
    {
        if (_settings.Window.Collapsed)
            return;
        if (_heightIsAutomatic)
        {
            // Height is still NaN while the window sizes itself; pin the real height first so the
            // arithmetic below never starts from NaN.
            Height = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            _heightIsAutomatic = false;
            // Keeps the Settings window's "automatic height" checkbox truthful - a manual drag is
            // exactly as manual as toggling the checkbox off (ViewModel_HeightAutomaticChanged is a
            // no-op the moment it re-applies Manual sizing here, since it is already set).
            ViewModel.HeightAutomatic = false;
        }
        // A manual drag always clears whatever ceiling the last automatic run left MaxHeight pinned
        // to (see UpdateDensity/MainWindow_SizeChanged, the only other two writers) - raised back to
        // the current monitor's own work area, never fought as a stale leftover from before the
        // window was last dragged small.
        var workAreaHeight = CurrentArea().Height;
        MaxHeight = workAreaHeight;
        Height = WindowPlacementService.GrowManualHeight(Height, dy, MinHeight, workAreaHeight, MeasureTallestContentHeight());
    }

    /// <summary>The content area's natural height, measured fresh rather than read from a stale
    /// layout pass - what GrowDown clamps against and what the density choice fits. Falls back to no
    /// limit if the scroll viewer's content is ever not a FrameworkElement, so a measurement failure
    /// never traps the window below its floor instead of just not clamping. A row of tiles scrolls
    /// sideways and so is measured without a width limit, the same as the scroll viewer lays it out;
    /// before the first layout there is no width to measure against yet either.</summary>
    private double MeasureContentDesiredHeight()
    {
        if (ContentScroll.Content is not FrameworkElement content)
            return double.PositiveInfinity;
        var width = ViewModel.IsHorizontal || ContentScroll.ActualWidth <= 0 ? double.PositiveInfinity : ContentScroll.ActualWidth;
        content.Measure(new Size(width, double.PositiveInfinity));
        return content.DesiredSize.Height;
    }

    private void GrowUp(double dy)
    {
        if (_settings.Window.Collapsed)
            return;
        if (_heightIsAutomatic)
        {
            Height = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            _heightIsAutomatic = false;
            ViewModel.HeightAutomatic = false;
        }
        // Same stale-ceiling fix as GrowDown - see its own comment.
        MaxHeight = CurrentArea().Height;
        var newHeight = Math.Max(MinHeight, Height - dy);
        Top += Height - newHeight;
        Height = newHeight;
    }

    private void ResizeTop_DragDelta(object sender, DragDeltaEventArgs e) => GrowUp(e.VerticalChange);

    private void ResizeBottom_DragDelta(object sender, DragDeltaEventArgs e) => GrowDown(e.VerticalChange);

    /// <summary>The effect of a double click on the top or bottom resize grip - pulled out as a pure
    /// action over the view model, the same reason <see cref="Controls.TitleBar.ShouldOpenEyePopup"/>
    /// is one, so it is provable without constructing the live window.</summary>
    internal static void ResetHeightToAutomaticOnDoubleClick(MainViewModel viewModel, int clickCount)
    {
        if (clickCount == 2)
            viewModel.HeightAutomatic = true;
    }

    /// <summary>The same for the left and right grips: the width goes back to the tile width.</summary>
    internal static void ResetWidthToAutomaticOnDoubleClick(MainViewModel viewModel, int clickCount)
    {
        if (clickCount == 2)
            viewModel.WidthAutomatic = true;
    }

    /// <summary>A double click on any grip fits the window to its content in the direction(s) that
    /// grip resizes. A dimension that is already automatic changes nothing and raises no event, yet
    /// the user still asked for a fit - a tile may have grown since the size was last resolved - so
    /// the fit is run again by hand.</summary>
    private void FitOnDoubleClick(int clickCount, bool height, bool width)
    {
        if (clickCount != 2)
            return;

        var heightWasAutomatic = ViewModel.HeightAutomatic;
        var widthWasAutomatic = ViewModel.WidthAutomatic;
        if (height)
            ResetHeightToAutomaticOnDoubleClick(ViewModel, clickCount);
        if (width)
            ResetWidthToAutomaticOnDoubleClick(ViewModel, clickCount);

        if (width && widthWasAutomatic)
            ApplyContentSize();
        if (height && heightWasAutomatic)
            UpdateDensity();
    }

    private void ResizeHeightGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        FitOnDoubleClick(e.ClickCount, height: true, width: false);

    private void ResizeWidthGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        FitOnDoubleClick(e.ClickCount, height: false, width: true);

    private void ResizeCornerGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        FitOnDoubleClick(e.ClickCount, height: true, width: true);

    private void ResizeLeft_DragDelta(object sender, DragDeltaEventArgs e) => GrowLeft(e.HorizontalChange);

    private void ResizeRight_DragDelta(object sender, DragDeltaEventArgs e) => GrowRight(e.HorizontalChange);

    private void ResizeTopLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowUp(e.VerticalChange);
        GrowLeft(e.HorizontalChange);
    }

    private void ResizeTopRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowUp(e.VerticalChange);
        GrowRight(e.HorizontalChange);
    }

    private void ResizeBottomLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowDown(e.VerticalChange);
        GrowLeft(e.HorizontalChange);
    }

    private void ResizeBottomRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowDown(e.VerticalChange);
        GrowRight(e.HorizontalChange);
    }

    private void Resize_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_heightIsAutomatic)
            Current.Height = Height;
        SaveWindowSettings();
    }

    /// <summary>Every move the window really makes is written, not just the end of a mouse drag: a
    /// window moved by the keyboard, by the shell or by one of this app's own snaps used to keep the
    /// position it had at the last drag, and a process that is terminated rather than closed (an
    /// update installing over the running copy) then restored that stale one - far enough from where
    /// the window actually stood that the remembered rectangle could miss every monitor, which puts
    /// the window back in the middle of the screen instead. The store's own debounce collapses a
    /// whole drag into a single write, so this costs one save per move, not one per pixel.</summary>
    private void MainWindow_LocationChanged(object? sender, EventArgs e)
    {
        // Before the first layout the position is still whatever ApplyRememberedPlacement just put
        // there, and a hidden window does not move on its own - neither is a position the user chose.
        if (!IsLoaded || !IsVisible)
            return;

        SaveWindowSettings();
    }

    private void SaveWindowSettings()
    {
        _settings.Window.Left = Left;
        _settings.Window.Top = Top;
        Current.Width = Width;
        var current = CurrentArea();
        _settings.Window.MonitorDeviceName = current.DeviceName ?? _settings.Window.MonitorDeviceName;
        _settingsStore.RequestSave(_settings);
    }

    private void Collapse()
    {
        _uncollapsedHeight = ActualHeight > 0 ? ActualHeight : Height;
        // Collapsed is set before SizeToContent changes, not after: for this layered, transparent
        // window that assignment can resize the HWND and raise SizeChanged synchronously, right here,
        // rather than on a later layout pass - MainWindow_SizeChanged() must already see Collapsed as
        // true at that point, or its own reentrant UpdateDensity() call resolves and reassigns the
        // ordinary automatic height, overwriting the collapse before this method even returns.
        _settings.Window.Collapsed = true;
        ContentScroll.Visibility = Visibility.Collapsed;
        // MinHeight must already be 0 before SizeToContent recalculates, or the old floor still
        // applies for one frame and leaves a strip of bare background below the title bar.
        MinHeight = WindowPlacementService.MinHeightFor(collapsed: true);
        SizeToContent = SizeToContent.Height;
        TitleBarControl.IsCollapsed = true;
    }

    private void Restore()
    {
        ContentScroll.Visibility = Visibility.Visible;
        MinHeight = WindowPlacementService.MinHeightFor(collapsed: false);
        _settings.Window.Collapsed = false;
        TitleBarControl.IsCollapsed = false;

        var automatic = Current.Height is null;
        _heightIsAutomatic = automatic;
        var restoreHeight = WindowPlacementService.RestoreHeight(_uncollapsedHeight, Current.Height, automatic);
        // Never SizeToContent.Height alone: the ScrollViewer's desired height while its content was
        // still hidden by the collapse is not the tiles' real height (measured 88, not 720), so WPF's
        // own remeasure cannot be trusted here either. Both branches now assign a concrete height this
        // program already knows how to compute, exactly like UpdateDensity() does for the same reason.
        SizeToContent = SizeToContent.Manual;
        Height = restoreHeight ?? WindowPlacementService.ResolveAutomaticHeight(
            VisibleRows().Count, TileDensity.Full, CurrentArea().Height).WindowHeight;

        // The density computed while the content sat hidden is stale the moment the
        // content comes back - recompute it now, before the size settles, rather than waiting on
        // whatever SizeChanged happens to fire next.
        UpdateDensity();
    }

    /// <summary>F5 refreshes, Ctrl+, opens Settings - the same actions already reachable via the
    /// title bar's own buttons. The two conditions are pulled out as pure,
    /// unit-testable predicates (<see cref="IsRefreshShortcut"/>/<see cref="IsSettingsShortcut"/>)
    /// because end-to-end proof on a real window was not obtainable: automated synthetic key
    /// delivery never reached this window in several attempts (F5, Ctrl+,, Esc all silently did
    /// nothing, while every mouse-driven interaction on the same window worked normally) - a
    /// disclosed test-tooling gap, not a code defect.</summary>
    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (IsRefreshShortcut(e.Key))
        {
            ViewModel.RefreshNow(userStarted: true);
            e.Handled = true;
        }
        else if (IsSettingsShortcut(e.Key, Keyboard.Modifiers))
        {
            TitleBarControl_SettingsRequested(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (IsStatsShortcut(e.Key, Keyboard.Modifiers))
        {
            TitleBarControl_StatsRequested(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (IsEyeMenuShortcut(e.Key, Keyboard.Modifiers))
        {
            TitleBarControl.OpenEyeMenu();
            e.Handled = true;
        }
        else if (IsWindowMenuShortcut(e.Key, Keyboard.Modifiers))
        {
            TitleBarControl.OpenWindowMenu();
            e.Handled = true;
        }
    }

    internal static bool IsRefreshShortcut(Key key) => key == Key.F5;

    internal static bool IsSettingsShortcut(Key key, ModifierKeys modifiers) => key == Key.OemComma && modifiers == ModifierKeys.Control;

    internal static bool IsStatsShortcut(Key key, ModifierKeys modifiers) => key == Key.T && modifiers == ModifierKeys.Control;

    internal static bool IsEyeMenuShortcut(Key key, ModifierKeys modifiers) => key == Key.L && modifiers == ModifierKeys.Control;

    /// <summary>Alt+Space is the standard Windows accelerator for a window's system menu - never
    /// reached this window on its own since AllowsTransparency drops the native non-client handling
    /// that would normally open it.</summary>
    internal static bool IsWindowMenuShortcut(Key key, ModifierKeys modifiers) => key == Key.Space && modifiers == ModifierKeys.Alt;

    private void TitleBarControl_RefreshRequested(object? sender, EventArgs e) => ViewModel.RefreshNow(userStarted: true);

    private SettingsWindow? _settingsWindow;

    private void TitleBarControl_SettingsRequested(object? sender, EventArgs e)
    {
        if (_settingsWindow is null)
        {
            var settingsViewModel = new SettingsViewModel(ViewModel, _settings, _settingsStore);
            _settingsWindow = new SettingsWindow(settingsViewModel);
            OwnerWindowResolver.ApplyOwner(_settingsWindow, this);
            _settingsWindow.Closed += (_, _) =>
            {
                settingsViewModel.Dispose(); // unsubscribes from LocalizationService
                _settingsWindow = null;
            };
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void EmptyStateSettings_Click(object sender, RoutedEventArgs e) =>
        TitleBarControl_SettingsRequested(sender, EventArgs.Empty);

    private StatsWindow? _statsWindow;

    /// <summary>Reachable from two places: the main window's own title bar and the tray menu,
    /// both routed here so there is exactly one place that decides whether to build a new window or
    /// just bring the existing one forward - the same single-instance shape as
    /// <see cref="TitleBarControl_SettingsRequested"/>. Built fresh from a
    /// new <see cref="StatsStore"/>/<see cref="StatsViewModel"/> each time the window itself is
    /// rebuilt, never kept around once closed - the window "remembers nothing", so there is
    /// nothing worth keeping alive past its own Closed event either.</summary>
    private void TitleBarControl_StatsRequested(object? sender, EventArgs e)
    {
        if (_statsWindow is null)
        {
            _statsWindow = new StatsWindow(new StatsViewModel(new StatsStore()), _settings, _settingsStore);
            OwnerWindowResolver.ApplyOwner(_statsWindow, this);
            _statsWindow.Closed += (_, _) => _statsWindow = null;
        }
        _statsWindow.Show();
        _statsWindow.Activate();
    }

    private void TitleBarControl_MinimizeRequested(object? sender, EventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>Minimized counts as not visible for the refresh cadence, the same as hidden into the
    /// tray; restoring from the taskbar makes it visible again and refreshes once, like
    /// <see cref="ShowAndActivate"/> does for a window coming back from the tray.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // A state change while the constructor still runs happens before the view model exists.
        if (ViewModel is null)
            return;
        if (WindowState == WindowState.Minimized)
        {
            ViewModel.WindowVisible = false;
        }
        else if (!_showingAndActivating && Visibility == Visibility.Visible && !ViewModel.WindowVisible)
        {
            ViewModel.WindowVisible = true;
            _tickTimer.Interval = ResolveTickInterval(windowVisible: true);
            ViewModel.RefreshNow();
        }
    }

    private bool _showingAndActivating;

    private void TitleBarControl_AlwaysOnTopToggleRequested(object? sender, bool value) => ViewModel.AlwaysOnTop = value;

    /// <summary>Called from the Ctrl+Alt+Arrow global hotkeys (see WndProc). A snapped window is
    /// exactly as manual as one dragged by hand: both dimensions stop following automatic sizing
    /// until the user switches it back on.</summary>
    private void SnapToHalf(SnapDirection direction)
    {
        if (_settings.Window.Collapsed)
            return;

        var area = CurrentArea();
        var snapped = WindowPlacementService.SnapToHalf(area, direction);

        if (_heightIsAutomatic)
        {
            SizeToContent = SizeToContent.Manual;
            _heightIsAutomatic = false;
            ViewModel.HeightAutomatic = false;
        }
        if (!Current.WidthIsManual)
            ViewModel.WidthAutomatic = false;

        Left = snapped.Left;
        Top = snapped.Top;
        Width = snapped.Width;
        Height = snapped.Height;
        Current.Height = Height;

        SaveWindowSettings();
    }

    // ShutdownMode is OnExplicitShutdown (App.xaml): closing the window now just hides it into the
    // tray - the tray menu's exit item (Tray.Exit) is the only real way to quit.
    private void TitleBarControl_CloseRequested(object? sender, EventArgs e) => HideToTray();

    /// <summary>Shared by the title bar's close button and by a cancelled window Closing (Alt+F4,
    /// the taskbar) - one flow so the two paths can never drift apart.</summary>
    private void HideToTray() =>
        HideToTrayFlow.Run(
            _settings.TrayHintShown,
            () =>
            {
                Hide();
                ViewModel.WindowVisible = false;
            },
            () => _tray.ShowBalloon(LocalizationService.Instance["Tray.StillRunning"]),
            () =>
            {
                _settings.TrayHintShown = true;
                _settingsStore.SaveNow(_settings);
            });

    private void Application_SessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _reallyClosing = true;
        SaveWindowSettings();
        _settingsStore.SaveNow(_settings);
    }

    private static readonly TimeSpan UpdateRecheckInterval = TimeSpan.FromHours(6);

    // Kept in a field so the timer is rooted for the window's whole life.
    private DispatcherTimer? _updateTimer;

    /// <summary>The daily update check (it only runs when a day has passed, see <see
    /// cref="UpdateCheck.CheckIfDueAsync"/>): once shortly after start, then every few hours for a
    /// copy that stays in the tray for days. Started here rather than on Loaded because a tray-only
    /// start never loads the window.</summary>
    private void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = UpdateRecheckInterval };
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, async () => await CheckForUpdateAsync());
    }

    /// <summary>One update check that can never throw: both callers are fire-and-forget handlers, where
    /// an exception ends the process.</summary>
    private async Task CheckForUpdateAsync()
    {
        try
        {
            await ViewModel.Update.CheckAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogService.Shared.LogError($"Update check failed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e) =>
        await UpdateDialogs.InstallAsync(this, ViewModel.Update);

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => ViewModel.Update.Dismiss();

    /// <summary>The only way the window is really allowed to close - the tray menu's exit item (Tray.Exit).</summary>
    private void ShutdownFromTray()
    {
        _reallyClosing = true;
        Application.Current.Shutdown();
    }

    private void TitleBarControl_CollapseToggleRequested(object? sender, EventArgs e)
    {
        if (_settings.Window.Collapsed)
            Restore();
        else
            Collapse();

        SaveWindowSettings();
        _settingsStore.SaveNow(_settings);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_reallyClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _tickTimer.Stop();
        SaveWindowSettings();
        _settingsStore.SaveNow(_settings);
        Dispose();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameters_HighContrastChanged;
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        ThemeService.Applied -= ThemeService_Applied;
        ViewModel.Tiles.CollectionChanged -= ViewModel_Tiles_CollectionChanged;
        _updateTimer?.Stop();
        _globalHotkey?.Dispose();
        _tray.Dispose();
        // Blocking is safe here: this only ever runs from MainWindow_Closing on the UI thread, and
        // nothing DisposeAsync awaits needs to marshal back onto that same thread to complete.
        ViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
