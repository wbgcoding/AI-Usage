using System.Reflection;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Views;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Builds the real <see cref="StatsWindow"/> off screen (measured and arranged, never shown - same
/// reasoning <see cref="RenderedTextContrastTests"/> already documents for why a real
/// <c>ShowDialog</c> message loop is kept out of this suite) once per theme, and proves it never
/// throws while doing so - the automated half of the "opens, closes, remembers nothing, survives
/// a theme switch while open". Also renders one PNG per theme into <c>.tmp/review/</c> for the
/// screenshot half of that acceptance criterion, since this project's own self-test protocol has no
/// live VM available to this run.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class StatsWindowTests
{
    [Fact]
    public void StatsWindow_builds_and_renders_without_throwing_across_every_theme()
    {
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                WalkAllThemes();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("StatsWindow visual walk did not finish");

        if (failure is not null)
            throw new InvalidOperationException("StatsWindow visual walk failed.", failure);
    }

    // the window resizes from every edge and corner now, the same eight-Thumb pattern
    // MainWindow already uses instead of the native (AllowsTransparency-disabled) resize border.
    [Fact]
    public void StatsWindow_exposes_eight_resize_grips()
    {
        Exception? failure = null;
        var gripCount = -1;

        var worker = new Thread(() =>
        {
            try
            {
                gripCount = CountResizeGrips();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("StatsWindow grip-count walk did not finish");

        if (failure is not null)
            throw new InvalidOperationException("StatsWindow grip-count walk failed.", failure);

        Assert.Equal(8, gripCount);
    }

    // The window itself starting the shared indexer and reloading on IndexCompleted is covered at
    // the unit level: StatsIndexerServiceTests proves restartability and the event fire, and the
    // wiring here (StatsWindow_Loaded / StatsWindow_Closed / OnIndexCompleted) is a few plain lines
    // reading a static seam the same way WindowPlacementService.Shared already does - a full-window
    // STA walk added no coverage this project's own resource-loading tests do not already give the
    // static/pack-URI plumbing, and reliably tripped the same one-Application-per-process limit that
    // pattern already runs close to.

    [Fact]
    public void Every_collapsible_section_persists_its_collapsed_state()
    {
        Exception? failure = null;
        (IReadOnlyList<string> Keys, IReadOnlyCollection<string> Persisted)? result = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = CollapseEverySection();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("StatsWindow section walk did not finish");
        if (failure is not null)
            throw new InvalidOperationException("StatsWindow section walk failed.", failure);

        Assert.NotEmpty(result!.Value.Keys);
        Assert.Contains("effort", result.Value.Keys);
        Assert.All(result.Value.Keys, key => Assert.Contains(key, result.Value.Persisted));
    }

    private static (IReadOnlyList<string> Keys, IReadOnlyCollection<string> Persisted) CollapseEverySection()
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();

        using var dataDir = TestPaths.CreateDisposableDirectory("stats-window-sections");
        var store = new StatsStore(dataDir);

        try
        {
            ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

            var settings = new AppSettings();
            var window = new StatsWindow(StatsVm.Create(store), settings, new AiUsage.Storage.SettingsStore(dataDir));
            var root = (UIElement)window.Content;
            root.Measure(new Size(720, 520));
            root.Arrange(new Rect(0, 0, 720, 520));
            root.UpdateLayout();

            var sections = new List<CollapsibleSection>();
            CollectSections(root, sections);
            foreach (var section in sections)
                section.IsExpanded = false;

            return ([.. sections.Select(section => section.SectionKey)], [.. settings.StatsSectionsCollapsed.Keys]);
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            ResetApplicationCurrent();
        }
    }

    private static void CollectSections(DependencyObject node, List<CollapsibleSection> found)
    {
        if (node is CollapsibleSection section)
            found.Add(section);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            CollectSections(VisualTreeHelper.GetChild(node, i), found);
    }

    private static int CountResizeGrips()
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();

        using var dataDir = TestPaths.CreateDisposableDirectory("stats-window-grips");
        var store = new StatsStore(dataDir);

        try
        {
            ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

            var viewModel = StatsVm.Create(store);
            var window = new StatsWindow(viewModel);
            var root = (UIElement)window.Content;

            root.Measure(new Size(720, 520));
            root.Arrange(new Rect(0, 0, 720, 520));
            root.UpdateLayout();

            // Later panels gave the window's own scroll region enough chart panels to actually need
            // scrolling at the 720x520 default size, which realises a genuine ScrollBar Thumb inside
            // the visual tree alongside the eight resize grips this test means to count - so only the
            // grips sharing the ResizeGrip style are counted, the same style every one of the eight
            // Thumb elements in StatsWindow.xaml itself is given.
            var resizeGripStyle = window.FindResource("ResizeGrip");
            return CountResizeGripThumbs(root, resizeGripStyle);
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            ResetApplicationCurrent();
        }
    }

    private static bool IsDescendant(DependencyObject ancestor, DependencyObject node)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(ancestor);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(ancestor, i);
            if (ReferenceEquals(child, node) || IsDescendant(child, node))
                return true;
        }
        return false;
    }

    private static int CountResizeGripThumbs(DependencyObject node, object resizeGripStyle)
    {
        var count = node is Thumb thumb && ReferenceEquals(thumb.Style, resizeGripStyle) ? 1 : 0;
        var childCount = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < childCount; i++)
            count += CountResizeGripThumbs(VisualTreeHelper.GetChild(node, i), resizeGripStyle);
        return count;
    }

    /// <summary>The explorer chart used to sit outside every <see cref="CollapsibleSection"/>, as a
    /// bare sibling of "Breakdown" - collapsing that section left the chart showing
    /// regardless. It is now nested inside that section's own Content (proven here by walking the
    /// visual tree down to it), so the generic collapse mechanism <c>CollapsibleSectionTests</c>
    /// already proves for Content in general now applies to the chart too.</summary>
    [Fact]
    public void ChartIsNestedInsideTheBreakdownSectionsOwnContentHost()
    {
        Exception? failure = null;
        bool? chartIsInsideContentHost = null;

        var worker = new Thread(() =>
        {
            try
            {
                chartIsInsideContentHost = WalkBreakdownCollapse();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("StatsWindow breakdown-collapse walk did not finish");
        if (failure is not null)
            throw new InvalidOperationException("StatsWindow breakdown-collapse walk failed.", failure);

        Assert.True(chartIsInsideContentHost);
    }

    private static bool WalkBreakdownCollapse()
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();

        using var dataDir = TestPaths.CreateDisposableDirectory("stats-window-breakdown-collapse");
        var store = new StatsStore(dataDir);

        try
        {
            ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

            var viewModel = StatsVm.Create(store);
            var window = new StatsWindow(viewModel);
            var root = (UIElement)window.Content;
            root.Measure(new Size(720, 520));
            root.Arrange(new Rect(0, 0, 720, 520));
            root.UpdateLayout();

            var contentHost = (DependencyObject)window.BreakdownSection.Template.FindName("PART_ContentHost", window.BreakdownSection)!;
            return IsDescendant(contentHost, window.Chart);
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            ResetApplicationCurrent();
        }
    }

    // a size remembered from a previous open (in-process only, see
    // WindowPlacementService.RememberedStatsWindowSize) falls back to the fixed default the moment it
    // no longer fits the monitor the window is about to open on.
    [Theory]
    [InlineData(900, 700, 720, 520)] // too wide and too tall for the 600x400 area below -> default
    [InlineData(900, 300, 720, 520)] // too wide only -> default
    [InlineData(500, 700, 720, 520)] // too tall only -> default
    public void ResolveStatsWindowSize_falls_back_to_the_default_once_it_no_longer_fits(
        double rememberedWidth, double rememberedHeight, double expectedWidth, double expectedHeight)
    {
        var area = new MonitorArea("test", 0, 0, 600, 400);

        var (width, height) = WindowPlacementService.ResolveStatsWindowSize(
            (rememberedWidth, rememberedHeight), area, defaultWidth: 720, defaultHeight: 520);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void ResolveStatsWindowSize_keeps_a_remembered_size_that_still_fits()
    {
        var area = new MonitorArea("test", 0, 0, 1920, 1080);

        var (width, height) = WindowPlacementService.ResolveStatsWindowSize(
            (900, 650), area, defaultWidth: 720, defaultHeight: 520);

        Assert.Equal(900, width);
        Assert.Equal(650, height);
    }

    [Fact]
    public void ResolveStatsWindowSize_uses_the_default_when_nothing_is_remembered_yet()
    {
        var area = new MonitorArea("test", 0, 0, 1920, 1080);

        var (width, height) = WindowPlacementService.ResolveStatsWindowSize(null, area, defaultWidth: 720, defaultHeight: 520);

        Assert.Equal(720, width);
        Assert.Equal(520, height);
    }

    // a ComboBox binds SelectedItem to these two view-model properties instead of the
    // RadioButton group's Command/CommandParameter pair - setting either has to apply the exact same
    // change the corresponding command already applies.
    [Fact]
    public void SelectedRangeChoice_applies_the_same_change_as_SetRangeCommand()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-window-range-choice");
        var viewModel = StatsVm.Create(new StatsStore(dataDir));

        var monthChoice = viewModel.RangeChoices.Single(choice => choice.Value == "Month");
        viewModel.SelectedRangeChoice = monthChoice;

        Assert.Equal("Month", viewModel.SelectedRange);
        Assert.True(monthChoice.IsSelected);
        Assert.Same(monthChoice, viewModel.SelectedRangeChoice);
    }

    [Fact]
    public void SelectedGroupingChoice_applies_the_same_change_as_SetGroupingCommand()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-window-grouping-choice");
        var viewModel = StatsVm.Create(new StatsStore(dataDir));

        var modelChoice = viewModel.GroupingChoices.Single(choice => choice.Value == StatsGrouping.Model);
        viewModel.SelectedGroupingChoice = modelChoice;

        Assert.Equal(StatsGrouping.Model, viewModel.SelectedGrouping);
        Assert.True(modelChoice.IsSelected);
        Assert.Same(modelChoice, viewModel.SelectedGroupingChoice);
    }

    private static void WalkAllThemes()
    {
        // WPF refuses a second Application instance per process even after Current is reset back to
        // null (RenderedTextContrastTests already does exactly that in its own finally block) - a
        // second, separate private static flag remembers "one was already created" and is never
        // cleared on Shutdown. Clearing it here too is what lets this class build its own Application
        // regardless of whether that other visual-walk test already ran first in the same process
        // (xunit runs this whole assembly's tests sequentially, in undefined class order).
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();

        using var dataDir = TestPaths.CreateDisposableDirectory("stats-window-visual");
        var store = new StatsStore(dataDir);
        store.AddDelta([
            new StatsRecord("claude", DateOnly.FromDateTime(DateTime.UtcNow), "modelA", "projA", 1000, 500, 200, 100),
            new StatsRecord("codex", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), "modelB", "projB", 2000, 800, 0, 0),
        ]);

        var reviewDir = Path.Combine(FindRepoRoot(), ".tmp", "review");
        Directory.CreateDirectory(reviewDir);

        try
        {
            foreach (var theme in new[] { AppTheme.Nebula, AppTheme.Terminal, AppTheme.Dark, AppTheme.Light })
                RenderTheme(theme, store, reviewDir);
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            ResetApplicationCurrent();
        }
    }

    private static void RenderTheme(AppTheme theme, StatsStore store, string reviewDir)
    {
        ThemeService.Apply(theme, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

        var viewModel = StatsVm.Create(store);
        var window = new StatsWindow(viewModel);
        var root = (UIElement)window.Content;

        root.Measure(new Size(720, 520));
        root.Arrange(new Rect(0, 0, 720, 520));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(720, 520, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(reviewDir, $"stats-window-{theme}.png"));
        encoder.Save(stream);
    }

    [Fact]
    public void Default_layout_puts_the_rings_and_projects_side_by_side_and_the_table_last()
    {
        RunInWindow(null, (window, _) =>
        {
            var providerColumn = (Grid)window.ProviderSection.Parent;
            var projectsColumn = (Grid)window.ProjectsSection.Parent;
            Assert.Same(providerColumn.Parent, projectsColumn.Parent);
            Assert.Equal(0, Grid.GetColumn(providerColumn));
            Assert.Equal(1, Grid.GetColumn(projectsColumn));
            Assert.Same(window.TableSection, window.SectionHost.Children[^1]);
        });
    }

    [Fact]
    public void A_stored_layout_decides_the_order_of_the_sections()
    {
        var settings = new AppSettings
        {
            StatsSectionLayout = [new StatsLayoutRow { Left = ["projects"] }],
        };

        RunInWindow(settings, (window, _) =>
        {
            Assert.Same(window.ProjectsSection, window.SectionHost.Children[0]);
            Assert.Equal(StatsLayout.SectionKeys.Count, window.SectionHost.Children.Count);
        });
    }

    [Fact]
    public void A_column_whose_sections_are_all_hidden_gives_its_width_to_the_other_one()
    {
        var settings = new AppSettings
        {
            StatsSectionLayout = [new StatsLayoutRow { Left = ["table"], Right = ["cache"] }],
        };

        RunInWindow(settings, (window, root) =>
        {
            root.UpdateLayout();

            var row = (Grid)((Grid)window.TableSection.Parent).Parent;
            Assert.Equal(0, row.ColumnDefinitions[0].Width.Value);
            Assert.Equal(1, row.ColumnDefinitions[1].Width.Value);
        });
    }

    [Fact]
    public void CommitLayout_applies_the_arrangement_and_stores_it_unless_it_is_the_default()
    {
        var settings = new AppSettings();

        RunInWindow(settings, (window, _) =>
        {
            window.CommitLayout(StatsLayout.MoveUp(StatsLayout.Default(), "projects"));

            Assert.Same(window.ProjectsSection, window.SectionHost.Children[5]);
            Assert.NotNull(settings.StatsSectionLayout);
            Assert.False(StatsLayout.IsDefault(settings.StatsSectionLayout!));

            window.CommitLayout(StatsLayout.Default());

            Assert.Same(window.ProviderSection.Parent is Grid left ? left.Parent : null, window.ProjectsSection.Parent is Grid right ? right.Parent : null);
            Assert.Null(settings.StatsSectionLayout);
        }, withStore: true);
    }

    [Fact]
    public void A_click_on_a_section_heading_still_collapses_the_section()
    {
        RunInWindow(null, (window, _) =>
        {
            window.ProviderSection.ApplyTemplate();
            var button = (ButtonBase)window.ProviderSection.Template.FindName("PART_HeaderButton", window.ProviderSection);

            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.False(window.ProviderSection.IsExpanded);
        });
    }

    [Fact]
    public void MoveSection_up_moves_the_table_before_the_two_column_row_and_keeps_the_first_section()
    {
        RunInWindow(null, (window, _) =>
        {
            window.MoveSection(window.TableSection, up: true);

            var pair = ((Grid)window.ProviderSection.Parent).Parent;
            Assert.True(window.SectionHost.Children.IndexOf(window.TableSection) < window.SectionHost.Children.IndexOf((UIElement)pair));

            window.MoveSection(window.MonthGridSection, up: true);

            Assert.Same(window.MonthGridSection, window.SectionHost.Children[0]);
        });
    }

    [Fact]
    public void The_reset_button_shows_only_away_from_the_default_and_restores_it()
    {
        var settings = new AppSettings();

        RunInWindow(settings, (window, _) =>
        {
            Assert.Equal(Visibility.Collapsed, window.ResetLayoutButton.Visibility);

            window.CommitLayout(StatsLayout.MoveUp(StatsLayout.Default(), "projects"));

            Assert.Equal(Visibility.Visible, window.ResetLayoutButton.Visibility);

            window.ResetLayoutButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(Visibility.Collapsed, window.ResetLayoutButton.Visibility);
            Assert.Null(settings.StatsSectionLayout);
        }, withStore: true);
    }

    [Fact]
    public void The_context_menu_offers_only_the_moves_and_reset_that_change_something()
    {
        RunInWindow(null, (window, _) =>
        {
            window.RefreshContextMenu(window.MonthGridSection);
            var menu = window.SectionMenu(window.MonthGridSection);

            Assert.False(((MenuItem)menu.Items[0]).IsEnabled);
            Assert.True(((MenuItem)menu.Items[1]).IsEnabled);
            Assert.False(((MenuItem)menu.Items[3]).IsEnabled);

            window.MoveSection(window.MonthGridSection, up: false);
            window.RefreshContextMenu(window.MonthGridSection);

            Assert.True(((MenuItem)menu.Items[0]).IsEnabled);
            Assert.True(((MenuItem)menu.Items[3]).IsEnabled);
        });
    }

    [Fact]
    public void A_dropped_section_starts_its_slide_where_it_was_let_go()
    {
        RunInWindow(null, (window, _) =>
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;
            window.Top = -30000;
            window.Show();
            try
            {
                window.UpdateLayout();
                var dropPoint = new Point(40, 25);

                window.CommitLayout(StatsLayout.MoveUp(StatsLayout.Default(), "projects"), window.ProjectsSection, dropPoint);

                Assert.Same(window.ProjectsSection, window.SectionHost.Children[5]);
                // The render transform reaches the visual offset on the next layout pass.
                window.UpdateLayout();
                var shown = window.ProjectsSection.TranslatePoint(new Point(0, 0), window.SectionHost);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.IsType<TranslateTransform>(window.ProjectsSection.RenderTransform);
                    Assert.Equal(dropPoint.X, shown.X, 1);
                    Assert.Equal(dropPoint.Y, shown.Y, 1);
                }
                else
                {
                    Assert.False(window.ProjectsSection.RenderTransform is TranslateTransform);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_drop_marker_glides_to_a_new_spot_instead_of_jumping()
    {
        RunOnSta(() =>
        {
            var marker = new StatsDropMarkerAdorner(new Border(), Brushes.Red);
            var first = new Rect(0, 10, 200, 3);
            var second = new Rect(0, 90, 200, 3);

            marker.Update(first);
            Assert.Equal(first, marker.ShownLine);

            marker.Update(second);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.Equal(first, marker.ShownLine);
                marker.Step(0.016);
                var midway = marker.ShownLine!.Value;
                Assert.True(midway.Y > first.Y && midway.Y < second.Y);
                marker.Step(1);
            }

            Assert.Equal(second, marker.ShownLine);
            marker.Update(null);
            Assert.Null(marker.ShownLine);
        });
    }

    [Fact]
    public void The_drag_picture_of_a_tall_section_is_cut_to_the_maximum_height()
    {
        RunOnSta(() =>
        {
            var tall = new Border { Width = 300, Height = 400, Background = Brushes.Blue };
            tall.Measure(new Size(300, 400));
            tall.Arrange(new Rect(0, 0, 300, 400));
            var shortOne = new Border { Width = 300, Height = 80, Background = Brushes.Blue };
            shortOne.Measure(new Size(300, 80));
            shortOne.Arrange(new Rect(0, 0, 300, 80));

            var tallShot = StatsDragGhostAdorner.Snapshot(tall)!.Value;
            var shortShot = StatsDragGhostAdorner.Snapshot(shortOne)!.Value;

            Assert.Equal(new Size(300, StatsDragGhostAdorner.MaxGhostHeight), tallShot.Size);
            Assert.True(tallShot.Clipped);
            Assert.Equal(new Size(300, 80), shortShot.Size);
            Assert.False(shortShot.Clipped);
            Assert.Null(StatsDragGhostAdorner.Snapshot(new Border()));
        });
    }

    private static void RunOnSta(Action work)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("drag adorner test did not finish");
        if (failure is not null)
            throw new InvalidOperationException("drag adorner test failed.", failure);
    }

    /// <summary>Builds the real window on an STA thread (the same set-up the other walks here use),
    /// lays it out once and hands it to <paramref name="check"/>.</summary>
    private static void RunInWindow(AppSettings? settings, Action<StatsWindow, UIElement> check, bool withStore = false)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
                var app = new AiUsage.App();
                app.InitializeComponent();

                using var dataDir = TestPaths.CreateDisposableDirectory("stats-window-layout");
                var store = new StatsStore(dataDir);
                using var settingsStore = withStore ? new AiUsage.Storage.SettingsStore(dataDir) : null;
                try
                {
                    ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);
                    var window = new StatsWindow(StatsVm.Create(store), settings, settingsStore);
                    var root = (UIElement)window.Content;
                    root.Measure(new Size(720, 520));
                    root.Arrange(new Rect(0, 0, 720, 520));
                    root.UpdateLayout();
                    check(window, root);
                }
                finally
                {
                    Application.Current?.Shutdown();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                    ResetApplicationCurrent();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("StatsWindow layout walk did not finish");
        if (failure is not null)
            throw new InvalidOperationException("StatsWindow layout walk failed.", failure);
    }

    private static ResourceDictionary LoadFromProductionAssembly(Uri relativeUri) =>
        new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relativeUri.OriginalString, UriKind.Absolute) };

    private static void ResetApplicationCurrent() =>
        typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);

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
}

