using System.Globalization;
using System.Windows.Automation;
using System.Windows.Data;

namespace AiUsage.Views.Controls;

/// <summary>
/// One collapsible block of the statistics window: a borderless, full-row header (chevron, title,
/// and - only while collapsed - a dampened one-line <see cref="Summary"/> of what is hidden) above
/// the inherited <see cref="ContentControl.Content"/>. A lookless <see cref="ContentControl"/>
/// (template in CollapsibleSection.xaml, a plain <see cref="ResourceDictionary"/> <see
/// cref="Views.StatsWindow"/> merges into its own <c>Window.Resources</c>) rather than a compiled
/// <c>UserControl</c>: a UserControl's own compiled page establishes its own name scope, which
/// refuses to let content assigned from the *caller's* XAML (StatsWindow.xaml's own named charts,
/// e.g. <c>Chart</c>) keep their names registered in the caller's page - a lookless control's
/// <c>Content</c> carries no such boundary, the same way <c>Expander.Content</c> does not. Never
/// animates a state change (see CollapsibleSection.xaml's header template) - this program redraws
/// only on a real change. <see cref="SectionKey"/> is a plain data tag: the stable, short dictionary
/// key <see cref="Views.StatsWindow"/> persists this section's own <see cref="IsExpanded"/> under
/// (<c>AppSettings.StatsSectionsCollapsed</c>) - this control itself never reads or writes settings.
/// </summary>
[TemplatePart(Name = PartHeaderButton, Type = typeof(Button))]
public class CollapsibleSection : ContentControl
{
    private const string PartHeaderButton = "PART_HeaderButton";

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(CollapsibleSection), new PropertyMetadata(""));

    public static readonly DependencyProperty SummaryProperty =
        DependencyProperty.Register(nameof(Summary), typeof(string), typeof(CollapsibleSection), new PropertyMetadata(""));

    /// <summary>An optional control shown at the right end of the header row, outside the header
    /// button, so clicking it never collapses the section (the breakdown's grouping picker).</summary>
    public static readonly DependencyProperty HeaderContentProperty =
        DependencyProperty.Register(nameof(HeaderContent), typeof(object), typeof(CollapsibleSection), new PropertyMetadata(null));

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public static readonly DependencyProperty SectionKeyProperty =
        DependencyProperty.Register(nameof(SectionKey), typeof(string), typeof(CollapsibleSection), new PropertyMetadata(""));

    /// <summary>The explanatory text shown only over the heading itself, not the inherited
    /// <c>FrameworkElement.ToolTip</c>, which the control's own template does not set on the root -
    /// setting <c>ToolTip</c> directly on a <see cref="CollapsibleSection"/> instance would cover its
    /// whole card, header and content alike. Mirrored onto
    /// <see cref="AutomationProperties.HelpTextProperty"/> on the control itself so a screen reader
    /// still gets the same text a sighted hover would.</summary>
    public static readonly DependencyProperty HeaderToolTipProperty =
        DependencyProperty.Register(nameof(HeaderToolTip), typeof(string), typeof(CollapsibleSection),
            new PropertyMetadata("", OnHeaderToolTipChanged));

    public string HeaderToolTip
    {
        get => (string)GetValue(HeaderToolTipProperty);
        set => SetValue(HeaderToolTipProperty, value);
    }

    private static void OnHeaderToolTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        AutomationProperties.SetHelpText((CollapsibleSection)d, (string)e.NewValue);

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(CollapsibleSection),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsExpandedChanged));

    /// <summary>Raised whenever <see cref="IsExpanded"/> changes, whether from the header click or
    /// set from code - <see cref="Views.StatsWindow"/> listens to persist the new state.</summary>
    public event EventHandler? IsExpandedChanged;

    private Button? _headerButton;

    static CollapsibleSection()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CollapsibleSection), new FrameworkPropertyMetadata(typeof(CollapsibleSection)));
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Summary
    {
        get => (string)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public string SectionKey
    {
        get => (string)GetValue(SectionKeyProperty);
        set => SetValue(SectionKeyProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_headerButton is not null)
            _headerButton.Click -= HeaderButton_Click;

        _headerButton = GetTemplateChild(PartHeaderButton) as Button;

        if (_headerButton is not null)
            _headerButton.Click += HeaderButton_Click;
    }

    private void HeaderButton_Click(object sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CollapsibleSection)d).IsExpandedChanged?.Invoke(d, EventArgs.Empty);
}

/// <summary>An empty <see cref="HeaderToolTip"/> must show no tooltip at all rather than an empty
/// popup - <see cref="ToolTipService"/> only skips a null value, never an empty string, so the
/// template binds through this converter instead of the raw string.</summary>
public sealed class NullIfEmptyStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } text ? text : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
