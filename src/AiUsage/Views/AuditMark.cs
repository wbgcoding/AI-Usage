namespace AiUsage.Views;

/// <summary>Marks an element that the review tool's layout audit would report although it is meant
/// to look that way. Each mark holds the reason as plain text, so the exception explains itself where
/// it is set, for example <c>views:AuditMark.Overlap="the badge sits on the icon's corner"</c>.
/// The marks only change what the audit reports; the app never reads them.</summary>
public static class AuditMark
{
    /// <summary>The element may overlap its siblings, and its own children may overlap each other.</summary>
    public static readonly DependencyProperty OverlapProperty = DependencyProperty.RegisterAttached(
        "Overlap", typeof(string), typeof(AuditMark), new PropertyMetadata(null));

    /// <summary>The element and everything inside it may use colours that are not theme tokens.</summary>
    public static readonly DependencyProperty OffThemeProperty = DependencyProperty.RegisterAttached(
        "OffTheme", typeof(string), typeof(AuditMark), new PropertyMetadata(null));

    /// <summary>Texts in the element and everything inside it may be cut off or trimmed.</summary>
    public static readonly DependencyProperty ClipProperty = DependencyProperty.RegisterAttached(
        "Clip", typeof(string), typeof(AuditMark), new PropertyMetadata(null));

    /// <summary>The setting control may go without a tooltip.</summary>
    public static readonly DependencyProperty TooltipProperty = DependencyProperty.RegisterAttached(
        "Tooltip", typeof(string), typeof(AuditMark), new PropertyMetadata(null));

    /// <summary>The setting label may be longer than a short label.</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(AuditMark), new PropertyMetadata(null));

    public static string? GetTooltip(DependencyObject element) => (string?)element.GetValue(TooltipProperty);

    public static void SetTooltip(DependencyObject element, string? value) => element.SetValue(TooltipProperty, value);

    public static string? GetLabel(DependencyObject element) => (string?)element.GetValue(LabelProperty);

    public static void SetLabel(DependencyObject element, string? value) => element.SetValue(LabelProperty, value);

    public static string? GetOverlap(DependencyObject element) => (string?)element.GetValue(OverlapProperty);

    public static void SetOverlap(DependencyObject element, string? value) => element.SetValue(OverlapProperty, value);

    public static string? GetOffTheme(DependencyObject element) => (string?)element.GetValue(OffThemeProperty);

    public static void SetOffTheme(DependencyObject element, string? value) => element.SetValue(OffThemeProperty, value);

    public static string? GetClip(DependencyObject element) => (string?)element.GetValue(ClipProperty);

    public static void SetClip(DependencyObject element, string? value) => element.SetValue(ClipProperty, value);
}
