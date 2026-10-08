using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class MonitorAreaCacheTests
{
    private static readonly MonitorArea Area = new("DISPLAY1", 0, 0, 1920, 1040);

    // A 150 % monitor right of a 1920 px primary starts at 1280 in its own units, inside the
    // primary's 0..1920: the position alone picked the primary for a window on the secondary.
    [Fact]
    public void The_monitor_windows_names_wins_over_overlapping_scaled_areas()
    {
        var primary = new MonitorArea("DISPLAY1", 0, 0, 1920, 1040);
        var secondary = new MonitorArea("DISPLAY2", 1280, 0, 1706, 960);
        var onSecondary = new WindowRect(1300, 100, 300, 400);

        Assert.Equal(secondary, AiUsage.Views.MainWindow.PickArea([primary, secondary], "DISPLAY2", onSecondary));
        Assert.Equal(primary, AiUsage.Views.MainWindow.PickArea([primary, secondary], null, onSecondary));
        Assert.Equal(primary, AiUsage.Views.MainWindow.PickArea([primary, secondary], "DISPLAY9", onSecondary));
    }

    [Fact]
    public void Sixty_reads_of_Areas_enumerate_only_once()
    {
        var lookups = 0;
        var cache = new MonitorAreaCache(() => { lookups++; return [Area]; });

        for (var i = 0; i < 60; i++)
            _ = cache.Areas;

        Assert.Equal(1, lookups);
    }

    [Fact]
    public void Refresh_re_enumerates_and_the_result_reflects_the_new_layout()
    {
        var current = new MonitorArea[] { Area };
        var cache = new MonitorAreaCache(() => current);
        Assert.Equal(1920, cache.Areas[0].Width);

        current = [Area with { Width = 2560 }];
        cache.Refresh();

        Assert.Equal(2560, cache.Areas[0].Width);
    }

    [Fact]
    public void Sixty_ticks_with_one_display_change_in_the_middle_enumerate_at_most_twice()
    {
        var lookups = 0;
        var cache = new MonitorAreaCache(() => { lookups++; return [Area]; });

        for (var i = 0; i < 60; i++)
        {
            if (i == 30)
                cache.Refresh();
            _ = cache.Areas;
        }

        Assert.True(lookups <= 2, $"expected at most 2 enumerations, got {lookups}");
    }
}
