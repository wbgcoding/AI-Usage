using System.Threading;
using System.Windows;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Which right side of a usage row header shows (<see cref="HeaderRowPanel"/>): the decision
/// itself, and the layout on plain fixed-size elements. WPF objects are thread-affine, so each layout
/// case builds, lays out and reads on its own STA thread.</summary>
public class HeaderRowPanelTests
{
    // The label group's right margin; RenderSize excludes it.
    private const double Margin = 8;

    private sealed class Box(double width, double height) : FrameworkElement
    {
        protected override Size MeasureOverride(Size availableSize) =>
            new(Math.Min(width, availableSize.Width), Math.Min(height, availableSize.Height));
    }

    [Theory]
    [InlineData(60, 100, 200, false)]
    [InlineData(60, 100, 160, false)]
    [InlineData(60, 100, 159, true)]
    [InlineData(60, 100, 10, true)]
    [InlineData(60, 100, double.PositiveInfinity, false)]
    public void The_short_form_shows_only_when_the_label_would_be_cut_for_the_full_one(
        double label, double full, double available, bool expectShort) =>
        Assert.Equal(expectShort, HeaderRowPanel.UseShortForm(label, full, available));

    private static (double LabelWidth, double FullWidth, double ShortWidth) Run(double available)
    {
        (double, double, double) result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                var panel = new HeaderRowPanel();
                var labelGroup = new FitOrHidePanel { Margin = new Thickness(0, 0, Margin, 0) };
                labelGroup.Children.Add(new Box(60, 16));
                labelGroup.Children.Add(new Box(40, 12)); // a token figure, shown only when it fits
                var full = new Box(100, 16);
                var @short = new Box(50, 16);
                panel.Children.Add(labelGroup);
                panel.Children.Add(full);
                panel.Children.Add(@short);

                panel.Measure(new Size(available, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, double.IsInfinity(available) ? panel.DesiredSize.Width : available, panel.DesiredSize.Height));
                result = (labelGroup.RenderSize.Width, full.RenderSize.Width, @short.RenderSize.Width);
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
        return result;
    }

    [Fact]
    public void With_room_the_full_right_side_shows_and_the_short_one_takes_no_space()
    {
        var (label, full, @short) = Run(400);

        Assert.Equal(400 - 100 - Margin, label);
        Assert.Equal(100, full);
        Assert.Equal(0, @short);
    }

    [Fact]
    public void When_the_label_would_be_cut_the_short_right_side_takes_over_and_the_label_stays_whole()
    {
        // The label needs 60 + 8 (its margin) = 68; with the full side (100) that is 168 > 150.
        var (label, full, @short) = Run(150);

        Assert.Equal(0, full);
        Assert.Equal(50, @short);
        Assert.Equal(150 - 50 - Margin, label);
        Assert.True(label >= 60); // the label's own 60 is whole, only its slot margin is not part of it
    }

    [Fact]
    public void When_even_the_short_side_leaves_too_little_the_label_gives_way_last()
    {
        var (label, full, @short) = Run(90);

        Assert.Equal(0, full);
        Assert.Equal(50, @short);
        Assert.Equal(90 - 50 - Margin, label);
    }

    [Fact]
    public void The_usage_bar_header_uses_the_panel_and_both_forms_of_the_right_side()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot.Find(), "src", "AiUsage", "Views", "Controls", "UsageBar.xaml"));

        Assert.Contains("<controls:HeaderRowPanel", xaml);
        Assert.Contains("Tag=\"{Binding DetailText}\"", xaml);
        Assert.Contains("Tag=\"{Binding CountdownText}\"", xaml);
    }
}
