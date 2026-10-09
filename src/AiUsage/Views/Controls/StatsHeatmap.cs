using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace AiUsage.Views.Controls;

/// <summary>
/// Self-drawn weekday by hour grid: seven rows (the culture's first day of the week on top) and 24
/// columns, one cell per weekday and hour of the day, shaded in steps of the accent color by the
/// tokens that fell into it. Built like <see cref="StatsMonthGrid"/>: a bare <see
/// cref="FrameworkElement"/> that draws itself, keeps the cell rectangles of the last render for hit
/// testing, and can be walked cell by cell with the arrow keys.
/// </summary>
public sealed class StatsHeatmap : FrameworkElement
{
    internal const int Rows = 7;
    internal const int Columns = 24;

    private const double LeftLabelWidth = 30;
    private const double TopLabelHeight = 16;
    private const double CellGap = 2;
    private const double CellCorner = 2;
    private const double MaxCellHeight = 22;
    private const double MinCellHeight = 12;
    private const double LabelFontSize = 10;
    private const double DefaultWidth = 480;

    public StatsHeatmap()
    {
        ClipToBounds = true;
        IsHitTestVisible = true;
        Focusable = true;
        FocusVisualStyle = null;
    }

    /// <summary>The 7 x 24 totals, row by row, the first row being <see cref="FirstDay"/>.</summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<long>), typeof(StatsHeatmap),
        new FrameworkPropertyMetadata(Array.Empty<long>(), FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public IReadOnlyList<long> Values
    {
        get => (IReadOnlyList<long>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>The weekday of the first row.</summary>
    public static readonly DependencyProperty FirstDayProperty = DependencyProperty.Register(
        nameof(FirstDay), typeof(DayOfWeek), typeof(StatsHeatmap),
        new FrameworkPropertyMetadata(DayOfWeek.Monday, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public DayOfWeek FirstDay
    {
        get => (DayOfWeek)GetValue(FirstDayProperty);
        set => SetValue(FirstDayProperty, value);
    }

    /// <summary>The hover text with four placeholders: weekday, first hour, last hour, token count.</summary>
    public static readonly DependencyProperty TooltipFormatProperty = DependencyProperty.Register(
        nameof(TooltipFormat), typeof(string), typeof(StatsHeatmap), new FrameworkPropertyMetadata("", OnDataChanged));

    public string TooltipFormat
    {
        get => (string)GetValue(TooltipFormatProperty);
        set => SetValue(TooltipFormatProperty, value);
    }

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(StatsHeatmap),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(StatsHeatmap),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public static readonly DependencyProperty EmptyCellBrushProperty = DependencyProperty.Register(
        nameof(EmptyCellBrush), typeof(Brush), typeof(StatsHeatmap),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush EmptyCellBrush
    {
        get => (Brush)GetValue(EmptyCellBrushProperty);
        set => SetValue(EmptyCellBrushProperty, value);
    }

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(StatsHeatmap),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public static readonly DependencyProperty TooltipBackgroundProperty = DependencyProperty.Register(
        nameof(TooltipBackground), typeof(Brush), typeof(StatsHeatmap),
        new FrameworkPropertyMetadata(Brushes.Black));

    public Brush TooltipBackground
    {
        get => (Brush)GetValue(TooltipBackgroundProperty);
        set => SetValue(TooltipBackgroundProperty, value);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var heatmap = (StatsHeatmap)d;
        heatmap.UpdateSummaryName();
        if (heatmap._focusedCell is not null)
            heatmap.UpdateFocusedAutomationName();
    }

    /// <summary>True when there is at least one token anywhere in the grid.</summary>
    private bool HasData => Values.Count >= Rows * Columns && Values.Any(value => value > 0);

    // ---- Pure helpers ----

    /// <summary>The row's weekday: the first day, then the days after it.</summary>
    internal static DayOfWeek DayOfRow(DayOfWeek firstDay, int row) => (DayOfWeek)(((int)firstDay + row) % 7);

    /// <summary>The shade of a cell, 0 (empty) to 4, by the cell's rank among the non-zero cells so a
    /// few very busy hours do not flatten everything else.</summary>
    internal static int Step(long value, IReadOnlyList<long> sortedNonZero) =>
        StatsMonthGrid.QuantileStep(value, sortedNonZero);

    internal static IReadOnlyList<long> SortedNonZero(IReadOnlyList<long> values) =>
        values.Where(value => value > 0).Order().ToList();

    /// <summary>Where an arrow key moves the focused cell: one step in that direction, stopping at the
    /// edge; Home and End jump to the first and last column of the row. A null current cell yields
    /// the first cell.</summary>
    internal static (int Row, int Column) MoveFocus((int Row, int Column)? current, Key key)
    {
        if (current is not { } cell)
            return (0, 0);

        return key switch
        {
            Key.Left => (cell.Row, Math.Max(0, cell.Column - 1)),
            Key.Right => (cell.Row, Math.Min(Columns - 1, cell.Column + 1)),
            Key.Up => (Math.Max(0, cell.Row - 1), cell.Column),
            Key.Down => (Math.Min(Rows - 1, cell.Row + 1), cell.Column),
            Key.Home => (cell.Row, 0),
            Key.End => (cell.Row, Columns - 1),
            _ => cell,
        };
    }

    /// <summary>The hour label shown for the start or the end of a cell's hour, in the culture's own
    /// short time pattern.</summary>
    internal static string HourLabel(int hour, CultureInfo culture) =>
        new DateTime(2000, 1, 1, hour % 24, 0, 0).ToString("t", culture);

    /// <summary>The text of one cell: weekday, the hour it covers and the token count.</summary>
    internal static string BuildTooltip(string format, DayOfWeek day, int hour, long total, CultureInfo culture) =>
        string.Format(
            culture, format, culture.DateTimeFormat.DayNames[(int)day], HourLabel(hour, culture), HourLabel(hour + 1, culture),
            total.ToString("N0", culture));

    /// <summary>The column labels: every third hour, as a plain number.</summary>
    internal static bool HasColumnLabel(int column) => column % 3 == 0;

    // ---- Layout ----

    private readonly record struct CellHit(Rect Rect, int Row, int Column);

    private readonly List<CellHit> _cellHits = [];
    private CellHit? _hoverCell;
    private (int Row, int Column)? _focusedCell;

    private static double CellHeightFor(double width)
    {
        var cellWidth = (width - LeftLabelWidth - (Columns - 1) * CellGap) / Columns;
        return Math.Clamp(cellWidth, MinCellHeight, MaxCellHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width;
        var height = TopLabelHeight + Rows * CellHeightFor(width) + (Rows - 1) * CellGap;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        _cellHits.Clear();

        var width = ActualWidth;
        if (width <= 0 || Values.Count < Rows * Columns)
            return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = ChartFonts.UiTypeface(this);
        var culture = CultureInfo.CurrentCulture;

        if (!HasData)
        {
            var empty = new FormattedText(
                EmptyText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12, TextBrush, dpi)
            {
                MaxTextWidth = Math.Max(1, width),
                TextAlignment = TextAlignment.Center,
            };
            drawingContext.DrawText(empty, new Point(0, Math.Max(0, (ActualHeight - empty.Height) / 2)));
            return;
        }

        var cellWidth = (width - LeftLabelWidth - (Columns - 1) * CellGap) / Columns;
        var cellHeight = CellHeightFor(width);
        var sorted = SortedNonZero(Values);
        var accent = AccentBrush is SolidColorBrush solid ? solid.Color : Colors.SteelBlue;

        for (var column = 0; column < Columns; column++)
        {
            if (!HasColumnLabel(column))
                continue;

            var x = LeftLabelWidth + column * (cellWidth + CellGap);
            var label = new FormattedText(
                column.ToString(culture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, LabelFontSize, TextBrush, dpi);
            drawingContext.DrawText(label, new Point(x, (TopLabelHeight - label.Height) / 2));
        }

        for (var row = 0; row < Rows; row++)
        {
            var y = TopLabelHeight + row * (cellHeight + CellGap);
            var day = DayOfRow(FirstDay, row);
            var rowLabel = new FormattedText(
                culture.DateTimeFormat.AbbreviatedDayNames[(int)day], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, LabelFontSize, TextBrush, dpi);
            drawingContext.DrawText(rowLabel, new Point(0, y + (cellHeight - rowLabel.Height) / 2));

            for (var column = 0; column < Columns; column++)
            {
                var value = Values[row * Columns + column];
                var rect = new Rect(LeftLabelWidth + column * (cellWidth + CellGap), y, cellWidth, cellHeight);
                var step = Step(value, sorted);
                var fill = step == 0 ? EmptyCellBrush : StatsMonthGrid.IntensityBrush(accent, step);
                drawingContext.DrawRoundedRectangle(fill, null, rect, CellCorner, CellCorner);

                if (IsKeyboardFocused && _focusedCell == (row, column))
                {
                    var ring = new Rect(rect.X - 1.5, rect.Y - 1.5, rect.Width + 3, rect.Height + 3);
                    drawingContext.DrawRoundedRectangle(null, new Pen(AccentBrush, 1.5), ring, CellCorner + 1, CellCorner + 1);
                }

                _cellHits.Add(new CellHit(rect, row, column));
            }
        }
    }

    // ---- Tooltip, mouse, keyboard ----

    private ChartTooltipPopup? _tooltipPopup;

    private string TextOf(int row, int column) =>
        BuildTooltip(TooltipFormat, DayOfRow(FirstDay, row), column, Values[row * Columns + column], CultureInfo.CurrentCulture);

    private void ShowTooltip(CellHit hit)
    {
        _tooltipPopup ??= new ChartTooltipPopup();
        _tooltipPopup.Show(this, hit.Rect, [TextOf(hit.Row, hit.Column)], TooltipBackground, TextBrush, TextBrush);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var hit = _cellHits.FirstOrDefault(cell => cell.Rect.Contains(point));
        var hovered = hit.Rect.Width > 0 ? hit : (CellHit?)null;
        if (Nullable.Equals(hovered, _hoverCell))
            return;

        _hoverCell = hovered;
        if (hovered is { } cell)
            ShowTooltip(cell);
        else
            _tooltipPopup?.Hide();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverCell is null)
            return;

        _hoverCell = null;
        _tooltipPopup?.Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End) || Values.Count < Rows * Columns)
            return;

        e.Handled = true;
        var moved = MoveFocus(_focusedCell, e.Key);
        if (moved == _focusedCell)
            return;

        _focusedCell = moved;
        OnFocusedCellChanged();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (Values.Count < Rows * Columns)
            return;

        _focusedCell = MoveFocus(null, Key.None);
        OnFocusedCellChanged();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_focusedCell is null)
            return;

        _focusedCell = null;
        _tooltipPopup?.Hide();
        UpdateSummaryName();
        InvalidateVisual();
    }

    private void OnFocusedCellChanged()
    {
        UpdateFocusedAutomationName();
        InvalidateVisual();
        if (_focusedCell is { } cell && _cellHits.FirstOrDefault(hit => hit.Row == cell.Row && hit.Column == cell.Column) is { Rect.Width: > 0 } found)
            ShowTooltip(found);
    }

    private void UpdateFocusedAutomationName()
    {
        if (_focusedCell is not { } cell || Values.Count < Rows * Columns)
            return;

        AutomationProperties.SetName(this, TextOf(cell.Row, cell.Column));
    }

    /// <summary>What a screen reader announces for the whole grid: the busiest weekday and hour.</summary>
    private void UpdateSummaryName()
    {
        if (Values.Count < Rows * Columns || !HasData)
        {
            AutomationProperties.SetName(this, EmptyText);
            return;
        }

        var best = 0;
        for (var i = 1; i < Values.Count; i++)
        {
            if (Values[i] > Values[best])
                best = i;
        }

        AutomationProperties.SetName(this, TextOf(best / Columns, best % Columns));
    }
}
