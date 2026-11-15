using System.Collections;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using AiUsage.Views;

namespace ReadmeShots;

internal enum AuditKind { Clipped, Overlap, OffTheme }

/// <summary>One thing the audit found: what kind, which element, and the measured detail.</summary>
internal sealed record AuditFinding(AuditKind Kind, string Element, string Detail);

/// <summary>Review mode only: after a surface is laid out, lists what is easy to miss in hundreds of
/// pictures, one line per finding in <c>audit.txt</c>:
/// <list type="bullet">
/// <item>CLIPPED: a single-line text that needs more width than it got (a trimmed text too, marked
/// as such, because the ellipsis still hides part of it);</item>
/// <item>OVERLAP: two visible siblings in a layout panel whose bounds intersect. Children sharing a
/// grid cell and children of a canvas are layered on purpose and are not compared; popups live in
/// their own tree and adorner layers are skipped;</item>
/// <item>OFF-THEME: a background, foreground or border that comes from the control's system default
/// look, or whose colour is none of the colours the app's resource dictionaries define.</item>
/// </list>
/// An element meant to look that way carries an <see cref="AuditMark"/> with the reason.</summary>
internal static class LayoutAudit
{
    private const double Tolerance = 1.0;

    /// <summary>Null outside review mode, so the README pictures never leave a report beside them.</summary>
    internal static string? ReportPath { get; set; }

    internal static void Check(FrameworkElement root, string outputPath)
    {
        if (ReportPath is null)
            return;

        var findings = Run(root);
        if (findings.Count == 0)
            return;
        var picture = Picture.FromFile(outputPath);
        File.AppendAllLines(ReportPath, findings.Select(finding => Format(picture, finding)));
    }

    /// <summary>Audits a laid out element tree against the current application's resources.</summary>
    internal static IReadOnlyList<AuditFinding> Run(FrameworkElement root)
    {
        var palette = ThemePalette.FromApplication();
        var findings = new List<AuditFinding>();
        Walk(root, palette, findings, clipMarked: false, offThemeMarked: false);
        return findings;
    }

    internal static string Format(Picture picture, AuditFinding finding)
    {
        var kind = finding.Kind switch
        {
            AuditKind.Clipped => "CLIPPED",
            AuditKind.Overlap => "OVERLAP",
            _ => "OFF-THEME",
        };
        return $"{kind} | {picture.Surface} | {picture.Size} | {picture.Locale} | {picture.Theme} | {finding.Element} | {finding.Detail}";
    }

    /// <summary>The parts of a review picture's file name, <c>surface-theme-language-size.png</c>; the
    /// language itself may hold a dash (pt-BR, zh-Hans), the surface id never does.</summary>
    internal sealed record Picture(string Surface, string Size, string Locale, string Theme)
    {
        internal static Picture FromFile(string path)
        {
            var parts = Path.GetFileNameWithoutExtension(path).Split('-');
            if (parts.Length < 4)
                return new Picture(Path.GetFileName(path), "-", "-", "-");
            return new Picture(parts[0], parts[^1], string.Join('-', parts[2..^1]), parts[1]);
        }
    }

    private static void Walk(DependencyObject node, ThemePalette palette, List<AuditFinding> findings, bool clipMarked, bool offThemeMarked)
    {
        if (node is UIElement { Visibility: not Visibility.Visible })
            return;
        // Adorners (focus rings, drag marks) are drawn over the layout on purpose.
        if (node is AdornerLayer)
            return;

        clipMarked |= AuditMark.GetClip(node) is not null;
        offThemeMarked |= AuditMark.GetOffTheme(node) is not null;

        if (!clipMarked && node is TextBlock text && ClipDetail(text) is { } clip)
            findings.Add(new AuditFinding(AuditKind.Clipped, Describe(text), clip));
        if (!offThemeMarked && node is FrameworkElement element)
            CheckColours(element, palette, findings);
        if (node is Panel panel && AuditMark.GetOverlap(panel) is null)
            CheckOverlaps(panel, findings);

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            Walk(VisualTreeHelper.GetChild(node, i), palette, findings, clipMarked, offThemeMarked);
    }

    // CLIPPED ----------------------------------------------------------------------------------------