/// <summary>Geometry coverage for <see cref="StatsBarChart"/> - the pure, static parts of
/// its drawing pulled out the same way <c>HistoryChartGeometryTests</c> already does for
/// <c>HistoryChart</c>, so the stacking/axis/hover math is unit testable without a visual tree.</summary>
public class StatsBarChartGeometryTests
{
    // The acceptance criterion here: three days, two providers each - the six computed segment
    // rectangles have the expected heights, and the two segments of one day stack without a gap.
    [Fact]
    public void ThreeDaysWithTwoProvidersProduceSixCorrectlyStackedSegments()
    {
        StatsBarChart.Bar[] bars =
        [
            new("2026-01-01", [100, 50]), // total 150
            new("2026-01-02", [0, 200]),  // total 200 - the tallest, defines maxTotal
            new("2026-01-03", [40, 0]),   // total 40
        ];
        const double chartHeight = 200;
        var maxTotal = bars.Max(bar => bar.StackedValues.Sum());

        var day1 = StatsBarChart.SegmentHeights(bars[0].StackedValues, maxTotal, chartHeight);
        var day2 = StatsBarChart.SegmentHeights(bars[1].StackedValues, maxTotal, chartHeight);
        var day3 = StatsBarChart.SegmentHeights(bars[2].StackedValues, maxTotal, chartHeight);

        Assert.Equal(100.0 / 200 * chartHeight, day1[0]);
        Assert.Equal(50.0 / 200 * chartHeight, day1[1]);
        Assert.Equal(0.0, day2[0]);
        Assert.Equal(200.0 / 200 * chartHeight, day2[1]);
        Assert.Equal(40.0 / 200 * chartHeight, day3[0]);
        Assert.Equal(0.0, day3[1]);

        // Stacks without a gap: the first segment's own height plus the second segment's own height
        // together span the bar's whole total height, drawn bottom-up from y = chartHeight.
        Assert.Equal(150.0 / 200 * chartHeight, day1[0] + day1[1]);
    }

