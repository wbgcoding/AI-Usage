using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.Stats;

/// <summary>The provider filter as stored: provider ids joined by commas, an empty text for all
/// providers. A text from an older version (one id) reads as a one-item list.</summary>
public static class StatsProviderSelection
{
    /// <summary>The ids of <paramref name="text"/> that are in <paramref name="known"/>, each once,
    /// in the order of <paramref name="known"/>. Unknown ids are dropped; empty means all providers.</summary>
    public static List<string> Parse(string? text, IReadOnlyList<string> known)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var wanted = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. known.Where(id => wanted.Contains(id, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>True when <paramref name="provider"/> counts under <paramref name="selection"/>: an
    /// empty selection means all providers.</summary>
    public static bool IsActive(IReadOnlyList<string> selection, string provider) =>
        selection.Count == 0 || selection.Contains(provider);

    /// <summary>The selection after a click on one provider pill. Under an empty selection (all) the
    /// click switches that provider off and keeps the others; otherwise it toggles the provider. A
    /// result that holds every provider in <paramref name="shown"/> is the empty selection (all), and
    /// switching off the last active provider changes nothing, so the view is never empty. The result
    /// follows the order of <paramref name="shown"/>.</summary>
    public static List<string> Toggle(IReadOnlyList<string> selection, string provider, IReadOnlyList<string> shown)
    {
        if (!shown.Contains(provider))
            return [.. selection];

        var active = selection.Count == 0 ? [.. shown] : shown.Where(selection.Contains).ToList();
        if (active.Contains(provider))
        {
            if (active.Count == 1)
                return [.. selection];
            active.Remove(provider);
        }
        else
        {
            active.Add(provider);
        }

        var next = shown.Where(active.Contains).ToList();
        return next.Count == shown.Count ? [] : next;
    }

    /// <summary>The stored text of a selection.</summary>
    public static string Format(IEnumerable<string> ids) => string.Join(',', ids);
}

/// <summary>One provider pill of the filter row. The active state is pushed by the view model,
/// which raises a change even when the value stays the same, so a click that changes nothing still
/// puts the button back.</summary>
public sealed class StatsProviderOption : ObservableObject
{
    private bool _isChecked;

    public StatsProviderOption(string value, string displayName)
    {
        Label = displayName;
        Value = value;
        Brush = new SolidColorBrush(ChartPalette.ForProvider(value));
        Brush.Freeze();
    }

    /// <summary>The provider id.</summary>
    public string Value { get; }

    /// <summary>The provider's name; it reads the same in every language.</summary>
    public string Label { get; }

    /// <summary>The colour that names the provider in the charts, for the pill's dot and tint.</summary>
    public Brush Brush { get; }

    /// <summary>True while the provider counts toward the figures.</summary>
    public bool IsChecked => _isChecked;

    internal void SetChecked(bool value)
    {
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }

    public override string ToString() => Label;
}
