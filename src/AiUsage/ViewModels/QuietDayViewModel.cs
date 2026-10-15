using AiUsage.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.ViewModels;

/// <summary>One weekday check box under "allow notifications on every day": checked means
/// notifications may come that day, unchecked keeps it completely quiet.</summary>
public sealed partial class QuietDayViewModel : ObservableObject
{
    private readonly Action<DayOfWeek, bool> _changed;

    public DayOfWeek Day { get; }

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private bool isAllowed;

    public QuietDayViewModel(DayOfWeek day, bool isAllowed, Action<DayOfWeek, bool> changed)
    {
        Day = day;
        _changed = changed;
        this.isAllowed = isAllowed;
        name = NameOf(day);
    }

    /// <summary>The day's name in the active UI language, first letter upper case ("Montag", "Monday").</summary>
    public static string NameOf(DayOfWeek day)
    {
        var culture = LocalizationService.Instance.ActiveCulture;
        var text = culture.DateTimeFormat.GetDayName(day);
        return text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];
    }

    /// <summary>Writes the name again in the current language.</summary>
    public void RefreshName() => Name = NameOf(Day);

    partial void OnIsAllowedChanged(bool value) => _changed(Day, value);
}
