using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace AiUsage.Views.Controls;

/// <summary>
/// Attached behavior for a trimming <see cref="TextBlock"/>: its full text shows as a tooltip, but only
/// while the text is actually cut off. A name that fits gets no tooltip that would only repeat it.
/// </summary>
public static class TrimmedToolTip
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(TrimmedToolTip), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        if (e.NewValue is true)
        {
            var tip = new ToolTip();
            BindingOperations.SetBinding(tip, ContentControl.ContentProperty, new Binding(nameof(TextBlock.Text)) { Source = textBlock });
            textBlock.ToolTip = tip;
            textBlock.ToolTipOpening += OnToolTipOpening;
        }
        else
        {
            textBlock.ToolTipOpening -= OnToolTipOpening;
            textBlock.ClearValue(FrameworkElement.ToolTipProperty);
        }
    }

    // Handled cancels the tooltip: asked at the moment it would open, so no size tracking is needed.
    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is TextBlock textBlock && !IsTrimmed(textBlock))
            e.Handled = true;
    }

    private static bool IsTrimmed(TextBlock textBlock)
    {
        if (string.IsNullOrEmpty(textBlock.Text) || textBlock.TextTrimming == TextTrimming.None)
            return false;

        var text = new FormattedText(
            textBlock.Text, CultureInfo.CurrentUICulture, textBlock.FlowDirection,
            new Typeface(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch),
            textBlock.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(textBlock).PixelsPerDip);
        return text.Width > textBlock.ActualWidth + 0.5;
    }
}
