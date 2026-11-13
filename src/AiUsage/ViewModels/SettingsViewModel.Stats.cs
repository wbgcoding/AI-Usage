using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using AiUsage.Services;
using AiUsage.Stats;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.ViewModels;

/// <summary>The "Token usage" page: how the statistics window opens and looks, and the size of the index.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>"Last used" and the periods the window can open on; the custom period is not offered.</summary>
    public ObservableCollection<Choice<string>> StatsOpenRangeChoices { get; } =
    [
        new("Settings.Stats.OpenRange.Last", StatsDisplayChoices.LastUsedRange),
        new("Chart.Range.Week", "Week"),
        new("Chart.Range.Month", "Month"),
        new("Stats.Range.ThisMonth", "ThisMonth"),
        new("Stats.Range.LastMonth", "LastMonth"),
        new("StatsWindow.Range.TwelveMonths", "Year"),
        new("Chart.Range.All", "All"),
    ];

    public Choice<string>? SelectedStatsOpenRangeChoice
    {
        get => StatsOpenRangeChoices.FirstOrDefault(choice => choice.IsSelected);
        set
        {
            if (value is null || value.Value == StatsDisplayChoices.NormalizeOpenRange(_settings.StatsOpenRange))
                return;

            _settings.StatsOpenRange = value.Value;
            Choice.Select(StatsOpenRangeChoices, value.Value);
            _store.RequestSave(_settings);
            OnPropertyChanged();
        }
    }

    public ObservableCollection<Choice<string>> StatsWeekStartChoices { get; } =
    [
        new("Settings.Stats.WeekStart.System", StatsDisplayChoices.SystemWeekStart),
        new("Settings.Stats.WeekStart.Monday", "Monday"),
        new("Settings.Stats.WeekStart.Sunday", "Sunday"),
    ];

    public Choice<string>? SelectedStatsWeekStartChoice
    {
        get => StatsWeekStartChoices.FirstOrDefault(choice => choice.IsSelected);
        set
        {
            if (value is null || value.Value == StatsDisplayChoices.NormalizeWeekStart(_settings.StatsWeekStart))
                return;

            _settings.StatsWeekStart = value.Value;
            Choice.Select(StatsWeekStartChoices, value.Value);
            _store.RequestSave(_settings);
            OnPropertyChanged();
            Main.StatsDisplayChanged();
            StatsDisplayChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>One check box per section of the statistics window, in the order of its default arrangement.</summary>
    public ObservableCollection<StatsSectionToggleViewModel> StatsSections { get; } = [];

    /// <summary>Raised when the week start or the shown sections changed, so the open statistics window
    /// and the day grid tile follow at once.</summary>
    public event EventHandler? StatsDisplayChanged;

    /// <summary>The size of the token index ("Index: 1,234 entries, 3.2 MB"); empty until read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatsIndexSize))]
    private string statsIndexSizeText = "";

    public bool HasStatsIndexSize => StatsIndexSizeText.Length > 0;

    internal Task StatsIndexInfoLoadTask { get; private set; } = Task.CompletedTask;

    private (long Entries, long Bytes)? _statsIndexInfo;

    // True while the check boxes are set from the settings (open, reset, import): that is not a choice.
    private bool _settingStatsSections;

    private void InitializeStatsPage()
    {
        RebuildStatsSections();
        Choice.Select(StatsOpenRangeChoices, StatsDisplayChoices.NormalizeOpenRange(_settings.StatsOpenRange));
        Choice.Select(StatsWeekStartChoices, StatsDisplayChoices.NormalizeWeekStart(_settings.StatsWeekStart));
        StatsIndexInfoLoadTask = LoadStatsIndexInfoAsync();
    }

    private void RebuildStatsSections()
    {
        var hidden = StatsDisplayChoices.NormalizeHiddenSections(_settings.StatsHiddenSections);
        _settingStatsSections = true;
        try
        {
            StatsSections.Clear();
            foreach (var (key, titleKey) in StatsDisplayChoices.Sections)
                StatsSections.Add(new StatsSectionToggleViewModel(key, titleKey, !hidden.Contains(key), OnStatsSectionToggled));
        }
        finally
        {
            _settingStatsSections = false;
        }
    }

    private void OnStatsSectionToggled()
    {
        if (_settingStatsSections)
            return;

        _settings.StatsHiddenSections = [.. StatsSections.Where(section => !section.IsShown).Select(section => section.Key)];
        _store.RequestSave(_settings);
        StatsDisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the stored period, week start and sections after a reset or an import replaced them.</summary>
    private void RefreshStatsDisplayChoices()
    {
        _settings.StatsOpenRange = StatsDisplayChoices.NormalizeOpenRange(_settings.StatsOpenRange);
        _settings.StatsWeekStart = StatsDisplayChoices.NormalizeWeekStart(_settings.StatsWeekStart);
        _settings.StatsHiddenSections = StatsDisplayChoices.NormalizeHiddenSections(_settings.StatsHiddenSections);
        Choice.Select(StatsOpenRangeChoices, _settings.StatsOpenRange);
        Choice.Select(StatsWeekStartChoices, _settings.StatsWeekStart);
        OnPropertyChanged(nameof(SelectedStatsOpenRangeChoice));
        OnPropertyChanged(nameof(SelectedStatsWeekStartChoice));
        RebuildStatsSections();
        Main.StatsDisplayChanged();
    }

    private void RefreshStatsLabels()
    {
        foreach (var choice in StatsOpenRangeChoices)
            choice.RefreshLabel();
        foreach (var choice in StatsWeekStartChoices)
            choice.RefreshLabel();
        foreach (var section in StatsSections)
            section.RefreshTitle();
        RefreshStatsIndexSizeText();
    }

    /// <summary>Reads the row count and the file size of the index off the UI thread; a failing read
    /// leaves the text empty.</summary>
    private async Task LoadStatsIndexInfoAsync()
    {
        if (!_readsMachineList)
            return;

        var text = "";
        try
        {
            var info = await Task.Run(() => _statsStore.IndexInfo());
            _statsIndexInfo = info;
            text = FormatStatsIndexSize(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: the line stays empty
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.BeginInvoke(() => StatsIndexSizeText = text);
        else
            StatsIndexSizeText = text;
    }

    private void RefreshStatsIndexSizeText()
    {
        if (_statsIndexInfo is not null)
            StatsIndexSizeText = FormatStatsIndexSize(_statsIndexInfo);
    }

    private static string FormatStatsIndexSize((long Entries, long Bytes)? info) =>
        info is { } known
            ? LocalizationService.Instance.Format("Settings.Stats.IndexSize", known.Entries.ToString("N0", CultureInfo.CurrentCulture), FormatHistorySize(known.Bytes))
            : "";
}
