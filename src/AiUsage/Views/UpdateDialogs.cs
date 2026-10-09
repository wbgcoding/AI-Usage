using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using AiUsage.Services;
using AiUsage.ViewModels;

namespace AiUsage.Views;

/// <summary>The one place an install request is started from a window and its failure is shown, so
/// the notice bar and the About page behave the same.</summary>
internal static class UpdateDialogs
{
    /// <summary>Runs the install and, for anything but a hand-over, tells the user what happened and
    /// offers the release page.</summary>
    public static async Task InstallAsync(Window owner, UpdateNoticeViewModel update)
    {
        UpdateInstallResult result;
        try
        {
            result = await update.InstallAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Shared.LogError($"Update install failed ({ex.GetType().Name}): {PathSanitizer.Sanitize(ex.Message)}");
            result = new UpdateInstallResult(UpdateOutcome.DownloadFailed, update.ReleaseUrl);
        }

        if (result.Outcome == UpdateOutcome.Started)
            return;

        var loc = LocalizationService.Instance;
        var message = loc[MessageKey(result.Outcome)];
        if (result.Outcome == UpdateOutcome.SwapFailedRestoreNeeded)
            message = RestoreMessage(message, result.RestoreNeededPath);

        if (result.Outcome is UpdateOutcome.InstalledRestartNeeded or UpdateOutcome.SwapFailedRestoreNeeded or UpdateOutcome.NotStarted)
        {
            // Nothing a download would fix (the new version is in place, the program file needs renaming,
            // or the setup only has to be started again): only a close button.
            ConfirmWindow.Show(owner, AppInfo.ProductName, message, loc["TitleBar.Close"], "");
            return;
        }

        if (ConfirmWindow.Show(owner, AppInfo.ProductName, message, loc["About.OpenReleasePage"], loc["TitleBar.Close"]))
            OpenReleasePage(result.ReleaseUrl);
    }

    /// <summary>The restore message with the path the swap reported; the path is never worked out again
    /// here, since the running program's own path may already name the parked copy.</summary>
    internal static string RestoreMessage(string template, string? oldPath) =>
        string.Format(CultureInfo.CurrentCulture, template, oldPath ?? "AI-Usage.exe.old");

    internal static string MessageKey(UpdateOutcome outcome) => outcome switch
    {
        UpdateOutcome.NotVerified => "Update.NotVerified",
        UpdateOutcome.NotNewer => "Update.NotNewer",
        UpdateOutcome.InstalledRestartNeeded => "Update.InstalledRestart",
        UpdateOutcome.SwapFailedRestoreNeeded => "Update.RestoreNeeded",
        UpdateOutcome.NotStarted => "Update.NotStarted",
        _ => "Update.NotLoaded",
    };

    public static void OpenReleasePage(string? url)
    {
        var target = UpdateCheck.IsReleasePageUrl(url) ? url! : AppInfo.ArchiveUrl + "/releases";
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser registered - nothing sensible to recover into.
        }
    }
}
