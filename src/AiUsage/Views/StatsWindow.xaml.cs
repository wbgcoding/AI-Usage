using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views.Controls;

namespace AiUsage.Views;

/// <summary>
/// The statistics window. Resizable in every direction the same way <see cref="MainWindow"/>
/// is - eight Thumb grips dragging <see cref="Width"/>/<see cref="Height"/>, needed regardless of
/// <c>AllowsTransparency</c> since <c>ResizeMode=NoResize</c> already keeps WPF's own native resize
/// border out of it. The selected period never survives a close, but the size does,
/// persisted through <see cref="WindowPlacementService.Shared"/> - it falls back to the fixed
/// 720x520 default only once a remembered size no longer fits the monitor the window is about to
/// open on. <see cref="StatsViewModel"/> itself still carries no <see cref="Models.AppSettings"/> -
/// the opacity mode still comes from <see cref="WindowOpacity.CurrentPercent"/> for
/// that reason - but this window now takes an optional settings/store pair of its own, just to
/// persist which <see cref="Controls.CollapsibleSection"/>s are collapsed
/// (<see cref="AppSettings.StatsSectionsCollapsed"/>); either or both left null (as every test but
/// <see cref="MainWindow"/>'s own real construction does) simply skips that persistence.
/// </summary>
public partial class StatsWindow : Window
{

    private readonly StatsViewModel _viewModel;
    private readonly AppSettings? _settings;
    private readonly SettingsStore? _settingsStore;
    private double _rememberedScrollOffset;
    private bool _reloadingFromSettings;

    // True once the window has really been shown. A window built ahead of time and never shown must not
    // write its XAML default size over the remembered one when it is discarded.
    private bool _opened;
    private readonly StatsLayoutPresenter _layoutPresenter;
    private readonly StatsSectionDragController _dragController;
    private readonly Dictionary<Controls.CollapsibleSection, ContextMenu> _sectionMenus = [];

