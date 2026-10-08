using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Walks the real, rendered visual tree of every view and control except <see cref="SignInWindow"/>
/// and <see cref="MainWindow"/> (built, measured and arranged, never shown) across every theme, and
/// asserts that each <c>TextBlock</c> with real text both takes its <c>Foreground</c> from the active
/// theme palette and reaches WCAG 4.5:1 contrast against the background actually painted behind it by
/// its ancestors. <see cref="AccessibilityTests"/> documents an earlier attempt at exactly this (real
/// Window/UserControl instances on an STA thread with a bootstrapped Application) that was abandoned
/// for reliably leaving a hung <c>dotnet test</c> process behind. Two things are different here and
/// make it safe: the whole walk runs on one thread this test creates itself, marked background and
/// joined with a timeout, so a stuck call fails the test instead of hanging the host process; and
/// <see cref="SignInWindow"/> still stays out of the walk, because it hosts a WebView2 control that
/// starts a real browser process the moment it is constructed, the likely cause of the original hang,
/// and it has no fake/local state to substitute the way every other view here does.
///
/// <see cref="MainWindow"/> is excluded for a second, separate reason found while building this test:
/// its constructor unconditionally starts a background task that immediately compacts and prunes the
/// real per-user history files under the real application data directory, with no test seam able to
/// redirect it elsewhere. Every other object built below is fed fake, in-memory or newly-invented
/// state instead of anything the real app has written to disk. Since MainWindow's own XAML root
/// carries the same single Foreground attribute as every other window, and the one thing it hosts
/// beyond that (the shared <see cref="ProviderTile"/>) is already covered directly, nothing here is
/// left untested by the exclusion.
///
/// Every window's <c>Icon</c> (and SettingsWindow's own app-mark <c>Image</c> in its About category) used a bare
/// <c>pack://application:,,,/Assets/app.ico</c> reference, which resolves against
/// <c>Application.ResourceAssembly</c> - fixed for the whole process to whatever assembly
/// <c>Assembly.GetEntryAssembly()</c> returns the first time anything reads it, and never
/// reassignable once that has happened (confirmed by disassembling WPF's own
/// <c>Application.set_ResourceAssembly</c>: the setter silently no-ops once
/// <c>Assembly.GetEntryAssembly()</c> is non-null, which it always is under vstest - "testhost", not
/// this test project). No trick reachable from test code (an explicit set, a
/// <c>[ModuleInitializer]</c> racing to run first) can beat that, so the five windows' XAML now
/// qualify that one reference to <c>AI-Usage;component/Assets/app.ico</c> instead - the normal,
/// idiomatic WPF form, naming the assembly explicitly rather than depending on which one happens to
/// be the process entry point. Same embedded icon, same assembly, just resolved by name.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class RenderedTextContrastTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> ViolationsByTheme = new(RunVisualWalk);

    [Theory]
    [InlineData("Nebula")]
    [InlineData("Terminal")]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("HighContrast")]
    public void RenderedTextTakesAThemeColorAndMeetsContrast(string themeName)
    {
        var violations = ViolationsByTheme.Value[themeName];
        Assert.True(violations.Count == 0, $"{themeName}:\n" + string.Join("\n", violations));
    }

    // The theme dictionaries this project copies into its own Themes/ folder (so the WPF resource
    // pipeline can embed them under the test assembly's own logical paths) must stay byte-identical
    // to their source - a stale copy would silently test the wrong colors instead of failing loudly.
    [Fact]
    public void CopiedThemeFilesMatchTheirSource()
    {
        var (sourceDir, copiedDir) = FindThemeDirs();
        var mismatches = new List<string>();

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*.xaml"))
        {
            var copiedFile = Path.Combine(copiedDir, Path.GetFileName(sourceFile));
            if (!File.Exists(copiedFile))
            {
                mismatches.Add($"{Path.GetFileName(sourceFile)}: not copied to {copiedFile}");
                continue;
            }
            if (!File.ReadAllBytes(sourceFile).AsSpan().SequenceEqual(File.ReadAllBytes(copiedFile)))
                mismatches.Add($"{Path.GetFileName(sourceFile)}: copy at {copiedFile} differs from source");
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    private static (string SourceDir, string CopiedDir) FindThemeDirs([CallerFilePath] string here = "")
    {
        var projectDir = Path.GetDirectoryName(here)!;
        var repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(projectDir))!;
        return (Path.Combine(repoRoot, "src", "AiUsage", "Themes"), Path.Combine(projectDir, "Themes"));
    }

    // Everything below runs once, memoized, and is shared by the five theory cases above - a second
    // WPF Application can never be created in the same process, so there is exactly one bootstrapped
    // Application and one worker thread for the whole class, not one per theme.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> RunVisualWalk()
    {
        Dictionary<string, IReadOnlyList<string>>? result = null;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = WalkAllThemes();
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
            throw new TimeoutException("visual walk did not finish");

        if (failure is not null)
            throw new InvalidOperationException("Rendered-text visual walk failed.", failure);

        return result!;
    }

    private static Dictionary<string, IReadOnlyList<string>> WalkAllThemes()
    {
        // WPF refuses a second Application instance per process even after Current is reset back to
        // null below - a second, separate private static flag remembers "one was already created"
        // and is never cleared on Shutdown. Clearing it here too is what makes this class order
        // independent against StatsWindowTests, the other visual-walk test in this project that
        // builds its own Application the same way (xunit runs this whole assembly's tests
        // sequentially, in undefined class order, so either one can run first).
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();

        var settingsStore = new SettingsStore(TempDirectory());
        var settings = new AppSettings();
        // One hidden provider so the title bar's hidden-count badge is part of the walk too, not
        // only its Collapsed default.
        settings.Providers["codex"].Visible = false;
        var mainViewModel = new MainViewModel(settingsStore, settings);
        // The public 3-arg constructor's own default theme-preview loader uses a bare relative pack
        // URI, which does not resolve in a test host. SettingsViewModel already ships an internal
        // test seam for exactly this; feeding it the same assembly-qualified loader used for
        // ThemeService.Apply below gets real preview colors. Autostart/language callbacks are
        // fakes for the same reason every other test in this project already uses them here: a test
        // must never toggle the real Windows Run key or the process-wide LocalizationService.
        var settingsViewModel = new SettingsViewModel(mainViewModel, settings, settingsStore,
            isAutostartEnabled: () => false,
            enableAutostart: _ => true,
            disableAutostart: () => true,
            applyLanguage: _ => { },
            loadThemeDictionary: LoadFromProductionAssembly);

        try
        {
            return new Dictionary<string, IReadOnlyList<string>>
            {
                ["Nebula"] = WalkTheme(AppTheme.Nebula, highContrast: false, mainViewModel, settingsViewModel),
                ["Terminal"] = WalkTheme(AppTheme.Terminal, highContrast: false, mainViewModel, settingsViewModel),
                ["Dark"] = WalkTheme(AppTheme.Dark, highContrast: false, mainViewModel, settingsViewModel),
                ["Light"] = WalkTheme(AppTheme.Light, highContrast: false, mainViewModel, settingsViewModel),
                ["HighContrast"] = WalkTheme(AppTheme.Nebula, highContrast: true, mainViewModel, settingsViewModel),
            };
        }
        finally
        {
            settingsViewModel.Dispose();
            mainViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            ResetApplicationCurrent();
        }
    }

    // Application.Shutdown() never clears Application.Current back to null (confirmed empirically:
    // its hash code is unchanged after Shutdown() and InvokeShutdown() both return) - a one-way
    // process-wide side effect that would otherwise leak into every test running after this one in
    // the same `dotnet test` process (xunit runs this whole assembly's tests sequentially - see
    // AssemblyInfo.cs). Concretely, SettingsViewModel's own production LoadThemeDictionary branches
    // on "is Application.Current null", so a stray live Application here made unrelated,
    // already-passing tests that construct a plain SettingsViewModel start hitting the exact
    // ResourceAssembly problem this class works around, only for a resource this project never
    // populates for them. Resetting the private static field WPF itself never resets is the only way
    // found to undo that leak; nothing else in this class or elsewhere depends on Application.Current
    // being non-null once this walk is done.
    private static void ResetApplicationCurrent() =>
        typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);

    private static string TempDirectory() => TestPaths.CreateDirectory("ai-usage-rendered-text-contrast");

    private static IReadOnlyList<string> WalkTheme(
        AppTheme theme, bool highContrast, MainViewModel mainViewModel, SettingsViewModel settingsViewModel)
    {
        ThemeService.Apply(theme, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => highContrast);

        var palette = CollectPaletteRgb(Application.Current.Resources);
        var violations = new List<string>();

        foreach (var (label, element) in BuildViews(mainViewModel, settingsViewModel))
        {
            element.Measure(new Size(1200, 1600));
            element.Arrange(new Rect(0, 0, 1200, 1600));
            element.UpdateLayout();

            WalkVisualTree(element, Color.FromArgb(0, 0, 0, 0), label, palette, violations);
        }

        return violations;
    }

    // The assembly-qualified form: unlike ThemeService.Apply's own default loader, this always reads
    // the production AI-Usage.dll's compiled theme resources, regardless of which assembly
    // Application.ResourceAssembly happens to be fixed to in this process (the test host, once
    // anything here touches it).
    private static ResourceDictionary LoadFromProductionAssembly(Uri relativeUri) =>
        new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relativeUri.OriginalString, UriKind.Absolute) };

    private static IEnumerable<(string Label, UIElement Element)> BuildViews(MainViewModel mainViewModel, SettingsViewModel settingsViewModel)
    {
        foreach (var status in Enum.GetValues<ProviderStatus>())
        foreach (var density in Enum.GetValues<TileDensity>())
            yield return ($"ProviderTile[{status},{density}]", BuildProviderTile(status, density));

        yield return ("UsageBar", BuildUsageBar());
        yield return ("ComboBoxClosed", BuildComboBoxClosed());
        yield return ("TitleBar", new TitleBar { DataContext = mainViewModel });
        yield return ("ConfirmWindow", (UIElement)new ConfirmWindow("Titel", "Meldung", "OK", "Abbrechen").Content);
        yield return ("CrashWindow", (UIElement)new CrashWindow("details", "C:\\logs").Content);
        yield return ("SettingsWindow", (UIElement)new SettingsWindow(settingsViewModel).Content);
        yield return ("WelcomeWindow", (UIElement)new WelcomeWindow(mainViewModel.Tiles).Content);
    }

    private static ProviderTile BuildProviderTile(ProviderStatus status, TileDensity density)
    {
        var viewModel = new ProviderTileViewModel("probe", "Probe");
        var snapshot = new ProviderSnapshot(
            ProviderId: "probe",
            Windows:
            [
                new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, Now.AddHours(2), 300),
                new UsageWindow("Window_Weekly", WindowKind.Weekly, 63, Now.AddDays(3), 10080),
            ],
            PlanType: "Plus",
            SourceKind: SourceKind.LocalFile,
            FetchedAt: Now,
            DataTimestamp: Now,
            Status: status,
            Error: null,
            Diagnostics: ["Looked in a place.", "Found nothing there."]);

        viewModel.Apply(snapshot, Now);
        viewModel.DetailsExpanded = true;
        viewModel.Density = density;

        return new ProviderTile { DataContext = viewModel };
    }

    private static UIElement BuildUsageBar()
    {
        var row = new UsageRowViewModel(new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 91, Now.AddHours(2), 300), Now);
        var bar = new UsageBar { DataContext = row };

        // UsageBar draws no background of its own - it only ever appears inside ProviderTile's own
        // themed card. Hosting it the same way here gives the walk a real background to check its
        // text against, instead of the fully transparent one it would otherwise inherit.
        return new Border { Background = (Brush)Application.Current!.Resources["Bg.Surface"], Child = bar };
    }

    // The shared ComboBox style (Themes/Controls.xaml) draws its selected value through an
    // auto-generated TextBlock (a plain string Content), so a closed, unopened ComboBox already
    // exercises the closed box's own text against its own background without needing the popup open.
    private static UIElement BuildComboBoxClosed()
    {
        var comboBox = new ComboBox { ItemsSource = new[] { "Alpha", "Beta" }, SelectedIndex = 0 };

        return new Border { Background = (Brush)Application.Current!.Resources["Bg.Surface"], Child = comboBox };
    }

    private static HashSet<(byte R, byte G, byte B)> CollectPaletteRgb(ResourceDictionary root)
    {
        var rgbValues = new HashSet<(byte, byte, byte)>();
        void Visit(ResourceDictionary dictionary)
        {
            foreach (var value in dictionary.Values)
            {
                if (value is SolidColorBrush brush)
                    rgbValues.Add((brush.Color.R, brush.Color.G, brush.Color.B));
            }
            foreach (var merged in dictionary.MergedDictionaries)
                Visit(merged);
        }
        Visit(root);
        return rgbValues;
    }

    private static void WalkVisualTree(
        DependencyObject node, Color inheritedBackground, string viewLabel,
        HashSet<(byte R, byte G, byte B)> palette, List<string> violations)
    {
        // A detached/collapsed subtree never reports IsVisible on its own, so pruning has to happen
        // here, on the way down, rather than by checking visibility after the fact.
        if (node is UIElement { Visibility: not Visibility.Visible })
            return;
        if (node is UIElement { Opacity: 0 })
            return;

        var background = inheritedBackground;
        if (TryGetBackgroundBrush(node) is SolidColorBrush backgroundBrush)
        {
            var effectiveAlpha = backgroundBrush.Color.A / 255.0 * backgroundBrush.Opacity;
            if (effectiveAlpha > 0)
                background = Blend(backgroundBrush.Color, background, effectiveAlpha);
        }

        if (node is TextBlock { Text.Length: > 0 } textBlock && textBlock.Foreground is SolidColorBrush foregroundBrush)
        {
            var rgb = (foregroundBrush.Color.R, foregroundBrush.Color.G, foregroundBrush.Color.B);
            var ratio = ContrastRatio(foregroundBrush.Color, background);
            if (!palette.Contains(rgb) || ratio < 4.5)
            {
                violations.Add(
                    $"{viewLabel} > \"{textBlock.Text}\" fg=#{Hex(foregroundBrush.Color)} bg=#{Hex(background)} ratio={ratio:F2}" +
                    (palette.Contains(rgb) ? "" : " (not a theme palette color)"));
            }
        }

        var childCount = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < childCount; i++)
            WalkVisualTree(VisualTreeHelper.GetChild(node, i), background, viewLabel, palette, violations);
    }

    // Shapes (Path/Rectangle/Ellipse) are deliberately not inspected here: a progress bar's fill
    // legitimately sits on a track color this walk never resolves, and resize-grip glyphs carry a
    // system default fill. Only Panel/Border/Control expose a Background property, and they expose it
    // under three unrelated declarations of the same name rather than one shared base member.
    //
    // CheckBox/RadioButton are a real exception, found empirically: neither gets an app-defined
    // Template in this project (only Foreground/Margin are set), so each keeps WPF's own default
    // ToggleButton chrome, where Background paints the small check/radio glyph itself, not a
    // full-bleed wallpaper behind the whole control - unlike every button style here, whose Template
    // wraps its entire bounds in a Border fed from that same Background. Tracking it as if it were
    // the label's real background produced exactly the false positive this walk exists to avoid: the
    // glyph's own near-white default fill, reported as the background behind readable theme-colored
    // text several logical layers away from it.
    private static Brush? TryGetBackgroundBrush(DependencyObject node) => node switch
    {
        Panel panel => panel.Background,
        Border border => border.Background,
        CheckBox or RadioButton => null,
        Control control => control.Background,
        _ => null,
    };

    private static Color Blend(Color foreground, Color background, double alpha)
    {
        byte Mix(byte fg, byte bg) => (byte)Math.Round(fg * alpha + bg * (1 - alpha));
        return Color.FromRgb(Mix(foreground.R, background.R), Mix(foreground.G, background.G), Mix(foreground.B, background.B));
    }

    private static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    // Same WCAG 2.x relative-luminance/contrast formula as ThemeContrastTests - duplicated rather
    // than shared, since it is ten lines of pure math and the two test classes check different things
    // (static token pairs there, real rendered elements here).
    private static double RelativeLuminance(Color c)
    {
        double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var (l1, l2) = (RelativeLuminance(a), RelativeLuminance(b));
        var (lighter, darker) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
