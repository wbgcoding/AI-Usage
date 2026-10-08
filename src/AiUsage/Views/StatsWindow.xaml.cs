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
    private const double DefaultWidth = 720;
    private const double DefaultHeight = 520;

    private readonly StatsViewModel _viewModel;
    private readonly AppSettings? _settings;
    private readonly SettingsStore? _settingsStore;
    private double _rememberedScrollOffset;
    private readonly StatsLayoutPresenter _layoutPresenter;
    private readonly StatsSectionDragController _dragController;
    private readonly Dictionary<Controls.CollapsibleSection, ContextMenu> _sectionMenus = [];

    public StatsWindow(StatsViewModel viewModel, AppSettings? settings = null, SettingsStore? settingsStore = null)
    {
        _viewModel = viewModel;
        _settings = settings;
        _settingsStore = settingsStore;
        InitializeComponent();
        WindowChromeNative.Bootstrap(this);
        DataContext = _viewModel;

        _layoutPresenter = new StatsLayoutPresenter(SectionHost, Sections.ToDictionary(section => section.SectionKey));
        _layoutPresenter.Apply(_settings?.StatsSectionLayout ?? StatsLayout.Default());
        _dragController = new StatsSectionDragController(this, ContentScroller, _layoutPresenter, CommitLayout);
        WireSectionChrome();
        UpdateResetButton();

        PreviewKeyDown += Window_PreviewKeyDown;
        Loaded += StatsWindow_Loaded;
        Closed += StatsWindow_Closed;

        // Opening this window is also the trigger for a fresh walk over whatever landed since the
        // app started or since the last one - StartInBackground is a no op while one is already
        // running, so this never piles up overlapping walks. StatsIndexerService.IndexCompleted then
        // fires on its own background thread, so the reload is marshalled back here.
        if (StatsIndexerService.Shared is { } indexer)
        {
            indexer.IndexCompleted += OnIndexCompleted;
            indexer.StartInBackground();
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

        if (_settings is not null && Enum.TryParse<StatsPerDayView>(_settings.StatsPerDayView, out var restoredView))
            _viewModel.SetPerDayViewCommand.Execute(restoredView);

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StatsViewModel.IsStackedByProvider) or nameof(StatsViewModel.Bars)
                or nameof(StatsViewModel.SelectedPerDayView) or nameof(StatsViewModel.ProjectColors))
                RefreshSeriesBrushes();
            if (e.PropertyName == nameof(StatsViewModel.SelectedPerDayView))
                PersistPerDayView();
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
            var menu = new ContextMenu { Items = { up, down, new Separator(), reset } };
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
        var reset = (MenuItem)_sectionMenus[section].Items[3];
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
        FiguresSection, BreakdownSection, PerDaySection, ProviderSection, ModelSection, EffortSection, CacheSection,
        ProjectsSection, MonthGridSection, TableSection,
    ];

    /// <summary>Sets every section to its remembered state (missing from the dictionary counts as
    /// expanded) and starts persisting the next change onward. A no-op with no <see
    /// cref="_settings"/>/<see cref="_settingsStore"/> - every caller but <see cref="MainWindow"/>'s
    /// own real construction leaves them null, and the sections then simply stay at their default,
    /// expanded state for that window's own lifetime.</summary>
    private void ApplySectionStates()
    {
        foreach (var section in Sections)
        {
            section.IsExpanded = !(_settings?.StatsSectionsCollapsed.GetValueOrDefault(section.SectionKey) ?? false);
            section.IsExpandedChanged += (_, _) => PersistSectionState(section);
        }
    }

    private void PersistSectionState(Controls.CollapsibleSection section)
    {
        if (_settings is null || _settingsStore is null)
            return;

        _settings.StatsSectionsCollapsed[section.SectionKey] = !section.IsExpanded;
        _settingsStore.RequestSave(_settings);
    }

    /// <summary>Mirrors <see cref="PersistSectionState"/> for the "Per day" panel's own view switch -
    /// same no-op-without-settings shape, restored the same way on the next construction (see the
    /// constructor's own <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/> call).</summary>
    private void PersistPerDayView()
    {
        if (_settings is null || _settingsStore is null)
            return;

        _settings.StatsPerDayView = _viewModel.SelectedPerDayView.ToString();
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
    private async Task RecomputeLoggedAsync()
    {
        try
        {
            await _viewModel.RecomputeAsync();
        }
        catch (Exception ex)
        {
            LogService.Shared.LogError($"Stats load failed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>Applies the remembered size, if any, once the window has a real position to test a
    /// monitor's work area against - the constructor still only knows the XAML default.</summary>
    private void StatsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // The view model reads nothing on construction; this is its one initial load.
        _ = RecomputeLoggedAsync();

        // By handle first: with mixed scaling the per-monitor areas overlap.
        var area = MainWindow.PickArea(
            NativeMonitors.WorkAreas(),
            NativeMonitors.DeviceNameOfWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle),
            new WindowRect(Left + Width / 2, Top + Height / 2, 0, 0));

        var (width, height) = WindowPlacementService.ResolveStatsWindowSize(
            WindowPlacementService.Shared?.RememberedStatsWindowSize, area, DefaultWidth, DefaultHeight);
        if (width != Width || height != Height)
        {
            Width = width;
            Height = height;
            UpdateLayout();
        }

        // CenterOwner next to an edge-docked widget can leave the window partly off-screen.
        WindowPlacementService.ClampIntoArea(this, area);
    }

    private void StatsWindow_Closed(object? sender, EventArgs e)
    {
        if (WindowPlacementService.Shared is { } placement)
            placement.RememberedStatsWindowSize = (Width, Height);

        if (StatsIndexerService.Shared is { } indexer)
            indexer.IndexCompleted -= OnIndexCompleted;

        _dragController.Cancel();
        _layoutPresenter.Detach();
        ThemeService.Applied -= OnThemeApplied;
        LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;
    }

    /// <summary>Runs on <see cref="StatsIndexerService"/>'s own background thread - <see
    /// cref="Dispatcher.BeginInvoke(Delegate)"/> is the marshal back to this window's thread, the
    /// only one <see cref="_viewModel"/>'s bound properties are safe to change from. The async form
    /// keeps the window responsive while the records are read.</summary>
    private void OnIndexCompleted(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => _ = RecomputeLoggedAsync());

    /// <summary>One brush per stacked segment: two (one per provider) for day/week grouping, one for
    /// model/project grouping. The provider pair always names Claude and Codex through <see
    /// cref="ChartPalette.ForProvider"/> - the same fixed colors that name them everywhere else on
    /// this window - the single, ungrouped series names no provider or model in particular, so it
    /// stays on the plain theme accent. The "Per day" panel is provider-stacked only in its own
    /// by-day view (<see cref="StatsViewModel.IsPerDayViewStackedByProvider"/>) - by-weekday and
    /// by-hour name no single provider either, so they fall back to the same plain accent. Model,
    /// effort and project grouping color each bar on its own instead, through <see cref="BarColorBrushes"/> -
    /// <see cref="Controls.StatsBarChart.BarBrushes"/> then wins over <see
    /// cref="Controls.StatsBarChart.SeriesBrushes"/> for those bars, so the accent assignment above
    /// still applies, just unused while it does.</summary>
    private void RefreshSeriesBrushes()
    {
        var accent = (System.Windows.Media.Brush)FindResource("Accent");
        var providerBrushes = ProviderBrushes();

        Chart.SeriesBrushes = _viewModel.IsStackedByProvider ? providerBrushes : [accent];
        Chart.SeriesLabels = _viewModel.IsStackedByProvider ? Stats.StatsViewModel.StackedProviderDisplayNames : [];
        Chart.BarBrushes = BarColorBrushes(
            _viewModel.SelectedGrouping, _viewModel.Bars, _viewModel.ProjectColors, (System.Windows.Media.Brush)FindResource("Text.Muted"));

        PerDayChart.SeriesBrushes = _viewModel.IsPerDayViewStackedByProvider ? providerBrushes : [accent];
        PerDayChart.SeriesLabels = _viewModel.IsPerDayViewStackedByProvider ? Stats.StatsViewModel.StackedProviderDisplayNames : [];
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

    private static List<System.Windows.Media.Brush> ProviderBrushes() =>
        StatsAggregator.StackedProviderOrder.Select(id => (System.Windows.Media.Brush)new SolidColorBrush(ChartPalette.ForProvider(id))).ToList();

    /// <summary>Every themed brush on this window that names no provider or model in particular. The
    /// provider ring is refreshed separately, in <see cref="RefreshProviderRingBrushes"/> - like <see
    /// cref="RefreshModelRingBrushes"/>, its own colors now depend on which providers the current
    /// data actually lists and in what order, not just on the active theme.</summary>
    private void RefreshShareBrushes()
    {
        var accent = (System.Windows.Media.Brush)FindResource("Accent");
        var secondary = (System.Windows.Media.Brush)FindResource("Text.Muted");
        var accentHover = (System.Windows.Media.Brush)FindResource("Accent.Hover");
        var borderControl = (System.Windows.Media.Brush)FindResource("Border.Control");

        // No "danger"/"warning" connotation fits four token kinds that are all perfectly normal, and
        // none of the four is a provider or a model either - this stays on the window's own theme
        // brushes rather than borrowing a palette built to tell providers and models apart.
        CacheShareBar.SegmentBrushes = [accent, accentHover, borderControl, secondary];

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
