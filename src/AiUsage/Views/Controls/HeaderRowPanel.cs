using System.Windows;
using System.Windows.Controls;

namespace AiUsage.Views.Controls;

/// <summary>The header of a usage row: the label group on the left, the answer on the right. The right
/// side comes in two forms, a full one (percent, countdown, reset clock) and a short one (percent,
/// countdown). The full one shows unless the label would have to be cut to make room for it; then the
/// short one shows, and only when even that leaves too little does the label trim. Children: 0 the
/// label group, 1 the full right side, 2 the short right side. The unused right side is measured and
/// arranged at zero size, never collapsed, so the choice cannot feed back into its own layout.</summary>
public sealed class HeaderRowPanel : Panel
{
    /// <summary>True when the short right side is the one to show: the label's untrimmed width plus the
    /// full right side exceeds the room. Infinite room never picks the short form.</summary>
    internal static bool UseShortForm(double labelWidth, double fullWidth, double available) =>
        labelWidth + fullWidth > available;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
            return new Size(0, 0);

        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        var label = children[0];
        label.Measure(infinite);
        var labelWidth = UntrimmedLabelWidth(label);

        var right = ChooseRight(children, labelWidth, availableSize.Width, infinite);
        var rightWidth = right?.DesiredSize.Width ?? 0;
        label.Measure(new Size(Math.Max(0, availableSize.Width - rightWidth), availableSize.Height));

        var height = label.DesiredSize.Height;
        if (right is not null)
            height = Math.Max(height, right.DesiredSize.Height);
        var width = label.DesiredSize.Width + rightWidth;
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
            return finalSize;

        var shown = children.Count > 1 ? children[Shown(children)] : null;
        var rightWidth = shown is null ? 0 : Math.Min(shown.DesiredSize.Width, finalSize.Width);
        var label = children[0];
        label.Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - rightWidth), finalSize.Height));
        for (var i = 1; i < children.Count; i++)
        {
            if (children[i] == shown)
                children[i].Arrange(new Rect(finalSize.Width - rightWidth, 0, rightWidth, finalSize.Height));
            else
                children[i].Arrange(new Rect(0, finalSize.Height, 0, 0));
        }

        return finalSize;
    }

    // The index of the right side that is shown, kept from the measure pass.
    private int _shownIndex = 1;

    private int Shown(UIElementCollection children) => Math.Min(_shownIndex, children.Count - 1);

    private UIElement? ChooseRight(UIElementCollection children, double labelWidth, double available, Size infinite)
    {
        if (children.Count < 2)
            return null;

        var full = children[1];
        full.Measure(infinite);
        var index = 1;
        if (children.Count > 2)
        {
            var @short = children[2];
            @short.Measure(infinite);
            if (UseShortForm(labelWidth, full.DesiredSize.Width, available))
                index = 2;
        }

        _shownIndex = index;
        for (var i = 1; i < children.Count; i++)
        {
            if (i != index)
                children[i].Measure(new Size(0, 0));
        }

        var chosen = children[index];
        chosen.Measure(infinite);
        return chosen;
    }

    // What the label group needs to show its label whole, its own margins included.
    private static double UntrimmedLabelWidth(UIElement label)
    {
        if (label is FitOrHidePanel group)
        {
            var margin = group.Margin;
            return group.FirstChildWidth + margin.Left + margin.Right;
        }

        return label.DesiredSize.Width;
    }
}