    private static string? ClipDetail(TextBlock text)
    {
        if (text.TextWrapping != TextWrapping.NoWrap || text.ActualWidth <= 0 || string.IsNullOrWhiteSpace(text.Text))
            return null;
        // Runs in their own weight or size cannot be measured as one string; such a text is skipped
        // rather than reported wrong.
        if (text.Inlines.Count > 1)
            return null;

        var formatted = new FormattedText(
            text.Text, text.Language.GetSpecificCulture(), text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, Brushes.Black, 1.0);
        var needed = formatted.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;
        // A text given less room than it asked for keeps its full width and is cut by its layout clip,
        // so the clip, not the text's own width, says how much of it shows.
        var shown = text.ActualWidth;
        if (LayoutInformation.GetLayoutClip(text) is { } clip)
            shown = Math.Min(shown, clip.Bounds.Width);
        if (needed <= shown + Tolerance)
            return null;

        var trimmed = text.TextTrimming == TextTrimming.None ? "" : ", trimmed";
        return string.Create(CultureInfo.InvariantCulture, $"needs {needed:0} px, has {shown:0} px{trimmed}");
    }

    // OVERLAP ----------------------------------------------------------------------------------------

    private static void CheckOverlaps(Panel panel, List<AuditFinding> findings)
    {
        // A canvas places its children freely; charts and drawings layer on purpose.
        if (panel is Canvas)
            return;

        var children = new List<(FrameworkElement Element, Rect Bounds)>();
        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement element || element.Visibility != Visibility.Visible)
                continue;
            if (AuditMark.GetOverlap(element) is not null)
                continue;
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
                continue;
            children.Add((element, BoundsIn(panel, element)));
        }

        for (var i = 0; i < children.Count; i++)
        {
            for (var j = i + 1; j < children.Count; j++)
            {
                var (first, firstBounds) = children[i];
                var (second, secondBounds) = children[j];
                if (panel is Grid && ShareCell(first, second))
                    continue;
                var overlap = Rect.Intersect(firstBounds, secondBounds);
                if (overlap.IsEmpty || overlap.Width <= Tolerance || overlap.Height <= Tolerance)
                    continue;
                findings.Add(new AuditFinding(AuditKind.Overlap, Describe(first), string.Create(CultureInfo.InvariantCulture,
                    $"overlaps {Describe(second)} by {overlap.Width:0} x {overlap.Height:0} px in {Label(panel)}")));
            }
        }
    }

    /// <summary>The part of a child the panel actually shows: its arranged box, cut to the layout clip
    /// the panel gives a child that wanted more room than it got, in the panel's coordinates.</summary>
    private static Rect BoundsIn(Panel panel, FrameworkElement child)
    {
        var box = new Rect(child.RenderSize);
        if (LayoutInformation.GetLayoutClip(child) is { } clip)
            box.Intersect(clip.Bounds);
        return child.TransformToAncestor(panel).TransformBounds(box);
    }

    /// <summary>True when two grid children share at least one cell: such children are stacked on
    /// purpose (a background under its content, a badge over an icon).</summary>
    private static bool ShareCell(UIElement first, UIElement second)
    {
        static bool Intersects(int start1, int span1, int start2, int span2) =>
            start1 < start2 + Math.Max(1, span2) && start2 < start1 + Math.Max(1, span1);

        return Intersects(Grid.GetRow(first), Grid.GetRowSpan(first), Grid.GetRow(second), Grid.GetRowSpan(second))
            && Intersects(Grid.GetColumn(first), Grid.GetColumnSpan(first), Grid.GetColumn(second), Grid.GetColumnSpan(second));
    }

    // OFF-THEME --------------------------------------------------------------------------------------

    private static void CheckColours(FrameworkElement element, ThemePalette palette, List<AuditFinding> findings)
    {
        foreach (var (name, property) in ColourProperties(element))
        {
            if (element.GetValue(property) is not Brush brush || ThemePalette.IsInvisible(brush))
                continue;
            var source = DependencyPropertyHelper.GetValueSource(element, property).BaseValueSource;
            // An inherited value is checked where it was set.
            if (source == BaseValueSource.Inherited)
                continue;

            var systemDefault = source is BaseValueSource.Default or BaseValueSource.DefaultStyle or BaseValueSource.DefaultStyleTrigger;
            if (!systemDefault && palette.Accepts(brush))
                continue;
            var origin = systemDefault ? "system default" : "not a theme colour";
            findings.Add(new AuditFinding(AuditKind.OffTheme, Describe(element), $"{name} {ThemePalette.Name(brush)} ({origin})"));
        }
    }

    private static IEnumerable<(string Name, DependencyProperty Property)> ColourProperties(FrameworkElement element)
    {
        switch (element)
        {
            case Control:
                yield return ("Background", Control.BackgroundProperty);
                yield return ("Foreground", Control.ForegroundProperty);
                yield return ("BorderBrush", Control.BorderBrushProperty);
                break;
            case Border:
                yield return ("Background", Border.BackgroundProperty);
                yield return ("BorderBrush", Border.BorderBrushProperty);
                break;
            case Panel:
                yield return ("Background", Panel.BackgroundProperty);
                break;
            case TextBlock:
                yield return ("Background", TextBlock.BackgroundProperty);
                yield return ("Foreground", TextBlock.ForegroundProperty);
                break;
        }
    }

    // Naming -----------------------------------------------------------------------------------------

    /// <summary>The element, its text where it has one, and the nearest user control or named element
    /// around it, so a line can be traced back to its XAML.</summary>
    internal static string Describe(FrameworkElement element)
    {
        var label = Label(element);
        var text = element switch
        {
            TextBlock block => block.Text,
            ContentControl { Content: string content } => content,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(text))
            label += " \"" + (text.Length > 40 ? text[..40] + "..." : text) + "\"";

        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is UserControl or Window || parent is FrameworkElement { Name.Length: > 0 })
                return label + " in " + Label((FrameworkElement)parent);
        }
        return label;
    }

    private static string Label(FrameworkElement element) =>
        string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.GetType().Name + "#" + element.Name;
}

