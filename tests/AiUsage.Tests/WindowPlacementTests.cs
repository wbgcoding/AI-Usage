using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

public class WindowPlacementTests
{
    [Fact]
    public void A_window_centred_on_an_owner_at_the_right_edge_is_pulled_back_into_the_work_area()
    {
        var area = new MonitorArea("DISPLAY1", 0, 0, 1920, 1040);
        var owner = new WindowRect(1720, 0, 200, 100);
        var centred = new WindowRect(owner.Left + owner.Width / 2 - 400, owner.Top + owner.Height / 2 - 300, 800, 600);

        var placed = WindowPlacementService.ClampIntoArea(centred, area);

        Assert.True(placed.Left + placed.Width <= 1920);
        Assert.True(placed.Top >= 0);
        Assert.Equal(800, placed.Width);
        Assert.Equal(600, placed.Height);
    }

    [Fact]
    public void A_window_already_inside_the_work_area_keeps_its_position()
    {
        var area = new MonitorArea("DISPLAY1", 100, 50, 1920, 1040);
        var inside = new WindowRect(400, 300, 800, 600);

        Assert.Equal(inside, WindowPlacementService.ClampIntoArea(inside, area));
    }

    [Fact]
    public void A_remembered_stats_size_survives_a_fresh_instance_built_on_the_same_settings_object()
    {
        var settings = new AppSettings();
        var first = new WindowPlacementService(settings);
        first.RememberedStatsWindowSize = (900, 650);

        // A fresh instance, not the one that wrote it - the settings object is what stands in for a
        // restart (it is what actually gets serialized to and reloaded from settings.json).
        var second = new WindowPlacementService(settings);

        Assert.Equal((900.0, 650.0), second.RememberedStatsWindowSize);
    }

    [Fact]
    public void An_instance_with_nothing_remembered_yet_reports_null()
    {
        var settings = new AppSettings();
        var service = new WindowPlacementService(settings);

        Assert.Null(service.RememberedStatsWindowSize);
    }

    [Fact]
    public void A_remembered_stats_size_below_the_windows_own_minimum_is_clamped_up_to_it()
    {
        var settings = new AppSettings();
        var service = new WindowPlacementService(settings);

        service.RememberedStatsWindowSize = (100, 50);

        Assert.Equal((SettingsRanges.MinStatsWindowWidth, SettingsRanges.MinStatsWindowHeight), service.RememberedStatsWindowSize);
    }

    [Fact]
    public void Clearing_the_remembered_stats_size_writes_back_the_zero_sentinel()
    {
        var settings = new AppSettings();
        var service = new WindowPlacementService(settings);
        service.RememberedStatsWindowSize = (900, 650);

        service.RememberedStatsWindowSize = null;

        Assert.Null(service.RememberedStatsWindowSize);
        Assert.Equal(0, settings.Window.StatsWidth);
        Assert.Equal(0, settings.Window.StatsHeight);
    }
    private static readonly MonitorArea Primary = new("\\\\.\\DISPLAY1", Left: 0, Top: 0, Width: 1920, Height: 1040);
    private static readonly MonitorArea Secondary = new("\\\\.\\DISPLAY2", Left: 1920, Top: 0, Width: 1920, Height: 1040);

    [Fact]
    public void A_rectangle_fully_inside_a_monitor_is_returned_unchanged()
    {
        var desired = new WindowRect(Left: 100, Top: 100, Width: 340, Height: 400);

        var resolved = WindowPlacementService.ResolvePosition(desired, [Primary, Secondary], Primary);

        Assert.Equal(desired, resolved);
    }

    [Fact]
    public void A_rectangle_partially_outside_every_monitor_is_clamped_into_the_overlapping_one()
    {
        // Mostly on Primary but its right edge hangs 100px past DISPLAY1's boundary at x=1920.
        var desired = new WindowRect(Left: 1700, Top: 100, Width: 340, Height: 400);

        var resolved = WindowPlacementService.ResolvePosition(desired, [Primary, Secondary], Primary);

        Assert.Equal(Primary.Right - 340, resolved.Left);
        Assert.Equal(100, resolved.Top);
        Assert.Equal(340, resolved.Width);
        Assert.Equal(400, resolved.Height);
    }

