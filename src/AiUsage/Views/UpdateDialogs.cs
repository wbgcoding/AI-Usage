using System.ComponentModel;
using System.Diagnostics;
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
        var result = await update.InstallAsync(CancellationToken.None);
        if (result.Outcome == UpdateOutcome.Started)
            return;

        var loc = LocalizationService.Instance;
        var message = loc[MessageKey(result.Outcome)];
        if (ConfirmWindow.Show(owner, AppInfo.ProductName, message, loc["About.OpenReleasePage"], loc["TitleBar.Close"]))
            OpenReleasePage(result.ReleaseUrl);
    }

    internal static string MessageKey(UpdateOutcome outcome) => outcome switch
    {
        UpdateOutcome.NotVerified => "Update.NotVerified",
        UpdateOutcome.NotNewer => "Update.NotNewer",
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
