using AiUsage.Models;
using AiUsage.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.ViewModels;

/// <summary>What happened to an install request: the outcome, and the release page the failure
/// dialog offers to open.</summary>
public sealed record UpdateInstallResult(UpdateOutcome Outcome, string? ReleaseUrl, string? RestoreNeededPath = null);

/// <summary>
/// The one place that knows whether a newer version exists: the daily check writes its finding into
/// the settings (so a restart between two checks still shows it), and both the notice bar in the main
/// window and the About page read it from here. Installing always re-reads the newest release first,
/// so the files it loads are the current ones.
/// </summary>
public sealed partial class UpdateNoticeViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _save;
    private readonly Func<IUpdateHost> _createHost;
    private bool _installing;

    public UpdateNoticeViewModel(AppSettings settings, Action<AppSettings> save, Func<IUpdateHost>? createHost = null)
    {
        _settings = settings;
        _save = save;
        _createHost = createHost ?? (() => new UpdateHost(() => RequestExit?.Invoke()));
        Refresh();
    }

    /// <summary>How the running copy ends once a verified update has been handed over; set by the main
    /// window, which owns the real shutdown (the widget otherwise only hides into the tray).</summary>
    internal Action? RequestExit { get; set; }

    /// <summary>The release lookup; replaced in tests so nothing here touches the network.</summary>
    internal Func<CancellationToken, Task<UpdateCheck.Release?>> FetchLatestRelease { get; set; } = UpdateCheck.FetchLatestAsync;

    /// <summary>Test seam for the clock the daily check compares against.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The version a newer release carries (without the "v"), null while there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(ShowNotice))]
    [NotifyPropertyChangedFor(nameof(NoticeText))]
    private string? availableVersion;

    /// <summary>Set once a check has actually reached the release feed and found nothing newer.</summary>
    [ObservableProperty]
    private bool isKnownUpToDate;

    /// <summary>"Later": hides the bar until the next program start, the About page keeps its line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotice))]
    private bool dismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool isInstalling;

    /// <summary>False while an install is under way, so a second click does nothing.</summary>
    public bool CanInstall => !IsInstalling;

    public string? ReleaseUrl => _settings.KnownLatestUrl;

    public bool HasUpdate => AvailableVersion is not null;

    public bool ShowNotice => HasUpdate && !Dismissed;

    public string NoticeText => AvailableVersion is { } version
        ? LocalizationService.Instance.Format("About.UpdateAvailable", version)
        : "";

    public void Dismiss() => Dismissed = true;

    /// <summary>Runs the daily check (see <see cref="UpdateCheck.CheckIfDueAsync"/>) and refreshes
    /// the notice from what it found. Returns the release the feed answered with, null when the
    /// check was off, not due or failed.</summary>
    public async Task<UpdateCheck.Release?> CheckAsync(CancellationToken ct)
    {
        var release = await UpdateCheck.CheckIfDueAsync(_settings, _save, FetchLatestRelease, Now(), ct);
        if (release is not null)
            Record(release);

        Refresh();
        return release;
    }

    /// <summary>Re-reads the newest release and installs it when it is newer. The caller shows a
    /// dialog for anything but <see cref="UpdateOutcome.Started"/>.</summary>
    public async Task<UpdateInstallResult> InstallAsync(CancellationToken ct)
    {
        if (_installing)
            return new UpdateInstallResult(UpdateOutcome.DownloadFailed, ReleaseUrl);

        _installing = true;
        IsInstalling = true;
        try
        {
            var release = await FetchLatestRelease(ct);
            if (release is null || !UpdateCheck.IsNewer(AppInfo.Version, release.TagName))
                return new UpdateInstallResult(UpdateOutcome.DownloadFailed, ReleaseUrl);

            Record(release);
            var installer = new UpdateInstaller(_createHost(), LogService.Shared.LogInfo);
            var outcome = await installer.InstallAsync(release, ct);
            return new UpdateInstallResult(outcome, release.HtmlUrl, installer.RestoreNeededPath);
        }
        finally
        {
            _installing = false;
            IsInstalling = false;
        }
    }

    private void Record(UpdateCheck.Release release)
    {
        var newer = UpdateCheck.IsNewer(AppInfo.Version, release.TagName);
        _settings.KnownLatestTag = newer ? release.TagName : null;
        _settings.KnownLatestUrl = newer ? release.HtmlUrl : null;
        _save(_settings);
        IsKnownUpToDate = !newer;
    }

    private void Refresh() =>
        AvailableVersion = _settings.CheckForUpdates && _settings.KnownLatestTag is { } tag && UpdateCheck.IsNewer(AppInfo.Version, tag)
            ? UpdateCheck.DisplayVersion(tag)
            : null;
}
