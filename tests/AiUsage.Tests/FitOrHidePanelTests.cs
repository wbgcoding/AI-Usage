using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Measure/Arrange of <see cref="FitOrHidePanel"/> on plain fixed-size elements, no window.
/// WPF objects are thread-affine, so each case builds, lays out and reads on its own STA thread.</summary>
public class FitOrHidePanelTests
{
    private const double Gap = 6;

    private sealed class Box : FrameworkElement
    {
        private readonly Size _size;

        public Box(double width, double height) => _size = new Size(width, height);

        protected override Size MeasureOverride(Size availableSize) =>
            new(Math.Min(_size.Width, availableSize.Width), Math.Min(_size.Height, availableSize.Height));
    }

    private sealed record Layout(Size Desired, Rect[] Slots, bool LayoutSettled);

    private static Layout Run(double availableWidth, params (double W, double H)[] sizes)
    {
        Layout? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                var panel = new FitOrHidePanel();
                foreach (var (w, h) in sizes)
                    panel.Children.Add(new Box(w, h));

                panel.Measure(new Size(availableWidth, double.PositiveInfinity));
                var desired = panel.DesiredSize;
                var arrangeWidth = double.IsInfinity(availableWidth) ? desired.Width : availableWidth;
                panel.Arrange(new Rect(0, 0, arrangeWidth, desired.Height));
                result = new Layout(
                    desired,
                    panel.Children.Cast<FrameworkElement>()
                        .Select(c => new Rect(LayoutInformation.GetLayoutSlot(c).Location, c.RenderSize))
                        .ToArray(),
                    panel.IsMeasureValid && panel.IsArrangeValid);
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
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null)
            throw new InvalidOperationException("layout failed", failure);
        return result!;
    }

    [Fact]
    public void Everything_that_fits_is_shown_in_order_with_a_gap()
    {
        var layout = Run(400, (100, 16), (50, 12), (60, 12));

        Assert.Equal(100, layout.Slots[0].Width);
        Assert.Equal(100 + Gap, layout.Slots[1].X);
        Assert.Equal(50, layout.Slots[1].Width);
        Assert.Equal(100 + Gap + 50 + Gap, layout.Slots[2].X);
        Assert.Equal(60, layout.Slots[2].Width);
        Assert.Equal(16, layout.Desired.Height);
    }

    [Fact]
    public void A_child_that_does_not_fit_entirely_is_arranged_at_zero_size()
    {
        // 100 + 6 + 50 = 156 fits, the next 60 + 6 would end at 222 > 200.
        var layout = Run(200, (100, 16), (50, 12), (60, 12));

        Assert.Equal(50, layout.Slots[1].Width);
        Assert.Equal(0, layout.Slots[2].Width);
        Assert.Equal(0, layout.Slots[2].Height);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(200)]
    [InlineData(130)]
    public void Arranging_never_invalidates_the_panels_own_measure(double available)
    {
        // Re-measuring a child inside arrange at a different size changes its desired size, which
        // invalidates the panel again: an endless layout loop that left every bar beside it unfilled.
        var layout = Run(available, (100, 16), (50, 12), (60, 12));

        Assert.True(layout.LayoutSettled);
    }

    [Fact]
    public void A_later_child_that_fits_still_shows_when_an_earlier_one_was_dropped()
    {
        // The wide second child is dropped, the small third one fits after the first.
        var layout = Run(160, (100, 16), (90, 12), (30, 12));

        Assert.Equal(0, layout.Slots[1].Width);
        Assert.Equal(30, layout.Slots[2].Width);
        Assert.Equal(100 + Gap, layout.Slots[2].X);
    }

    [Fact]
    public void The_first_child_stays_and_takes_what_is_left_when_it_is_wider_than_the_panel()
    {
        var layout = Run(80, (300, 16), (50, 12));

        Assert.Equal(80, layout.Slots[0].Width);
        Assert.Equal(0, layout.Slots[1].Width);
    }

    [Fact]
    public void Children_sit_on_a_shared_bottom_and_the_height_is_the_tallest()
    {
        var layout = Run(400, (100, 20), (50, 12));

        Assert.Equal(20, layout.Desired.Height);
        Assert.Equal(20, layout.Slots[0].Bottom);
        Assert.Equal(20, layout.Slots[1].Bottom);
    }

    [Fact]
    public void An_unbounded_width_shows_every_child()
    {
        var layout = Run(double.PositiveInfinity, (100, 16), (50, 12), (60, 12));

        Assert.All(layout.Slots, slot => Assert.True(slot.Width > 0));
    }
}
