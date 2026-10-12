using AiUsage.Services;

namespace AiUsage.Tests;

public class MonitorPrimaryTests
{
    private static readonly MonitorArea Left = new("DISPLAY2", 0, 0, 1280, 1000);
    private static readonly MonitorArea Main = new("DISPLAY1", 1280, 0, 1920, 1040, IsPrimary: true);

    [Fact]
    public void The_primary_monitor_is_found_by_its_flag_whatever_the_order()
    {
        Assert.Equal(Main, WindowPlacementService.PrimaryOf([Left, Main]));
        Assert.Equal(Main, WindowPlacementService.PrimaryOf([Main, Left]));
    }

    [Fact]
    public void Without_any_flag_the_first_listed_monitor_stands_in()
    {
        var plain = new MonitorArea("A", 0, 0, 800, 600);

        Assert.Equal(plain, WindowPlacementService.PrimaryOf([plain, Left]));
        Assert.Equal(default, WindowPlacementService.PrimaryOf([]));
    }

    [Fact]
    public void A_widget_whose_monitor_is_gone_is_centred_on_the_flagged_primary_not_the_first_listed()
    {
        var gone = new WindowRect(9000, 9000, 340, 400);
        var monitors = new[] { Left, Main };

        var resolved = WindowPlacementService.ResolvePosition(gone, monitors, WindowPlacementService.PrimaryOf(monitors));

        Assert.Equal(Main.Left + (Main.Width - 340) / 2, resolved.Left);
        Assert.Equal((Main.Height - 400) / 2, resolved.Top);
    }

    [Fact]
    public void A_widget_straddling_the_seam_goes_to_the_monitor_holding_most_of_it()
    {
        // 40 px on the left monitor, 300 px on the right one.
        var straddling = new WindowRect(Left.Right - 40, 100, 340, 400);

        var resolved = WindowPlacementService.ResolvePosition(straddling, [Left, Main], Main);

        Assert.Equal(Main.Left, resolved.Left);
        Assert.Equal(100, resolved.Top);
    }

    [Fact]
    public void The_larger_overlap_wins_in_either_enumeration_order()
    {
        var straddling = new WindowRect(Left.Right - 300, 100, 340, 400);

        Assert.Equal(Left.Right - 340, WindowPlacementService.ResolvePosition(straddling, [Left, Main], Main).Left);
        Assert.Equal(Left.Right - 340, WindowPlacementService.ResolvePosition(straddling, [Main, Left], Main).Left);
    }

    [Fact]
    public void The_native_enumeration_reads_the_primary_flag()
    {
        var source = SourceScan.Read("src", "AiUsage", "Services", "NativeMonitors.cs");

        Assert.Contains("(info.Flags & MonitorInfoFPrimary) != 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void No_caller_takes_the_first_enumerated_monitor_as_the_primary()
    {
        foreach (var file in new[]
        {
            new[] { "src", "AiUsage", "Views", "MainWindow.xaml.cs" },
            new[] { "src", "AiUsage", "Views", "MainWindow.Placement.cs" },
            new[] { "src", "AiUsage", "Views", "WelcomeWindow.xaml.cs" },
        })
        {
            Assert.DoesNotContain("monitors[0]", SourceScan.Read(file), StringComparison.Ordinal);
        }
    }
}
