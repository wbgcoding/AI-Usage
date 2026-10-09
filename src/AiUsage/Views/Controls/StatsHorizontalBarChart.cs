using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiUsage.Services;
using AiUsage.Stats;

namespace AiUsage.Views.Controls;

/// <summary>
/// Self-drawn horizontal bar chart for the "top projects" panel - each row's own last folder segment
/// on the left (further trimmed with an ellipsis, in the middle, when even that does not fit; still
/// pixel-clipped with a trailing ellipsis as a last resort if the row's own <see
/// cref="Stats.StatsProjectRow"/> preparation left it too wide), a bar proportional to the
/// row's own share of the largest row, and the shortened figure on the right. Hovering a row shows its
/// full, untrimmed path plus its total, share and first/last activity day as this control's own <see
/// cref="FrameworkElement.ToolTip"/> - a shortened label alone can no longer tell two similarly-named
/// projects under different parents apart. Rows are drawn in whatever order <see cref="Rows"/> already
/// lists them in - the caller (the aggregator function) is the one that sorts them, longest first.
/// </summary>
public sealed class StatsHorizontalBarChart : FrameworkElement
{
    public StatsHorizontalBarChart()
    {
        ClipToBounds = true;
        IsHitTestVisible = true;
    }

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IReadOnlyList<StatsProjectRow>), typeof(StatsHorizontalBarChart),
        new FrameworkPropertyMetadata(Array.Empty<StatsProjectRow>(), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnRowsChanged));

    public IReadOnlyList<StatsProjectRow> Rows
    {
        get => (IReadOnlyList<StatsProjectRow>)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (StatsHorizontalBarChart)d;
        chart._iconCache.Clear();
        chart._focusedRowIndex = -1;
        AutomationProperties.SetName(chart, BuildAccessibleSummary((IReadOnlyList<StatsProjectRow>)e.NewValue));
    }

    /// <summary>Opts a row-per-project chart into 852's richer row (icon or color dot, bold name,
    /// muted middle-trimmed path, right-aligned value and share, a thin colored bar under the row)
    /// and its own popup tooltip - the "top projects" panel's own <c>TopProjectsChart</c> sets this,
    /// the day-detail panel's plainer model/project mini-lists never do, so they keep the original
    /// single-line row and plain <see cref="System.Windows.FrameworkElement.ToolTip"/> string exactly
    /// as before.</summary>
    public static readonly DependencyProperty ShowProjectDetailsProperty = DependencyProperty.Register(
        nameof(ShowProjectDetails), typeof(bool), typeof(StatsHorizontalBarChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnShowProjectDetailsChanged));

    public bool ShowProjectDetails
    {
        get => (bool)GetValue(ShowProjectDetailsProperty);
        set => SetValue(ShowProjectDetailsProperty, value);
    }

    private static void OnShowProjectDetailsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Only the rich layout offers row-by-row keyboard navigation, so only it gets a tab stop -
        // the day-detail mini-lists gain no new, unlabeled stop in the window's tab order just by
        // sharing this control.
        var chart = (StatsHorizontalBarChart)d;
        var show = (bool)e.NewValue;
        chart.Focusable = show;
        System.Windows.Input.KeyboardNavigation.SetIsTabStop(chart, show);
    }

    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
        nameof(MutedBrush), typeof(Brush), typeof(StatsHorizontalBarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The folder-path line under a rich row's own name - <see cref="TextBrush"/> stays the
    /// name and figure's own, more prominent color.</summary>
    public Brush MutedBrush
    {
        get => (Brush)GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    public static readonly DependencyProperty TooltipBackgroundProperty = DependencyProperty.Register(
        nameof(TooltipBackground), typeof(Brush), typeof(StatsHorizontalBarChart), new PropertyMetadata(Brushes.White));

    public Brush TooltipBackground
    {
        get => (Brush)GetValue(TooltipBackgroundProperty);
        set => SetValue(TooltipBackgroundProperty, value);
    }

    public static readonly DependencyProperty TooltipBorderBrushProperty = DependencyProperty.Register(
        nameof(TooltipBorderBrush), typeof(Brush), typeof(StatsHorizontalBarChart), new PropertyMetadata(Brushes.Gray));

    public Brush TooltipBorderBrush
    {
        get => (Brush)GetValue(TooltipBorderBrushProperty);
        set => SetValue(TooltipBorderBrushProperty, value);
    }

    public static readonly DependencyProperty ProviderDisplayNamesProperty = DependencyProperty.Register(
        nameof(ProviderDisplayNames), typeof(IReadOnlyDictionary<string, string>), typeof(StatsHorizontalBarChart),
        new PropertyMetadata(new Dictionary<string, string>()));

    public IReadOnlyDictionary<string, string> ProviderDisplayNames
    {
        get => (IReadOnlyDictionary<string, string>)GetValue(ProviderDisplayNamesProperty);
        set => SetValue(ProviderDisplayNamesProperty, value);
    }

    private static DependencyProperty RegisterTipFormat(string name) => DependencyProperty.Register(
        name, typeof(string), typeof(StatsHorizontalBarChart), new PropertyMetadata(""));

    public static readonly DependencyProperty FolderFormatProperty = RegisterTipFormat(nameof(FolderFormat));
    public string FolderFormat { get => (string)GetValue(FolderFormatProperty); set => SetValue(FolderFormatProperty, value); }

    public static readonly DependencyProperty TokensFormatProperty = RegisterTipFormat(nameof(TokensFormat));
    public string TokensFormat { get => (string)GetValue(TokensFormatProperty); set => SetValue(TokensFormatProperty, value); }

    public static readonly DependencyProperty InputOutputCacheFormatProperty = RegisterTipFormat(nameof(InputOutputCacheFormat));
    public string InputOutputCacheFormat { get => (string)GetValue(InputOutputCacheFormatProperty); set => SetValue(InputOutputCacheFormatProperty, value); }

    public static readonly DependencyProperty ProvidersFormatProperty = RegisterTipFormat(nameof(ProvidersFormat));
    public string ProvidersFormat { get => (string)GetValue(ProvidersFormatProperty); set => SetValue(ProvidersFormatProperty, value); }

    public static readonly DependencyProperty MainModelFormatProperty = RegisterTipFormat(nameof(MainModelFormat));
    public string MainModelFormat { get => (string)GetValue(MainModelFormatProperty); set => SetValue(MainModelFormatProperty, value); }

    public static readonly DependencyProperty ActiveFormatProperty = RegisterTipFormat(nameof(ActiveFormat));
    public string ActiveFormat { get => (string)GetValue(ActiveFormatProperty); set => SetValue(ActiveFormatProperty, value); }

    public static readonly DependencyProperty SessionsFormatProperty = RegisterTipFormat(nameof(SessionsFormat));
    public string SessionsFormat { get => (string)GetValue(SessionsFormatProperty); set => SetValue(SessionsFormatProperty, value); }

    /// <summary>The rich row's own hover/keyboard-focus tooltip: the project's own bare name first
    /// (no path, no trimming - <see cref="ShortLabel"/> is what the row itself draws, already
    /// shortened for its own pixel width), then one line per fact that actually exists. Every format
    /// string left empty falls back to a plain, unlabelled value instead of vanishing - only a row's
    /// own genuinely missing data (no provider, no main model, no known session count) ever omits a
    /// line outright. Pure so every line, and every omission, is unit testable without a visual tree.
    /// </summary>
    internal static IReadOnlyList<string> BuildProjectDetailTooltipLines(
        StatsProjectRow row,
        IReadOnlyDictionary<string, string> providerDisplayNames,
        string folderFormat, string tokensFormat, string inputOutputCacheFormat,
        string providersFormat, string mainModelFormat, string activeFormat, string sessionsFormat,
        string tokenWord, CultureInfo culture, string? displayName = null)
    {
        var projectName = displayName ?? ProjectName(row);

        var totalText = row.Total.ToString("N0", culture);
        var totalWithUnit = string.IsNullOrEmpty(tokenWord) ? totalText : $"{totalText} {tokenWord}";
        var percentText = row.Percent.ToString("0.#", culture);
        var inputText = row.InputTokens.ToString("N0", culture);
        var outputText = row.OutputTokens.ToString("N0", culture);
        var cacheText = row.CachedTokens.ToString("N0", culture);

        var lines = new List<string>
        {
            projectName,
            string.IsNullOrEmpty(folderFormat) ? row.FullPath : string.Format(culture, folderFormat, row.FullPath),
            string.IsNullOrEmpty(tokensFormat) ? $"{totalWithUnit} ({percentText}%)" : string.Format(culture, tokensFormat, totalWithUnit, percentText),
            string.IsNullOrEmpty(inputOutputCacheFormat)
                ? $"{inputText} / {outputText} / {cacheText}"
                : string.Format(culture, inputOutputCacheFormat, inputText, outputText, cacheText),
        };

        if (row.ProviderIds is { Count: > 0 } providerIds)
        {
            var joined = string.Join(", ", providerIds.Select(id => providerDisplayNames.GetValueOrDefault(id, id)));
            lines.Add(string.IsNullOrEmpty(providersFormat) ? joined : string.Format(culture, providersFormat, joined));
        }

        if (!string.IsNullOrEmpty(row.MainModel))
        {
            var modelName = ModelDisplayNames.Resolve(row.MainModel);
            lines.Add(string.IsNullOrEmpty(mainModelFormat) ? modelName : string.Format(culture, mainModelFormat, modelName));
        }

        var activeText = StatsTooltipDateFormatter.FormatRange(row.FirstActivity, row.LastActivity, culture);
        lines.Add(string.IsNullOrEmpty(activeFormat) ? activeText : string.Format(culture, activeFormat, activeText));

        if (row.SessionCount is { } sessionCount)
            lines.Add(string.IsNullOrEmpty(sessionsFormat) ? sessionCount.ToString(culture) : string.Format(culture, sessionsFormat, sessionCount));

        return lines;
    }

    /// <summary>A row's own name: its last folder segment, or the full path when it has none.</summary>
    private static string ProjectName(StatsProjectRow row)
    {
        var name = System.IO.Path.GetFileName(
            row.FullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? row.FullPath : name;
    }

    /// <summary>The name each row shows: its short label, and where two rows would read the same,
    /// the name of the folder above it in parentheses so they can be told apart. Pure so it is unit
    /// testable without a visual tree.</summary>
    internal static IReadOnlyList<string> DisplayNames(IReadOnlyList<StatsProjectRow> rows)
    {
        var names = rows.Select(row => row.ShortLabel).ToArray();
        var seen = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Length; i++)
        {
            if (!seen.Contains(names[i]))
                continue;

            var parent = System.IO.Path.GetFileName(
                (System.IO.Path.GetDirectoryName(rows[i].FullPath.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) ?? "")
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent))
                names[i] = $"{names[i]} ({parent})";
        }
        return names;
    }

    /// <summary>Pure so the summary text is unit testable without a visual tree. Names each row by its
    /// own full path, not the shortened label a sighted reader sees on the bar - a screen reader has
    /// no truncated label to disambiguate two same-named folders under different parents.</summary>
    internal static string BuildAccessibleSummary(IReadOnlyList<StatsProjectRow> rows) =>
        string.Join("; ", rows.Select(row => $"{row.FullPath}: {row.Total.ToString(CultureInfo.CurrentCulture)}"));

    public static readonly DependencyProperty TokenWordProperty = DependencyProperty.Register(
        nameof(TokenWord), typeof(string), typeof(StatsHorizontalBarChart), new PropertyMetadata(""));

    public string TokenWord
    {
        get => (string)GetValue(TokenWordProperty);
        set => SetValue(TokenWordProperty, value);
    }

    public static readonly DependencyProperty TotalLabelProperty = DependencyProperty.Register(
        nameof(TotalLabel), typeof(string), typeof(StatsHorizontalBarChart), new PropertyMetadata(""));

    public string TotalLabel
    {
        get => (string)GetValue(TotalLabelProperty);
        set => SetValue(TotalLabelProperty, value);
    }

    public static readonly DependencyProperty ActiveLabelProperty = DependencyProperty.Register(
        nameof(ActiveLabel), typeof(string), typeof(StatsHorizontalBarChart), new PropertyMetadata(""));

    public string ActiveLabel
    {
        get => (string)GetValue(ActiveLabelProperty);
        set => SetValue(ActiveLabelProperty, value);
    }

    /// <summary>The row's own full path, its total and share, and the first/last day it saw any
    /// tokens in the current period - what the hover tooltip shows beyond the bar's own shortened
    /// label, since that label alone can no longer tell two similarly-named projects apart. Pure so
    /// it is unit testable without a visual tree.</summary>
    internal static IReadOnlyList<string> BuildTooltipLines(StatsProjectRow row, string tokenWord, string totalLabel, string activeLabel, CultureInfo culture)
    {
        var totalText = row.Total.ToString("N0", culture);
        var totalWithUnit = string.IsNullOrEmpty(tokenWord) ? totalText : $"{totalText} {tokenWord}";
        var activeText = StatsTooltipDateFormatter.FormatRange(row.FirstActivity, row.LastActivity, culture);

        return
        [
            row.FullPath,
            string.IsNullOrEmpty(totalLabel) ? totalWithUnit : $"{totalLabel}: {totalWithUnit}",
            StatsBarChart.PercentLabel(row.Percent, culture),
            string.IsNullOrEmpty(activeLabel) ? activeText : $"{activeLabel}: {activeText}",
        ];
    }

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(StatsHorizontalBarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(StatsHorizontalBarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public static readonly DependencyProperty ThousandSuffixProperty = DependencyProperty.Register(
        nameof(ThousandSuffix), typeof(string), typeof(StatsHorizontalBarChart),
        new FrameworkPropertyMetadata("K", FrameworkPropertyMetadataOptions.AffectsRender));

    public string ThousandSuffix
    {
        get => (string)GetValue(ThousandSuffixProperty);
        set => SetValue(ThousandSuffixProperty, value);
    }

    public static readonly DependencyProperty MillionSuffixProperty = DependencyProperty.Register(
        nameof(MillionSuffix), typeof(string), typeof(StatsHorizontalBarChart),
        new FrameworkPropertyMetadata("M", FrameworkPropertyMetadataOptions.AffectsRender));

    public string MillionSuffix
    {
        get => (string)GetValue(MillionSuffixProperty);
        set => SetValue(MillionSuffixProperty, value);
    }

    public static readonly DependencyProperty BillionSuffixProperty = DependencyProperty.Register(
        nameof(BillionSuffix), typeof(string), typeof(StatsHorizontalBarChart),
        new FrameworkPropertyMetadata("B", FrameworkPropertyMetadataOptions.AffectsRender));

    public string BillionSuffix
    {
        get => (string)GetValue(BillionSuffixProperty);
        set => SetValue(BillionSuffixProperty, value);
    }

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(StatsHorizontalBarChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    private const double RowHeight = 22;
    private const double NameColumnWidth = 0.35;
    private const double FigureColumnWidth = 56;
    private const double NameBarGap = 6;
    private const double MinBarWidth = 4;

    // The rich row (852): an icon/dot plus one text line on top of a thin colored share bar,
    // taller than the plain single-line row above.
    private const double RichRowHeight = 32;
    private const double RichIconSize = 16;
    private const double RichDotSize = 10;
    private const double RichBarHeight = 3;
    private const double RichBarGap = 2;
    private const double RichIconGap = 6;
    private const double RichFigureGap = 8;

    private double CurrentRowHeight => ShowProjectDetails ? RichRowHeight : RowHeight;

    /// <summary>The width of the name column of a plain row: at least its fixed share of the chart,
    /// widened toward the widest name so a short model name is not cut while the bar still has room,
    /// but never so wide that the bar shrinks below <see cref="MinBarWidth"/>. Pure so it is unit
    /// testable without a visual tree.</summary>
    internal static double NameColumnWidthFor(double chartWidth, double widestName)
    {
        var share = chartWidth * NameColumnWidth;
        var roomForNames = chartWidth - FigureColumnWidth - NameBarGap - MinBarWidth;
        return Math.Max(share, Math.Min(widestName + 2, roomForNames));
    }

    /// <summary>The font size of the names in a plain row: the regular size while the widest name fits
    /// its column, stepping down in proportion to a lower limit when it does not, so a narrow chart
    /// shows as much of every name as it can before the ellipsis takes over. Pure so it is unit
    /// testable without a visual tree.</summary>
    internal static double NameFontSizeFor(double widestNameAtRegular, double nameColumnWidth)
    {
        if (widestNameAtRegular <= 0 || widestNameAtRegular <= nameColumnWidth)
            return NameFontSize;
        return Math.Clamp(NameFontSize * nameColumnWidth / widestNameAtRegular, NameFontSizeMin, NameFontSize);
    }

    private const double NameFontSize = 11;
    private const double NameFontSizeMin = 8.5;

    /// <summary>Every row's own top Y position for a chart <paramref name="rowCount"/> rows tall -
    /// pure so it is unit testable without a visual tree.</summary>
    internal static IReadOnlyList<double> RowTops(int rowCount, double rowHeight = RowHeight) =>
        Enumerable.Range(0, rowCount).Select(i => i * rowHeight).ToList();

    /// <summary>A row's own bar width, proportional to its value's share of <paramref
    /// name="maxValue"/> (the largest row's own value, not the sum of every row - a "top N" bar
    /// chart's own bars are each read against the longest one, not stacked toward a shared total).
    /// Pure so it is unit testable without a visual tree.</summary>
    internal static double BarWidth(long value, long maxValue, double maxWidth) =>
        maxValue <= 0 ? 0 : Math.Max(0, value) / (double)maxValue * maxWidth;

    private readonly Dictionary<string, BitmapSource?> _iconCache = [];

    /// <summary>Decodes and caches <paramref name="iconPath"/> once - re-decoding a project's icon
    /// on every single render pass would undo the whole point of <see cref="ProjectColorResolver"/>'s
    /// own on-disk cache. Cleared whenever <see cref="Rows"/> changes (<see cref="OnRowsChanged"/>),
    /// since a project's icon can only ever change between one <see cref="Rows"/> assignment and the
    /// next, never mid-render.</summary>
    private BitmapSource? GetIcon(string? iconPath)
    {
        if (string.IsNullOrEmpty(iconPath))
            return null;
        if (_iconCache.TryGetValue(iconPath, out var cached))
            return cached;

        BitmapSource? decoded = null;
        try
        {
            using var stream = System.IO.File.OpenRead(iconPath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 0)
                decoded = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            decoded = null;
        }

        _iconCache[iconPath] = decoded;
        return decoded;
    }

    private int _hoverIndex = -1;
    private int _focusedRowIndex = -1;
    private ChartTooltipPopup? _tooltipPopup;

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var rows = Rows;
        var rowHeight = CurrentRowHeight;
        var index = rows.Count == 0 ? -1 : Math.Clamp((int)(e.GetPosition(this).Y / rowHeight), 0, rows.Count - 1);

        if (!ShowProjectDetails)
        {
            ToolTip = index >= 0 && index < rows.Count
                ? string.Join("\n", BuildTooltipLines(rows[index], TokenWord, TotalLabel, ActiveLabel, CultureInfo.CurrentCulture))
                : null;
            return;
        }

        var mouse = e.GetPosition(this);
        if (index == _hoverIndex)
        {
            if (index >= 0)
                _tooltipPopup?.MoveTo(mouse);
            return;
        }
        _hoverIndex = index;
        UpdateRichTooltip(mouse);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!ShowProjectDetails)
        {
            ToolTip = null;
            return;
        }

        _hoverIndex = -1;
        _tooltipPopup?.Hide();
    }

    private void UpdateRichTooltip(Point? mouse = null)
    {
        var rows = Rows;
        if (_hoverIndex < 0 || _hoverIndex >= rows.Count || ActualWidth <= 0)
        {
            _tooltipPopup?.Hide();
            return;
        }

        var anchorRect = new Rect(0, _hoverIndex * RichRowHeight, ActualWidth, RichRowHeight);
        var lines = BuildProjectDetailTooltipLines(
            rows[_hoverIndex], ProviderDisplayNames, FolderFormat, TokensFormat, InputOutputCacheFormat,
            ProvidersFormat, MainModelFormat, ActiveFormat, SessionsFormat, TokenWord, CultureInfo.CurrentCulture,
            DisplayNames(rows)[_hoverIndex]);
        _tooltipPopup ??= new ChartTooltipPopup();
        _tooltipPopup.Show(this, anchorRect, lines, TooltipBackground, TextBrush, TooltipBorderBrush, mouse);
    }

    /// <summary>Arrow-key navigation between rows for the rich layout only - the day-detail
    /// mini-lists are never focusable in the first place (<see cref="OnShowProjectDetailsChanged"/>),
    /// so this never fires for them.</summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var rows = Rows;
        if (!ShowProjectDetails || rows.Count == 0)
            return;

        if (e.Key == System.Windows.Input.Key.Down)
        {
            _focusedRowIndex = Math.Min(rows.Count - 1, Math.Max(0, _focusedRowIndex) + 1);
            UpdateFocusedRowAutomationName();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Up)
        {
            _focusedRowIndex = Math.Max(0, _focusedRowIndex - 1);
            UpdateFocusedRowAutomationName();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnGotKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (!ShowProjectDetails || Rows.Count == 0)
            return;

        _focusedRowIndex = Math.Clamp(_focusedRowIndex < 0 ? 0 : _focusedRowIndex, 0, Rows.Count - 1);
        UpdateFocusedRowAutomationName();
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_focusedRowIndex == -1)
            return;

        _focusedRowIndex = -1;
        AutomationProperties.SetName(this, BuildAccessibleSummary(Rows));
        InvalidateVisual();
    }

    /// <summary>What a screen reader announces for the currently keyboard-focused row - the exact
    /// same lines <see cref="UpdateRichTooltip"/> shows a mouse, joined into one sentence instead of
    /// one popup per line.</summary>
    private void UpdateFocusedRowAutomationName()
    {
        var rows = Rows;
        if (_focusedRowIndex < 0 || _focusedRowIndex >= rows.Count)
            return;

        var lines = BuildProjectDetailTooltipLines(
            rows[_focusedRowIndex], ProviderDisplayNames, FolderFormat, TokensFormat, InputOutputCacheFormat,
            ProvidersFormat, MainModelFormat, ActiveFormat, SessionsFormat, TokenWord, CultureInfo.CurrentCulture,
            DisplayNames(rows)[_focusedRowIndex]);
        AutomationProperties.SetName(this, string.Join(". ", lines));
    }

    /// <summary>The bar brush of a row in the plain drawing mode: the row's own color when it has
    /// one, <paramref name="fallback"/> otherwise. Pure so it is unit testable without a visual
    /// tree.</summary>
    internal static Brush PlainRowBrush(StatsProjectRow row, Brush fallback) =>
        row.Color == default ? fallback : new SolidColorBrush(row.Color);

    /// <summary>As tall as its rows, so a panel with one project is one row high instead of a
    /// fixed block of empty space; with no rows, one row of height for the empty text.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, Math.Max(1, Rows.Count) * CurrentRowHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var rows = Rows;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = ChartFonts.UiTypeface(this);

        if (rows.Count == 0)
        {
            if (!string.IsNullOrEmpty(EmptyText))
            {
                var formatted = new FormattedText(
                    EmptyText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12, TextBrush, dpi);
                drawingContext.DrawText(formatted, new Point((width - formatted.Width) / 2, (height - formatted.Height) / 2));
            }
            return;
        }

        var maxValue = rows.Max(row => row.Total);

        if (ShowProjectDetails)
        {
            var semiBold = new Typeface(typeface.FontFamily, typeface.Style, FontWeights.SemiBold, typeface.Stretch);
            var tops = RowTops(rows.Count, RichRowHeight);
            var names = DisplayNames(rows);
            for (var i = 0; i < rows.Count; i++)
            {
                var y = tops[i];
                if (y >= height)
                    break;
                DrawRichRow(drawingContext, rows[i], names[i], y, width, maxValue, typeface, semiBold, dpi, i == _focusedRowIndex);
            }
            return;
        }

        var widestName = rows
            .Select(row => new FormattedText(
                row.ShortLabel, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, NameFontSize, TextBrush, dpi).Width)
            .DefaultIfEmpty(0)
            .Max();
        var nameWidth = NameColumnWidthFor(width, widestName);
        var nameFontSize = NameFontSizeFor(widestName, nameWidth);
        var barAreaX = nameWidth + NameBarGap;
        var barAreaWidth = Math.Max(0, width - barAreaX - FigureColumnWidth);
        var plainTops = RowTops(rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var y = plainTops[i];
            if (y >= height)
                break;

            var nameText = new FormattedText(
                rows[i].ShortLabel, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, nameFontSize, TextBrush, dpi)
            {
                MaxTextWidth = Math.Max(1, nameWidth),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            drawingContext.DrawText(nameText, new Point(0, y + (RowHeight - nameText.Height) / 2));

            var barWidth = BarWidth(rows[i].Total, maxValue, barAreaWidth);
            if (barWidth > 0)
            {
                const double barHeight = 12;
                drawingContext.DrawRectangle(PlainRowBrush(rows[i], BarBrush), null, new Rect(barAreaX, y + (RowHeight - barHeight) / 2, barWidth, barHeight));
            }

            var figureText = new FormattedText(
                StatsAggregator.ShortenTokenCount(rows[i].Total, ThousandSuffix, MillionSuffix, BillionSuffix), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, 11, TextBrush, dpi);
            drawingContext.DrawText(figureText, new Point(width - figureText.Width, y + (RowHeight - figureText.Height) / 2));
        }
    }

    /// <summary>The project name of a rich row: always one line, cut with an ellipsis - without the
    /// line limit a long name wraps into the bar beneath it.</summary>
    internal static FormattedText CreateRichNameText(string name, double maxWidth, CultureInfo culture, Typeface semiBold, Brush brush, double dpi) =>
        new(name, culture, FlowDirection.LeftToRight, semiBold, 12, brush, dpi)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    /// <summary>One 852-style row: an icon (or, missing one, a dot in the project's own color), the
    /// project's name in semibold (the full path is in the tooltip only), cut to whatever width is
    /// left once the right-aligned value/share text is measured, and a thin bar under the whole row
    /// in the project's own color, proportional to its share of
    /// <paramref name="maxValue"/>. A focused row (keyboard navigation, never the mouse) gets a
    /// one-pixel outline in <see cref="MutedBrush"/> around it, the only visual cue this control has
    /// for keyboard focus since it draws its own content instead of hosting real, individually
    /// focusable child elements.</summary>
    private void DrawRichRow(
        DrawingContext drawingContext, StatsProjectRow row, string projectName, double y, double width, long maxValue,
        Typeface regular, Typeface semiBold, double dpi, bool isFocused)
    {
        var culture = CultureInfo.CurrentUICulture;
        var textAreaHeight = RichRowHeight - RichBarHeight - RichBarGap;
        var textX = RichIconSize + RichIconGap;

        var icon = GetIcon(row.IconPath);
        if (icon is not null)
        {
            var iconY = y + (textAreaHeight - RichIconSize) / 2;
            drawingContext.DrawImage(icon, new Rect(0, iconY, RichIconSize, RichIconSize));
        }
        else
        {
            var dotY = y + (textAreaHeight - RichDotSize) / 2;
            drawingContext.DrawEllipse(
                new SolidColorBrush(row.Color), null,
                new Point(RichIconSize / 2, dotY + RichDotSize / 2), RichDotSize / 2, RichDotSize / 2);
        }

        var figureText = new FormattedText(
            $"{StatsAggregator.ShortenTokenCount(row.Total, ThousandSuffix, MillionSuffix, BillionSuffix)} · {StatsBarChart.PercentLabel(row.Percent, culture)}",
            culture, FlowDirection.LeftToRight, regular, 11, TextBrush, dpi);
        var figureX = Math.Max(textX, width - figureText.Width);
        drawingContext.DrawText(figureText, new Point(figureX, y + (textAreaHeight - figureText.Height) / 2));

        var nameAreaWidth = Math.Max(1, figureX - textX - RichFigureGap);

        var nameText = CreateRichNameText(projectName, nameAreaWidth, culture, semiBold, TextBrush, dpi);
        drawingContext.DrawText(nameText, new Point(textX, y + (textAreaHeight - nameText.Height) / 2));

        var barWidth = BarWidth(row.Total, maxValue, width);
        if (barWidth > 0)
            drawingContext.DrawRectangle(new SolidColorBrush(row.Color), null, new Rect(0, y + textAreaHeight + RichBarGap, barWidth, RichBarHeight));

        if (isFocused)
            drawingContext.DrawRectangle(null, new Pen(MutedBrush, 1), new Rect(0.5, y + 0.5, width - 1, RichRowHeight - 1));
    }
}
