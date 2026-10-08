using System.Windows;
using System.Windows.Controls;

namespace AiUsage.Views.Controls;

/// <summary>A horizontal row whose first child always stays and trims itself, and whose later
/// children each appear only when they fit entirely next to what is already shown. A child that
/// does not fit is arranged at zero size (never collapsed), so the row's layout can never feed back
/// into its own visibility. Children are measured only in MeasureOverride: measuring one again
/// during arrange changes its desired size, which invalidates this panel and loops the layout
/// forever, so nothing around it (such as a progress bar's fill) ever settles.</summary>
public sealed class FitOrHidePanel : Panel
{
    private const double Gap = 6;

    // Each child's untrimmed width from the last measure pass, reused by arrange.
    private double[] _natural = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        var desired = new double[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            desired[i] = children[i].DesiredSize.Width;
        }

        _natural = desired;
        var (shown, width) = Fit(desired, availableSize.Width);
        var height = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            if (!shown[i])
            {
                // Measured at zero too, so it is truly empty rather than only clipped.
                children[i].Measure(new Size(0, 0));
                continue;
            }

            // The first child trims to what the others leave it, measured at a finite width so a text
            // formats its line for the width it is arranged at.
            if (i == 0)
                children[i].Measure(new Size(FirstWidth(desired, shown, width), availableSize.Height));
            height = Math.Max(height, children[i].DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var desired = _natural.Length == children.Count ? _natural : new double[children.Count];
        var (shown, _) = Fit(desired, finalSize.Width);
        var x = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (!shown[i])
            {
                child.Arrange(new Rect(x, finalSize.Height, 0, 0));
                continue;
            }

            var width = i == 0 ? FirstWidth(desired, shown, finalSize.Width) : desired[i];
            var height = Math.Min(child.DesiredSize.Height, finalSize.Height);
            child.Arrange(new Rect(x, Math.Max(0, finalSize.Height - height), width, height));
            x += width + Gap;
        }

        return finalSize;
    }

    /// <summary>Which children show, and the width the shown ones take together (gaps included).</summary>
    private static (bool[] Shown, double Width) Fit(double[] desired, double available)
    {
        var shown = new bool[desired.Length];
        if (desired.Length == 0)
            return (shown, 0);

        shown[0] = true;
        var used = Math.Min(desired[0], available);
        for (var i = 1; i < desired.Length; i++)
        {
            // An empty (collapsed) child takes no gap either.
            if (desired[i] <= 0)
                continue;
            var needed = used + Gap + desired[i];
            if (needed > available)
                continue;
            shown[i] = true;
            used = needed;
        }

        return (shown, used);
    }

    /// <summary>The first child's width: its own unless the others leave it less. Subtracting the
    /// others back out of a sum they were added to can land a hair below its own width, which would
    /// trim a text that fits, so a shortfall under a hundredth of a pixel counts as none.</summary>
    private static double FirstWidth(double[] desired, bool[] shown, double total)
    {
        var room = Math.Max(0, total - OthersWidth(desired, shown));
        return room > desired[0] - 0.01 ? desired[0] : room;
    }

    private static double OthersWidth(double[] desired, bool[] shown)
    {
        var sum = 0.0;
        for (var i = 1; i < desired.Length; i++)
        {
            if (shown[i])
                sum += Gap + desired[i];
        }

        return sum;
    }
}
