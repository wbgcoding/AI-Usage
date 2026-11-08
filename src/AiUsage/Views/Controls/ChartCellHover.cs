using System.Windows;
using System.Windows.Media;

namespace AiUsage.Views.Controls;

/// <summary>
/// The pointer side of a cell grid (<see cref="StatsMonthGrid"/>, <see cref="StatsHeatmap"/>): finds
/// the cell under the pointer, remembers it so a stationary pointer does not re-open the tooltip, and
/// owns the tooltip popup the keyboard focus shares with the hover.
/// </summary>
internal sealed class ChartCellHover<THit> where THit : struct
{
    private ChartTooltipPopup? _popup;
    private THit? _hovered;

    /// <summary>True while a hovered cell is remembered - what a data change has to clear.</summary>
    internal bool HasHover => _hovered is not null;

    internal void Remember(THit? hit) => _hovered = hit;

    internal void Show(FrameworkElement owner, Rect anchor, IReadOnlyList<string> lines, Brush background, Brush text)
    {
        _popup ??= new ChartTooltipPopup();
        _popup.Show(owner, anchor, lines, background, text, text);
    }

    internal void Hide() => _popup?.Hide();

    /// <summary>The data under a stationary pointer changed: the old cell and its tooltip text no longer match.</summary>
    internal void Reset()
    {
        _hovered = null;
        Hide();
    }

    /// <summary>Follows the pointer: when it entered another cell (or left all of them), remembers that
    /// cell and shows its tooltip through <paramref name="show"/>, or hides the tooltip.</summary>
    internal void Move(Point point, IReadOnlyList<THit> hits, Func<THit, Rect> rectOf, Action<THit> show)
    {
        THit? hovered = null;
        foreach (var hit in hits)
        {
            var rect = rectOf(hit);
            if (!rect.Contains(point))
                continue;

            if (rect.Width > 0)
                hovered = hit;
            break;
        }

        if (Nullable.Equals(hovered, _hovered))
            return;

        _hovered = hovered;
        if (hovered is { } cell)
            show(cell);
        else
            Hide();
    }

    internal void Leave()
    {
        if (_hovered is not null)
            Reset();
    }
}
