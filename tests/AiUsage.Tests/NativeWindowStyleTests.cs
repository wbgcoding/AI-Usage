using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class NativeWindowStyleTests
{
    [Fact]
    public void TrySetRoundedCorners_swallows_a_failing_call()
    {
        var exception = Record.Exception(() => NativeWindowStyle.TrySetRoundedCorners(IntPtr.Zero));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(8, 1.0, 8)]
    [InlineData(8, 1.5, 12)]
    [InlineData(8, 2.0, 16)]
    public void RegionRadiusPixels_scales_the_chrome_radius_with_the_dpi(double dips, double scale, int expected) =>
        Assert.Equal(expected, NativeWindowStyle.RegionRadiusPixels(dips, scale));

    [Fact]
    public void The_region_radius_matches_the_chrome_radius_token()
    {
        var tokens = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ThemeFixtures", "Tokens.xaml"));
        var chrome = File.ReadAllText(Path.Combine(FindSrcDir(), "Services", "WindowChromeNative.cs"));

        Assert.Contains("<CornerRadius x:Key=\"Radius.Window\">8</CornerRadius>", tokens, StringComparison.Ordinal);
        Assert.Contains("ChromeRadiusDips = 8;", chrome, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(26200, true, 3, true, true, true)]
    [InlineData(19045, true, 3, true, true, false)] // Windows 10 has no system rounding
    [InlineData(26200, false, 3, true, true, false)] // composition off
    [InlineData(26200, true, 0, true, true, false)] // software rendering: rounding is switched off
    [InlineData(26200, true, 3, false, true, false)] // the preference did not stick
    [InlineData(26200, true, 2, true, false, false)] // a virtual adapter: WPF reports a tier, Windows does not round
    public void SystemRoundsCorners_needs_every_condition(int build, bool composition, int tier, bool preference, bool hardware, bool expected) =>
        Assert.Equal(expected, NativeWindowStyle.SystemRoundsCorners(build, composition, tier, preference, hardware));

    [Theory]
    [InlineData("Microsoft Hyper-V Video", true)]
    [InlineData("Microsoft Basic Display Adapter", true)]
    [InlineData("Microsoft Basic Render Driver", true)]
    [InlineData("NVIDIA GeForce RTX 4070", false)]
    [InlineData("Intel(R) UHD Graphics", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSoftwareAdapterName_flags_virtual_and_basic_adapters(string? name, bool expected) =>
        Assert.Equal(expected, NativeWindowStyle.IsSoftwareAdapterName(name));

    private static bool BootstrapLeavesARegion(bool systemRounds)
    {
        var original = NativeWindowStyle.SystemRoundingProbe;
        NativeWindowStyle.SystemRoundingProbe = _ => systemRounds;
        try
        {
            return RunOnStaThread(() =>
            {
                // SourceInitialized only fires on a real show, so the window is shown, off screen and
                // without taking focus.
                var window = new Window
                {
                    Width = 300, Height = 200, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                    ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                };
                WindowChromeNative.Bootstrap(window);
                window.Show();
                var hwnd = new WindowInteropHelper(window).Handle;
                var region = CreateRectRgn(0, 0, 1, 1);
                try
                {
                    return GetWindowRgn(hwnd, region) != 0;
                }
                finally
                {
                    DeleteObject(region);
                    window.Close();
                }
            });
        }
        finally
        {
            NativeWindowStyle.SystemRoundingProbe = original;
        }
    }

    [Fact]
    public void Bootstrap_cuts_a_region_only_where_the_system_does_not_round() => Assert.True(BootstrapLeavesARegion(systemRounds: false));

    [Fact]
    public void Bootstrap_leaves_the_window_alone_where_the_system_rounds() => Assert.False(BootstrapLeavesARegion(systemRounds: true));

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PtInRegion(IntPtr hRgn, int x, int y);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>The window's own clip: the square corner pixels fall outside it, the pixels just
    /// inside the 8 px arc and the middle stay in - so nothing of the window can show around the
    /// rounded chrome.</summary>
    [Fact]
    public void ApplyRoundedRegion_cuts_the_corners_and_keeps_the_rest()
    {
        var (corner, outsideArc, insideArc, middle) = RunOnStaThread(() =>
        {
            var window = new Window { Width = 300, Height = 200, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            NativeWindowStyle.ApplyRoundedRegion(hwnd, 8);
            var region = CreateRectRgn(0, 0, 1, 1);
            try
            {
                Assert.NotEqual(0, GetWindowRgn(hwnd, region));
                return (PtInRegion(region, 0, 0), PtInRegion(region, 1, 1), PtInRegion(region, 3, 3), PtInRegion(region, 150, 100));
            }
            finally
            {
                DeleteObject(region);
                window.Close();
            }
        });

        Assert.False(corner);
        Assert.False(outsideArc); // 1,1 is 10 px from the arc's center (8,8), beyond the 8 px radius
        Assert.True(insideArc); // 3,3 is 7 px from it
        Assert.True(middle);
    }

    private static string FindSrcDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(here)))!, "src", "AiUsage");

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
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null)
            throw failure;
        return result!;
    }
}
