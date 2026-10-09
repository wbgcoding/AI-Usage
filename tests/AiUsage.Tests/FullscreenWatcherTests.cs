using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;

namespace AiUsage.Tests;

public class FullscreenWatcherTests
{
    private static readonly ScreenRect Monitor = new(0, 0, 2560, 1440);
    private const int AcceptsNotifications = 5;
    private const int RunningD3dFullScreen = 3;
    private const int PresentationMode = 4;
    private const int Busy = 2;

    [Fact]
    public void A_foreground_window_that_fills_the_monitor_is_full_screen()
    {
        Assert.True(FullscreenWatcher.IsFullscreen(false, "Chrome_WidgetWin_1", Monitor, Monitor, AcceptsNotifications));
    }

    [Fact]
    public void A_window_larger_than_the_monitor_still_covers_it()
    {
        var oversized = new ScreenRect(-8, -8, 2568, 1448);

        Assert.True(FullscreenWatcher.IsFullscreen(false, "Notepad", oversized, Monitor, AcceptsNotifications));
    }

    [Theory]
    [InlineData(0, 0, 2560, 1400)]
    [InlineData(0, 0, 2552, 1440)]
    [InlineData(8, 0, 2560, 1440)]
    [InlineData(0, 40, 2560, 1440)]
    public void A_window_that_leaves_any_edge_of_the_monitor_uncovered_is_not_full_screen(int left, int top, int right, int bottom)
    {
        Assert.False(FullscreenWatcher.IsFullscreen(false, "Notepad", new ScreenRect(left, top, right, bottom), Monitor, AcceptsNotifications));
    }

    [Fact]
    public void A_maximized_window_stops_at_the_taskbar()
    {
        var maximized = new ScreenRect(-8, -8, 2568, 1392);

        Assert.False(FullscreenWatcher.IsFullscreen(false, "Notepad", maximized, Monitor, AcceptsNotifications));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void The_desktop_and_the_taskbar_never_count(string className)
    {
        Assert.True(FullscreenWatcher.IsShellWindowClass(className));
        Assert.False(FullscreenWatcher.IsFullscreen(false, className, Monitor, Monitor, AcceptsNotifications));
    }

    [Fact]
    public void Our_own_window_never_counts()
    {
        Assert.False(FullscreenWatcher.IsFullscreen(true, "HwndWrapper[AI-Usage]", Monitor, Monitor, AcceptsNotifications));
    }

    [Theory]
    [InlineData(RunningD3dFullScreen, true)]
    [InlineData(PresentationMode, true)]
    [InlineData(Busy, false)]
    [InlineData(AcceptsNotifications, false)]
    public void The_shell_reporting_a_full_screen_game_or_a_presentation_counts_on_its_own(int state, bool expected)
    {
        var small = new ScreenRect(100, 100, 400, 400);

        Assert.Equal(expected, FullscreenWatcher.IsFullscreen(false, "SomeGame", small, Monitor, state));
    }

    [Fact]
    public void Without_a_foreground_window_nothing_is_full_screen()
    {
        Assert.False(FullscreenWatcher.IsFullscreen(false, null, default, Monitor, AcceptsNotifications));
    }

    [Fact]
    public void Hiding_on_full_screen_is_on_by_default_and_survives_a_round_trip()
    {
        Assert.True(new AppSettings().HideOnFullscreen);

        using var directory = TestPaths.CreateDisposableDirectory("ai-usage-fullscreen");
        using var store = new SettingsStore(directory);
        store.SaveNow(new AppSettings { WindowLayer = WindowLayers.Normal, HideOnFullscreen = false });
        using var reload = new SettingsStore(directory);

        Assert.False(reload.Load().HideOnFullscreen);
    }
}
