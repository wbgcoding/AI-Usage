using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using AiUsage.Services;
using AiUsage.Web;

namespace AiUsage.Views;

/// <summary>The install flow's real screens: the app's own themed dialog for the question, the
/// progress and the failure notice (see <see cref="ConfirmWindow"/>).</summary>
internal sealed class WebViewInstallUi(Window owner) : IWebViewInstallUi
{
    public bool ConfirmInstall()
    {
        var loc = LocalizationService.Instance;
        return ConfirmWindow.Show(owner, AppInfo.ProductName, loc["WebView2.Install.Prompt"], loc["WebView2.Install.Confirm"], loc["Action.Cancel"]);
    }

    public WebViewInstallResult RunInstall(WebViewRuntimeInstaller installer)
    {
        var loc = LocalizationService.Instance;
        return ConfirmWindow.RunWithProgress(
            owner, AppInfo.ProductName, loc["WebView2.Install.Downloading"], loc["Action.Cancel"],
            (progress, enterPhase, ct) => installer.InstallAsync(progress, () => enterPhase(loc["WebView2.Install.Installing"]), ct));
    }

    public void ShowFailure(string? cause)
    {
        var loc = LocalizationService.Instance;
        var openPage = ConfirmWindow.Show(
            owner, AppInfo.ProductName, loc["WebView2.Install.Failed"], loc["WebView2.Install.OpenDownloadPage"], loc["Action.Cancel"],
            detailText: cause ?? "");
        if (!openPage)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(WebViewAvailability.DownloadPageUrl) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser registered - nothing sensible to recover into.
        }
    }
}