    [Fact]
    public void A_rectangle_fully_outside_every_monitor_is_centered_on_the_primary_monitor()
    {
        // The remembered monitor was unplugged; its old coordinates now land far off any screen.
        var desired = new WindowRect(Left: 5000, Top: 5000, Width: 340, Height: 400);

        var resolved = WindowPlacementService.ResolvePosition(desired, [Primary], Primary);

        Assert.Equal((1920 - 340) / 2.0, resolved.Left);
        Assert.Equal((1040 - 400) / 2.0, resolved.Top);
    }

    [Fact]
    public void A_rectangle_sitting_entirely_on_a_monitor_no_longer_in_the_list_lands_inside_the_first_one()
    {
        // The window was last placed on Secondary, but only Primary remains at startup now.
        var desired = new WindowRect(Left: 2200, Top: 200, Width: 340, Height: 400);

        var resolved = WindowPlacementService.ResolvePosition(desired, [Primary], Primary);

        Assert.True(resolved.Left >= Primary.Left && resolved.Left + resolved.Width <= Primary.Right);
        Assert.True(resolved.Top >= Primary.Top && resolved.Top + resolved.Height <= Primary.Bottom);
    }

    [Fact]
    public void The_cursor_monitor_reset_centers_on_the_monitor_the_cursor_is_on()
    {
        var resolved = WindowPlacementService.CenterOnCursorMonitor(
            cursorX: 2500, cursorY: 300, width: 340, height: 400, [Primary, Secondary], Primary);

        Assert.Equal(Secondary.Left + (Secondary.Width - 340) / 2, resolved.Left);
        Assert.Equal(Secondary.Top + (Secondary.Height - 400) / 2, resolved.Top);
    }

    [Fact]
    public void The_cursor_monitor_reset_falls_back_to_the_primary_monitor_when_the_cursor_is_on_none()
    {
        var resolved = WindowPlacementService.CenterOnCursorMonitor(
            cursorX: -500, cursorY: -500, width: 340, height: 400, [Primary, Secondary], Primary);

        Assert.Equal(Primary.Left + (Primary.Width - 340) / 2, resolved.Left);
        Assert.Equal(Primary.Top + (Primary.Height - 400) / 2, resolved.Top);
    }

    [Fact]
    public void Automatic_height_that_fits_at_the_preferred_density_keeps_that_density()
    {
        var resolution = WindowPlacementService.ResolveAutomaticHeight(visibleTileCount: 2, TileDensity.Full, workAreaHeight: 1040);

        Assert.Equal(TileDensity.Full, resolution.Density);
        Assert.False(resolution.NeedsScroll);
        Assert.Equal(32 + 8 + 2 * 200, resolution.WindowHeight);
    }

    [Fact]
    public void Automatic_height_that_overflows_steps_the_density_down_before_capping()
    {
        // 6 tiles at Full (1240px) do not fit a 700px work area, but Mini (304px) does.
        var resolution = WindowPlacementService.ResolveAutomaticHeight(visibleTileCount: 6, TileDensity.Full, workAreaHeight: 700);

        Assert.Equal(TileDensity.Mini, resolution.Density);
        Assert.False(resolution.NeedsScroll);
        Assert.Equal(32 + 8 + 6 * 44, resolution.WindowHeight);
    }

    [Fact]
    public void Automatic_height_that_overflows_even_at_mini_density_is_capped_and_needs_scroll()
    {
        // 20 tiles never fit any density on a 700px work area.
        var resolution = WindowPlacementService.ResolveAutomaticHeight(visibleTileCount: 20, TileDensity.Full, workAreaHeight: 700);

        Assert.Equal(TileDensity.Mini, resolution.Density);
        Assert.True(resolution.NeedsScroll);
        Assert.Equal(700, resolution.WindowHeight);
    }