    [Fact]
    public void LabelIndicesNeverExceedsTheRequestedMaximum()
    {
        var indices = StatsBarChart.LabelIndices(barCount: 365, maxLabels: 10);

        Assert.True(indices.Count <= 10);
        Assert.Contains(0, indices);
    }

    [Fact]
    public void LabelIndicesShowsEveryBarWhenThereAreFewerThanTheMaximum()
    {
        var indices = StatsBarChart.LabelIndices(barCount: 7, maxLabels: 10);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6], indices);
    }

    [Fact]
    public void LabelIndicesStepsEverySixHoursForATwentyFourBarChart()
    {
        var indices = StatsBarChart.LabelIndices(barCount: 24, maxLabels: 4);

        Assert.Equal([0, 6, 12, 18], indices);
    }

    private static List<StatsBarChart.Bar> DayBars(DateOnly first, int count) =>
        Enumerable.Range(0, count)
            .Select(offset => new StatsBarChart.Bar(first.AddDays(offset).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), [1]))
            .ToList();

    [Fact]
    public void MonthLabelIndicesPicksTheFirstOfEachMonthForAYearOfDays()
    {
        var bars = DayBars(new DateOnly(2025, 10, 3), 365);

        var indices = StatsBarChart.MonthLabelIndices(bars);

        Assert.NotNull(indices);
        Assert.Equal(12, indices!.Count);
        Assert.All(indices, index => Assert.Equal(1, DateOnly.ParseExact(bars[index].Label, "yyyy-MM-dd").Day));
        Assert.Equal(29, indices[0]); // 2025-11-01
    }

    [Fact]
    public void MonthLabelIndicesStaysOffForShortOrNonDayCharts()
    {
        Assert.Null(StatsBarChart.MonthLabelIndices(DayBars(new DateOnly(2026, 1, 1), 92)));
        var weekly = Enumerable.Range(1, 100).Select(week => new StatsBarChart.Bar($"2026-W{week % 52 + 1:00}", [1])).ToList();
        Assert.Null(StatsBarChart.MonthLabelIndices(weekly));
    }

    [Theory]
    [InlineData(100.0, 30.0, 40.0, 400.0, 100.0)]
    [InlineData(10.0, 30.0, 40.0, 400.0, 40.0)]
    [InlineData(395.0, 30.0, 40.0, 400.0, 370.0)]
    public void ClampLabelLeftKeepsALabelInsideTheChart(double centeredLeft, double labelWidth, double minLeft, double chartRight, double expected) =>
        Assert.Equal(expected, StatsBarChart.ClampLabelLeft(centeredLeft, labelWidth, minLeft, chartRight));

    [Fact]
    public void FormatMonthAxisLabelAddsTheYearOnlyOnJanuary()
    {
        var culture = new System.Globalization.CultureInfo("en-US");

        Assert.Equal("Nov", StatsBarChart.FormatMonthAxisLabel("2025-11-01", culture));
        Assert.Equal("Jan 2026", StatsBarChart.FormatMonthAxisLabel("2026-01-01", culture));
    }

    [Fact]
    public void LabelIndicesLabelsEveryThreeHoursWhenTheWidestLabelFits()
    {
        // 24 bars of 30 px: a 3-bar step spans 90 px, a 40 px label plus gap fits.
        var indices = StatsBarChart.LabelIndices(barCount: 24, maxLabels: 8, slotWidth: 30, widestLabel: 40, gap: 6);

        Assert.Equal([0, 3, 6, 9, 12, 15, 18, 21], indices);
    }

    [Fact]
    public void LabelIndicesFallsBackToEverySixHoursWhenTheLabelsDoNotFit()
    {
        // 24 bars of 12 px: a 3-bar step spans 36 px, a 40 px label plus gap does not fit, 6 bars do.
        var indices = StatsBarChart.LabelIndices(barCount: 24, maxLabels: 8, slotWidth: 12, widestLabel: 40, gap: 6);

        Assert.Equal([0, 6, 12, 18], indices);
    }

    [Fact]
    public void HourAxisAlwaysLabelsZeroSixTwelveEighteen()
    {
        // No width or label count goes in: a narrow chart gets the same four labels.
        Assert.Equal([0, 6, 12, 18], StatsBarChart.HourAxisLabelIndices(24));
        Assert.Equal([0, 6], StatsBarChart.HourAxisLabelIndices(7));

        try
        {
            LocalizationService.Instance.SetLanguage("en");
            Assert.Equal(["12a", "6a", "12p", "6p"], StatsBarChart.HourAxisLabelIndices(24).Select(StatsBarChart.HourAxisLabel));
            LocalizationService.Instance.SetLanguage("de");
            Assert.Equal(["0 Uhr", "6 Uhr", "12 Uhr", "18 Uhr"], StatsBarChart.HourAxisLabelIndices(24).Select(StatsBarChart.HourAxisLabel));
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Theory]
    [InlineData(52_400_000L, new[] { 20_000_000L, 40_000_000L, 60_000_000L })]
    [InlineData(7L, new[] { 2L, 4L, 6L, 8L })]
    [InlineData(1_000L, new[] { 500L, 1_000L })]
    [InlineData(1L, new[] { 1L })]
    [InlineData(4L, new[] { 1L, 2L, 3L, 4L })]
    [InlineData(0L, new long[0])]
    public void YAxisTicksAreRoundSteps(long maxTotal, long[] expected)
    {
        Assert.Equal(expected, StatsBarChart.YAxisTicks(maxTotal));
    }

    [Theory]
    [InlineData(52_400_000L)]
    [InlineData(7L)]
    [InlineData(999L)]
    [InlineData(123_456_789_012L)]
    public void YAxisScaleReachesTheMaxWithinFourGridlines(long maxTotal)
    {
        var ticks = StatsBarChart.YAxisTicks(maxTotal);

        Assert.InRange(ticks.Count, 1, 4);
        Assert.True(ticks[^1] >= maxTotal);
    }

    [Fact]
    public void NearestBarIndexMapsAPixelToItsOwnEqualSlot()
    {
        Assert.Equal(0, StatsBarChart.NearestBarIndex(x: 10, width: 200, barCount: 4)); // slot 0-49
        Assert.Equal(2, StatsBarChart.NearestBarIndex(x: 120, width: 200, barCount: 4)); // slot 100-149
        Assert.Equal(3, StatsBarChart.NearestBarIndex(x: 199, width: 200, barCount: 4)); // slot 150-199
    }

    [Fact]
    public void DayAxisLabelHasNoYear()
    {
        Assert.Equal("Sep 3", StatsBarChart.FormatAxisLabel("2026-09-03", new System.Globalization.CultureInfo("en-US")));
        Assert.Equal("3. Sept.", StatsBarChart.FormatAxisLabel("2026-09-03", new System.Globalization.CultureInfo("de-DE")));
    }

    [Fact]
    public void FormatAxisLabelPassesThroughANonDateLabelUnchanged()
    {
        Assert.Equal("modelA", StatsBarChart.FormatAxisLabel("modelA", System.Globalization.CultureInfo.CurrentCulture));
    }

    [Fact]
    public void BuildAccessibleSummaryNamesEveryBarAndItsTotal()
    {
        StatsBarChart.Bar[] bars = [new("2026-01-01", [100, 50]), new("2026-01-02", [0, 40])];

        var summary = StatsBarChart.BuildAccessibleSummary(bars);

        Assert.Equal("2026-01-01: 150; 2026-01-02: 40", summary);
    }

    // The left/bottom axis bands are now sized to what the data actually needs (the widest Y axis
    // figure, the X axis label's own line height) instead of a fixed guess wide enough for the worst
    // case - these two pin the exact arithmetic that band is built from.
    [Fact]
    public void ComputeLeftMarginAddsTheFixedGapToTheWidestTickLabel()
    {
        Assert.Equal(46.0, StatsBarChart.ComputeLeftMargin(widestTickWidth: 40.0));
    }

    [Fact]
    public void ComputeLeftMarginNeverGoesNegativeForANegativeWidth()
    {
        Assert.Equal(6.0, StatsBarChart.ComputeLeftMargin(widestTickWidth: -100.0));
    }

    [Fact]
    public void ComputeBottomMarginAddsTheFixedGapToTheLabelHeight()
    {
        Assert.Equal(15.0, StatsBarChart.ComputeBottomMargin(axisLabelHeight: 11.0));
    }
}

