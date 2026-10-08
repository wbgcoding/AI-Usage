using System.Runtime.CompilerServices;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The pure arithmetic behind <see cref="WindowOpacity"/>'s native, per-window alpha - kept separate
/// from the actual P/Invoke calls (see that class's own <c>NativeMethods</c> seam), so both are
/// testable without a real HWND.
/// </summary>
public class WindowOpacityTests
{
    [Theory]
    [InlineData(100, 255)]
    [InlineData(0, 0)]
    [InlineData(70, 178)] // 70 is SettingsRanges.MinWindowOpacityPercent, the slider's real floor
    [InlineData(50, 127)]
    public void AlphaFromPercent_scales_0_to_100_onto_0_to_255(int percent, byte expectedAlpha)
    {
        Assert.Equal(expectedAlpha, WindowOpacity.AlphaFromPercent(percent));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(150, 255)]
    public void AlphaFromPercent_clamps_an_out_of_range_percent_first(int percent, byte expectedAlpha)
    {
        Assert.Equal(expectedAlpha, WindowOpacity.AlphaFromPercent(percent));
    }

    // A fully opaque window must be an ordinary window: a layered one shows nothing until it paints
    // again, which left the main window invisible after start.
    [Fact]
    public void Opaque_window_without_click_through_is_not_layered()
    {
        Assert.Equal(0x100, WindowOpacity.RequiredExStyle(0x80000 | 0x100, 100));
    }

    [Fact]
    public void Translucent_window_is_layered_and_keeps_its_other_bits()
    {
        Assert.Equal(0x80000 | 0x100, WindowOpacity.RequiredExStyle(0x100, 60));
    }

    // WS_EX_TRANSPARENT (0x20) is click-through's bit; it only works on a layered window, and applying
    // opacity must neither set nor clear it.
    [Theory]
    [InlineData(100)]
    [InlineData(60)]
    public void Click_through_window_stays_layered_at_any_opacity(int percent)
    {
        Assert.Equal(0x80000 | 0x20, WindowOpacity.RequiredExStyle(0x20, percent));
    }

    // WPF writes its own cached extended style back without the layered bit; while the window is
    // translucent the pending change has to keep it, or the alpha is dropped and nothing shows.
    [Fact]
    public void A_pending_change_that_drops_the_layered_bit_keeps_it_while_translucent()
    {
        var (corrected, hide) = WindowOpacity.ResolveStyleNotice(changing: true, oldStyle: 0xC0008, newStyle: 0x40008, percent: 40);

        Assert.Equal(0xC0008u, corrected);
        Assert.True(hide);
    }

    [Fact]
    public void A_pending_change_on_an_opaque_window_passes_unchanged()
    {
        var (corrected, hide) = WindowOpacity.ResolveStyleNotice(changing: true, oldStyle: 0x40008, newStyle: 0x40108, percent: 100);

        Assert.Equal(0x40108u, corrected);
        Assert.False(hide);
    }

    [Fact]
    public void Switching_back_to_opaque_removes_the_layer_and_hides_that_flip_from_wpf()
    {
        var (corrected, hide) = WindowOpacity.ResolveStyleNotice(changing: true, oldStyle: 0xC0008, newStyle: 0x40008, percent: 100);

        Assert.Equal(0x40008u, corrected);
        Assert.True(hide);
    }

    [Fact]
    public void A_finished_change_is_never_rewritten()
    {
        var (corrected, _) = WindowOpacity.ResolveStyleNotice(changing: false, oldStyle: 0xC0008, newStyle: 0x40008, percent: 40);

        Assert.Equal(0x40008u, corrected);
    }

    [Theory]
    [InlineData(0x40008, 0xC0008, true)]
    [InlineData(0x40008, 0xC0028, true)]
    [InlineData(0x40008, 0xC0108, false)]
    [InlineData(0x40008, 0x40028, false)]
    public void Only_a_layering_flip_counts_as_this_classes_own_change(long oldStyle, long newStyle, bool expected)
    {
        Assert.Equal(expected, WindowOpacity.IsOwnLayeringChange(oldStyle, newStyle));
    }

    [Fact]
    public void Only_the_widget_follows_the_opacity_setting()
    {
        var views = Path.Combine(RepoRoot(), "src", "AiUsage", "Views");
        var followers = Directory.GetFiles(views, "*.xaml.cs")
            .Where(f => File.ReadAllText(f).Contains("followsOpacity: true"))
            .Select(f => Path.GetFileName(f))
            .ToArray();

        Assert.Equal(["MainWindow.xaml.cs"], followers);
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(here)))!;
}