    // The window height in automatic mode comes from the tiles' real measured height per stage, so a
    // tile taller than any per-tile estimate still fits: these heights are what a measurement
    // returns, deliberately far from the 200/44 model.
    private static double Measured(TileDensity density) => density switch
    {
        TileDensity.Full => 910,
        _ => 260,
    };

    [Fact]
    public void Measured_content_that_fits_at_full_takes_full_and_exactly_its_own_height()
    {
        var resolved = WindowPlacementService.ResolveMeasuredDensityHeight(Measured, chromeHeight: 56, manualOverride: null, workAreaHeight: 1032);

        Assert.Equal(TileDensity.Full, resolved.Density);
        Assert.Equal(966, resolved.Height);
    }

    [Fact]
    public void Measured_content_too_tall_at_full_steps_down_to_the_largest_stage_that_fits()
    {
        var resolved = WindowPlacementService.ResolveMeasuredDensityHeight(Measured, chromeHeight: 56, manualOverride: null, workAreaHeight: 700);

        Assert.Equal(TileDensity.Mini, resolved.Density);
        Assert.Equal(316, resolved.Height);
    }

    [Fact]
    public void Measured_content_too_tall_even_at_mini_is_capped_to_the_work_area()
    {
        var resolved = WindowPlacementService.ResolveMeasuredDensityHeight(Measured, chromeHeight: 56, manualOverride: null, workAreaHeight: 300);

        Assert.Equal(TileDensity.Mini, resolved.Density);
        Assert.Equal(300, resolved.Height);
    }

    [Fact]
    public void A_fractional_measured_height_rounds_up_so_the_content_never_needs_a_scroll_bar()
    {
        var resolved = WindowPlacementService.ResolveMeasuredDensityHeight(_ => 255.03, chromeHeight: 34, manualOverride: null, workAreaHeight: 720);

        Assert.Equal(290, resolved.Height);
    }

    [Fact]
    public void A_hand_chosen_stage_keeps_its_stage_and_takes_its_own_measured_height()
    {
        var resolved = WindowPlacementService.ResolveMeasuredDensityHeight(Measured, chromeHeight: 56, manualOverride: TileDensity.Mini, workAreaHeight: 1032);

        Assert.Equal(TileDensity.Mini, resolved.Density);
        Assert.Equal(316, resolved.Height);
    }

    [Fact]
    public void A_stored_manual_height_larger_than_the_new_screen_is_capped_to_the_work_area()
    {
        var capped = WindowPlacementService.ResolveManualHeight(storedHeight: 1200, workAreaHeight: 800);

        Assert.Equal(800, capped);
    }

    [Fact]
    public void A_stored_manual_height_that_still_fits_is_left_unchanged()
    {
        var capped = WindowPlacementService.ResolveManualHeight(storedHeight: 500, workAreaHeight: 800);

        Assert.Equal(500, capped);
    }

    // A window pulled small keeps growing right back up: the ceiling this recomputes on every drag
    // is always the current work area, never whatever Window.MaxHeight happened to be pinned to the
    // last time the height was automatic (a small ceiling from back then used to leave the window
    // stuck a few pixels below the mouse instead of following it).
    [Fact]
    public void A_downward_drag_grows_the_window_by_the_full_delta_when_only_the_work_area_binds()
    {
        var grown = WindowPlacementService.GrowManualHeight(
            currentHeight: 400, delta: 300, minHeight: 88, workAreaHeight: 1000, contentDesiredHeight: 2000);

        Assert.Equal(700, grown);
    }

    [Fact]
    public void A_downward_drag_still_stops_at_the_work_area_even_with_room_left_in_the_content()
    {
        var grown = WindowPlacementService.GrowManualHeight(
            currentHeight: 900, delta: 300, minHeight: 88, workAreaHeight: 1000, contentDesiredHeight: 2000);

        Assert.Equal(1000, grown);
    }

