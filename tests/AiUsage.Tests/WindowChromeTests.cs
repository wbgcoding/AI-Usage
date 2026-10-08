using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The three window-level layout faults this project shipped once, each measured on a real window
/// built off screen (the STA-thread-with-timeout technique <see cref="RenderedTextContrastTests"/>
/// documents, so a stuck call fails the test instead of hanging the run): a chrome frame inset from
/// the window edge by a gutter nothing could be done with, a settings pane measured at unlimited
/// width so every wrapping label was cut off instead, and a statistics window whose scroll region
/// covered only the panels at its bottom.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class WindowChromeTests
{
    /// <summary>The dialog's border comes from the same themed <c>Border.Window</c> resource every
    /// other window's chrome uses (see the <c>WindowChrome</c> style in <c>Controls.xaml</c>), which
    /// every theme file defines as its own dark or light color, never white - a hardcoded white or
    /// near-white literal creeping back into this one file would draw a border that fits none of
    /// them.</summary>
    [Fact]
    public void ConfirmWindowNamesNoHardcodedWhiteBorder()
    {
        var path = Path.Combine(FindAppSourceRoot(), "Views", "ConfirmWindow.xaml");
        var text = File.ReadAllText(path);

        Assert.DoesNotContain("White", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#FFF", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindAppSourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "AiUsage")))
            dir = Path.GetDirectoryName(dir);
        return dir is null
            ? throw new InvalidOperationException("Could not locate the src/AiUsage directory from the test output path.")
            : Path.Combine(dir, "src", "AiUsage");
    }

    [Fact]
    public void TheChromeFrameSitsOnTheWindowEdgeWithNoGutterAroundIt()
    {
        var margin = OnUiThread(static _ =>
        {
            var window = new ConfirmWindow("Titel", "Meldung", "OK", "Abbrechen");
            var root = (FrameworkElement)window.Content;
            Arrange(root, 420, 240);

            return ((FrameworkElement)root.FindName("ChromeBorder")).Margin;
        });

        Assert.Equal(new Thickness(0), margin);
    }

    [Fact]
    public void AConfirmWindowIsWideEnoughForItsButtonsBeforeItShows()
    {
        var (shortWidth, longWidth, missing) = OnUiThread(static _ =>
        {
            (double Width, double Missing) Open(string confirm)
            {
                var window = new ConfirmWindow("Titel", "Meldung", confirm, "Abbrechen");
                var root = (FrameworkElement)window.Content;
                Arrange(root, window.Width, 400);
                var row = (StackPanel)root.FindName("ButtonRow");
                var needed = row.Children.OfType<UIElement>().Sum(c => c.DesiredSize.Width);
                return (window.Width, needed - ((FrameworkElement)row.Parent).ActualWidth);
            }

            var shortCase = Open("OK");
            var longCase = Open("Alle Einstellungen jetzt wirklich zuruecksetzen");
            return (shortCase.Width, longCase.Width, longCase.Missing);
        });

        Assert.Equal(356, shortWidth);
        Assert.True(longWidth > 356, $"long labels kept {longWidth}");
        Assert.True(missing <= 0, $"buttons still miss {missing}");
    }

    [Fact]
    public void ACheckBoxWrapsAPlainTextLabelAndKeepsAnElementLabelAsItIs()
    {
        var (wrapping, elementText) = OnUiThread(static _ =>
        {
            var textBox = new CheckBox { Content = "A label far too long to fit on one line of this narrow box", Width = 120 };
            var elementBox = new CheckBox { Content = new TextBlock { Text = "Own element" }, Width = 120 };
            var host = new StackPanel();
            host.Children.Add(textBox);
            host.Children.Add(elementBox);
            Arrange(host, 120, 200);

            return (FindDescendant<AccessText>(textBox)?.TextWrapping, FirstTextBlockText(elementBox));
        });

        Assert.Equal(TextWrapping.Wrap, wrapping);
        Assert.Equal("Own element", elementText);
    }

    [Fact]
    public void AClosedComboBoxShowsItsSelectedRowsLabelRatherThanTheRowsTypeName()
    {
        var text = OnUiThread(static context =>
        {
            var box = new ComboBox
            {
                ItemsSource = context.Settings.LayoutChoices,
                SelectedItem = context.Settings.LayoutChoices[0],
                Width = 200,
            };
            // The box needs a parent that is itself laid out: a ComboBox arranged on its own never
            // gets as far as realising the presenter inside its template.
            var host = new Grid();
            host.Children.Add(box);
            Arrange(host, 200, 32);

            return FirstTextBlockText(box);
        });

        Assert.Equal(LocalizationService.Instance["Layout.Vertical"], text);
    }

    [Theory]
    [InlineData("Display")]
    [InlineData("Updates")]
    [InlineData("Notifications")]
    [InlineData("Providers")]
    [InlineData("System")]
    [InlineData("About")]
    public void NoSettingsLabelReachesPastTheWindowEdge(string category)
    {
        const double width = 560;

        var overflowing = OnUiThread(context =>
        {
            // The provider rows are the longest text this window holds, and they are empty until a
            // snapshot has landed: without one the category the fault was reported in would be
            // measured with nothing in it.
            Assert.NotEmpty(context.Settings.Main.Tiles);
            var now = DateTimeOffset.UtcNow;
            foreach (var tile in context.Settings.Main.Tiles)
                tile.Apply(new ProviderSnapshot(tile.ProviderId, [], "Pro", SourceKind.WebSession,
                    now, now, ProviderStatus.Ok, null, [], "Beispiel Person, Beispielfirma GmbH"), now);

            context.Settings.SelectedCategory = category;
            var window = new SettingsWindow(context.Settings);
            var root = (FrameworkElement)window.Content;
            Arrange(root, width, 700);

            return CollectTextRunningPastTheRightEdge(root, width);
        });

        Assert.True(overflowing.Count == 0,
            $"Text running past the {width:0}px window edge in the \"{category}\" category:\n" +
            string.Join('\n', overflowing));
    }

    /// <summary>Every category panel used to stay <c>Hidden</c> (never <c>Collapsed</c>) so the
    /// window, sized via <c>SizeToContent="Height"</c>, always came out as tall as the tallest
    /// category - a short one like "System" left an empty band at the bottom instead of shrinking the
    /// window. Now only the selected panel is <c>Visible</c>; a real spread between the shortest and
    /// the tallest category's measured height is what proves each one sizes to its own content
    /// again, without pinning the exact pixel counts, which the next added row would break.</summary>
    [Fact]
    public void DifferentCategoriesMeasureDifferentWindowHeights()
    {
        var heights = OnUiThread(context =>
        {
            Assert.NotEmpty(context.Settings.Main.Tiles);
            var now = DateTimeOffset.UtcNow;
            foreach (var tile in context.Settings.Main.Tiles)
                tile.Apply(new ProviderSnapshot(tile.ProviderId, [], "Pro", SourceKind.WebSession,
                    now, now, ProviderStatus.Ok, null, [], "Beispiel Person, Beispielfirma GmbH"), now);

            var result = new Dictionary<string, double>();
            foreach (var category in new[] { "Display", "Updates", "Notifications", "Providers", "System", "About" })
            {
                context.Settings.SelectedCategory = category;
                var window = new SettingsWindow(context.Settings);
                var root = (FrameworkElement)window.Content;
                Arrange(root, 560, 2000);
                result[category] = root.DesiredSize.Height;
            }

            return result;
        });

        var shortest = heights.MinBy(kv => kv.Value);
        var tallest = heights.MaxBy(kv => kv.Value);
        Assert.True(tallest.Value - shortest.Value > 100,
            "Every category still measures roughly the same window height: "
            + string.Join(", ", heights.Select(kv => $"{kv.Key}={kv.Value:0}")));
    }

    /// <summary>The context menu's "Show details"/"Hide details" entry (only visible at all once
    /// <see cref="ProviderTileViewModel.HasDiagnostics"/> is true - a healthy tile with nothing to
    /// explain hides it outright, covered in <c>ProviderTileViewModelTests</c>) is meant to grow the
    /// tile in place when it expands the diagnostics panel underneath it, and shrink it back when
    /// toggled off. A ContextMenu is a Popup with no reliable on-screen presence to drive from outside
    /// the process, so this measures the same real, themed <see cref="ProviderTile"/> the popup's
    /// command would act on, off screen, the way every other layout test in this class already
    /// does.</summary>
    [Fact]
    public void TogglingDetailsGrowsTheTileAndTogglingBackShrinksItAgain()
    {
        var (collapsedHeight, expandedHeight, shrunkHeight) = OnUiThread(_ =>
        {
            var viewModel = new ProviderTileViewModel("claude", "Claude");
            viewModel.Apply(new ProviderSnapshot("claude", [], "Pro", SourceKind.WebSession,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProviderStatus.NoLocalData, null,
                ["Gesucht in: ~/.claude", "Nichts gefunden."], null), DateTimeOffset.UtcNow);
            Assert.True(viewModel.HasDiagnostics);

            var tile = new ProviderTile { DataContext = viewModel, Width = 400 };
            Arrange(tile, 400, 400);
            var collapsed = tile.DesiredSize.Height;

            viewModel.ToggleDetailsCommand.Execute(null);
            Arrange(tile, 400, 400);
            var expanded = tile.DesiredSize.Height;

            viewModel.ToggleDetailsCommand.Execute(null);
            Arrange(tile, 400, 400);
            var shrunk = tile.DesiredSize.Height;

            return (collapsed, expanded, shrunk);
        });

        Assert.True(expandedHeight - collapsedHeight > 10,
            $"Expanding details did not grow the tile: collapsed={collapsedHeight:0}, expanded={expandedHeight:0}");
        Assert.Equal(collapsedHeight, shrunkHeight, precision: 3);
    }

    /// <summary>The previous-week comparison line was drawn first, behind the real series -
    /// harmless while the two disagree, but a steady user's week-over-week figure often tracks its own
    /// current one closely, and then the real series' own opaque line, painted second, lands exactly on
    /// top of the muted comparison line and erases every trace of it. Proven here by giving both series
    /// the very same points: the real series' <c>SolidPen</c> is fully opaque, so if the comparison line
    /// is drawn UNDER it, the rendered pixel at the line is the real series' color with no trace of the
    /// comparison line's own (semi-transparent) tint left in it at all.</summary>
    [Fact]
    public void ThePreviousWeekLineStillShowsWhenItMatchesThisWeeksOwnValues()
    {
        var maxDelta = OnUiThread(_ =>
        {
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var end = start.AddHours(24);
            var points = Enumerable.Range(0, 24)
                .Select(i => new HistoryChart.ChartPoint(start.AddHours(i), 50.0))
                .ToArray();

            Color[] RenderRow(IReadOnlyList<HistoryChart.ChartPoint> previousWeek, int y)
            {
                var chart = new HistoryChart
                {
                    Width = 300,
                    Height = 64,
                    WeeklyValues = points,
                    PreviousWeekValues = previousWeek,
                    RangeStart = start,
                    RangeEnd = end,
                    ShowAxes = true,
                };
                Arrange(chart, 300, 64);

                var bmp = new RenderTargetBitmap(300, 64, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(chart);
                var pixels = new byte[300 * 4];
                bmp.CopyPixels(new Int32Rect(0, y, 300, 1), pixels, 300 * 4, 0);
                var row = new Color[300];
                for (var x = 0; x < 300; x++)
                    row[x] = Color.FromArgb(pixels[x * 4 + 3], pixels[x * 4 + 2], pixels[x * 4 + 1], pixels[x * 4]);
                return row;
            }

            // Same shape as WeeklyValues - the case that used to be erased outright by the real
            // series' own opaque line, drawn on top of it. No comparison line at all is the baseline:
            // the plain real-series color, everywhere along its own line.
            var matchingRow = RenderRow(points, 26); // the 50% line sits at mid-height of the 52 px plot area
            var plainRow = RenderRow([], 26);

            static double Delta(Color a, Color b) =>
                Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2) + Math.Pow(a.A - b.A, 2));

            // The largest difference anywhere along the line - a handful of anti-aliased edge pixels
            // differing by a few units is noise, not a visible line; the real series' own line spans
            // most of the width, so a genuinely visible overlay changes a wide stretch of it clearly.
            return Enumerable.Range(0, 300).Max(x => Delta(matchingRow[x], plainRow[x]));
        });

        Assert.True(maxDelta > 25,
            $"The previous-week line leaves no visible trace when it matches this week's own values (max color delta {maxDelta:0.0}).");
    }

    /// <summary>The statistics window's own MinWidth (line 10 of <c>StatsWindow.xaml</c>) is the
    /// narrowest it can ever be resized to - the period selector, the grouping selector and the CSV
    /// export button all sharing one header row is exactly the layout most likely to run out of
    /// room there. <see cref="NoSettingsLabelReachesPastTheWindowEdge"/> does not cover this: it
    /// arranges the settings window over its five categories and never opens the statistics window
    /// at all.</summary>
    [Fact]
    public void NoStatsLabelReachesPastTheWindowEdge()
    {
        const double width = 480;

        var (overflowing, buttonVisible) = OnUiThread(static _ =>
        {
            using var dataDir = TestPaths.CreateDisposableDirectory("stats-window-min-width");
            var window = new StatsWindow(StatsVm.Create(new StatsStore(dataDir)));
            var root = (FrameworkElement)window.Content;
            Arrange(root, width, 420);

            var button = (Button?)root.FindName("ExportCsvButton");
            var right = button is null ? double.NaN : button.TransformToAncestor(root).Transform(new Point(button.ActualWidth, 0)).X;
            var visible = button is not null && button.ActualWidth > 0 && right <= width + 0.5;

            return (CollectTextRunningPastTheRightEdge(root, width), visible);
        });

        Assert.True(overflowing.Count == 0,
            $"Text running past the {width:0}px window edge:\n" + string.Join('\n', overflowing));
        Assert.True(buttonVisible, "The CSV export button is not fully visible at the window's MinWidth.");
    }

    /// <summary>The CSV export follows the grouping chosen on the breakdown section's header, so its
    /// button sits on that header (the table section is collapsed under the Day grouping).</summary>
    [Fact]
    public void TheCsvExportButtonSitsOnTheBreakdownSectionHeader()
    {
        var insideTableSection = OnUiThread(static _ =>
        {
            using var dataDir = TestPaths.CreateDisposableDirectory("stats-window-csv-button");
            var window = new StatsWindow(StatsVm.Create(new StatsStore(dataDir)));
            var root = (FrameworkElement)window.Content;
            Arrange(root, 800, 600);

            var button = (Button?)root.FindName("ExportCsvButton");
            var section = (DependencyObject?)root.FindName("BreakdownSection");
            for (DependencyObject? node = button; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, section))
                    return button!.ActualWidth > 0;
            }
            return false;
        });

        Assert.True(insideTableSection, "ExportCsvButton is not a visible part of the BreakdownSection header.");
    }

    [Fact]
    public void TheStatisticsWindowScrollsItsWholeContentAndNotOnlyItsLowerHalf()
    {
        var rangeSelectorScrolls = OnUiThread(static context =>
        {
            using var dataDir = TestPaths.CreateDisposableDirectory("stats-window-scroll");
            var window = new StatsWindow(StatsVm.Create(new StatsStore(dataDir)));
            var root = (FrameworkElement)window.Content;
            Arrange(root, 720, 520);

            // The range selector is the very first control under the title bar. Finding it inside the
            // scroll region is what proves the region covers the whole window rather than the panels
            // below it, which is all it used to reach.
            var scroller = FindDescendant<ScrollViewer>(root)!;
            return FindDescendant<ComboBox>(scroller) is not null;
        });

        Assert.True(rangeSelectorScrolls);
    }

    /// <summary>Every window in this app draws its own title bar and hides the system one with
    /// <c>WindowStyle="None"</c>. On a window that can be resized, that combination still leaves
    /// Windows 10/11 drawing the sizing frame, whose top edge shows as a white strip above the app's
    /// own title bar. Two ways out: give up resizing (<c>ResizeMode="NoResize"</c>) or take the whole
    /// non-client area over with a shell <c>WindowChrome</c>. A window with neither ships the
    /// strip.</summary>
    [Fact]
    public void NoFramelessWindowKeepsTheSystemSizingFrameAboveItsOwnTitleBar()
    {
        var viewsDirectory = Path.Combine(FindRepoRoot(), "src", "AiUsage", "Views");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(viewsDirectory, "*.xaml"))
        {
            var source = File.ReadAllText(file);
            if (!source.Contains("WindowStyle=\"None\"", StringComparison.Ordinal))
                continue;
            if (source.Contains("ResizeMode=\"NoResize\"", StringComparison.Ordinal)
                || source.Contains("<WindowChrome.WindowChrome>", StringComparison.Ordinal))
                continue;

            offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "Resizable frameless windows with no shell WindowChrome to cover the sizing frame: "
            + string.Join(", ", offenders));
    }

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

    private sealed record Context(SettingsViewModel Settings);

    /// <summary>Every laid-out <see cref="TextBlock"/> whose right edge, measured against the window
    /// root it sits in, is beyond <paramref name="width"/> - text the window can only cut off. A label
    /// that is allowed to wrap never produces one; a label that is not is arranged at the width its
    /// single line needs, however narrow the column around it, and always does.
    /// <para>Visibility is read off the element rather than through IsVisible: a window that was
    /// arranged off screen was never shown, so IsVisible is false for every element in it and a walk
    /// filtering on it would silently find nothing at all.</para></summary>
    private static List<string> CollectTextRunningPastTheRightEdge(FrameworkElement root, double width)
    {
        var offenders = new List<string>();

        void Visit(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
                return;

            if (node is TextBlock { Text.Length: > 0 } text)
            {
                var right = text.TransformToAncestor(root).Transform(new Point(text.ActualWidth, 0)).X;
                // Half a pixel of slack: a rounded layout position is not an overflowing label.
                if (right > width + 0.5)
                    offenders.Add($"{right - width:0.#}px past the edge: \"{text.Text}\"");
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                Visit(VisualTreeHelper.GetChild(node, i));
        }

        Visit(root);
        return offenders;
    }

    private static void Arrange(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static string? FirstTextBlockText(DependencyObject node) =>
        node is TextBlock text ? text.Text : FindDescendant<TextBlock>(node)?.Text;

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }

    /// <summary>Runs <paramref name="body"/> on a private STA thread with a real WPF
    /// <see cref="Application"/> and the shipped theme applied, and joins it with a timeout so a call
    /// that never returns fails this test rather than leaving the test process hanging.</summary>
    private static TResult OnUiThread<TResult>(Func<Context, TResult> body)
    {
        TResult result = default!;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = Run(body);
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
            throw new TimeoutException("window-chrome walk did not finish");
        if (failure is not null)
            throw new InvalidOperationException("Window-chrome walk failed.", failure);

        return result;
    }

    private static TResult Run<TResult>(Func<Context, TResult> body)
    {
        // WPF refuses a second Application per process even after Current is reset to null; the
        // private flag that remembers "one was created" is never cleared on Shutdown, so clearing it
        // here is what keeps this class independent of whichever other visual-walk test ran first.
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();

        var settingsStore = new SettingsStore(TestPaths.CreateDirectory("window-chrome"));
        var settings = new AppSettings();
        var mainViewModel = new MainViewModel(settingsStore, settings);
        var settingsViewModel = new SettingsViewModel(mainViewModel, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadFromProductionAssembly);

        try
        {
            ThemeService.Apply(AppTheme.Nebula,
                Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);
            return body(new Context(settingsViewModel));
        }
        finally
        {
            settingsViewModel.Dispose();
            mainViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        }
    }



    private static ResourceDictionary LoadFromProductionAssembly(Uri relativeUri) =>
        new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relativeUri.OriginalString, UriKind.Absolute) };
}
