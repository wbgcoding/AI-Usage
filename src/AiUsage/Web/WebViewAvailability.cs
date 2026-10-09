using Microsoft.Web.WebView2.Core;

namespace AiUsage.Web;

/// <summary>
/// Checks whether the WebView2 runtime is installed. This class only probes and never downloads
/// anything; a missing runtime is the "RuntimeMissing" state, where the app offers to install it
/// through Microsoft's signed bootstrapper (see WebViewRuntimeInstaller), with a link to Microsoft's
/// own download page as the manual alternative.
/// </summary>
public static class WebViewAvailability
{
    public const string DownloadPageUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static bool IsInstalled() => IsInstalled(() => CoreWebView2Environment.GetAvailableBrowserVersionString());

    /// <summary>Test seam: a fake probe instead of the real CoreWebView2Environment call.</summary>
    internal static bool IsInstalled(Func<string?> probe)
    {
        try
        {
            return !string.IsNullOrEmpty(probe());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }
}
