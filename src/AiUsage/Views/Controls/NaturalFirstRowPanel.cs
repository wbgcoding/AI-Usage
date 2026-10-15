using System.Windows;
using System.Windows.Controls;

namespace AiUsage.Views.Controls;

/// <summary>Two children side by side. The first keeps its natural (single line) width while the row has
/// room; when it does not, the first wraps so the second keeps at least <see cref="ReservedSecondWidth"/>
/// (or its own natural width when smaller). The second takes whatever is left and wraps in it. A plain
/// Auto column cannot do this: it is measured at unlimited width and never gives way.</summary>
public sealed class NaturalFirstRowPanel : Panel
{
    public static readonly DependencyProperty ReservedSecondWidthProperty = DependencyProperty.Register(
        nameof(ReservedSecondWidth), typeof(double), typeof(NaturalFirstRowPanel),
        new FrameworkPropertyMetadata(86d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ReservedSecondWidth
    {
        get => (double)GetValue(ReservedSecondWidthProperty);
        set => SetValue(ReservedSecondWidthProperty, value);
    }

    /// <summary>Width the first child may use: its natural width, cut back only as far as needed to leave
    /// the second child its reserve. Unlimited room never cuts.</summary>
    internal static double FirstWidth(double firstNatural, double secondNatural, double available, double reserve)
    {
        if (double.IsInfinity(available))
            return firstNatural;
        return Math.Min(firstNatural, Math.Max(0, available - Math.Min(secondNatural, reserve)));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
            return new Size(0, 0);

        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        var first = children[0];
        first.Measure(infinite);
        var second = children.Count > 1 ? children[1] : null;
        second?.Measure(infinite);

        var firstWidth = FirstWidth(first.DesiredSize.Width, second?.DesiredSize.Width ?? 0,
            availableSize.Width, ReservedSecondWidth);
        first.Measure(new Size(firstWidth, availableSize.Height));
        var used = first.DesiredSize.Width;
        var height = first.DesiredSize.Height;
        if (second is not null)
        {
            var rest = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - used);
            second.Measure(new Size(rest, availableSize.Height));
            used += second.DesiredSize.Width;
            height = Math.Max(height, second.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? used : Math.Min(used, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
            return finalSize;

        var firstWidth = Math.Min(children[0].DesiredSize.Width, finalSize.Width);
        children[0].Arrange(new Rect(0, 0, firstWidth, finalSize.Height));
        if (children.Count > 1)
            children[1].Arrange(new Rect(firstWidth, 0, Math.Max(0, finalSize.Width - firstWidth), finalSize.Height));
        for (var i = 2; i < children.Count; i++)
            children[i].Arrange(new Rect(0, finalSize.Height, 0, 0));

        return finalSize;
    }
}
