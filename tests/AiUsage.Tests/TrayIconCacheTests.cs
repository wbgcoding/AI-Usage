using System.Drawing;
using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

public class TrayIconCacheTests
{
    private static byte[] Pixels(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    [Fact]
    public void The_same_level_percent_and_size_is_drawn_once()
    {
        // A size no other test renders, so the count below is this test's alone.
        const int size = 37;
        var before = TrayIconRenderer.ComposeCount;

        using var first = TrayIconRenderer.Render(UsageLevel.Warn, 61, size);
        using var second = TrayIconRenderer.Render(UsageLevel.Warn, 61, size);

        Assert.Equal(before + 1, TrayIconRenderer.ComposeCount);
        Assert.Equal(Pixels(first), Pixels(second));
    }

    [Fact]
    public void A_different_percent_level_or_size_is_drawn_again()
    {
        const int size = 39;
        var before = TrayIconRenderer.ComposeCount;

        using var a = TrayIconRenderer.Render(UsageLevel.Ok, 12, size);
        using var b = TrayIconRenderer.Render(UsageLevel.Ok, 13, size);
        using var c = TrayIconRenderer.Render(UsageLevel.Crit, 12, size);
        using var d = TrayIconRenderer.Render(UsageLevel.Ok, 12, size + 1);

        Assert.Equal(before + 4, TrayIconRenderer.ComposeCount);
    }

    [Fact]
    public void Each_caller_gets_its_own_bitmap_to_dispose()
    {
        const int size = 41;
        var first = TrayIconRenderer.Render(UsageLevel.Ok, 7, size);
        first.Dispose();

        using var second = TrayIconRenderer.Render(UsageLevel.Ok, 7, size);

        Assert.Equal(size, second.Width);
        Assert.NotEqual(0, second.GetPixel(size / 2, size / 2).A);
    }

    [Fact]
    public void Percents_outside_the_gauge_share_the_clamped_icon()
    {
        const int size = 43;
        var before = TrayIconRenderer.ComposeCount;

        using var full = TrayIconRenderer.Render(UsageLevel.Crit, 100, size);
        using var over = TrayIconRenderer.Render(UsageLevel.Crit, 140, size);

        Assert.Equal(before + 1, TrayIconRenderer.ComposeCount);
        Assert.Equal(Pixels(full), Pixels(over));
    }

    [Fact]
    public void The_text_colour_choice_uses_the_shared_contrast_measure()
    {
        var source = SourceScan.Read("src", "AiUsage", "Services", "TrayIconRenderer.cs");

        Assert.Contains("AccentColors.Contrast", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RelativeLuminance", source, StringComparison.Ordinal);
    }
}
