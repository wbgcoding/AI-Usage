using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Measure and arrange results of <see cref="BaselineStackPanel"/> and <see cref="TextBaseline"/>,
/// on an STA thread with unshown elements laid out by hand.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class BaselineStackPanelTests
{
    private static readonly FontFamily Font = new("Segoe UI");

    [Fact]
    public void The_baseline_is_the_font_baseline_scaled_by_the_size()
    {
        Sta(() =>
        {
            var small = new TextBlock { FontFamily = Font, FontSize = 10 };
            var large = new TextBlock { FontFamily = Font, FontSize = 30 };

            Assert.Equal(Font.Baseline * 10, TextBaseline.Of(small), 6);
            Assert.Equal(Font.Baseline * 30, TextBaseline.Of(large), 6);
            Assert.Equal(3 * TextBaseline.Of(small), TextBaseline.Of(large), 6);
        });
    }

    [Fact]
    public void The_top_margin_moves_the_baseline_down_and_the_other_margins_do_not()
    {
        Sta(() =>
        {
            var plain = new TextBlock { FontFamily = Font, FontSize = 12 };
            var margined = new TextBlock { FontFamily = Font, FontSize = 12, Margin = new Thickness(5, 7, 9, 11) };

            Assert.Equal(TextBaseline.Of(plain) + 7, TextBaseline.Of(margined), 6);
        });
    }

    [Fact]
    public void A_one_pixel_font_puts_the_baseline_just_below_the_top_margin()
    {
        Sta(() =>
        {
            var text = new TextBlock { FontFamily = Font, FontSize = 1, Margin = new Thickness(0, 4, 0, 0) };

            Assert.Equal(4 + Font.Baseline, TextBaseline.Of(text), 6);
        });
    }

    [Fact]
    public void An_empty_panel_measures_zero()
    {
        Sta(() =>
        {
            var panel = new BaselineStackPanel();

            panel.Measure(new Size(200, 100));

            Assert.Equal(new Size(0, 0), panel.DesiredSize);
        });
    }

    [Fact]
    public void The_width_is_the_sum_of_the_visible_children()
    {
        Sta(() =>
        {
            var panel = new BaselineStackPanel();
            panel.Children.Add(Box(30, 10));
            panel.Children.Add(Box(50, 20));
            panel.Children.Add(Box(7, 5));

            panel.Measure(new Size(500, 100));

            Assert.Equal(87, panel.DesiredSize.Width);
            Assert.Equal(20, panel.DesiredSize.Height);
        });
    }

    [Fact]
    public void A_collapsed_child_adds_neither_width_nor_height()
    {
        Sta(() =>
        {
            var panel = new BaselineStackPanel();
            panel.Children.Add(Box(30, 10));
            var hidden = Box(500, 500);
            hidden.Visibility = Visibility.Collapsed;
            panel.Children.Add(hidden);

            panel.Measure(new Size(1000, 1000));

            Assert.Equal(new Size(30, 10), panel.DesiredSize);
        });
    }

    [Fact]
    public void Zero_sized_children_measure_to_zero()
    {
        Sta(() =>
        {
            var panel = new BaselineStackPanel();
            panel.Children.Add(Box(0, 0));
            panel.Children.Add(Box(0, 0));

            panel.Measure(new Size(100, 100));

            Assert.Equal(new Size(0, 0), panel.DesiredSize);
        });
    }

    [Fact]
    public void Texts_of_different_sizes_share_one_baseline_after_arranging()
    {
        Sta(() =>
        {
            var small = Text("small", 11);
            var large = Text("large", 32);
            var panel = new BaselineStackPanel();
            panel.Children.Add(small);
            panel.Children.Add(large);

            Layout(panel, 400, 100);

            var smallBaseline = LayoutInformation.GetLayoutSlot(small).Top + TextBaseline.Of(small);
            var largeBaseline = LayoutInformation.GetLayoutSlot(large).Top + TextBaseline.Of(large);
            Assert.Equal(largeBaseline, smallBaseline, 6);
            Assert.Equal(0, LayoutInformation.GetLayoutSlot(large).Top, 6);
            Assert.True(LayoutInformation.GetLayoutSlot(small).Top > 0);
        });
    }

    [Fact]
    public void The_measured_height_holds_the_tallest_stack_above_plus_below_the_shared_baseline()
    {
        Sta(() =>
        {
            var small = Text("small", 11);
            var large = Text("large", 32);
            var panel = new BaselineStackPanel();
            panel.Children.Add(small);
            panel.Children.Add(large);

            panel.Measure(new Size(400, 100));

            var above = Math.Max(TextBaseline.Of(small), TextBaseline.Of(large));
            var below = Math.Max(small.DesiredSize.Height - TextBaseline.Of(small), large.DesiredSize.Height - TextBaseline.Of(large));
            Assert.Equal(above + below, panel.DesiredSize.Height, 6);
            Assert.True(panel.DesiredSize.Height >= large.DesiredSize.Height);
        });
    }

    [Fact]
    public void Children_are_placed_left_to_right_without_gaps()
    {
        Sta(() =>
        {
            var first = Box(30, 10);
            var second = Box(50, 10);
            var third = Box(7, 10);
            var panel = new BaselineStackPanel();
            panel.Children.Add(first);
            panel.Children.Add(second);
            panel.Children.Add(third);

            Layout(panel, 200, 40);

            Assert.Equal(0, LayoutInformation.GetLayoutSlot(first).Left);
            Assert.Equal(30, LayoutInformation.GetLayoutSlot(second).Left);
            Assert.Equal(80, LayoutInformation.GetLayoutSlot(third).Left);
        });
    }

    [Fact]
    public void A_child_that_is_not_text_is_centred_on_the_row_height()
    {
        Sta(() =>
        {
            var icon = Box(10, 20);
            var text = Text("text", 24);
            var panel = new BaselineStackPanel();
            panel.Children.Add(icon);
            panel.Children.Add(text);

            Layout(panel, 200, 60);

            Assert.Equal((60 - 20) / 2.0, LayoutInformation.GetLayoutSlot(icon).Top, 6);
        });
    }

    [Fact]
    public void A_child_taller_than_the_row_is_never_placed_above_the_top()
    {
        Sta(() =>
        {
            var tall = Box(10, 80);
            var panel = new BaselineStackPanel();
            panel.Children.Add(tall);

            panel.Measure(new Size(100, 100));
            panel.Arrange(new Rect(0, 0, 100, 30));

            Assert.Equal(0, LayoutInformation.GetLayoutSlot(tall).Top);
        });
    }

    [Fact]
    public void A_collapsed_text_does_not_move_the_baseline_of_the_visible_ones()
    {
        Sta(() =>
        {
            var small = Text("small", 11);
            var hiddenLarge = Text("large", 40);
            hiddenLarge.Visibility = Visibility.Collapsed;
            var panel = new BaselineStackPanel();
            panel.Children.Add(small);
            panel.Children.Add(hiddenLarge);

            Layout(panel, 300, 100);

            Assert.Equal(0, LayoutInformation.GetLayoutSlot(small).Top, 6);
        });
    }

    [Fact]
    public void A_text_margin_is_part_of_the_shared_baseline()
    {
        Sta(() =>
        {
            var plain = Text("plain", 14);
            var margined = Text("margined", 14);
            margined.Margin = new Thickness(0, 6, 0, 0);
            var panel = new BaselineStackPanel();
            panel.Children.Add(plain);
            panel.Children.Add(margined);

            Layout(panel, 300, 100);

            var plainBaseline = LayoutInformation.GetLayoutSlot(plain).Top + TextBaseline.Of(plain);
            var marginedBaseline = LayoutInformation.GetLayoutSlot(margined).Top + TextBaseline.Of(margined);
            Assert.Equal(marginedBaseline, plainBaseline, 6);
        });
    }

    [Fact]
    public void Arranging_gives_back_the_full_size_it_was_offered()
    {
        Sta(() =>
        {
            var panel = new BaselineStackPanel();
            panel.Children.Add(Box(10, 10));

            Layout(panel, 123, 45);

            Assert.Equal(new Size(123, 45), panel.RenderSize);
        });
    }

    private static void Layout(UIElement panel, double width, double height)
    {
        panel.Measure(new Size(width, height));
        panel.Arrange(new Rect(0, 0, width, height));
    }

    private static Rectangle Box(double width, double height) => new() { Width = width, Height = height };

    private static TextBlock Text(string content, double size) => new() { Text = content, FontFamily = Font, FontSize = size };

    private static void Sta(Action body)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                body();
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
        if (!worker.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Baseline panel test did not finish");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
