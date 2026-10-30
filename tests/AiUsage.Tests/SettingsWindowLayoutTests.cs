using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Layout checks on the real <see cref="SettingsWindow"/>, built and measured off screen
/// on an STA thread (never shown).</summary>
[Collection(SharedStateTestsCollection.Name)]
public class SettingsWindowLayoutTests
{
    [Fact]
    public void Scrolling_the_content_leaves_the_category_list_where_it_is()
    {
        RunOnSta(window =>
        {
            var root = (UIElement)window.Content;
            root.Measure(new Size(560, 300));
            root.Arrange(new Rect(0, 0, 560, 300));
            root.UpdateLayout();

            var before = window.CategoryList.TranslatePoint(new Point(0, 0), root);
            Assert.True(window.ContentScroll.ScrollableHeight > 0, "the content must be taller than the viewport for this check");

            window.ContentScroll.ScrollToEnd();
            root.UpdateLayout();

            Assert.True(window.ContentScroll.VerticalOffset > 0);
            var after = window.CategoryList.TranslatePoint(new Point(0, 0), root);
            Assert.Equal(before, after);
        });
    }

    [Fact]
    public void Escape_in_the_hotkey_box_closes_the_window_without_reaching_the_box()
    {
        RunOnSta(window =>
        {
            Lay(window, 560);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            using var source = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "escape-test", IntPtr.Zero);
            var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
            };

            window.HotkeyCaptureBox.RaiseEvent(args);

            Assert.True(closed);
            Assert.True(args.Handled);
            Assert.False(((SettingsViewModel)window.DataContext).Main.HotkeyNeedsModifier);
        });
    }

    private static UIElement Lay(SettingsWindow window, double width)
    {
        var root = (UIElement)window.Content;
        root.Measure(new Size(width, 900));
        root.Arrange(new Rect(0, 0, width, 900));
        root.UpdateLayout();
        return root;
    }

    [Fact]
    public void Theme_size_and_Mica_share_one_row()
    {
        RunOnSta(window =>
        {
            // Mica is hidden for the test theme; show it so its place can be measured.
            window.MicaCheck.Visibility = Visibility.Visible;
            var root = Lay(window, 560);

            var themes = window.ThemeSwatches.TranslatePoint(new Point(0, 0), root);
            var zoom = window.ZoomCombo.TranslatePoint(new Point(0, 0), root);
            var mica = window.MicaCheck.TranslatePoint(new Point(0, 0), root);
            Assert.True(zoom.X >= themes.X + window.ThemeSwatches.ActualWidth, "The size picker must sit right of the theme swatches");
            Assert.True(mica.X >= zoom.X + window.ZoomCombo.ActualWidth, "Mica must sit right of the size picker");
            Assert.True(zoom.Y < themes.Y + window.ThemeSwatches.ActualHeight, "The size picker must share the theme row");
            Assert.True(mica.Y < zoom.Y + window.ZoomCombo.ActualHeight && mica.Y + window.MicaCheck.ActualHeight > zoom.Y,
                "Mica must sit level with the size picker");
        });
    }

    [Fact]
    public void At_the_default_width_all_theme_swatches_fit_one_line()
    {
        RunOnSta(window =>
        {
            window.MicaCheck.Visibility = Visibility.Visible;
            var root = Lay(window, 560);
            var first = (FrameworkElement)window.ThemeSwatches.ItemContainerGenerator.ContainerFromIndex(0);
            var last = (FrameworkElement)window.ThemeSwatches.ItemContainerGenerator.ContainerFromIndex(window.ThemeSwatches.Items.Count - 1);
            var widths = string.Join("/", Enumerable.Range(0, window.ThemeSwatches.Items.Count).Select(i => ((FrameworkElement)window.ThemeSwatches.ItemContainerGenerator.ContainerFromIndex(i)).ActualWidth.ToString("0")));
            Assert.True(Math.Abs(first.TranslatePoint(new Point(0, 0), root).Y - last.TranslatePoint(new Point(0, 0), root).Y) < 1, $"panel {window.ThemeSwatches.ActualWidth:0}, swatches {widths}, zoom {window.ZoomCombo.ActualWidth:0}, mica {window.MicaCheck.ActualWidth:0}");
        });
    }

    [Fact]
    public void Without_Mica_the_size_picker_keeps_its_place()
    {
        RunOnSta(window =>
        {
            var root = Lay(window, 560);
            var shown = window.ZoomCombo.TranslatePoint(new Point(0, 0), root);

            window.MicaCheck.Visibility = Visibility.Visible;
            var root2 = Lay(window, 560);

            Assert.Equal(shown, window.ZoomCombo.TranslatePoint(new Point(0, 0), root2));
        });
    }

    [Fact]
    public void Order_is_as_wide_as_Arrangement()
    {
        RunOnSta(window =>
        {
            var root = Lay(window, 560);
            var layoutRight = window.LayoutCombo.TranslatePoint(new Point(window.LayoutCombo.ActualWidth, 0), root).X;
            var orderRight = window.TileOrderCombo.TranslatePoint(new Point(window.TileOrderCombo.ActualWidth, 0), root).X;
            var layoutLeft = window.LayoutCombo.TranslatePoint(new Point(0, 0), root).X;
            var orderLeft = window.TileOrderCombo.TranslatePoint(new Point(0, 0), root).X;

            Assert.Equal(layoutRight, orderRight, 0.5);
            Assert.Equal(layoutLeft, orderLeft, 0.5);
        });
    }

    [Fact]
    public void The_fullscreen_label_stays_on_one_line_at_the_default_width_and_wraps_without_clipping_at_the_minimum()
    {
        RunOnSta(window =>
        {
            var root = Lay(window, 560);
            var label = (TextBlock)window.HideOnFullscreenCheck.Content;
            var oneLine = label.FontFamily.LineSpacing * label.FontSize;
            Assert.True(label.ActualHeight <= oneLine * 1.3, $"label height {label.ActualHeight} vs one line {oneLine}");

            root = Lay(window, 480);
            var viewport = window.ContentScroll.ViewportWidth;
            foreach (var box in new[] { window.HideOnFullscreenCheck, window.ClickThroughCheck })
            {
                var right = box.TranslatePoint(new Point(box.ActualWidth, 0), window.ContentScroll).X;
                Assert.True(right <= viewport + 0.5, $"{box.Name} reaches {right} but the viewport is {viewport}");
                Assert.True(box.ActualWidth >= 90, $"{box.Name} is only {box.ActualWidth} wide");
            }
        });
    }

    private static void RunOnSta(Action<SettingsWindow> check)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
                var app = new AiUsage.App();
                app.InitializeComponent();
                try
                {
                    ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);

                    var settingsStore = new SettingsStore(TestPaths.CreateDirectory("settings-window-layout"));
                    var settings = new AppSettings();
                    var mainViewModel = new MainViewModel(settingsStore, settings);
                    var settingsViewModel = new SettingsViewModel(mainViewModel, settings, settingsStore,
                        isAutostartEnabled: () => false,
                        enableAutostart: _ => true,
                        disableAutostart: () => true,
                        applyLanguage: _ => { },
                        loadThemeDictionary: LoadFromProductionAssembly,
                        fetchLatestRelease: _ => Task.FromResult<UpdateCheck.Release?>(null));

                    check(new SettingsWindow(settingsViewModel));
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
            throw new TimeoutException("SettingsWindow layout walk did not finish");
        if (failure is not null)
            throw new InvalidOperationException("SettingsWindow layout walk failed.", failure);
    }

    private static ResourceDictionary LoadFromProductionAssembly(Uri relativeUri) =>
        new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relativeUri.OriginalString, UriKind.Absolute) };

    private static void ResetApplicationCurrent() =>
        typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
}
