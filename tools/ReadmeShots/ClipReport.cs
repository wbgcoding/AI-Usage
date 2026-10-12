using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReadmeShots;

/// <summary>Review mode only: after a surface is laid out, lists every single-line text that needs
/// more width than it got, so a cut-off label shows up as a line in <c>clipped.txt</c> instead of
/// having to be spotted in hundreds of pictures. A trimmed text (ellipsis) is listed too, marked as
/// such, because some of them are meant to trim and the reader decides.</summary>
internal static class ClipReport
{
    private const double Tolerance = 1.0;

    /// <summary>Null outside review mode, so the README pictures never leave a report beside them.</summary>
    internal static string? ReportPath { get; set; }

    internal static void Check(FrameworkElement root, string outputPath)
    {
        if (ReportPath is null)
            return;

        var lines = new List<string>();
        Walk(root, Path.GetFileName(outputPath), lines);
        if (lines.Count > 0)
            File.AppendAllLines(ReportPath, lines);
    }

    private static void Walk(DependencyObject node, string picture, List<string> lines)
    {
        if (node is UIElement { Visibility: not Visibility.Visible })
            return;

        if (node is TextBlock text && Describe(text) is { } line)
            lines.Add($"{picture} | {line}");

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            Walk(VisualTreeHelper.GetChild(node, i), picture, lines);
    }

    private static string? Describe(TextBlock text)
    {
        if (text.TextWrapping != TextWrapping.NoWrap || text.ActualWidth <= 0 || string.IsNullOrWhiteSpace(text.Text))
            return null;
        // Runs in their own weight or size cannot be measured as one string; such a text is skipped
        // rather than reported wrong.
        if (text.Inlines.Count > 1)
            return null;

        var formatted = new FormattedText(
            text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, Brushes.Black, 1.0);
        var needed = formatted.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;
        if (needed <= text.ActualWidth + Tolerance)
            return null;

        var kind = text.TextTrimming == TextTrimming.None ? "CLIPPED" : "trimmed";
        return string.Create(CultureInfo.InvariantCulture,
            $"{kind} | needs {needed:0} px, has {text.ActualWidth:0} px | \"{text.Text}\"");
    }
}