    [Fact]
    public void A_downward_drag_still_stops_at_the_content_s_own_height_within_the_work_area()
    {
        var grown = WindowPlacementService.GrowManualHeight(
            currentHeight: 400, delta: 300, minHeight: 88, workAreaHeight: 1000, contentDesiredHeight: 100);

        Assert.Equal(140, grown); // 100 content + 32 title bar + 8 chrome, from ClampToContentHeight
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Stacked_tiles_give_the_window_one_tile_width_whatever_the_count(int visibleTiles)
    {
        var width = WindowPlacementService.ResolveContentWidth(visibleTiles, horizontal: false, workAreaWidth: 1920);

        Assert.Equal(
            WindowPlacementService.TileWidth + WindowPlacementService.ChromeWidth + 2 * WindowPlacementService.ShadowMargin,
            width);
    }

    [Theory]
    [InlineData(1, 374)]
    [InlineData(2, 718)]
    [InlineData(3, 1062)]
    [InlineData(4, 1406)]
    public void A_row_of_tiles_gets_the_full_width_of_every_tile_plus_the_gaps(int visibleTiles, double expected)
    {
        var width = WindowPlacementService.ResolveContentWidth(visibleTiles, horizontal: true, workAreaWidth: 1920);

        Assert.Equal(expected, width);
    }

    [Fact]
    public void A_row_wider_than_the_screen_is_capped_short_of_the_edge()
    {
        var width = WindowPlacementService.ResolveContentWidth(visibleTiles: 4, horizontal: true, workAreaWidth: 1366);

        Assert.Equal(1366 - WindowPlacementService.EdgeMargin, width);
    }

    [Fact]
    public void No_visible_tile_still_leaves_a_window_wide_enough_for_the_empty_state()
    {
        var width = WindowPlacementService.ResolveContentWidth(visibleTiles: 0, horizontal: false, workAreaWidth: 1920);

        Assert.Equal(
            WindowPlacementService.TileWidth + WindowPlacementService.ChromeWidth + 2 * WindowPlacementService.ShadowMargin,
            width);
    }

    [Fact]
    public void A_work_area_narrower_than_the_minimum_never_pushes_the_window_below_it()
    {
        var width = WindowPlacementService.ResolveContentWidth(visibleTiles: 1, horizontal: false, workAreaWidth: 200);

        Assert.Equal(WindowPlacementService.MinWindowWidth, width);
    }

    [Fact]
    public void TheRestoredMinHeightFloorIs88()
    {
        Assert.Equal(88, WindowPlacementService.MinWindowHeight);
    }

    [Fact]
    public void ACollapsedWindowHasNoMinHeightFloor()
    {
        Assert.Equal(0, WindowPlacementService.MinHeightFor(collapsed: true));
    }

    [Fact]
    public void ARestoredWindowGetsTheNormalMinHeightFloorBack()
    {
        Assert.Equal(WindowPlacementService.MinWindowHeight, WindowPlacementService.MinHeightFor(collapsed: false));
    }

    [Fact]
    public void RestoringAnAutomaticHeightWindowLeavesItToSizeToContent()
    {
        Assert.Null(WindowPlacementService.RestoreHeight(uncollapsedHeight: 420, manualHeight: null, automatic: true));
    }

    // The null RestoreHeight() returns for the automatic case is not the end of the story: the
    // caller (MainWindow.Restore()) falls back to ResolveAutomaticHeight() for a concrete height
    // instead of leaving it to SizeToContent, since the hidden ScrollViewer's own desired height
    // while collapsed is not the tiles' real height. This is that fallback, proven from the tile
    // count rather than null.
    [Fact]
    public void TheAutomaticRestoreFallbackComputesAHeightFromTheTileCountInsteadOfNull()
    {
        var restoreHeight = WindowPlacementService.RestoreHeight(uncollapsedHeight: 420, manualHeight: null, automatic: true);
        var resolved = restoreHeight ?? WindowPlacementService.ResolveAutomaticHeight(
            visibleTileCount: 4, TileDensity.Full, workAreaHeight: 10_000).WindowHeight;

        Assert.Null(restoreHeight);
        Assert.Equal(32 + 8 + 4 * TileDensitySelector.HeightFor(TileDensity.Full), resolved);
    }

    [Fact]
    public void RestoringAManualHeightWindowUsesTheHeightCapturedBeforeCollapsing()
    {
        Assert.Equal(420, WindowPlacementService.RestoreHeight(uncollapsedHeight: 420, manualHeight: 300, automatic: false));
    }

    [Fact]
    public void RestoringAManualHeightWindowFallsBackToTheRememberedHeightWhenNoneWasCaptured()
    {
        Assert.Equal(300, WindowPlacementService.RestoreHeight(uncollapsedHeight: 0, manualHeight: 300, automatic: false));
    }

    [Fact]
    public void ADesiredHeightBelowTheContentIsLeftAlone()
    {
        Assert.Equal(300, WindowPlacementService.ClampToContentHeight(desiredHeight: 300, contentDesiredHeight: 400));
    }

    [Fact]
    public void ADesiredHeightExactlyAtTheContentIsLeftAlone()
    {
        // 32 + 8: the title bar and chrome heights ClampToContentHeight adds on top of the
        // content, same constants ResolveAutomaticHeight's own tests above use.
        var atContent = 400 + 32 + 8;
        Assert.Equal(atContent, WindowPlacementService.ClampToContentHeight(desiredHeight: atContent, contentDesiredHeight: 400));
    }

    [Fact]
    public void ADesiredHeightAboveTheContentIsClampedDownToIt()
    {
        var atContent = 400 + 32 + 8;
        Assert.Equal(atContent, WindowPlacementService.ClampToContentHeight(desiredHeight: 900, contentDesiredHeight: 400));
    }

    // A window that sizes itself to its content has to stay inside the work area even when a tile
    // turns out taller than the height model predicted.
    private static readonly MonitorArea Screen = new(@"\\.\DISPLAY1", 0, 0, 1920, 1000);

    [Fact]
    public void AWindowThatFitsKeepsItsPosition()
    {
        var (top, maxHeight) = WindowPlacementService.FitVertically(top: 100, contentHeight: 400, Screen);

        Assert.Equal(100, top);
        Assert.Equal(1000, maxHeight);
    }

    [Fact]
    public void AWindowHangingOffTheBottomSlidesUpInsteadOfBeingCut()
    {
        var (top, _) = WindowPlacementService.FitVertically(top: 800, contentHeight: 600, Screen);

        Assert.Equal(400, top);
    }

    [Fact]
    public void ContentTallerThanTheScreenStartsAtTheTopAndIsCappedThere()
    {
        var (top, maxHeight) = WindowPlacementService.FitVertically(top: 300, contentHeight: 1600, Screen);

        Assert.Equal(0, top);
        Assert.Equal(1000, maxHeight);
    }

    [Fact]
    public void AWindowAboveTheWorkAreaIsPushedBackOntoIt()
    {
        var taskbarOnTop = new MonitorArea(@"\\.\DISPLAY1", 0, 60, 1920, 940);

        var (top, _) = WindowPlacementService.FitVertically(top: -20, contentHeight: 300, taskbarOnTop);

        Assert.Equal(60, top);
    }

    [Fact]
    public void AWindowWithinToleranceOfOneEdgeSnapsToIt()
    {
        var window = new WindowRect(Left: 6, Top: 300, Width: 340, Height: 400);

        var snapped = WindowPlacementService.SnapToEdges(window, Screen);

        Assert.Equal(0, snapped.Left);
        Assert.Equal(300, snapped.Top);
        Assert.Equal(340, snapped.Width);
        Assert.Equal(400, snapped.Height);
    }

    [Fact]
    public void AWindowWithinToleranceOfTwoEdgesAtOnceSnapsIntoTheCorner()
    {
        var window = new WindowRect(Left: 5, Top: 4, Width: 340, Height: 400);

        var snapped = WindowPlacementService.SnapToEdges(window, Screen);

        Assert.Equal(0, snapped.Left);
        Assert.Equal(0, snapped.Top);
    }

    [Fact]
    public void AWindowJustOutsideToleranceIsLeftUnchanged()
    {
        var window = new WindowRect(Left: 13, Top: 300, Width: 340, Height: 400);

        var snapped = WindowPlacementService.SnapToEdges(window, Screen);

        Assert.Equal(window, snapped);
    }

    [Fact]
    public void AWindowWiderThanTheAreaSnapsItsLeftEdgeWithoutTheRightEdgeDraggingItBack()
    {
        var window = new WindowRect(Left: 5, Top: 300, Width: 2000, Height: 400);

        var snapped = WindowPlacementService.SnapToEdges(window, Screen);

        Assert.Equal(0, snapped.Left);
        Assert.Equal(2000, snapped.Width);
    }

    [Fact]
    public void SnapToHalf_left_takes_the_left_half_of_the_area()
    {
        var rect = WindowPlacementService.SnapToHalf(Screen, SnapDirection.Left);

        Assert.Equal(new WindowRect(0, 0, 960, 1000), rect);
    }

    [Fact]
    public void SnapToHalf_right_takes_the_right_half_of_the_area()
    {
        var rect = WindowPlacementService.SnapToHalf(Screen, SnapDirection.Right);

        Assert.Equal(new WindowRect(960, 0, 960, 1000), rect);
    }

    [Fact]
    public void SnapToHalf_up_takes_the_top_half_of_the_area()
    {
        var rect = WindowPlacementService.SnapToHalf(Screen, SnapDirection.Up);

        Assert.Equal(new WindowRect(0, 0, 1920, 500), rect);
    }

    [Fact]
    public void SnapToHalf_down_takes_the_bottom_half_of_the_area()
    {
        var rect = WindowPlacementService.SnapToHalf(Screen, SnapDirection.Down);

        Assert.Equal(new WindowRect(0, 500, 1920, 500), rect);
    }
}

/// <summary>
/// <see cref="WindowChromeNative.Bootstrap"/> is the one place every custom-chrome window applies
/// its opacity and rounded corners from, now that no window ever sets <c>AllowsTransparency</c>.
/// Built on a bare <c>Window</c> with no XAML/InitializeComponent, never <c>Show()</c>n - <see
/// cref="AccessibilityTests"/> and <see cref="RenderedTextContrastTests"/> document why a real,
/// XAML-loaded window needs an STA thread with a bootstrapped Application and can hang without one;
/// this stays off that path entirely, but still runs on a background STA thread with a short timeout
/// since constructing any <c>Window</c> at all needs a Dispatcher.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class WindowChromeNativeTests
{
    /// <summary>No window ever sets <c>AllowsTransparency</c> any more - opacity is a native, per-HWND
    /// alpha (<see cref="WindowOpacity"/>) applied after the window is shown, never a construction-time
    /// WPF property.</summary>
    [Fact]
    public void Bootstrap_never_touches_AllowsTransparency()
    {
        var stillDefault = RunOnStaThread(() =>
        {
            var window = new Window();
            WindowChromeNative.Bootstrap(window);
            return window.AllowsTransparency;
        });

        Assert.False(stillDefault);
    }

    /// <summary>The sign-in window hosts a native WebView2 child - unlike an <c>AllowsTransparency</c>
    /// layered window, the native, per-HWND alpha and DWM corner rounding <see
    /// cref="WindowChromeNative.Bootstrap"/> applies compose it fine, so this window goes through the
    /// same shared helper as every other one now.</summary>
    [Fact]
    public void The_sign_in_window_calls_the_shared_bootstrap_helper()
    {
        var source = File.ReadAllText(Path.Combine(FindSrcDir(), "Views", "SignInWindow.xaml.cs"));

        Assert.Contains("WindowChromeNative.Bootstrap(this)", source, StringComparison.Ordinal);
    }

    private static string FindSrcDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        var repoRoot = Path.GetDirectoryName(testsDir)!;
        return Path.Combine(repoRoot, "src", "AiUsage");
    }

    private static T RunOnStaThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = action();
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

        if (!worker.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Bootstrap did not finish.");

        if (failure is not null)
            throw new InvalidOperationException("Bootstrap check failed.", failure);

        return result!;
    }
}
