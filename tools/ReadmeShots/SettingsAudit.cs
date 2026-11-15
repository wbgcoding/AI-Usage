using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AiUsage.Views;

namespace ReadmeShots;

/// <summary>The layout audit's checks for settings surfaces. A setting is a control the user changes
/// a value with (check box, radio button, toggle, combo box, slider, text box, date picker); its label
/// is its own caption, or else the text just before it in its row.
/// <list type="bullet">
/// <item>NO-TOOLTIP: neither the setting, its label nor the row around it (up to two levels) has a
/// tooltip that would show;</item>
/// <item>LONG-LABEL: the label has more than <see cref="MaxLabelLength"/> characters, a sentence
/// where a short name belongs (the explanation goes in the tooltip).</item>
/// </list>
/// A setting meant that way carries <see cref="AuditMark.TooltipProperty"/> or
/// <see cref="AuditMark.LabelProperty"/> with the reason.</summary>
internal static class SettingsAudit
{
    internal const int MaxLabelLength = 40;

    private const int RowLevels = 2;

    internal static void Check(FrameworkElement root, List<AuditFinding> findings)
    {
        var reported = new HashSet<TextBlock>(ReferenceEqualityComparer.Instance);
        foreach (var setting in Settings(root))
        {
            var label = LabelOf(setting);
            if (AuditMark.GetTooltip(setting) is null && !HasTooltip(setting, label))
                findings.Add(new AuditFinding(AuditKind.NoTooltip, LayoutAudit.Describe(setting) + LabelNote(setting, label),
                    "no tooltip on the setting, its label or its row"));

            if (label is null || AuditMark.GetLabel(setting) is not null || AuditMark.GetLabel(label) is not null)
                continue;
            var text = LayoutAudit.TextOf(label).Trim();
            if (text.Length > MaxLabelLength && reported.Add(label))
                findings.Add(new AuditFinding(AuditKind.LongLabel, LayoutAudit.Describe(label),
                    $"{text.Length} characters, at most {MaxLabelLength}"));
        }
    }

    /// <summary>Every visible setting control in the tree, without the parts of another control's
    /// template (the toggle inside a combo box, the text box inside a date picker).</summary>
    private static IEnumerable<Control> Settings(DependencyObject node)
    {
        if (node is UIElement { Visibility: not Visibility.Visible })
            yield break;
        if (node is Control control && IsSetting(control))
        {
            yield return control;
            yield break;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            foreach (var setting in Settings(VisualTreeHelper.GetChild(node, i)))
                yield return setting;
        }
    }

    private static bool IsSetting(Control control) =>
        control.TemplatedParent is null
        && control is ToggleButton or ComboBox or Slider or TextBox or PasswordBox or DatePicker;

    /// <summary>The setting's own caption (a check box's text), else the last text before it in the
    /// panel it sits in: the label above a picker, or left of it in the same grid row.</summary>
    internal static TextBlock? LabelOf(Control setting)
    {
        if (setting is ContentControl { Content: not null } captioned && FirstText(captioned) is { } caption)
            return caption;

        if (VisualTreeHelper.GetParent(setting) is not Panel panel)
            return null;
        TextBlock? before = null;
        foreach (UIElement child in panel.Children)
        {
            if (ReferenceEquals(child, setting))
                break;
            if (child is TextBlock { Visibility: Visibility.Visible } text && !string.IsNullOrWhiteSpace(LayoutAudit.TextOf(text))
                && (panel is not Grid || Grid.GetRow(text) == Grid.GetRow(setting)))
                before = text;
        }
        return before;
    }

    private static TextBlock? FirstText(DependencyObject node)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock { Visibility: Visibility.Visible } text && !string.IsNullOrWhiteSpace(LayoutAudit.TextOf(text)))
                return text;
            if (child is UIElement { Visibility: not Visibility.Visible })
                continue;
            if (FirstText(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static bool HasTooltip(Control setting, TextBlock? label)
    {
        if (ShowsTooltip(setting) || (label is not null && ShowsTooltip(label)))
            return true;
        DependencyObject? row = setting;
        for (var level = 0; level < RowLevels; level++)
        {
            row = LogicalTreeHelper.GetParent(row) ?? VisualTreeHelper.GetParent(row);
            if (row is null || row is ScrollViewer)
                return false;
            if (ShowsTooltip(row))
                return true;
        }
        return false;
    }

    private static bool ShowsTooltip(DependencyObject element) =>
        ToolTipService.GetIsEnabled(element)
        && element.GetValue(FrameworkElement.ToolTipProperty) switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            ToolTip { Content: string text } => !string.IsNullOrWhiteSpace(text),
            _ => true,
        };

    /// <summary>Names the label beside a setting; a caption inside the setting is named by it already.</summary>
    private static string LabelNote(Control setting, TextBlock? label)
    {
        if (label is null || label.IsDescendantOf(setting))
            return "";
        var text = LayoutAudit.TextOf(label).Trim();
        return " for \"" + (text.Length > 40 ? text[..40] + "..." : text) + "\"";
    }
}