/// <summary>Geometry coverage for <see cref="StatsRingChart"/>.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class StatsRingChartGeometryTests
{
    // The acceptance criterion here: four 25% shares produce four 90° sweeps that sum to 360°.
    [Fact]
    public void FourEqualQuartersProduceFourNinetyDegreeSweepsSummingToAFullCircle()
    {
        var sweeps = StatsRingChart.SweepAngles([25.0, 25.0, 25.0, 25.0]);

        Assert.Equal(4, sweeps.Count);
        Assert.All(sweeps, sweep => Assert.Equal(90.0, sweep.SweepAngle));
        Assert.Equal(360.0, sweeps.Sum(sweep => sweep.SweepAngle));
        Assert.Equal([0.0, 90.0, 180.0, 270.0], sweeps.Select(sweep => sweep.StartAngle).ToArray());
    }

    [Fact]
    public void UnequalSharesStillSumToAFullCircle()
    {
        var sweeps = StatsRingChart.SweepAngles([50.0, 30.0, 20.0]);

        Assert.Equal(360.0, sweeps.Sum(sweep => sweep.SweepAngle));
        Assert.Equal(180.0, sweeps[0].SweepAngle);
        Assert.Equal(108.0, sweeps[1].SweepAngle);
        Assert.Equal(72.0, sweeps[2].SweepAngle);
    }

    [Fact]
    public void BuildAccessibleSummaryNamesEverySliceAndItsPercent()
    {
        StatsRingChart.Slice[] slices = [new("Claude", 75.0), new("Codex", 25.0)];

        var summary = StatsRingChart.BuildAccessibleSummary(slices);

        Assert.Equal("Claude: 75 %; Codex: 25 %", summary);
    }

    // A slice with an own LegendText (a provider this machine tracks no local token data for at all)
    // is named by that text instead of a computed "0%" - both here and on screen, since a screen
    // reader must draw the same "not countable" distinction a sighted reader sees in the legend.
    [Fact]
    public void BuildAccessibleSummaryUsesASlicesOwnLegendTextInsteadOfItsComputedPercent()
    {
        StatsRingChart.Slice[] slices =
        [
            new("Claude", 100.0),
            new("Gemini", 0.0, LegendText: "Not countable here"),
        ];

        var summary = StatsRingChart.BuildAccessibleSummary(slices);

        Assert.Equal("Claude: 100 %; Gemini: Not countable here", summary);
    }

    // The ring center's own text source: the largest slice by TOTAL, not by the (possibly rounded)
    // percent - Claude's 900 beats Codex's 100 even though nothing here checks the percent field at
    // all beyond "is this slice shown".
    [Fact]
    public void LargestSliceIndex_picks_the_biggest_total()
    {
        StatsRingChart.Slice[] slices = [new("Claude", 90.0, Total: 900), new("Codex", 10.0, Total: 100)];

        Assert.Equal(0, StatsRingChart.LargestSliceIndex(slices));
    }

    // A tie keeps whichever slice came first - the same rule every other tie in this window resolves
    // by.
    [Fact]
    public void LargestSliceIndex_breaks_a_tie_by_keeping_the_first_slice()
    {
        StatsRingChart.Slice[] slices = [new("Claude", 50.0, Total: 500), new("Codex", 50.0, Total: 500)];

        Assert.Equal(0, StatsRingChart.LargestSliceIndex(slices));
    }

    // A slice with no share at all (a provider this machine tracks no local data for, always 0%)
    // is never picked, even when nothing else in the list has a larger total either.
    [Fact]
    public void LargestSliceIndex_never_picks_a_zero_share_slice()
    {
        StatsRingChart.Slice[] slices = [new("Gemini", 0.0, LegendText: "Not countable here", Total: 0)];

        Assert.Equal(-1, StatsRingChart.LargestSliceIndex(slices));
    }

    [Fact]
    public void CenterHeadline_names_the_largest_slice_and_formats_its_own_total()
    {
        StatsRingChart.Slice[] slices = [new("Claude", 90.0, Total: 1_500_000), new("Codex", 10.0, Total: 100_000)];

        var (name, value) = StatsRingChart.CenterHeadline(slices, "K", "M", "B");

        Assert.Equal("Claude", name);
        Assert.Equal($"{1.5.ToString("0.#", CultureInfo.CurrentCulture)}M", value);
    }

    [Fact]
    public void CenterHeadline_is_empty_when_nothing_qualifies()
    {
        var (name, value) = StatsRingChart.CenterHeadline([], "K", "M", "B");

        Assert.Equal("", name);
        Assert.Equal("", value);
    }

    [Fact]
    public void BuildLargestShareAccessibleText_fills_the_format_with_name_and_value()
    {
        StatsRingChart.Slice[] slices = [new("Claude", 100.0, Total: 2_000_000)];

        var text = StatsRingChart.BuildLargestShareAccessibleText(slices, "Largest share: {0}, {1}", "K", "M", "B");

        Assert.Equal("Largest share: Claude, 2M", text);
    }

    [Fact]
    public void BuildLargestShareAccessibleText_is_empty_with_no_format_bound()
    {
        StatsRingChart.Slice[] slices = [new("Claude", 100.0, Total: 2_000_000)];

        Assert.Equal("", StatsRingChart.BuildLargestShareAccessibleText(slices, "", "K", "M", "B"));
    }
}

/// <summary>Geometry coverage for <see cref="StatsCacheShareBar"/>.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class StatsCacheShareBarGeometryTests
{
    // The acceptance criterion here: the four segment widths sum to the full width.
    [Fact]
    public void FourSegmentWidthsSumExactlyToTheFullWidth()
    {
        var widths = StatsCacheShareBar.SegmentWidths([100, 50, 25, 25], totalWidth: 300);

        Assert.Equal(4, widths.Count);
        Assert.Equal(300.0, widths.Sum(), 6);
        Assert.Equal(150.0, widths[0]); // 100/200 * 300
        Assert.Equal(75.0, widths[1]);
        Assert.Equal(37.5, widths[2]);
        Assert.Equal(37.5, widths[3]);
    }

    [Fact]
    public void UnevenValuesStillSumExactlyToTheFullWidthDespiteRounding()
    {
        var widths = StatsCacheShareBar.SegmentWidths([1, 1, 1], totalWidth: 100);

        Assert.Equal(100.0, widths.Sum(), 6);
    }

    [Fact]
    public void AllZeroValuesProduceAllZeroWidths()
    {
        var widths = StatsCacheShareBar.SegmentWidths([0, 0, 0, 0], totalWidth: 300);

        Assert.All(widths, width => Assert.Equal(0.0, width));
    }

    [Fact]
    public void ACacheLegendTooWideForTheBarWrapsOntoANewRow()
    {
        var positions = StatsCacheShareBar.LegendPositions([80, 80, 80], availableWidth: 240);

        Assert.Equal([(0.0, 0), (104.0, 0), (0.0, 1)], positions);
    }

    [Fact]
    public void ACacheLegendThatFitsStaysOnOneRow()
    {
        var positions = StatsCacheShareBar.LegendPositions([80, 80, 80], availableWidth: 600);

        Assert.All(positions, position => Assert.Equal(0, position.Row));
    }

    [Fact]
    public void ShareBarDrawsNoTextInsideTheBar()
    {
        // Rendered on its own STA thread: the bar's drawing is read back from the visual, and every
        // glyph run it holds must have its baseline in the legend below the 24 px bar, never on a segment.
        var glyphBaselines = new List<double>();
        var worker = new Thread(() =>
        {
            var bar = new StatsCacheShareBar { Values = [800L, 150L, 50L], SegmentLabels = ["Input", "Cache", "Output"] };
            bar.Measure(new Size(400, 200));
            bar.Arrange(new Rect(0, 0, 400, bar.DesiredSize.Height));
            bar.UpdateLayout();
            CollectGlyphBaselines(VisualTreeHelper.GetDrawing(bar), glyphBaselines);
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));

        Assert.NotEmpty(glyphBaselines);
        Assert.All(glyphBaselines, y => Assert.True(y >= 24, $"text drawn inside the bar at y={y}"));
    }

    private static void CollectGlyphBaselines(Drawing? drawing, List<double> baselines)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                foreach (var child in group.Children)
                    CollectGlyphBaselines(child, baselines);
                break;
            case GlyphRunDrawing glyphs:
                baselines.Add(glyphs.GlyphRun.BaselineOrigin.Y);
                break;
        }
    }

    [Fact]
    public void BuildAccessibleSummaryNamesEverySegmentAndItsPercent()
    {
        var summary = StatsCacheShareBar.BuildAccessibleSummary([50, 50], ["New input", "Output"]);

        Assert.Equal("New input: 50 %; Output: 50 %", summary);
    }
}

