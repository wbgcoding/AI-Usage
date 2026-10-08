using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.ViewModels;

/// <summary>One provider's own notification override: a single "own value" switch, and, only once
/// that is ticked, three sliders (5-Stunden/Woche/Andere) with their own Ein/Aus switch each. Writes
/// straight into the shared <see cref="ThresholdSettings"/> instance
/// <see cref="NotificationService"/> already reads (through <see cref="SettingsRanges.ResolveThreshold"/>).</summary>
public sealed partial class ThresholdRowViewModel : ObservableObject
{
    private readonly ThresholdSettings _thresholds;
    private readonly Action _save;

    /// <summary>The stable key a caller or a test uses to find one provider's row - not something the view binds.</summary>
    public string ProviderId { get; }

    /// <summary>The row's own label, one that tells two accounts of the same provider apart (see
    /// <see cref="ProviderTileViewModel.HeaderDisplayName"/>, whose value this is built from) - a
    /// plain provider name would show two identical unlabeled "Claude" rows once a second account
    /// exists.</summary>
    public string HeaderDisplayName { get; private set; }

    /// <summary>An account's name can arrive after this row was built (its first snapshot while the
    /// window is open); the owning <see cref="SettingsViewModel"/> hands the new text in.</summary>
    internal void SetHeaderDisplayName(string headerDisplayName)
    {
        if (headerDisplayName == HeaderDisplayName)
            return;
        HeaderDisplayName = headerDisplayName;
        OnPropertyChanged(nameof(HeaderDisplayName));
        RefreshLabels();
    }

    [ObservableProperty]
    private bool useCustom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FiveHourLabel))]
    [NotifyPropertyChangedFor(nameof(FiveHourFullLabel))]
    private double fiveHour;

    [ObservableProperty]
    private bool fiveHourEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WeeklyLabel))]
    [NotifyPropertyChangedFor(nameof(WeeklyFullLabel))]
    private double weekly;

    [ObservableProperty]
    private bool weeklyEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OtherLabel))]
    [NotifyPropertyChangedFor(nameof(OtherFullLabel))]
    private double other;

    [ObservableProperty]
    private bool otherEnabled;

    public string FiveHourLabel => PercentLabel(FiveHour);
    public string WeeklyLabel => PercentLabel(Weekly);
    public string OtherLabel => PercentLabel(Other);

    private static string PercentLabel(double percent) =>
        StatusTextMap.FormatPercent(percent.ToString("0", CultureInfo.CurrentCulture));

    public string FiveHourFullLabel => $"{Loc["Window.FiveHour"]}: {FiveHourLabel}";
    public string WeeklyFullLabel => $"{Loc["Window.Weekly"]}: {WeeklyLabel}";
    public string OtherFullLabel => $"{Loc["Window.Other"]}: {OtherLabel}";

    /// <summary>Names what each window's own Ein/Aus check box actually toggles, including which
    /// provider's row it lives in - without this a screen reader announces four bare "Ein" boxes
    /// (the checkbox's own Content text) with no way to tell them apart.</summary>
    public string FiveHourEnabledFullLabel => $"{HeaderDisplayName} {Loc["Window.FiveHour"]}: {Loc["Settings.On"]}";
    public string WeeklyEnabledFullLabel => $"{HeaderDisplayName} {Loc["Window.Weekly"]}: {Loc["Settings.On"]}";
    public string OtherEnabledFullLabel => $"{HeaderDisplayName} {Loc["Window.Other"]}: {Loc["Settings.On"]}";

    public string OwnThresholdFullLabel => $"{HeaderDisplayName}: {Loc["Settings.OwnThreshold"]}";

    private static LocalizationService Loc => LocalizationService.Instance;

    /// <summary>Called by the owning <see cref="SettingsViewModel"/>'s single subscription
    /// - this row never subscribes to <see cref="LocalizationService"/> itself, so
    /// rebuilding <see cref="SettingsViewModel.ThresholdRows"/> (e.g. <see cref="SettingsViewModel.ResetToDefaults"/>)
    /// never leaves an orphaned handler behind.</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(FiveHourFullLabel));
        OnPropertyChanged(nameof(WeeklyFullLabel));
        OnPropertyChanged(nameof(OtherFullLabel));
        OnPropertyChanged(nameof(FiveHourEnabledFullLabel));
        OnPropertyChanged(nameof(WeeklyEnabledFullLabel));
        OnPropertyChanged(nameof(OtherEnabledFullLabel));
        OnPropertyChanged(nameof(OwnThresholdFullLabel));
    }

    public ThresholdRowViewModel(string providerId, string headerDisplayName, ThresholdSettings thresholds, Action save)
    {
        ProviderId = providerId;
        HeaderDisplayName = headerDisplayName;
        _thresholds = thresholds;
        _save = save;

        useCustom = thresholds.UseCustom;
        fiveHour = thresholds.FiveHour;
        fiveHourEnabled = thresholds.FiveHourEnabled;
        weekly = thresholds.Weekly;
        weeklyEnabled = thresholds.WeeklyEnabled;
        other = thresholds.Other;
        otherEnabled = thresholds.OtherEnabled;
    }

    /// <summary>What a screen reader reads for this row's list item, which has no name of its own
    /// and therefore falls back to this - the type name until now.</summary>
    public override string ToString() => HeaderDisplayName;

    partial void OnUseCustomChanged(bool value)
    {
        _thresholds.UseCustom = value;
        _save();
    }

    partial void OnFiveHourChanged(double value)
    {
        var clamped = SettingsRanges.ClampThreshold(value);
        if (Math.Abs(clamped - value) > 0.001)
        {
            FiveHour = clamped;
            return;
        }
        _thresholds.FiveHour = clamped;
        _save();
    }

    partial void OnFiveHourEnabledChanged(bool value)
    {
        _thresholds.FiveHourEnabled = value;
        _save();
    }

    partial void OnWeeklyChanged(double value)
    {
        var clamped = SettingsRanges.ClampThreshold(value);
        if (Math.Abs(clamped - value) > 0.001)
        {
            Weekly = clamped;
            return;
        }
        _thresholds.Weekly = clamped;
        _save();
    }

    partial void OnWeeklyEnabledChanged(bool value)
    {
        _thresholds.WeeklyEnabled = value;
        _save();
    }

    partial void OnOtherChanged(double value)
    {
        var clamped = SettingsRanges.ClampThreshold(value);
        if (Math.Abs(clamped - value) > 0.001)
        {
            Other = clamped;
            return;
        }
        _thresholds.Other = clamped;
        _save();
    }

    partial void OnOtherEnabledChanged(bool value)
    {
        _thresholds.OtherEnabled = value;
        _save();
    }
}
