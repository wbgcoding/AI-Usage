// README screenshots
//
// Renders the three pictures the README shows into .github/images/ from invented sample data:
//   home-claude-full.png   the widget with only the Claude tile, full density
//   home-all-small.png     the widget with all five tiles, small density
//   token-usage.png        the statistics window on its 30 day range
//
// Run it from the repository root:
//   dotnet run --project tools/ReadmeShots
//
// Everything is drawn offscreen (no window is ever shown), in English, with the dark theme, at twice
// the logical size, clipped to the app's own window corner radius with transparent corners. The
// widget uses a temporary data folder under .tmp/readme-shots-data that is removed again at the end,
// so nothing of the machine's real settings, history or usage index is read or written.
//
// Review mode draws every window, tile, dialog and menu the app has, for looking at them:
//   dotnet run --project tools/ReadmeShots -- --review <folder> [options]
//   --surfaces <list>   surface ids and/or @groups, comma separated (default: all). Groups: @widget,
//                       @stats, @settings, @tiles, @dialogs. An unknown value exits with 2 and
//                       lists every valid id and group.
//   --lang en|de|all    language (default: all)
//   --size min|default|both
//                       min = the window's smallest width, default = its normal width (default: both)
//   --theme <name>|all  Dark, Light, Nebula, Terminal or HighContrast (default: all)
// Files are named <surface-id>-<theme>-<language>-<size>.png. Nothing is written outside <folder>.

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views;
using Microsoft.Data.Sqlite;

namespace ReadmeShots;

internal static class Program
{
    private const double WindowCornerRadius = 8;
    private const int ImageScale = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        var reviewIndex = Array.IndexOf(args, "--review");
        string? reviewDirectory = null;
        ReviewOptions? reviewOptions = null;
        if (reviewIndex >= 0)
        {
            if (reviewIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("--review needs an output folder.");
                return 2;
            }
            if (reviewIndex != 0)
            {
                Console.Error.WriteLine("--review must be the first argument.");
                return 2;
            }
            reviewDirectory = Path.GetFullPath(args[reviewIndex + 1]);
            reviewOptions = ReviewOptions.Parse(args, reviewIndex + 2, out var parseError);
            if (reviewOptions is null)
            {
                Console.Error.WriteLine(parseError);
                return 2;
            }
        }
        else if (args.Length > 0)
        {
            Console.Error.WriteLine($"Unknown argument \"{args[0]}\". Use --review <folder> or no arguments.");
            return 2;
        }

        var repoRoot = FindRepoRoot();
        var outputDirectory = Path.Combine(repoRoot, ".github", "images");
        var dataDirectory = Path.Combine(repoRoot, ".tmp", "readme-shots-data");

        try
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
            Directory.CreateDirectory(dataDirectory);
            if (reviewDirectory is null)
                Directory.CreateDirectory(outputDirectory);

            SetLanguage("en");

            // Every path the app itself would write to (settings, history, the project colour cache)
            // goes to the throwaway folder.
            AppPaths.SetOverride(dataDirectory);

            // Awaited work started by a view model continues on this thread while it pumps.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LoadResources(application);

            if (reviewDirectory is not null && reviewOptions is not null)
            {
                var surfaces = ReviewSurfaces.Build(ReviewSettings.CategoryValues(dataDirectory));
                var themes = reviewOptions.ResolveThemes(out var optionError);
                var chosen = themes is null ? null : reviewOptions.Resolve(surfaces, out optionError);
                if (themes is null || chosen is null)
                {
                    Console.Error.WriteLine(optionError);
                    application.Shutdown();
                    return 2;
                }
                Directory.CreateDirectory(reviewDirectory);
                RenderReviewSet(application, dataDirectory, reviewDirectory, reviewOptions, themes, chosen);
            }
            else
            {
                RenderHomeShots(dataDirectory, outputDirectory);
                RenderStatisticsShot(dataDirectory, outputDirectory);
            }

            application.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            // A pooled SQLite connection keeps stats.db open past its own dispose.
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void SetLanguage(string code)
    {
        var culture = new CultureInfo(code == "de" ? "de-DE" : "en-US");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        LocalizationService.Instance.SetLanguage(code);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Run this from inside the repository.");
    }

