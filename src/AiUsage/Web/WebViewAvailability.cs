using Microsoft.Web.WebView2.Core;

namespace AiUsage.Web;

/// <summary>
/// Checks whether the WebView2 runtime is installed, without installing or downloading anything -
/// a missing runtime shows a plain-text hint and a link to Microsoft's own download page instead
/// (the "RuntimeMissing" state). The user clicks that link themselves.
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
