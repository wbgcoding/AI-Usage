using System.Text.RegularExpressions;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;

namespace AiUsage.Tests;

public class WindowZoomTests
{
    // Content measured at 100 %: Full 910, Mini 260 (the same model the density tests use).
    private static double Measured(TileDensity density) => density == TileDensity.Full ? 910 : 260;

    [Theory]
    [InlineData(90, 90)]
    [InlineData(100, 100)]
    [InlineData(125, 125)]
    [InlineData(150, 150)]
    [InlineData(110, 100)]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    [InlineData(200, 100)]
    public void Only_the_offered_zoom_steps_are_kept(int stored, int expected) =>
        Assert.Equal(expected, WindowZoom.Normalize(stored));

    [Fact]
    public void The_factor_is_the_percent_over_a_hundred() =>
        Assert.Equal(1.25, WindowZoom.Factor(125));

    [Fact]
    public void The_widget_is_not_zoomed_by_default() =>
        Assert.Equal(100, new AppSettings().ZoomPercent);

    [Theory]
    [InlineData(1.0, 1000, TileDensity.Full)]
    [InlineData(1.5, 1000, TileDensity.Mini)]
    [InlineData(1.5, 1365, TileDensity.Full)]
    [InlineData(1.5, 1363, TileDensity.Mini)]
    [InlineData(0.9, 820, TileDensity.Full)]
    [InlineData(0.9, 817, TileDensity.Mini)]
    public void The_density_threshold_grows_and_shrinks_with_the_zoom(double zoom, double available, TileDensity expected) =>
        Assert.Equal(expected, TileDensitySelector.SelectFitting(Measured, available, manualOverride: null, zoom));

    [Fact]
    public void A_hand_chosen_density_ignores_the_zoom() =>
        Assert.Equal(TileDensity.Full, TileDensitySelector.SelectFitting(Measured, 100, TileDensity.Full, zoom: 1.5));

    [Fact]
    public void The_first_estimate_of_a_tile_follows_the_zoom()
    {
        Assert.Equal(300, TileDensitySelector.HeightFor(TileDensity.Full, 1.5));
        Assert.Equal(200, TileDensitySelector.HeightFor(TileDensity.Full));
    }

    [Theory]
    [InlineData(1.0, 300)]
    [InlineData(1.5, 450)]
    [InlineData(0.9, 270)]
    public void The_narrowest_window_follows_the_zoom(double zoom, double expected) =>
        Assert.Equal(expected, WindowPlacementService.MinWidthFor(zoom), precision: 6);

    [Fact]
    public void The_floor_of_the_smallest_zoom_holds_for_remembered_sizes() =>
        Assert.Equal(270, WindowPlacementService.MinWidthFor(WindowZoom.MinFactor), precision: 6);

    [Fact]
    public void A_stacked_widget_at_150_percent_is_one_and_a_half_times_as_wide()
    {
        var at100 = WindowPlacementService.ResolveContentWidth(3, horizontal: false, workAreaWidth: 1920);
        var at150 = WindowPlacementService.ResolveContentWidth(3, horizontal: false, workAreaWidth: 1920, zoom: 1.5);

        Assert.Equal(at100 * 1.5, at150, precision: 6);
    }

    [Fact]
    public void On_a_narrow_work_area_the_width_stops_at_the_zoomed_minimum()
    {
        var width = WindowPlacementService.ResolveContentWidth(4, horizontal: true, workAreaWidth: 300, zoom: 1.5);

        Assert.Equal(450, width, precision: 6);
    }

    [Fact]
    public void The_height_the_content_takes_scales_with_the_zoom()
    {
        var at100 = WindowPlacementService.ResolveMeasuredDensityHeight(Measured, chromeHeight: 40, manualOverride: null, workAreaHeight: 2000);
        var at150 = WindowPlacementService.ResolveMeasuredDensityHeight(Measured, chromeHeight: 60, manualOverride: null, workAreaHeight: 2000, zoom: 1.5);

        Assert.Equal(950, at100.Height);
        Assert.Equal(TileDensity.Full, at150.Density);
        Assert.Equal(60 + 910 * 1.5, at150.Height);
    }

