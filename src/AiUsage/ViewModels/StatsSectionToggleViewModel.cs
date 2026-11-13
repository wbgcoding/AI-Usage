using AiUsage.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.ViewModels;

/// <summary>One check box of "Shown sections" on the "Token usage" page: checked means the section is
/// shown in the statistics window.</summary>
public sealed partial class StatsSectionToggleViewModel : ObservableObject
{
    private readonly string _titleKey;
    private readonly Action _changed;

    public string Key { get; }

    [ObservableProperty]
    private string title;

    [ObservableProperty]
    private bool isShown;

    public StatsSectionToggleViewModel(string key, string titleKey, bool isShown, Action changed)
    {
        Key = key;
        _titleKey = titleKey;
        _changed = changed;
        this.isShown = isShown;
        title = LocalizationService.Instance[titleKey];
    }

    /// <summary>Writes the title again in the current language.</summary>
    public void RefreshTitle() => Title = LocalizationService.Instance[_titleKey];

    partial void OnIsShownChanged(bool value) => _changed();
}