    // The app's own App.xaml merges its resource files with bare relative paths, which only resolve
    // against the running exe's assembly. This tool is a different exe, so the same files are loaded
    // by their assembly qualified address instead, in the same order.
    private static void LoadResources(Application application)
    {
        ResourceDictionary Load(Uri relative) =>
            new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relative.OriginalString, UriKind.Absolute) };

        var merged = application.Resources.MergedDictionaries;
        merged.Add(Load(new Uri("Themes/Tokens.xaml", UriKind.Relative)));
        merged.Add(Load(new Uri("Themes/Controls.xaml", UriKind.Relative)));
        merged.Add(Load(new Uri("Themes/Icons.xaml", UriKind.Relative)));
        ThemeService.Apply(AppTheme.Dark, merged, Load, () => false);
    }

    // The review set -----------------------------------------------------------------------------

    private static void RenderReviewSet(
        Application application, string dataDirectory, string reviewDirectory, ReviewOptions options,
        IReadOnlyList<(string Name, AppTheme Theme, bool HighContrast)> themes, IReadOnlyList<Surface> surfaces)
    {
        ResourceDictionary Load(Uri relative) =>
            new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relative.OriginalString, UriKind.Absolute) };

        var now = DateTimeOffset.Now;
        var samples = SampleProviders.BuildReview(now);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var records = SampleUsage.Build(today);
        var busyDay = SampleUsage.PickBusyWeekday(records, today);
        var counter = new[] { 0 };

        foreach (var (name, theme, highContrast) in themes)
        {
            ThemeService.Apply(theme, application.Resources.MergedDictionaries, Load, () => highContrast);
            foreach (var language in options.Languages)
            {
                SetLanguage(language);
                foreach (var surface in surfaces)
                {
                    // A surface without a smaller variant is drawn once, whatever --size says.
                    var sizes = surface.HasMinSize ? options.Sizes : [SizeKind.Default];
                    foreach (var size in sizes)
                    {
                        surface.Render(new SurfaceContext(
                            name, language, size, reviewDirectory, dataDirectory, samples, records, busyDay, now, counter));
                    }
                }
            }
        }
    }

    // The widget ---------------------------------------------------------------------------------

    private const string WidgetChromeXaml = """
        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:controls="clr-namespace:AiUsage.Views.Controls;assembly=AI-Usage"
                xmlns:vm="clr-namespace:AiUsage.ViewModels;assembly=AI-Usage"
                Style="{StaticResource WindowChrome}"
                TextElement.Foreground="{DynamicResource Text.Primary}"
                RenderOptions.BitmapScalingMode="HighQuality">
            <DockPanel LastChildFill="True">
                <controls:TitleBar DockPanel.Dock="Top" EyeSource="{Binding}" ShowWindowMenu="True" ShowStats="True"/>
                <ItemsControl ItemsSource="{Binding DisplayRows}" Margin="8,8,8,0" DockPanel.Dock="Top">
                    <ItemsControl.Resources>
                        <DataTemplate DataType="{x:Type vm:ProviderTileViewModel}">
                            <controls:ProviderTile Margin="0,0,0,4"/>
                        </DataTemplate>
                        <DataTemplate DataType="{x:Type vm:DayGridTileViewModel}">
                            <controls:DayGridTile Margin="0,0,0,4"/>
                        </DataTemplate>
                    </ItemsControl.Resources>
                    <ItemsControl.ItemContainerStyle>
                        <Style TargetType="ContentPresenter">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding IsHidden}" Value="True">
                                    <Setter Property="Visibility" Value="Collapsed"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </ItemsControl.ItemContainerStyle>
                </ItemsControl>
            </DockPanel>
        </Border>
        """;

    /// <summary>The widget's usage-per-day tile alone at a given width (the review mode's widget shots
    /// keep it hidden), so the week count that fits a wide widget can be looked at.</summary>
    internal static void RenderDayTile(string dataDirectory, IReadOnlyList<StatsRecord> records, double width, TileDensity density, string outputPath)
    {
        Directory.CreateDirectory(dataDirectory);
        var store = new StatsStore(dataDirectory);
        store.AddDelta(records);
        var tileViewModel = new DayGridTileViewModel(store) { Density = density };
        // Pump instead of blocking: the refresh resumes on this dispatcher after its background load.
        var refresh = tileViewModel.RefreshAsync();
        PumpUntil(() => refresh.IsCompleted, TimeSpan.FromSeconds(30), settle: TimeSpan.Zero);
        refresh.GetAwaiter().GetResult();

        var host = (Border)XamlReader.Parse("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:controls="clr-namespace:AiUsage.Views.Controls;assembly=AI-Usage"
                    Style="{StaticResource WindowChrome}" Padding="8"
                    TextElement.Foreground="{DynamicResource Text.Primary}">
                <controls:DayGridTile/>
            </Border>
            """);
        ((FrameworkElement)host.Child).DataContext = tileViewModel;
        for (var pass = 0; pass < 3; pass++)
        {
            PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
            host.Measure(new Size(width, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
            host.UpdateLayout();
        }
        RenderToPng(host, width, Math.Ceiling(host.DesiredSize.Height), outputPath);
    }

    private static void RenderHomeShots(string dataDirectory, string outputDirectory)
    {
        var now = DateTimeOffset.Now;
        var samples = SampleProviders.Build(now);

        RenderWidget(dataDirectory, samples, now, visible: ["claude"], TileDensity.Full, Path.Combine(outputDirectory, "home-claude-full.png"));
        RenderWidget(dataDirectory, samples, now, visible: AppSettings.KnownProviderIds, TileDensity.Mini, Path.Combine(outputDirectory, "home-all-small.png"));
    }

    internal static void RenderWidget(
        string dataDirectory, IReadOnlyList<SampleProviders.Sample> samples, DateTimeOffset now,
        IReadOnlyList<string> visible, TileDensity density, string outputPath, double width = 380)
    {
        var settings = new AppSettings();
        foreach (var id in AppSettings.KnownProviderIds)
            settings.Providers[id].Visible = visible.Contains(id);

        var settingsStore = new SettingsStore(dataDirectory);
        var historyStore = new HistoryStore(dataDirectory, () => now);
        var providers = samples.Select(sample => (IUsageProvider)new SampleProviders.Provider(sample)).ToList();
        var viewModel = new MainViewModel(settingsStore, settings, providers, historyStore);

        try
        {
            foreach (var tile in viewModel.Tiles)
            {
                var sample = samples.Single(candidate => candidate.Id == tile.ProviderId);
                tile.Density = density;
                tile.Apply(sample.Snapshot, now);
                if (sample.History is { } history)
                    tile.UpdateHistory(history, [], now - TimeSpan.FromDays(7), now);
            }

            var chrome = (Border)XamlReader.Parse(WidgetChromeXaml);
            chrome.DataContext = viewModel;

            // Bindings activate at data-bind priority, so the dispatcher gets a turn before and after each
            // layout pass: the first fills the list, the second binds the tiles the list just created.
            PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
            chrome.Measure(new Size(width, double.PositiveInfinity));
            PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
            chrome.Measure(new Size(width, double.PositiveInfinity));
            var height = Math.Ceiling(chrome.DesiredSize.Height);
            RenderToPng(chrome, width, height, outputPath);
        }
        finally
        {
            viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // The statistics window ------------------------------------------------------------------------

    private static void RenderStatisticsShot(string dataDirectory, string outputDirectory)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var records = SampleUsage.Build(today);
        RenderStatistics(dataDirectory, records, SampleUsage.PickBusyWeekday(records, today), fullHeight: false,
            Path.Combine(outputDirectory, "token-usage.png"));
    }

    internal static void RenderStatistics(
        string dataDirectory, IReadOnlyList<StatsRecord> records, DateOnly busyDay, bool fullHeight, string outputPath,
        bool minWidth = false)
    {
        Directory.CreateDirectory(dataDirectory);
        var store = new StatsStore(dataDirectory);
        store.AddDelta(records);

        var viewModel = new StatsViewModel(store);
        viewModel.Recompute();
        viewModel.SelectedRangeChoice = viewModel.RangeChoices.Single(choice => choice.Value == "Month");
        PumpUntil(() => !viewModel.IsLoading, TimeSpan.FromSeconds(10), settle: TimeSpan.FromSeconds(1));

        var window = new StatsWindow(viewModel);
        viewModel.SelectDay(busyDay);
        // The project colours and icons resolve on a background task and land back on this thread.
        PumpUntil(() => false, TimeSpan.FromSeconds(2), settle: TimeSpan.Zero);

        // Cut just above the per-day section, and
        // without the scrollbar a cut-off page would otherwise show.
        var width = minWidth ? window.MinWidth : 900;
        var root = (FrameworkElement)window.Content;
        window.ContentScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        root.Measure(new Size(width, 2400));
        root.Arrange(new Rect(0, 0, width, 2400));
        root.UpdateLayout();
        // Offscreen the scroller raises no SizeChanged, so the grid learns its width here and the
        // second pass draws it reaching back as far as the card is wide.
        viewModel.MonthGridAvailableWidth = window.MonthGridScroller.ActualWidth;
        PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
        root.Measure(new Size(width, 2400));
        root.Arrange(new Rect(0, 0, width, 2400));
        root.UpdateLayout();
        if (fullHeight)
        {
            root.Measure(new Size(width, double.PositiveInfinity));
            root.UpdateLayout();
        }
        var height = fullHeight
            ? Math.Ceiling(root.DesiredSize.Height)
            : Math.Floor(window.PerDaySection.TranslatePoint(new Point(0, 0), root).Y - 4);
        RenderToPng(root, width, height, outputPath);
    }

    // Shared -------------------------------------------------------------------------------------------

    internal static void RenderToPng(FrameworkElement root, double width, double height, string outputPath)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
        root.UpdateLayout();
        root.Clip = new RectangleGeometry(new Rect(0, 0, width, height), WindowCornerRadius, WindowCornerRadius);

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * ImageScale), (int)Math.Ceiling(height * ImageScale),
            96 * ImageScale, 96 * ImageScale, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        Console.WriteLine($"{Path.GetFileName(outputPath)}: {bitmap.PixelWidth} x {bitmap.PixelHeight}");
    }

    internal static void PumpUntil(Func<bool> done, TimeSpan timeout, TimeSpan settle)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !done())
            PumpOnce();

        var settleUntil = DateTime.UtcNow + settle;
        while (DateTime.UtcNow < settleUntil)
            PumpOnce();
    }

    private static void PumpOnce()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        Thread.Sleep(10);
    }
}
