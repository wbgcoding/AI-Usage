using AiUsage.Services;
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

    /// <summary>The stored text of a selection.</summary>
    public static string Format(IEnumerable<string> ids) => string.Join(',', ids);
}

/// <summary>One check box of the provider drop-down: the "all providers" row (empty value) or a
/// provider. The checked state is pushed by the view model, which raises a change even when the
/// value stays the same, so a click that changes nothing still puts the box back.</summary>
public sealed class StatsProviderOption : ObservableObject
{
    private readonly string? _labelKey;
    private string _label;
    private bool _isChecked;

    /// <summary>The "all providers" row, labelled from a resource key.</summary>
    public StatsProviderOption(string labelKey)
    {
        _labelKey = labelKey;
        _label = LocalizationService.Instance[labelKey];
        Value = "";
    }

    /// <summary>A provider row; its name reads the same in every language.</summary>
    public StatsProviderOption(string value, string displayName)
    {
        _label = displayName;
        Value = value;
    }

    /// <summary>The provider id; empty for the "all providers" row.</summary>
    public string Value { get; }

    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    public bool IsChecked => _isChecked;

    internal void SetChecked(bool value)
    {
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }

    public void RefreshLabel()
    {
        if (_labelKey is not null)
            Label = LocalizationService.Instance[_labelKey];
    }

    public override string ToString() => Label;
}
