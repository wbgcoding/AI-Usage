using System.Windows;
using System.Windows.Controls;

namespace AiUsage.Views.Controls;

/// <summary>A horizontal row whose texts share one baseline (the lowest of them, so the largest text
/// stays at the top) and whose other children are centred on the row's height.</summary>
public sealed class BaselineStackPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, aboveBaseline = 0, belowBaseline = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (child.Visibility == Visibility.Collapsed)
                continue;

            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
            if (child is TextBlock text)
            {
                var baseline = TextBaseline.Of(text);
                aboveBaseline = Math.Max(aboveBaseline, baseline);
                belowBaseline = Math.Max(belowBaseline, child.DesiredSize.Height - baseline);
            }
        }

        return new Size(width, Math.Max(height, aboveBaseline + belowBaseline));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var baseline = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            if (child is TextBlock text && child.Visibility != Visibility.Collapsed)
                baseline = Math.Max(baseline, TextBaseline.Of(text));
        }

        var x = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            var size = child.DesiredSize;
            var top = child is TextBlock text ? baseline - TextBaseline.Of(text) : (finalSize.Height - size.Height) / 2;
            child.Arrange(new Rect(x, Math.Max(0, top), size.Width, size.Height));
            x += size.Width;
        }

        return finalSize;
    }
}
