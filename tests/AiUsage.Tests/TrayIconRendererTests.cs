using System.Drawing;
using AiUsage.Models;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class TrayIconRendererTests
{
    [Theory]
    [InlineData(UsageLevel.Ok)]
    [InlineData(UsageLevel.Warn)]
    [InlineData(UsageLevel.Crit)]
    public void The_chosen_text_colour_reaches_the_graphical_contrast_floor_against_its_level_colour(UsageLevel level)
    {
        var background = TrayIconRenderer.BackgroundColor(level);
        var text = TrayIconRenderer.ContrastingTextColor(background);

        var ratio = ContrastRatio(background, text);
        Assert.True(ratio >= 4.5, $"{level}: {text} on {background} is only {ratio:F2}:1");
    }

    [Fact]
    public void A_full_percent_is_written_as_three_digits() =>
        Assert.Equal("100", TrayIconRenderer.FormatPercent(100));

    [Fact]
    public void A_percent_above_a_hundred_reads_as_a_full_gauge() =>
        Assert.Equal("100", TrayIconRenderer.FormatPercent(150));

    [Fact]
    public void A_negative_percent_reads_as_zero() =>
        Assert.Equal("0", TrayIconRenderer.FormatPercent(-3));

    [Fact]
    public void A_two_digit_percent_renders_plainly() =>
        Assert.Equal("42", TrayIconRenderer.FormatPercent(42));

    [Fact]
    public void Ninety_nine_percent_still_renders_as_digits() =>
        Assert.Equal("99", TrayIconRenderer.FormatPercent(99));

    [Fact]
    public void A_one_digit_percent_renders_plainly() =>
        Assert.Equal("7", TrayIconRenderer.FormatPercent(7));

    [Fact]
    public void The_rendered_bitmap_is_sixteen_by_sixteen_at_that_size()
    {
        using var bitmap = TrayIconRenderer.Render(UsageLevel.Warn, 73, 16);

        Assert.Equal(16, bitmap.Width);
        Assert.Equal(16, bitmap.Height);
    }

    [Fact]
    public void The_rendered_bitmap_honours_a_larger_requested_size()
    {
        using var bitmap = TrayIconRenderer.Render(UsageLevel.Ok, 5, 32);

        Assert.Equal(32, bitmap.Width);
        Assert.Equal(32, bitmap.Height);
    }

    [Fact]
    public void A_full_percent_still_renders_at_the_requested_size()
    {
        using var bitmap = TrayIconRenderer.Render(UsageLevel.Crit, 100, 16);

        Assert.Equal(16, bitmap.Width);
        Assert.Equal(16, bitmap.Height);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(20, 20)]
    [InlineData(24, 24)]
    [InlineData(32, 32)]
    [InlineData(8, 16)]
    [InlineData(128, 64)]
    public void The_icon_size_is_the_shells_small_icon_metric_within_sane_limits(int metric, int expected) =>
        Assert.Equal(expected, TrayIconRenderer.IconSizeFor(metric));

    // The digits sit in the middle of the tile: the same free rows above and below, and across at
    // most half a pixel off once the faint anti-aliased edge columns count as part of a pixel. They
    // keep the margin clear of the rounded corners and stay large enough to read.
    [Theory]
    [InlineData(16, 7)]
    [InlineData(16, 9)]
    [InlineData(16, 42)]
    [InlineData(16, 47)]
    [InlineData(16, 88)]
    [InlineData(16, 100)]
    [InlineData(20, 4)]
    [InlineData(20, 42)]
    [InlineData(24, 7)]
    [InlineData(24, 42)]
    [InlineData(24, 100)]
    [InlineData(32, 7)]
    [InlineData(32, 42)]
    [InlineData(32, 100)]
    public void The_digits_sit_centred_inside_the_margin(int size, int percent)
    {
        using var bitmap = TrayIconRenderer.Render(UsageLevel.Ok, percent, size);
        var background = TrayIconRenderer.BackgroundColor(UsageLevel.Ok);

        int minX = size, maxX = -1, minY = size, maxY = -1;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.A < 200 || Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B) < 60)
                continue;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        var margin = TrayIconRenderer.Margin(size);
        var (left, right, top, bottom) = (minX, size - 1 - maxX, minY, size - 1 - maxY);
        Assert.True(maxY - minY + 1 >= size * (percent >= 100 ? 0.4 : 0.5), $"{percent} at {size}: ink is only {maxY - minY + 1} px tall");
        Assert.True(Math.Min(Math.Min(left, right), Math.Min(top, bottom)) >= margin,
            $"{percent} at {size}: ink comes closer than {margin} px to the edge");
        Assert.True(Math.Abs(left - right) <= 1, $"{percent} at {size}: {left} px left, {right} px right");
        Assert.True(top == bottom, $"{percent} at {size}: {top} px above, {bottom} px below");

        double Coverage(bool column, int index)
        {
            var max = 0.0;
            for (var i = 0; i < size; i++)
            {
                var c = column ? bitmap.GetPixel(index, i) : bitmap.GetPixel(i, index);
                max = Math.Max(max, (c.G - background.G) / (255.0 - background.G));
            }
            return max;
        }

        int softMin = size, softMax = -1;
        for (var x = 0; x < size; x++)
        {
            if (Coverage(true, x) <= 0.05)
                continue;
            softMin = Math.Min(softMin, x);
            softMax = Math.Max(softMax, x);
        }

        var visualCentre = ((softMin + 1 - Coverage(true, softMin)) + (softMax + Coverage(true, softMax))) / 2;
        Assert.True(Math.Abs(visualCentre - size / 2.0) <= 0.5, $"{percent} at {size}: centre at {visualCentre:F2}, tile centre {size / 2.0}");
    }

    // A copy that resampled the digits instead of moving whole pixels blurred them and pushed every
    // number a quarter pixel down; the rows above and below the ink must hold no stray coverage.
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void The_digits_are_copied_without_resampling(int size)
    {
        using var bitmap = TrayIconRenderer.Render(UsageLevel.Ok, 42, size);
        using var digits = TrayIconRenderer.DigitsBitmap("42", size, Color.White, out var ink);
        var (left, top) = TrayIconRenderer.CentredOrigin(digits, ink, size);

        for (var y = 0; y < ink.Height; y++)
        for (var x = 0; x < ink.Width; x++)
        {
            var source = digits.GetPixel(ink.Left + x, ink.Top + y);
            if (source.A != 255)
                continue;
            Assert.Equal(Color.White.ToArgb(), bitmap.GetPixel(left + x, top + y).ToArgb());
        }
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var (l1, l2) = (RelativeLuminance(a), RelativeLuminance(b));
        var (lighter, darker) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
