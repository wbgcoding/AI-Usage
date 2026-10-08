using System.Windows.Media;
using AiUsage.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.ViewModels;

/// <summary>Non-generic home for <see cref="Choice{TValue}"/>'s one static helper - a generic type
/// may not declare a static member of its own type parameter.</summary>
public static class Choice
{
    /// <summary>Marks the one row whose Value matches as selected and every other row not - the
    /// loop each choice group used to write out for itself.</summary>
    public static void Select<TValue>(IEnumerable<Choice<TValue>> choices, TValue value)
    {
        foreach (var choice in choices)
            choice.IsSelected = EqualityComparer<TValue>.Default.Equals(choice.Value, value);
    }

    /// <summary>A row labelled with a proper name that stays the same in every language.</summary>
    public static Choice<TValue> WithFixedLabel<TValue>(string label, TValue value) => new(new FixedLabel(label), value);
}

/// <summary>A label shown as is, never looked up as a resource key.</summary>
internal readonly record struct FixedLabel(string Text);

/// <summary>One row of a radio-style choice group (theme, language, chart range, tile density):
/// a value, a label re-resolved from a resource key on every language switch, and whether this is
/// the selected row. The two preview brushes are theme-only and left null for every other group.
/// They are themselves observable (not a plain get-only property) only because the "follow Windows"
/// theme choice has no fixed dictionary to preview - its swatch is refreshed with whichever theme is
/// currently resolved, unlike every other row's brushes, which are set once and never change.</summary>
public sealed partial class Choice<TValue> : ObservableObject
{
    private readonly string? _labelKey;

    public TValue Value { get; }

    [ObservableProperty]
    private Brush? bgPreview;

    [ObservableProperty]
    private Brush? accentPreview;

    [ObservableProperty]
    private string label;

    [ObservableProperty]
    private bool isSelected;

    public Choice(string labelKey, TValue value, Brush? bgPreview = null, Brush? accentPreview = null)
    {
        _labelKey = labelKey;
        label = LocalizationService.Instance[labelKey];
        Value = value;
        this.bgPreview = bgPreview;
        this.accentPreview = accentPreview;
    }

    /// <summary>A row whose label is a proper name (a provider, an account) that reads the same in
    /// every language, so there is no resource key to re-resolve. Built through
    /// <see cref="Choice.WithFixedLabel"/>; the label comes wrapped so that a plain pair of strings
    /// can never resolve to this constructor instead of the keyed one.</summary>
    internal Choice(FixedLabel fixedLabel, TValue value)
    {
        label = fixedLabel.Text;
        Value = value;
    }

    public void RefreshLabel()
    {
        if (_labelKey is not null)
            Label = LocalizationService.Instance[_labelKey];
    }

    /// <summary>What a screen reader reads for this row. A list item whose template carries no name
    /// of its own falls back to the bound object's ToString(), so without this the settings sidebar
    /// and every choice list announced the view model's type name instead of the label on screen.
    /// </summary>
    public override string ToString() => Label;

    /// <summary>Swaps in a freshly resolved pair of preview brushes - see the class summary.</summary>
    public void RefreshPreview(Brush? bgPreview, Brush? accentPreview)
    {
        BgPreview = bgPreview;
        AccentPreview = accentPreview;
    }
}
