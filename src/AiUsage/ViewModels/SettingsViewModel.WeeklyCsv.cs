using System.Collections.ObjectModel;
using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;

namespace AiUsage.ViewModels;

/// <summary>The schedule of the weekly CSV file on the "Token usage" page: the weekday and the full hour
/// it falls due, and the texts of its folder row.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>The seven weekdays in the language of the day names, starting with the week start of the settings.</summary>
    public ObservableCollection<Choice<DayOfWeek>> StatsWeeklyCsvDayChoices { get; } = [];

    /// <summary>The full hours 0:00 to 23:00 in the short time format of the region.</summary>
    public ObservableCollection<Choice<int>> StatsWeeklyCsvHourChoices { get; } = [];

    public Choice<DayOfWeek>? SelectedStatsWeeklyCsvDayChoice
    {
        get => StatsWeeklyCsvDayChoices.FirstOrDefault(choice => choice.IsSelected);
        set
        {
            if (value is null || value.Value == _settings.StatsWeeklyCsvDay)
                return;

            _settings.StatsWeeklyCsvDay = AppSettings.NormalizeWeeklyCsvDay(value.Value);
            Choice.Select(StatsWeeklyCsvDayChoices, _settings.StatsWeeklyCsvDay);
            _store.RequestSave(_settings);
            PushStatsWeeklyCsv();
            OnPropertyChanged();
        }
    }

    public Choice<int>? SelectedStatsWeeklyCsvHourChoice
    {
        get => StatsWeeklyCsvHourChoices.FirstOrDefault(choice => choice.IsSelected);
        set
        {
            if (value is null || value.Value == _settings.StatsWeeklyCsvHour)
                return;

            _settings.StatsWeeklyCsvHour = AppSettings.NormalizeWeeklyCsvHour(value.Value);
            Choice.Select(StatsWeeklyCsvHourChoices, _settings.StatsWeeklyCsvHour);
            _store.RequestSave(_settings);
            PushStatsWeeklyCsv();
            OnPropertyChanged();
        }
    }

    /// <summary>The folder path, or the hint that none is chosen yet.</summary>
    public string StatsWeeklyCsvFolderText =>
        HasStatsWeeklyCsvFolder ? StatsWeeklyCsvFolder : LocalizationService.Instance["Settings.Stats.WeeklyCsvNoFolder"];

    /// <summary>"Change folder" once a folder is chosen, "Choose folder" before.</summary>
    public string StatsWeeklyCsvFolderButtonText =>
        LocalizationService.Instance[HasStatsWeeklyCsvFolder ? "Settings.Stats.WeeklyCsvFolderChange" : "Settings.Stats.WeeklyCsvFolder"];

    /// <summary>Builds both lists again from the settings: after the week start, the language or the whole
    /// settings changed.</summary>
    private void RebuildStatsWeeklyCsvChoices()
    {
        var culture = LocalizationService.Instance.DisplayCulture;
        var first = StatsDisplayChoices.ResolveFirstDay(_settings.StatsWeekStart, culture);
        var day = AppSettings.NormalizeWeeklyCsvDay(_settings.StatsWeeklyCsvDay);
        var hour = AppSettings.NormalizeWeeklyCsvHour(_settings.StatsWeeklyCsvHour);
        _settings.StatsWeeklyCsvDay = day;
        _settings.StatsWeeklyCsvHour = hour;

        var days = Enumerable.Range(0, 7).Select(offset => (DayOfWeek)(((int)first + offset) % 7)).ToList();
        Fill(StatsWeeklyCsvDayChoices, days, QuietDayViewModel.NameOf);
        Fill(StatsWeeklyCsvHourChoices, Enumerable.Range(0, 24).ToList(), full => HourText(full, culture));

        Choice.Select(StatsWeeklyCsvDayChoices, day);
        Choice.Select(StatsWeeklyCsvHourChoices, hour);
        OnPropertyChanged(nameof(SelectedStatsWeeklyCsvDayChoice));
        OnPropertyChanged(nameof(SelectedStatsWeeklyCsvHourChoice));
    }

    /// <summary>A language switch only rewrites the labels and never touches the collections under the
    /// pickers; the day order follows the week start when the page opens or that setting changes.</summary>
    private void RelabelStatsWeeklyCsvChoices()
    {
        var culture = LocalizationService.Instance.DisplayCulture;
        foreach (var choice in StatsWeeklyCsvDayChoices)
            choice.Label = QuietDayViewModel.NameOf(choice.Value);
        foreach (var choice in StatsWeeklyCsvHourChoices)
            choice.Label = HourText(choice.Value, culture);
    }

    /// <summary>A list whose order stays the same is only relabelled.</summary>
    private static void Fill<TValue>(ObservableCollection<Choice<TValue>> choices, List<TValue> values, Func<TValue, string> label)
        where TValue : notnull
    {
        if (choices.Select(choice => choice.Value).SequenceEqual(values))
        {
            foreach (var choice in choices)
                choice.Label = label(choice.Value);
            return;
        }

        choices.Clear();
        foreach (var value in values)
            choices.Add(Choice.WithFixedLabel(label(value), value));
    }

    internal static string HourText(int hour, CultureInfo culture) =>
        new DateTime(2000, 1, 1, hour, 0, 0, DateTimeKind.Unspecified).ToString("t", culture);

    private void RefreshStatsWeeklyCsvTexts()
    {
        OnPropertyChanged(nameof(StatsWeeklyCsvFolderText));
        OnPropertyChanged(nameof(StatsWeeklyCsvFolderButtonText));
    }
}
