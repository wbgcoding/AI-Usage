using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using AiUsage.Stats;

namespace AiUsage.Views.Controls;

/// <summary>Which of the figures bar's four mini charts <see cref="StatsFigureMiniChart.Kind"/>
/// draws - each card names its own shape (see the type's own remarks), never picked from the data
/// itself, so a card with an empty period still draws its own kind of chart, just an empty one.
/// </summary>
public enum StatsFigureMiniChartKind
{
    AreaSparkline,
    TwoBar,
    Progress,
    Columns,
}

/// <summary>One figure card's own 28 px chart, drawn on this element directly (no charting library,
/// the same convention <see cref="StatsBarChart"/> and <see cref="StatsHorizontalBarChart"/> already
/// follow) - purely decorative, so it never joins the accessibility tree at all (<see
/// cref="OnCreateAutomationPeer"/>): the card's own accessible name already carries its value and
/// caption, and a screen reader gains nothing from a shape it cannot see.</summary>
public sealed class StatsFigureMiniChart : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatsFigureMiniChartKind), typeof(StatsFigureMiniChart),
        new FrameworkPropertyMetadata(StatsFigureMiniChartKind.AreaSparkline, FrameworkPropertyMetadataOptions.AffectsRender));

    public StatsFigureMiniChart()
    {
        // A long period can hand over far more points than fit; nothing may be drawn past the card.
        ClipToBounds = true;
    }

    public StatsFigureMiniChartKind Kind
    {
        get => (StatsFigureMiniChartKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The shared daily-totals series (<see cref="StatsAggregator.DailyTotalsSeries"/>) -
    /// what <see cref="StatsFigureMiniChartKind.AreaSparkline"/> and <see
    /// cref="StatsFigureMiniChartKind.Columns"/> both draw from, so every card that shows the
    /// period's daily shape agrees on the same days even though each draws them differently. Unused by <see
    /// cref="StatsFigureMiniChartKind.TwoBar"/> and <see cref="StatsFigureMiniChartKind.Progress"/>.
    /// </summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<long>), typeof(StatsFigureMiniChart),
        new FrameworkPropertyMetadata(Array.Empty<long>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<long> Values
    {
        get => (IReadOnlyList<long>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>The "previous" bar's own length for <see cref="StatsFigureMiniChartKind.TwoBar"/>.</summary>
    public static readonly DependencyProperty PreviousValueProperty = DependencyProperty.Register(
        nameof(PreviousValue), typeof(double), typeof(StatsFigureMiniChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PreviousValue
    {
        get => (double)GetValue(PreviousValueProperty);
        set => SetValue(PreviousValueProperty, value);
    }

    /// <summary>The "current" bar's own length for <see cref="StatsFigureMiniChartKind.TwoBar"/>.</summary>
    public static readonly DependencyProperty CurrentValueProperty = DependencyProperty.Register(
        nameof(CurrentValue), typeof(double), typeof(StatsFigureMiniChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double CurrentValue
    {
        get => (double)GetValue(CurrentValueProperty);
        set => SetValue(CurrentValueProperty, value);
    }

    /// <summary>The filled portion of <see cref="StatsFigureMiniChartKind.Progress"/> - active days.</summary>
    public static readonly DependencyProperty NumeratorProperty = DependencyProperty.Register(
        nameof(Numerator), typeof(double), typeof(StatsFigureMiniChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Numerator
    {
        get => (double)GetValue(NumeratorProperty);
        set => SetValue(NumeratorProperty, value);
    }

    /// <summary>The whole bar's own length of <see cref="StatsFigureMiniChartKind.Progress"/> - days
    /// in the period.</summary>
    public static readonly DependencyProperty DenominatorProperty = DependencyProperty.Register(
        nameof(Denominator), typeof(double), typeof(StatsFigureMiniChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Denominator
    {
        get => (double)GetValue(DenominatorProperty);
        set => SetValue(DenominatorProperty, value);
    }

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(StatsFigureMiniChart), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
        nameof(MutedBrush), typeof(Brush), typeof(StatsFigureMiniChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush MutedBrush
    {
        get => (Brush)GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    /// <summary>Decorative only - see this type's own remarks.</summary>
    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 28 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        switch (Kind)
        {
            case StatsFigureMiniChartKind.AreaSparkline:
                DrawSparkline(drawingContext, width, height);
                break;
            case StatsFigureMiniChartKind.TwoBar:
                DrawTwoBar(drawingContext, width, height);
                break;
            case StatsFigureMiniChartKind.Progress:
                DrawProgress(drawingContext, width, height);
                break;
            case StatsFigureMiniChartKind.Columns:
                DrawColumns(drawingContext, width, height);
                break;
        }
    }

    /// <summary>Folds a daily series that has more points than half the width in pixels into fewer
    /// points, each the average per day of its bundle of consecutive days (never the sum, so a bundled
    /// point stays comparable with a single day). A series that already fits comes back unchanged.</summary>
    internal static IReadOnlyList<double> BundleDailyAverages(IReadOnlyList<long> values, double width)
    {
        var daysPerPoint = DaysPerPoint(values.Count, width);
        if (daysPerPoint == 1)
            return values.Select(value => (double)value).ToList();

        var bundled = new List<double>((values.Count + daysPerPoint - 1) / daysPerPoint);
        for (var start = 0; start < values.Count; start += daysPerPoint)
        {
            var count = Math.Min(daysPerPoint, values.Count - start);
            long sum = 0;
            for (var i = start; i < start + count; i++)
                sum += values[i];
            bundled.Add(sum / (double)count);
        }

        return bundled;
    }

    /// <summary>How many consecutive days one drawn point stands for: 1 while the series fits at two
    /// pixels per point.</summary>
    private static int DaysPerPoint(int valueCount, double width)
    {
        var maxPoints = Math.Max(1, (int)(width / 2));
        return valueCount <= maxPoints ? 1 : (int)Math.Ceiling(valueCount / (double)maxPoints);
    }

    /// <summary>The drawn point that holds the busiest raw day - not the point with the highest
    /// average, which can be a different bundle (a single spike day inside a bundle of quiet days
    /// averages below a bundle of steadily busy ones). Same first-wins tie rule as <see
    /// cref="StatsAggregator.BusiestIndex"/>.</summary>
    internal static int? BusiestPointIndex(IReadOnlyList<long> values, double width) =>
        StatsAggregator.BusiestIndex(values) is { } day ? day / DaysPerPoint(values.Count, width) : null;

    private void DrawSparkline(DrawingContext drawingContext, double width, double height)
    {
        var values = BundleDailyAverages(Values, width);
        if (values.Count == 0)
            return;

        var max = Math.Max(1, values.Max());
        var stepX = values.Count > 1 ? width / (values.Count - 1) : 0;
        double PointY(double value) => height - value / max * height;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, PointY(values[0])), true, false);
            for (var i = 1; i < values.Count; i++)
                ctx.LineTo(new Point(i * stepX, PointY(values[i])), true, false);

            ctx.LineTo(new Point((values.Count - 1) * stepX, height), false, false);
            ctx.LineTo(new Point(0, height), false, false);
        }
        geometry.Freeze();

        var fillBrush = AccentBrush.Clone();
        fillBrush.Opacity = 0.25;
        drawingContext.DrawGeometry(fillBrush, null, geometry);
        drawingContext.DrawGeometry(null, new Pen(AccentBrush, 1.5), geometry);
    }

    private void DrawTwoBar(DrawingContext drawingContext, double width, double height)
    {
        var max = Math.Max(1, Math.Max(PreviousValue, CurrentValue));
        var barHeight = (height - 2) / 2;

        var previousWidth = Math.Clamp(PreviousValue / max, 0, 1) * width;
        var currentWidth = Math.Clamp(CurrentValue / max, 0, 1) * width;

        drawingContext.DrawRectangle(MutedBrush, null, new Rect(0, 0, previousWidth, barHeight));
        drawingContext.DrawRectangle(AccentBrush, null, new Rect(0, barHeight + 2, currentWidth, barHeight));
    }

    private void DrawProgress(DrawingContext drawingContext, double width, double height)
    {
        var barHeight = Math.Min(4, height);
        var y = (height - barHeight) / 2;
        var fraction = Denominator > 0 ? Math.Clamp(Numerator / Denominator, 0, 1) : 0;

        drawingContext.DrawRectangle(MutedBrush, null, new Rect(0, y, width, barHeight));
        if (fraction > 0)
            drawingContext.DrawRectangle(AccentBrush, null, new Rect(0, y, width * fraction, barHeight));
    }

    private void DrawColumns(DrawingContext drawingContext, double width, double height)
    {
        var values = BundleDailyAverages(Values, width);
        if (values.Count == 0)
            return;

        var max = Math.Max(1, values.Max());
        var busiestIndex = BusiestPointIndex(Values, width);
        const double gap = 1;
        var columnWidth = Math.Max(1, (width - gap * (values.Count - 1)) / values.Count);

        for (var i = 0; i < values.Count; i++)
        {
            var columnHeight = values[i] / max * height;
            var x = i * (columnWidth + gap);
            var brush = i == busiestIndex ? AccentBrush : MutedBrush;
            drawingContext.DrawRectangle(brush, null, new Rect(x, height - columnHeight, columnWidth, columnHeight));
        }
    }
}
