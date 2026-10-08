using AiUsage.Models;
using AiUsage.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.ViewModels;

/// <summary>One provider's own collapsible notification row: a master "notifications for this
/// tile at all" switch, and, gated underneath it and the global <see cref="AppSettings.NotifyOnReset"/>,
/// its own "tell me when it is free again" switch, plus its own threshold override
/// (<see cref="Threshold"/>). Writes straight into the shared <see cref="ProviderSettings"/> instance
/// <see cref="MainViewModel.Tail"/> already reads.</summary>
public sealed partial class NotificationRowViewModel : ObservableObject
{
    private readonly ProviderSettings _providerSettings;
    private readonly Action _save;

    /// <summary>The stable key a caller or a test uses to find one provider's row - not something the view binds.</summary>
    public string ProviderId { get; }

    /// <summary>The row's own label, one that tells two accounts of the same provider apart (see
    /// <see cref="ProviderTileViewModel.HeaderDisplayName"/>) - a plain provider name would show two
    /// identical unlabeled "Claude" rows once a second account exists.</summary>
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
        Threshold.SetHeaderDisplayName(headerDisplayName);
    }

    /// <summary>The same account's threshold override, shown inside this row once it is expanded.</summary>
    public ThresholdRowViewModel Threshold { get; }

    /// <summary>The tile this row belongs to, for which window kinds its provider actually has.</summary>
    public ProviderTileViewModel? Tile { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private bool notificationsEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private bool notifyOnResetEnabled;

    /// <summary>Collapsed by default: the page lists one short line per provider, the switches live
    /// behind it. Not persisted - a freshly opened settings window always starts tidy.</summary>
    [ObservableProperty]
    private bool isExpanded;

    /// <summary>What the collapsed row says about its hidden switches: off, the shared default, or
    /// its own values.</summary>
    public string SummaryText => !NotificationsEnabled
        ? Loc["Settings.NotifySummary.Off"]
        : Threshold.UseCustom ? Loc["Settings.NotifySummary.Custom"] : Loc["Settings.NotifySummary.Default"];

    /// <summary>"Meldungen für Claude" - the row's own master-switch label, with the provider name
    /// filled in so several rows never read as identical text.</summary>
    public string NotificationsForProviderText => Loc.Format("Settings.NotificationsForProvider", HeaderDisplayName);

    /// <summary>Names what the master switch actually toggles for a screen reader, which otherwise
    /// only announces the checkbox's own bare "Ein" content.</summary>
    public string NotificationsEnabledFullLabel => $"{NotificationsForProviderText}: {Loc["Settings.On"]}";

    public string NotifyOnResetEnabledFullLabel =>
        $"{HeaderDisplayName} {Loc["Settings.NotifyOnResetForProvider"]}: {Loc["Settings.On"]}";

    private static LocalizationService Loc => LocalizationService.Instance;

    /// <summary>Called by the owning <see cref="SettingsViewModel"/>'s single subscription - this row
    /// never subscribes to <see cref="LocalizationService"/> itself, so rebuilding
    /// <see cref="SettingsViewModel.NotificationRows"/> never leaves an orphaned handler behind.</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(NotificationsForProviderText));
        OnPropertyChanged(nameof(NotificationsEnabledFullLabel));
        OnPropertyChanged(nameof(NotifyOnResetEnabledFullLabel));
        OnPropertyChanged(nameof(SummaryText));
    }

    public NotificationRowViewModel(
        string providerId, string headerDisplayName, ProviderSettings providerSettings, Action save,
        ProviderTileViewModel? tile = null)
    {
        ProviderId = providerId;
        HeaderDisplayName = headerDisplayName;
        _providerSettings = providerSettings;
        _save = save;
        Tile = tile;
        Threshold = new ThresholdRowViewModel(providerId, headerDisplayName, providerSettings.Thresholds, save);
        Threshold.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThresholdRowViewModel.UseCustom))
                OnPropertyChanged(nameof(SummaryText));
        };

        notificationsEnabled = providerSettings.NotificationsEnabled;
        notifyOnResetEnabled = providerSettings.NotifyOnResetEnabled;
    }

    /// <summary>What a screen reader reads for this row's list item, which has no name of its own
    /// and therefore falls back to this - the type name until now.</summary>
    public override string ToString() => HeaderDisplayName;

    partial void OnNotificationsEnabledChanged(bool value)
    {
        _providerSettings.NotificationsEnabled = value;
        _save();
    }

    partial void OnNotifyOnResetEnabledChanged(bool value)
    {
        _providerSettings.NotifyOnResetEnabled = value;
        _save();
    }
}
