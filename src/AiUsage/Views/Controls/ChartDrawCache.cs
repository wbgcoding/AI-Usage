using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AiUsage.Views.Controls;

/// <summary>
/// Pens and brushes the hand-drawn charts reuse from one render to the next: a chart redraws on
/// every hover, and a fresh Pen or SolidColorBrush per draw call is garbage and change tracking for
/// nothing. Pens hang off the brush they stroke with, so a theme switch (a new brush instance) starts
/// from a clean slate and the old pens go with the old brush.
/// </summary>
internal static class ChartDrawCache
{
    private const int MaxPensPerBrush = 8;

    private static readonly ConditionalWeakTable<Brush, List<PenEntry>> PensByBrush = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Color, SolidColorBrush> ColorBrushes = new();

    private readonly record struct PenEntry(double Thickness, bool Dotted, bool Round, Pen Pen);

    /// <summary>A text origin on whole device pixels. A position that drifts by fractions while the
    /// window is resized otherwise lets the text snap a pixel up and back down from one frame to the next.</summary>
    internal static System.Windows.Point Snap(double x, double y, double pixelsPerDip) =>
        new(Math.Round(x * pixelsPerDip) / pixelsPerDip, Math.Round(y * pixelsPerDip) / pixelsPerDip);

    /// <summary>A frozen brush of one solid color, shared by every chart.</summary>
    internal static SolidColorBrush BrushFor(Color color) =>
        ColorBrushes.GetOrAdd(color, c =>
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        });

    /// <summary>A pen of <paramref name="thickness"/> in <paramref name="brush"/>, dotted or with round
    /// joins and caps on request; frozen whenever the brush allows it.</summary>
    internal static Pen PenFor(Brush brush, double thickness, bool dotted = false, bool round = false)
    {
        var pens = PensByBrush.GetOrCreateValue(brush);
        lock (pens)
        {
            foreach (var entry in pens)
            {
                if (entry.Thickness == thickness && entry.Dotted == dotted && entry.Round == round)
                    return entry.Pen;
            }

            var pen = new Pen(brush, thickness);
            if (dotted)
                pen.DashStyle = DashStyles.Dot;
            if (round)
            {
                pen.LineJoin = PenLineJoin.Round;
                pen.StartLineCap = PenLineCap.Round;
                pen.EndLineCap = PenLineCap.Round;
            }

            if (pen.CanFreeze)
                pen.Freeze();
            if (pens.Count >= MaxPensPerBrush)
                pens.RemoveAt(0);
            pens.Add(new PenEntry(thickness, dotted, round, pen));
            return pen;
        }
    }
}