    public StatsWindow(StatsViewModel viewModel, AppSettings? settings = null, SettingsStore? settingsStore = null)
    {
        _viewModel = viewModel;
        _settings = settings;
        _settingsStore = settingsStore;
        if (settings is not null)
        {
            _viewModel.AccountCountOf = providerId =>
                settings.Providers.Keys.Count(key => ProviderRegistry.BaseProviderId(key) == providerId);
        }

        InitializeComponent();
        WindowChromeNative.Bootstrap(this);
        DataContext = _viewModel;

        _layoutPresenter = new StatsLayoutPresenter(SectionHost, Sections.ToDictionary(section => section.SectionKey));
        _layoutPresenter.Apply(_settings?.StatsSectionLayout ?? StatsLayout.Default());
        if (_settings is not null)
        {
            _viewModel.RestoreSelection(
                _settings.StatsRange, _settings.StatsGrouping, _settings.StatsColorBy, _settings.StatsCustomFrom, _settings.StatsCustomTo,
                _settings.StatsProvider);
        }
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(StatsViewModel.SelectedRange) or nameof(StatsViewModel.SelectedGrouping)
                or nameof(StatsViewModel.SelectedColorBy) or nameof(StatsViewModel.SelectedProvider))
                RememberSelection();
        };
        _dragController = new StatsSectionDragController(this, ContentScroller, _layoutPresenter, CommitLayout);
        WireSectionChrome();
        UpdateResetButton();

        // The custom range is picked in a popup under the range list; it opens after the list has
        // closed, since a popup opened in the middle of the list's own click is closed again by it.
        LimitCustomRangePickers();
        _viewModel.CustomRangeRequested += (_, _) =>
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                LimitCustomRangePickers();
                CustomRangePopup.IsOpen = true;
            });
        _viewModel.CustomRangeApplied += (_, _) => CustomRangePopup.IsOpen = false;

        _viewModel.TakeSnapshot = () => StatsIndexerService.Shared?.Latest;

        PreviewKeyDown += Window_PreviewKeyDown;
        SourceInitialized += (_, _) => HideUntilFirstFrame();
        Loaded += StatsWindow_Loaded;
        IsVisibleChanged += StatsWindow_IsVisibleChanged;
        Closed += StatsWindow_Closed;

        // StatsIndexerService.IndexCompleted fires on its own background thread, so the reload is
        // marshalled back here. The walk itself starts when the window is really shown.
        if (StatsIndexerService.Shared is { } indexer)
        {
            indexer.IndexCompleted += OnIndexCompleted;
            indexer.ProgressChanged += OnIndexProgressChanged;
            _viewModel.IndexProgress = indexer.Progress;
        }

        MonthGrid.ProviderDisplayNames = Stats.StatsViewModel.ProviderDisplayNames;
        // The grid reaches back as far as its scroller's width fits; the view model only re-slices in
        // memory when that moves the start. A narrow window keeps the horizontal scroll, parked on the
        // newest end.
        MonthGridScroller.SizeChanged += (_, e) =>
        {
            _viewModel.MonthGridAvailableWidth = e.NewSize.Width;
            MonthGridScroller.ScrollToRightEnd();
        };
        MonthGrid.SizeChanged += (_, _) => MonthGridScroller.ScrollToRightEnd();

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StatsViewModel.IsStackedByProvider) or nameof(StatsViewModel.Bars)
                or nameof(StatsViewModel.ProjectColors) or nameof(StatsViewModel.ChartSeries) or nameof(StatsViewModel.ChartOverlay)
                or nameof(StatsViewModel.HeatmapLeaderKeys))
                RefreshSeriesBrushes();
            if (e.PropertyName == nameof(StatsViewModel.ProviderShareSlices))
                RefreshProviderRingBrushes();
            if (e.PropertyName == nameof(StatsViewModel.ModelShareSlices))
                RefreshModelRingBrushes();
            if (e.PropertyName == nameof(StatsViewModel.EffortShareSlices))
                RefreshEffortRingBrushes();
            if (e.PropertyName == nameof(StatsViewModel.SelectedDayDetail))
                RefreshDayProviderBrushes();
            if (e.PropertyName == nameof(StatsViewModel.IsLoading))
                OnIsLoadingChanged();
        };
        RefreshSeriesBrushes();
        RefreshShareBrushes();
        RefreshProviderRingBrushes();
        RefreshModelRingBrushes();
        RefreshEffortRingBrushes();
        RefreshDayProviderBrushes();
        ApplySectionStates();

        ThemeService.Applied += OnThemeApplied;
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Escape first abandons a running drag; only without one does it close the window.
            if (_dragController.IsDragging)
            {
                _dragController.Cancel();
                e.Handled = true;
                return;
            }

            if (TryClosePopupOnEscape(Keyboard.FocusedElement as DependencyObject ?? e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
                return;
            }

            if (EscapeKey.ClosesWindow(e))
                Close();
            return;
        }

        // Alt+Up/Down moves a section, but only from its heading button: in a combo box the same
        // keys must keep opening the list.
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.System && e.SystemKey is Key.Up or Key.Down
            && Keyboard.FocusedElement is Button { TemplatedParent: Controls.CollapsibleSection section }
            && _sectionMenus.ContainsKey(section))
        {
            MoveSection(section, e.SystemKey == Key.Up);
            e.Handled = true;
        }
    }

    /// <summary>Inside the custom range panel or the provider list Escape closes that popup and puts
    /// the focus back on the control that opened it; inside a date picker's calendar it is left to the
    /// calendar. False when <paramref name="focused"/> is in neither popup.</summary>
    internal bool TryClosePopupOnEscape(DependencyObject? focused)
    {
        if (CustomFromPicker.IsDropDownOpen || CustomToPicker.IsDropDownOpen)
            return false;

        var popup = EscapeKey.OpenPopupOf(focused);
        if (popup != CustomRangePopup && popup != ProviderPopup)
            return false;

        popup.IsOpen = false;
        if (popup == CustomRangePopup)
            RangeCombo.Focus();
        else
            ProviderButton.Focus();
        return true;
    }

    /// <summary>Moves a section one step up or down (see <see cref="StatsLayout.MoveUp"/>), then puts
    /// the keyboard focus back on its heading, since moving it detaches the focused button.</summary>
    internal void MoveSection(Controls.CollapsibleSection section, bool up)
    {
        var rows = up
            ? StatsLayout.MoveUp(_layoutPresenter.Current, section.SectionKey, VisibleSectionKeys())
            : StatsLayout.MoveDown(_layoutPresenter.Current, section.SectionKey, VisibleSectionKeys());
        if (StatsLayout.AreEqual(rows, _layoutPresenter.Current))
            return;

        CommitLayout(rows);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            HeaderButtonOf(section)?.Focus();
            section.BringIntoView();
        });
    }

    private static Button? HeaderButtonOf(Controls.CollapsibleSection section)
    {
        section.ApplyTemplate();
        return section.Template.FindName("PART_HeaderButton", section) as Button;
    }

    /// <summary>Gives every section heading its context menu and its keyboard hint.</summary>
    private void WireSectionChrome()
    {
        foreach (var section in Sections)
        {
            if (HeaderButtonOf(section) is not { } button)
                continue;

            var up = new MenuItem();
            up.Click += (_, _) => MoveSection(section, up: true);
            var down = new MenuItem();
            down.Click += (_, _) => MoveSection(section, up: false);
            var reset = new MenuItem();
            reset.Click += (_, _) => CommitLayout(StatsLayout.Default());
            var menu = new ContextMenu { Items = { up, down, reset } };
            _sectionMenus[section] = menu;
            button.ContextMenu = menu;
            button.ContextMenuOpening += (_, _) => RefreshContextMenu(section);
        }

        RefreshSectionChrome();
    }

    /// <summary>Sets the heading buttons' accelerator hint in the current language.</summary>
    private void RefreshSectionChrome()
    {
        foreach (var section in Sections)
        {
            if (HeaderButtonOf(section) is { } button)
                AutomationProperties.SetAcceleratorKey(button, LocalizationService.Instance["Stats.Layout.MoveKeys"]);
        }
    }

    /// <summary>The context menu of a section heading.</summary>
    internal ContextMenu SectionMenu(Controls.CollapsibleSection section) => _sectionMenus[section];

    /// <summary>Texts in the current language, and which entries make sense right now: a move only
    /// when it would change the arrangement, the reset only away from the default.</summary>
    internal void RefreshContextMenu(Controls.CollapsibleSection section)
    {
        var localization = LocalizationService.Instance;
        var current = _layoutPresenter.Current;
        var visible = VisibleSectionKeys();
        var up = (MenuItem)_sectionMenus[section].Items[0];
        var down = (MenuItem)_sectionMenus[section].Items[1];
        var reset = (MenuItem)_sectionMenus[section].Items[2];
        up.Header = localization["Stats.Layout.MoveUp"];
        up.InputGestureText = localization["Stats.Layout.MoveUpKey"];
        up.IsEnabled = !StatsLayout.AreEqual(StatsLayout.MoveUp(current, section.SectionKey, visible), current);
        down.Header = localization["Stats.Layout.MoveDown"];
        down.InputGestureText = localization["Stats.Layout.MoveDownKey"];
        down.IsEnabled = !StatsLayout.AreEqual(StatsLayout.MoveDown(current, section.SectionKey, visible), current);
        reset.Header = localization["Stats.Layout.Reset"];
        reset.IsEnabled = !StatsLayout.IsDefault(current);
    }

    /// <summary>Keys of the sections currently shown; a move steps over the others.</summary>
    private List<string> VisibleSectionKeys() =>
        Sections.Where(section => section.Visibility == Visibility.Visible).Select(section => section.SectionKey).ToList();

    /// <summary>The pickers offer no day before the start of 2000 and none after today, the same
    /// bounds the view model clamps a custom range to.</summary>
    private void LimitCustomRangePickers()
    {
        CustomFromPicker.DisplayDateEnd = DateTime.Today;
        CustomToPicker.DisplayDateEnd = DateTime.Today;
    }

    /// <summary>A popup that closed without an applied range puts the list back on the range that
    /// is really showing.</summary>
    private void CustomRangePopup_Closed(object? sender, EventArgs e) => _viewModel.ResyncRangeChoice();

    private void ResetLayoutButton_Click(object sender, RoutedEventArgs e) => CommitLayout(StatsLayout.Default());

    /// <summary>The reset button only shows while the arrangement differs from the default.</summary>
    private void UpdateResetButton() =>
        ResetLayoutButton.Visibility = StatsLayout.IsDefault(_layoutPresenter.Current) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Applies a new section arrangement and remembers it; the default arrangement is stored
    /// as null. Without a settings pair the arrangement only lasts for this window. The sections
    /// slide into their new places; a dropped one starts from <paramref name="flownFrom"/>.</summary>
    internal void CommitLayout(IReadOnlyList<StatsLayoutRow> rows, CollapsibleSection? flown = null, Point flownFrom = default)
    {
        _layoutPresenter.ApplyAnimated(rows, flown, flownFrom);
        UpdateResetButton();
        if (_settings is null || _settingsStore is null)
            return;

        _settings.StatsSectionLayout = StatsLayout.IsDefault(rows) ? null : _layoutPresenter.Current.ToList();
        _settingsStore.RequestSave(_settings);
    }

    /// <summary>A language switch while the window is open: the texts the view model composes in
    /// code are built again in the new language.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLanguageChanged(sender, e));
            return;
        }

        _viewModel.RefreshLanguage();
        RefreshSectionChrome();
    }

    /// <summary>A change of the theme: every code-behind brush is resolved again so the open window
    /// recolors at once.</summary>
    private void OnThemeApplied(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnThemeApplied(sender, e));
            return;
        }

        RefreshSeriesBrushes();
        RefreshShareBrushes();
        RefreshProviderRingBrushes();
        RefreshModelRingBrushes();
        RefreshEffortRingBrushes();
        RefreshDayProviderBrushes();
    }

    /// <summary>Every collapsible section this window owns, in no particular order - the list
    /// <see cref="ApplySectionStates"/> and <see cref="Controls.CollapsibleSection.SectionKey"/>
    /// (its own dictionary key in <see cref="AppSettings.StatsSectionsCollapsed"/>) work through.</summary>
    private IEnumerable<Controls.CollapsibleSection> Sections =>
    [
        FiguresSection, BreakdownSection, ProviderSection, ModelSection, EffortSection,
        LimitsSection, ProjectsSection, MonthGridSection,
    ];

    /// <summary>Sets every section to its remembered state (missing from the dictionary counts as
    /// expanded) and starts persisting the next change onward. A no-op with no <see
    /// cref="_settings"/>/<see cref="_settingsStore"/> - every caller but <see cref="MainWindow"/>'s
    /// own real construction leaves them null, and the sections then simply stay at their default,
    /// expanded state for that window's own lifetime.</summary>
    private void ApplySectionStates()
    {
        SetSectionStates();
        foreach (var section in Sections)
            section.IsExpandedChanged += (_, _) => PersistSectionState(section);
    }

    private void SetSectionStates()
    {
        foreach (var section in Sections)
            section.IsExpanded = !(_settings?.StatsSectionsCollapsed.GetValueOrDefault(section.SectionKey) ?? false);
    }

    /// <summary>Takes the arrangement, the collapsed sections and the period, grouping, color and
    /// provider choices from the settings again, after a reset or an import replaced them while this
    /// window is open. The charts follow from the records already loaded. Nothing is written back
    /// meanwhile, so a half-applied choice never overwrites the new values.</summary>
    public void ReloadFromSettings()
    {
        if (_settings is null)
            return;

        _reloadingFromSettings = true;
        try
        {
            _dragController.Cancel();
            _layoutPresenter.Apply(_settings.StatsSectionLayout ?? StatsLayout.Default());
            UpdateResetButton();
            SetSectionStates();
            _viewModel.RestoreSelection(
                _settings.StatsRange, _settings.StatsGrouping, _settings.StatsColorBy, _settings.StatsCustomFrom, _settings.StatsCustomTo,
                _settings.StatsProvider);
        }
        finally
        {
            _reloadingFromSettings = false;
        }
    }

    private void PersistSectionState(Controls.CollapsibleSection section)
    {
        if (_settings is null || _settingsStore is null || _reloadingFromSettings)
            return;

        _settings.StatsSectionsCollapsed[section.SectionKey] = !section.IsExpanded;
        _settingsStore.RequestSave(_settings);
    }

    /// <summary>Keeps the scroll position steady across a period/grouping change: the view model
    /// starts <see cref="StatsViewModel.RecomputeAsync"/> itself, so there is no call site here to
    /// remember the offset from - this reacts to <see cref="StatsViewModel.IsLoading"/> instead, the
    /// one signal that brackets every such rebuild. The restore runs through
    /// <see cref="Dispatcher.BeginInvoke(Delegate, DispatcherPriority)"/> at
    /// <see cref="DispatcherPriority.Loaded"/> because the new content's own height - and so its
    /// <see cref="ScrollViewer.ScrollableHeight"/> - is not settled until layout has run once with
    /// the rebuilt collections in place.</summary>
    private void OnIsLoadingChanged()
    {
        if (_viewModel.IsLoading)
        {
            _rememberedScrollOffset = ContentScroller.VerticalOffset;
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            ContentScroller.ScrollToVerticalOffset(Math.Min(_rememberedScrollOffset, ContentScroller.ScrollableHeight)));
    }

    /// <summary>The initial load runs unawaited, so a failure is logged here instead of vanishing
    /// in an unobserved task.</summary>
    private async Task RecomputeLoggedAsync(bool showLoading = true)
    {
        try
        {
            await _viewModel.RecomputeAsync(showLoading);
        }
        catch (Exception ex)
        {
            LogService.Shared.LogError($"Stats load failed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Builds what can be built without showing the window: the prepared data and a first
    /// layout pass, so that showing it later only has to render. Run at idle, never on the open path.</summary>
    internal void PrepareOffscreen()
    {
        _viewModel.TryShowSnapshot();
        ApplyRememberedSizeAhead();
        if (Content is UIElement root && !double.IsNaN(Width) && !double.IsNaN(Height))
        {
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));
            root.UpdateLayout();
        }
    }

    /// <summary>Takes the opening size before the first layout pass, so the open does not lay out
    /// every chart again at a new size. Without a remembered position the monitor is the widget's;
    /// <see cref="StatsWindow_Loaded"/> still corrects the size should the window land elsewhere.</summary>
    private void ApplyRememberedSizeAhead()
    {
        var placement = WindowPlacementService.Shared;
        var probe = placement?.RememberedStatsWindowPosition is { } position
            ? new WindowRect(position.Left + 1, position.Top + 1, 0, 0)
            : Application.Current?.MainWindow is { } main && !double.IsNaN(main.Left)
                ? new WindowRect(main.Left + main.ActualWidth / 2, main.Top + main.ActualHeight / 2, 0, 0)
                : new WindowRect(0, 0, 0, 0);
        var area = MainWindow.PickArea(NativeMonitors.WorkAreas(), null, probe);
        var size = WindowPlacementService.ResolveStatsWindowSize(placement?.RememberedStatsWindowSize, area, MinWidth, MinHeight);
        Width = size.Width;
        Height = size.Height;
    }

    private readonly List<CollapsibleSection> _deferredBodies = [];
    private bool _revealStarted;

    /// <summary>Leaves the bodies of the sections that start below the visible area out of the first
    /// frame, so opening lays out and draws only what is on screen. Needs a laid-out window (<see
    /// cref="PrepareOffscreen"/>); the bodies come back one per frame once the window is drawn.</summary>
    internal void DeferSectionsBelowTheFold()
    {
        var visibleHeight = ContentScroller.ViewportHeight;
        if (visibleHeight <= 0)
            return;

        foreach (var section in Sections)
        {
            if (section.Visibility != Visibility.Visible || !section.IsExpanded || section.IsBodyDeferred)
                continue;
            if (section.TranslatePoint(default, ContentScroller).Y < visibleHeight)
                continue;

            section.IsBodyDeferred = true;
            _deferredBodies.Add(section);
        }

        if (_deferredBodies.Count == 0)
            return;

        ContentRendered += (_, _) => StartRevealingSections();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, StartRevealingSections);
    }

    private void StartRevealingSections()
    {
        if (_revealStarted)
            return;
        _revealStarted = true;
        RevealNextSection();
    }

    /// <summary>One section per dispatcher round at background priority: each gets its own frame and
    /// input stays answered in between.</summary>
    private void RevealNextSection()
    {
        if (_deferredBodies.Count == 0)
            return;

        var section = _deferredBodies[0];
        _deferredBodies.RemoveAt(0);
        section.IsBodyDeferred = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RevealNextSection);
    }

    /// <summary>How long the window stays hidden at most when no first frame is reported.</summary>
    internal static readonly TimeSpan RevealFallback = TimeSpan.FromSeconds(1);

    /// <summary>Keeps the new window off the screen until its first frame is drawn, so it appears with
    /// its content instead of as an empty frame that fills a moment later.</summary>
    private void HideUntilFirstFrame()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (NativeWindowStyle.SetCloaked(handle, true) != 0)
            return;

        var fallback = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = RevealFallback };
        void Reveal(object? sender, EventArgs e)
        {
            ContentRendered -= Reveal;
            fallback.Stop();
            NativeWindowStyle.SetCloaked(handle, false);
        }
        ContentRendered += Reveal;
        fallback.Tick += Reveal;
        fallback.Start();
    }

    private long _openStartedAt;
    private double _openDataMs;

    /// <summary>Starts the clock for the one log line a real open writes: from the click to the first
    /// rendered frame. Without a start there is nothing to log (windows built by tests).</summary>
    internal void BeginOpenMeasurement(long startTimestamp)
    {
        _openStartedAt = startTimestamp;
        if (!_opened)
        {
            ContentRendered += StatsWindow_FirstRendered;
            return;
        }

        // A hidden window shown again draws no new first frame: the first idle moment after showing is the end.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => StatsWindow_FirstRendered(null, EventArgs.Empty));
    }

    private void StatsWindow_FirstRendered(object? sender, EventArgs e)
    {
        ContentRendered -= StatsWindow_FirstRendered;
        var total = Stopwatch.GetElapsedTime(_openStartedAt).TotalMilliseconds;
        LogService.Shared.LogInfo(FormatOpenLog(total, _openDataMs));
    }

    /// <summary>The line written to the log for every open. Layout is everything that is not data:
    /// building or preparing the window, laying it out and drawing the first frame.</summary>
    internal static string FormatOpenLog(double totalMs, double dataMs) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Statistics window: shown in {totalMs:F0} ms (data {dataMs:F0} ms, layout {Math.Max(0, totalMs - dataMs):F0} ms)");

    /// <summary>Applies the remembered size, if any, once the window has a real position to test a
    /// monitor's work area against - the constructor still only knows the XAML default.</summary>
    private void StatsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // The view model reads nothing on construction. With the indexer's prepared data the window is
        // filled at once and the read below only checks whether anything moved; without it this is the
        // one initial load.
        _opened = true;

        // Opening this window is also the trigger for a fresh walk over whatever landed since the
        // app started or since the last one - StartInBackground is a no op while one is already
        // running, so this never piles up overlapping walks.
        StatsIndexerService.Shared?.StartInBackground();

        var dataStart = Stopwatch.GetTimestamp();
        var hadSnapshot = _viewModel.TryShowSnapshot();
        _openDataMs = Stopwatch.GetElapsedTime(dataStart).TotalMilliseconds;
        _ = hadSnapshot ? RecomputeLoggedAsync(showLoading: false) : RecomputeLoggedAsync();

        var placement = WindowPlacementService.Shared;
        var rememberedPosition = placement?.RememberedStatsWindowPosition;
        var monitors = NativeMonitors.WorkAreas();

        // A remembered position names its monitor by where it lies; otherwise the monitor the window
        // opened on. By handle first there: with mixed scaling the per-monitor areas overlap.
        var area = rememberedPosition is { } position
            ? MainWindow.PickArea(monitors, null, new WindowRect(position.Left + 1, position.Top + 1, 0, 0))
            : MainWindow.PickArea(
                monitors,
                NativeMonitors.DeviceNameOfWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle),
                new WindowRect(Left + Width / 2, Top + Height / 2, 0, 0));

        var size = WindowPlacementService.ResolveStatsWindowSize(placement?.RememberedStatsWindowSize, area, MinWidth, MinHeight);
        if (size.Width != Width || size.Height != Height)
        {
            Width = size.Width;
            Height = size.Height;
            UpdateLayout();
        }

        // Centered on the widget until the window was moved once; always fully on a monitor.
        WindowRect? ownerBounds = Owner is { WindowState: WindowState.Normal } owner && !double.IsNaN(owner.Left)
            ? new WindowRect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight)
            : null;
        var (left, top) = WindowPlacementService.ResolveStatsWindowPosition(rememberedPosition, size, ownerBounds, area);
        Left = left;
        Top = top;
    }

    /// <summary>Keeps the range, grouping and color choice for the next time the window opens.</summary>
    private void RememberSelection()
    {
        if (_settings is null || _settingsStore is null || _reloadingFromSettings)
            return;

        _settings.StatsRange = _viewModel.SelectedRange;
        _settings.StatsGrouping = _viewModel.SelectedGrouping.ToString();
        _settings.StatsColorBy = _viewModel.SelectedColorBy.ToString();
        _settings.StatsProvider = _viewModel.SelectedProvider;
        if (_viewModel.SelectedRange == StatsViewModel.CustomRange)
        {
            (_settings.StatsCustomFrom, _settings.StatsCustomTo) = _viewModel.CustomPeriod;
        }
        _settingsStore.RequestSave(_settings);
    }

    /// <summary>Set by the widget: closing only hides the window, so the next open shows the window
    /// already drawn instead of building and laying out a new one. <see cref="CloseForGood"/> and the
    /// app's own exit still close it.</summary>
    internal bool HideOnClose { get; set; }

    private bool _closingForGood;

    internal void CloseForGood()
    {
        _closingForGood = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !HideOnClose || _closingForGood || Dispatcher.HasShutdownStarted
            || Application.Current?.Dispatcher.HasShutdownStarted == true)
            return;

        e.Cancel = true;
        _dragController.Cancel();
        KeepForNextOpen();
        Hide();
    }

    /// <summary>A hidden window shown again: the same fresh look at the data a first open takes.</summary>
    private void StatsWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || !_opened)
            return;

        StatsIndexerService.Shared?.StartInBackground();
        var dataStart = Stopwatch.GetTimestamp();
        var hadSnapshot = _viewModel.TryShowSnapshot();
        _openDataMs = Stopwatch.GetElapsedTime(dataStart).TotalMilliseconds;
        _ = hadSnapshot ? RecomputeLoggedAsync(showLoading: false) : RecomputeLoggedAsync();
    }

    /// <summary>The size and place for the next open and the data for the next start.</summary>
    private void KeepForNextOpen()
    {
        if (!_opened)
            return;

        if (WindowPlacementService.Shared is { } placement)
        {
            placement.RememberedStatsWindowSize = (Width, Height);
            if (!double.IsNaN(Left) && !double.IsNaN(Top))
                placement.RememberedStatsWindowPosition = (Left, Top);
        }

        // Only a window that was shown has seen the data worth keeping for the next start; a
        // prepared one closing at exit leaves that to the app's own exit. Off the UI thread.
        if (StatsIndexerService.Shared is { } indexer)
            _ = Task.Run(indexer.SavePersistedSnapshot);
    }

    private void StatsWindow_Closed(object? sender, EventArgs e)
    {
        KeepForNextOpen();
        if (StatsIndexerService.Shared is { } indexer)
        {
            indexer.IndexCompleted -= OnIndexCompleted;
            indexer.ProgressChanged -= OnIndexProgressChanged;
        }

        _dragController.Cancel();
        _layoutPresenter.Detach();
        ThemeService.Applied -= OnThemeApplied;
        LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;
    }

    /// <summary>Runs on <see cref="StatsIndexerService"/>'s own background thread - <see
    /// cref="Dispatcher.BeginInvoke(Delegate)"/> is the marshal back to this window's thread, the
    /// only one <see cref="_viewModel"/>'s bound properties are safe to change from. The async form
    /// keeps the window responsive while the records are read. The indexer has just prepared the
    /// data, so it is taken from there; only without it does the window read the store itself, and
    /// then without the loading pill.</summary>
    private void OnIndexCompleted(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            // A window built ahead of time takes the then-current data when it is shown.
            if (!_opened)
                return;
            _viewModel.TryShowSnapshot();
            _ = RecomputeLoggedAsync(showLoading: false);
        });

    /// <summary>Runs on the indexer's thread; the value is read again on this window's thread so a
    /// later change is never overwritten by an earlier event that arrives after it.</summary>
    private void OnIndexProgressChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (StatsIndexerService.Shared is { } indexer)
                _viewModel.IndexProgress = indexer.Progress;
        });

    /// <summary>One brush per stacked segment: two (one per provider) for day/week grouping, one for
    /// model/project grouping. The provider pair always names Claude and Codex through <see
    /// cref="ChartPalette.ForProvider"/> - the same fixed colors that name them everywhere else on
    /// this window - the single, ungrouped series names no provider or model in particular, so it
    /// stays on the plain theme accent - weekday and hour grouping name no single provider
    /// either, so they use it too. Model,
    /// effort and project grouping color each bar on its own instead, through <see cref="BarColorBrushes"/> -
    /// <see cref="Controls.StatsBarChart.BarBrushes"/> then wins over <see
    /// cref="Controls.StatsBarChart.SeriesBrushes"/> for those bars, so the accent assignment above
    /// still applies, just unused while it does.</summary>
    private void RefreshSeriesBrushes()
    {
        var accent = (System.Windows.Media.Brush)FindResource("Accent");

        var mutedBrush = (System.Windows.Media.Brush)FindResource("Text.Muted");
        var series = _viewModel.ChartSeries;
        Chart.SeriesBrushes = series.Count > 0 ? series.Select(item => SeriesBrush(item, mutedBrush)).ToList() : [accent];
        Chart.SeriesLabels = series.Select(item => item.Label).ToList();
        var primary = (System.Windows.Media.Brush)FindResource("Text.Primary");
        Chart.OverlayBrush = primary;
        var legend = series.Select(item => new LegendEntry(SeriesBrush(item, mutedBrush), item.Label, SwatchHeight: 10)).ToList();
        if (_viewModel.ChartOverlay.Count > 0)
            legend.Add(new LegendEntry(primary, LocalizationService.Instance["Stats.Average7"], SwatchHeight: 2));
        ChartLegend.ItemsSource = legend;
        ChartLegend.Visibility = legend.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Chart.BarBrushes = BarColorBrushes(
            _viewModel.SelectedGrouping, _viewModel.Bars, _viewModel.ProjectColors, (System.Windows.Media.Brush)FindResource("Text.Muted"));
        Heatmap.CellColors = HeatmapCellColors(series, _viewModel.HeatmapLeaderKeys, mutedBrush);
    }

    /// <summary>The color of each grid cell: its leading series' color; a cell with no leader gets the
    /// default (transparent) color, which the grid draws in its accent.</summary>
    internal static List<System.Windows.Media.Color> HeatmapCellColors(
        IReadOnlyList<ChartSeriesInfo> series, IReadOnlyList<string> leaderKeys, System.Windows.Media.Brush mutedBrush)
    {
        var colors = new Dictionary<string, System.Windows.Media.Color>();
        foreach (var item in series)
            colors[item.ColorKey] = SeriesBrush(item, mutedBrush) is SolidColorBrush solid ? solid.Color : default;
        return leaderKeys.Select(key => colors.GetValueOrDefault(key)).ToList();
    }

    /// <summary>Resolves every <see cref="StatsViewModel.Bars"/> entry's own <see
    /// cref="Controls.StatsBarChart.Bar.ColorProviderId"/>/<see
    /// cref="Controls.StatsBarChart.Bar.ColorRank"/> into an actual brush - empty for every grouping
    /// that carries no such color of its own (Day/Week keep <see cref="RefreshSeriesBrushes"/>'s own
    /// provider-stacked or accent brushes instead). Model reuses <see cref="ChartPalette.ForModel"/>,
    /// the exact call <see cref="RefreshModelRingBrushes"/> already colors that ring's own slices
    /// through; effort resolves through <see cref="ChartPalette.ForProvider"/> like the effort ring;
    /// project takes <see cref="StatsViewModel.ProjectColors"/>, the color its "top projects" row
    /// shows.</summary>
    internal static List<System.Windows.Media.Brush> BarColorBrushes(
        StatsGrouping grouping, IReadOnlyList<Controls.StatsBarChart.Bar> bars, IReadOnlyDictionary<string, System.Windows.Media.Color> projectColors,
        System.Windows.Media.Brush mutedBrush)
    {
        if (grouping == StatsGrouping.Model)
            return bars.Select(bar => (System.Windows.Media.Brush)new SolidColorBrush(ChartPalette.ForModel(bar.ColorProviderId, bar.ColorRank))).ToList();

        if (grouping == StatsGrouping.Effort)
            return bars.Select(bar => (System.Windows.Media.Brush)new SolidColorBrush(ChartPalette.ForProvider(bar.ColorProviderId))).ToList();

        // A project takes the color its "top projects" row shows; one not resolved yet keeps a
        // fallback hue until the colors arrive and this runs again.
        // The bundled "other projects" bar takes the muted brush.
        if (grouping == StatsGrouping.Project)
            return bars.Select(bar => bar.ColorProviderId == StatsViewModel.OtherProjectsColorKey
                ? mutedBrush
                : (System.Windows.Media.Brush)new SolidColorBrush(
                    projectColors.TryGetValue(bar.ColorProviderId, out var color) && color != default
                        ? color
                        : ChartPalette.ForProvider(bar.ColorProviderId))).ToList();

        return [];
    }

    /// <summary>The brush of one chart series: a model's ring color (<c>model:provider:rank</c>), a
    /// provider's own color, the N-th categorical color or the muted brush of the pooled remainder.</summary>
    internal static System.Windows.Media.Brush SeriesBrush(ChartSeriesInfo series, System.Windows.Media.Brush mutedBrush)
    {
        if (series.ColorKey == "other")
            return mutedBrush;
        if (TryParseModelKey(series.ColorKey, out var modelProvider, out var modelRank))
            return new SolidColorBrush(ChartPalette.ForModel(modelProvider, modelRank));
        if (series.ColorKey.StartsWith("cat:", StringComparison.Ordinal) && int.TryParse(series.ColorKey.AsSpan(4), out var index))
            return new SolidColorBrush(ChartPalette.Categorical(index));
        return new SolidColorBrush(ChartPalette.ForProvider(series.ColorKey));
    }

    /// <summary>Splits a <c>model:provider:rank</c> color key; false for any other key.</summary>
    private static bool TryParseModelKey(string colorKey, out string providerId, out int rank)
    {
        providerId = "";
        rank = 0;
        const string prefix = "model:";
        if (!colorKey.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var split = colorKey.LastIndexOf(':');
        if (split < prefix.Length || !int.TryParse(colorKey.AsSpan(split + 1), NumberStyles.None, CultureInfo.InvariantCulture, out rank))
            return false;

        providerId = colorKey[prefix.Length..split];
        return true;
    }

    /// <summary>Every themed brush on this window that names no provider or model in particular. The
    /// provider ring is refreshed separately, in <see cref="RefreshProviderRingBrushes"/> - like <see
    /// cref="RefreshModelRingBrushes"/>, its own colors now depend on which providers the current
    /// data actually lists and in what order, not just on the active theme.</summary>
    private void RefreshShareBrushes()
    {
        var accent = (System.Windows.Media.Brush)FindResource("Accent");

        // Order of the segments: new input, cache write, cache read, output. Theme tokens (system
        // colors in high contrast) that stay apart on every theme, with cache read (the largest share
        // in practice) in the theme's own accent.
        CacheShareBar.SegmentBrushes =
        [
            (System.Windows.Media.Brush)FindResource("Chart.Cache.New"),
            (System.Windows.Media.Brush)FindResource("Chart.Cache.Write"),
            accent,
            (System.Windows.Media.Brush)FindResource("Chart.Cache.Output"),
        ];

        TopProjectsChart.BarBrush = accent;

        DayHourChart.SeriesBrushes = [accent];
    }

    /// <summary>The day-detail panel's own provider share bar - recomputed on every <see
    /// cref="StatsViewModel.SelectedDayDetail"/> change rather than once, the same reasoning <see
    /// cref="RefreshProviderRingBrushes"/> already documents: which provider shows up, and in what
    /// order, changes with the selected day itself.</summary>
    private void RefreshDayProviderBrushes()
    {
        DayProviderBar.SegmentBrushes = _viewModel.SelectedDayDetail.ProviderIds
            .Select(id => (System.Windows.Media.Brush)new SolidColorBrush(ChartPalette.ForProvider(id)))
            .ToList();
    }

    /// <summary>The provider ring's own brushes - recomputed on every <see
    /// cref="StatsViewModel.ProviderShareSlices"/> change, since which provider sits at which
    /// position changes with the current period. Each slice keeps <see cref="ChartPalette.ForProvider"/>'s
    /// own fixed color for that provider.</summary>
    private void RefreshProviderRingBrushes()
    {
        ProviderRing.SliceBrushes = _viewModel.ProviderShareSlices
            .Select(slice => (System.Windows.Media.Brush)new SolidColorBrush(ChartPalette.ForProvider(slice.ProviderId)))
            .ToList();
    }

    /// <summary>The model ring's own brushes - unlike every other brush list on this window, these
    /// depend on the data itself, not just the active theme: which model sits at which position
    /// changes with the current period and grouping, so this is recomputed on every <see
    /// cref="StatsViewModel.ModelShareSlices"/> change rather than once. Each slice's own provider
    /// gets its rank within that same provider (the largest of its models first) counted off in
    /// display order, the same order <see cref="StatsAggregator.ShareByModel"/> already sorts by
    /// size - so the biggest model of a provider always lands on that provider's pure color. The
    /// pooled "Other"/"Andere" entry carries no provider and keeps the same muted brush every other
    /// pooled entry in this window uses.</summary>
    private void RefreshModelRingBrushes()
    {
        var pooledBrush = (System.Windows.Media.Brush)FindResource("Text.Muted");
        var rankByProvider = new Dictionary<string, int>();
        var brushes = new List<System.Windows.Media.Brush>();
        foreach (var slice in _viewModel.ModelShareSlices)
        {
            if (string.IsNullOrEmpty(slice.ProviderId))
            {
                brushes.Add(pooledBrush);
                continue;
            }

            var rank = rankByProvider.GetValueOrDefault(slice.ProviderId);
            rankByProvider[slice.ProviderId] = rank + 1;
            brushes.Add(new SolidColorBrush(ChartPalette.ForModel(slice.ProviderId, rank)));
        }
        ModelRing.SliceBrushes = brushes;
    }

    /// <summary>The effort ring's own brushes - recomputed on every <see
    /// cref="StatsViewModel.EffortShareSlices"/> change, the same reasoning <see
    /// cref="RefreshProviderRingBrushes"/> already documents. <see
    /// cref="Views.Controls.StatsRingChart.Slice.ProviderId"/> carries the raw, un-localized effort
    /// level here (never a real provider id) - <see cref="ChartPalette.ForProvider"/> resolves any
    /// unrecognized string to the same deterministic swatch it already falls back to for an unknown
    /// provider, which is exactly the "not a provider, still needs a stable color" case this is.</summary>
    private void RefreshEffortRingBrushes()
    {
        EffortRing.SliceBrushes = _viewModel.EffortShareSlices
            .Select(slice => (System.Windows.Media.Brush)new SolidColorBrush(ChartPalette.ForProvider(slice.ProviderId)))
            .ToList();
    }

    private void TitleBarControl_CloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>The session list scrolls inside its own box. At its top or bottom a further wheel turn
    /// goes on to the window's scroller instead of stopping dead on the list.</summary>
    private void SessionTable_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject table || FindScrollViewer(table) is not { } viewer)
            return;

        var atTop = e.Delta > 0 && viewer.VerticalOffset <= 0;
        var atBottom = e.Delta < 0 && viewer.VerticalOffset >= viewer.ScrollableHeight;
        if (!atTop && !atBottom && viewer.ScrollableHeight > 0)
            return;

        e.Handled = true;
        ContentScroller.ScrollToVerticalOffset(ContentScroller.VerticalOffset - e.Delta);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
                return viewer;
            if (FindScrollViewer(child) is { } found)
                return found;
        }

        return null;
    }

    private void ModelRing_SliceActivated(object? sender, int index) => _viewModel.ActivateModelSlice(index);

    private void TopProjectsChart_RowActivated(object? sender, int index) => _viewModel.ActivateTopProject(index);

    private void Chart_BarActivated(object? sender, int index) => _viewModel.ActivateBar(index);

    private void MonthGrid_DaySelected(object? sender, DateOnly day) => _viewModel.ToggleSelectedDay(day);

    /// <summary>The widget's own day-grid tile reaches this on a click - always lands on that exact
    /// day, never <see cref="StatsViewModel.ToggleSelectedDay"/>'s toggle-off, since a click coming
    /// from outside this window can never mean "deselect".</summary>
    public void SelectDay(DateOnly day) => _viewModel.SelectDay(day);

    // AllowsTransparency=True disables Win32's native non-client resize-border loop entirely for a
    // layered window, the same reason MainWindow.xaml.cs replaces it with eight Thumb grips.
    private void GrowRight(double dx) => Width = Math.Max(MinWidth, Width + dx);

    private void GrowLeft(double dx)
    {
        var newWidth = Math.Max(MinWidth, Width - dx);
        Left += Width - newWidth;
        Width = newWidth;
    }

    private void GrowDown(double dy) => Height = Math.Max(MinHeight, Height + dy);

    private void GrowUp(double dy)
    {
        var newHeight = Math.Max(MinHeight, Height - dy);
        Top += Height - newHeight;
        Height = newHeight;
    }

    private void ResizeTop_DragDelta(object sender, DragDeltaEventArgs e) => GrowUp(e.VerticalChange);

    private void ResizeBottom_DragDelta(object sender, DragDeltaEventArgs e) => GrowDown(e.VerticalChange);

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
        if (WindowPlacementService.Shared is { } placement)
            placement.RememberedStatsWindowSize = (Width, Height);
    }
}

/// <summary>One entry of the chart legend: a colored swatch (a thin one for a line) and its name.</summary>
internal sealed record LegendEntry(System.Windows.Media.Brush Brush, string Label, double SwatchHeight);