    [Fact]
    public void A_zoomed_window_that_no_longer_fits_the_work_area_drops_to_mini_and_is_capped()
    {
        var resolved = WindowPlacementService.ResolveMeasuredDensityHeight(Measured, chromeHeight: 60, manualOverride: null, workAreaHeight: 1000, zoom: 1.5);

        Assert.Equal(TileDensity.Mini, resolved.Density);
        Assert.Equal(60 + 260 * 1.5, resolved.Height);
    }

    [Fact]
    public void Chrome_snap_and_content_clamp_all_follow_the_zoom()
    {
        Assert.Equal(WindowPlacementService.DefaultChromeHeight * 1.25, WindowPlacementService.ChromeHeightFor(1.25));
        Assert.Equal(18, WindowPlacementService.SnapToleranceFor(1.5));
        Assert.Equal(132, WindowPlacementService.MinHeightFor(collapsed: false, zoom: 1.5));
        Assert.Equal(0, WindowPlacementService.MinHeightFor(collapsed: true, zoom: 1.5));
        // Content 100 plus title bar 32 and chrome 8, all drawn at 150 %.
        Assert.Equal(210, WindowPlacementService.ClampToContentHeight(desiredHeight: 900, contentDesiredHeight: 100, zoom: 1.5));
    }

    [Fact]
    public void A_snap_at_150_percent_reaches_as_far_as_the_scaled_tolerance()
    {
        var area = new MonitorArea("D", 0, 0, 1920, 1080);
        var window = new WindowRect(17, 100, 400, 300);

        var near = WindowPlacementService.SnapToEdges(window, area, WindowPlacementService.SnapToleranceFor(1.5));
        var far = WindowPlacementService.SnapToEdges(window, area, WindowPlacementService.SnapToleranceFor(1.0));

        Assert.Equal(0, near.Left);
        Assert.Equal(17, far.Left);
    }

    [Fact]
    public void Changing_the_zoom_rescales_both_remembered_layouts_and_keeps_the_position()
    {
        var window = new WindowSettings { Left = 40, Top = 60 };
        window.Vertical.Width = 400;
        window.Vertical.Height = 600;
        window.Horizontal.Width = 1000;
        window.Horizontal.Height = null;

        WindowPlacementService.ScaleRememberedSizes(window, 1.5);

        Assert.Equal(600, window.Vertical.Width, precision: 6);
        Assert.Equal(900, window.Vertical.Height!.Value, precision: 6);
        Assert.Equal(1500, window.Horizontal.Width, precision: 6);
        Assert.Null(window.Horizontal.Height);
        Assert.Equal(40, window.Left);
        Assert.Equal(60, window.Top);
    }

    [Fact]
    public void The_zoom_survives_a_save_and_load()
    {
        using var directory = TestPaths.CreateDisposableDirectory("ai-usage-zoom");
        using var store = new SettingsStore(directory);
        store.SaveNow(new AppSettings { WindowLayer = WindowLayers.Normal, ZoomPercent = 125 });
        using var reload = new SettingsStore(directory);

        Assert.Equal(125, reload.Load().ZoomPercent);
    }

    [Fact]
    public void A_size_remembered_at_90_percent_is_not_raised_to_the_100_percent_floor()
    {
        using var directory = TestPaths.CreateDisposableDirectory("ai-usage-zoom");
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"schemaVersion":1,"window":{"vertical":{"width":280,"height":90}}}""");
        using var store = new SettingsStore(directory);

        var loaded = store.Load();

        Assert.Equal(280, loaded.Window.Vertical.Width);
        Assert.Equal(90, loaded.Window.Vertical.Height);
    }

    [Fact]
    public void The_main_window_draws_its_chrome_through_one_layout_transform_and_the_grips_stay_outside_it()
    {
        var root = RepoRoot.Find();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "AiUsage", "Views", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "AiUsage", "Views", "MainWindow.xaml.cs"));

        Assert.Matches(@"<Border x:Name=""ChromeBorder""", xaml);
        Assert.DoesNotContain("LayoutTransform", xaml, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(code, @"ChromeBorder\.LayoutTransform = ZoomTransform\(").Count);
        // The resize grips are children of the root grid beside the chrome, so their drag distances stay in window units.
        Assert.Matches(@"</Border>\s*<!--[\s\S]*?-->\s*<Thumb|</Border>\s*<Thumb", xaml);
    }
}