/// <summary>Every brush and colour the application's resource dictionaries define (tokens, the live
/// theme, control styles), the yardstick for "comes from the theme". A brush made in code from a
/// theme colour, at any opacity, counts as the theme colour.</summary>
internal sealed class ThemePalette
{
    private readonly HashSet<Brush> _brushes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(byte R, byte G, byte B)> _colours = [];

    internal static ThemePalette FromApplication()
    {
        var palette = new ThemePalette();
        if (Application.Current is { } app)
            palette.Add(app.Resources);
        return palette;
    }

    internal static ThemePalette From(ResourceDictionary dictionary)
    {
        var palette = new ThemePalette();
        palette.Add(dictionary);
        return palette;
    }

    private void Add(ResourceDictionary dictionary)
    {
        foreach (var merged in dictionary.MergedDictionaries)
            Add(merged);
        foreach (DictionaryEntry entry in dictionary)
        {
            switch (entry.Value)
            {
                case Color colour:
                    _colours.Add((colour.R, colour.G, colour.B));
                    break;
                case SolidColorBrush solid:
                    _brushes.Add(solid);
                    _colours.Add((solid.Color.R, solid.Color.G, solid.Color.B));
                    break;
                case GradientBrush gradient:
                    _brushes.Add(gradient);
                    foreach (var stop in gradient.GradientStops)
                        _colours.Add((stop.Color.R, stop.Color.G, stop.Color.B));
                    break;
                case Brush other:
                    _brushes.Add(other);
                    break;
            }
        }
    }

    internal bool Accepts(Brush brush)
    {
        if (_brushes.Contains(brush))
            return true;
        return brush switch
        {
            SolidColorBrush solid => Known(solid.Color),
            GradientBrush gradient => gradient.GradientStops.All(stop => stop.Color.A == 0 || Known(stop.Color)),
            // Pictures and drawings carry their own colours.
            _ => true,
        };
    }

    private bool Known(Color colour) => _colours.Contains((colour.R, colour.G, colour.B));

    internal static bool IsInvisible(Brush brush) =>
        brush.Opacity == 0 || brush is SolidColorBrush { Color.A: 0 };

    internal static string Name(Brush brush) => brush is SolidColorBrush solid ? solid.Color.ToString(CultureInfo.InvariantCulture) : brush.GetType().Name;
}