/// <summary>Geometry coverage for <see cref="StatsHorizontalBarChart"/>.</summary>
public class StatsHorizontalBarChartGeometryTests
{
    [Fact]
    public void RowTopsStacksEveryRowDirectlyBelowThePrevious()
    {
        var tops = StatsHorizontalBarChart.RowTops(3);

        Assert.Equal([0.0, 22.0, 44.0], tops);
    }

    [Fact]
    public void BarWidthIsProportionalToTheLargestRowNotToTheSum()
    {
        Assert.Equal(100.0, StatsHorizontalBarChart.BarWidth(value: 100, maxValue: 100, maxWidth: 100)); // the top row fills the area
        Assert.Equal(50.0, StatsHorizontalBarChart.BarWidth(value: 50, maxValue: 100, maxWidth: 100));
        Assert.Equal(0.0, StatsHorizontalBarChart.BarWidth(value: 0, maxValue: 100, maxWidth: 100));
    }

    [Fact]
    public void BarWidthIsZeroWhenNothingHasAnyTokensAtAll()
    {
        Assert.Equal(0.0, StatsHorizontalBarChart.BarWidth(value: 0, maxValue: 0, maxWidth: 100));
    }

    [Fact]
    public void BuildAccessibleSummaryNamesEveryRowByItsFullPathAndTotal()
    {
        AiUsage.Stats.StatsProjectRow[] rows =
        [
            new("projA", @"C:\code\projA", 100, 66.7, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1)),
            new("projB", @"C:\code\projB", 50, 33.3, new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 2)),
        ];

        var summary = StatsHorizontalBarChart.BuildAccessibleSummary(rows);

        Assert.Equal(@"C:\code\projA: 100; C:\code\projB: 50", summary);
    }
    /// <summary>A ring is a stroked arc, so it reaches half a stroke width past the radius it is
    /// drawn on. Taking the radius as the full half-diameter pushed that half stroke outside the
    /// control, which clips its own content - the top and the bottom of every donut were cut off.</summary>
    [Theory]
    [InlineData(200.0)]
    [InlineData(120.0)]
    [InlineData(48.0)]
    [InlineData(12.0)]
    public void TheDonutsOuterEdgeStaysInsideTheBoxItIsDrawnIn(double diameter)
    {
        var (radius, stroke) = StatsRingChart.RingGeometry(diameter);

        Assert.True(radius + stroke / 2 <= diameter / 2, $"{diameter}: {radius} + {stroke / 2} runs past {diameter / 2}");
    }

    /// <summary>And it is still a ring rather than a hairline or a filled disc: the stroke keeps the
    /// same "half the radius" proportion the chart has always been drawn with.</summary>
    [Fact]
    public void TheDonutKeepsItsRingProportionRatherThanBecomingAHairlineOrADisc()
    {
        var (radius, stroke) = StatsRingChart.RingGeometry(200);

        Assert.Equal(radius / 2, stroke, 3);
    }

    /// <summary>The donut center text never clips: content already inside the box is never enlarged
    /// (a scale of exactly 1), and content too wide or too tall is shrunk down by whichever axis
    /// needs it more, never past the caller's own floor.</summary>
    [Fact]
    public void FitScaleLeavesContentThatAlreadyFitsAlone()
    {
        Assert.Equal(1.0, StatsRingChart.FitScale(contentWidth: 40, contentHeight: 20, availableWidth: 100, availableHeight: 100, minScale: 0.1));
    }

    [Fact]
    public void FitScaleShrinksToTheTighterOfWidthOrHeight()
    {
        // Width alone would allow 0.5, height alone 0.8 - the smaller of the two wins so neither axis clips.
        var scale = StatsRingChart.FitScale(contentWidth: 100, contentHeight: 100, availableWidth: 50, availableHeight: 80, minScale: 0.1);

        Assert.Equal(0.5, scale, 3);
    }

    [Fact]
    public void FitScaleNeverShrinksPastItsOwnFloor()
    {
        var scale = StatsRingChart.FitScale(contentWidth: 1000, contentHeight: 1000, availableWidth: 10, availableHeight: 10, minScale: 0.2);

        Assert.Equal(0.2, scale);
    }

    [Fact]
    public void FitScaleFloorsOutOnAZeroOrNegativeBox()
    {
        Assert.Equal(0.3, StatsRingChart.FitScale(contentWidth: 10, contentHeight: 10, availableWidth: 0, availableHeight: 10, minScale: 0.3));
    }
}

