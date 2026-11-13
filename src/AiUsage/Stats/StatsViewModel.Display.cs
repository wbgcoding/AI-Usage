using System.Globalization;
using AiUsage.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.Stats;

/// <summary>The display choices of the "Token usage" settings page that reach the window through the
/// view model: the first weekday and the sections switched off.</summary>
public sealed partial class StatsViewModel
{
    /// <summary>The first weekday of the weekday rows and the month grids. The window fills it from the
    /// settings; without one it is the first day of the Windows culture. Called on any thread.</summary>
    internal Func<DayOfWeek> FirstDayOfWeek { get; set; } = () => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// <summary>The weekday of the month grid's first row, as of the last rebuild.</summary>
    [ObservableProperty]
    private DayOfWeek monthGridFirstDay = DayOfWeek.Monday;

    private HashSet<string> _hiddenSections = [];

    /// <summary>Takes the keys of the sections switched off in Settings.</summary>
    internal void SetHiddenSections(IEnumerable<string> keys)
    {
        _hiddenSections = [.. keys];
        OnPropertyChanged(nameof(ShowPlanForecast));
    }

    /// <summary>True when the section is switched off in Settings (not just collapsed).</summary>
    internal bool IsSectionHidden(string key) => _hiddenSections.Contains(key);

    /// <summary>The plan forecast section shows when it has something and was not switched off.</summary>
    public bool ShowPlanForecast => HasForecast && !IsSectionHidden("planforecast");

    /// <summary>Puts the period the window opens on in place of the one it was left on. The caller reads
    /// the data right after, so nothing is rebuilt here. An empty or unknown value keeps the current one.</summary>
    internal void ApplyOpenRange(string? range)
    {
        var wanted = StatsDisplayChoices.NormalizeOpenRange(range);
        if (wanted == StatsDisplayChoices.LastUsedRange || wanted == SelectedRange)
            return;

        SelectedDay = null;
        SelectedRange = wanted;
        UpdateCustomChoiceLabel();
        Choice.Select(RangeChoices, SelectedRange);
        OnPropertyChanged(nameof(SelectedRangeChoice));
    }

    /// <summary>The week start changed in Settings: the weekday rows and the month grids are built again
    /// from the records already loaded.</summary>
    internal Task ApplyWeekStartAsync() => RefreshCostAsync();
}