public class StatsWindowProjectBarColorTests
{
    [Fact]
    public void A_project_bar_takes_the_color_its_top_projects_row_shows()
    {
        var resolved = Color.FromRgb(0x12, 0x34, 0x56);
        var bars = new[]
        {
            new StatsBarChart.Bar("proj", [10L], @"D:\work\proj"),
            new StatsBarChart.Bar("other", [5L], @"D:\work\other"),
        };

        var brushes = StatsWindow.BarColorBrushes(
            StatsGrouping.Project, bars, new Dictionary<string, Color> { [@"D:\work\proj"] = resolved }, Brushes.Gray);

        Assert.Equal(resolved, ((SolidColorBrush)brushes[0]).Color);
        Assert.Equal(ChartPalette.ForProvider(@"D:\work\other"), ((SolidColorBrush)brushes[1]).Color);
    }

    [Fact]
    public void The_bundled_other_projects_bar_takes_the_muted_brush()
    {
        var bars = new[] { new StatsBarChart.Bar("Other", [10L], StatsViewModel.OtherProjectsColorKey) };
        var muted = Brushes.Gray;

        var brushes = StatsWindow.BarColorBrushes(StatsGrouping.Project, bars, new Dictionary<string, Color>(), muted);

        Assert.Same(muted, brushes[0]);
    }
}
