namespace AiUsage.Web;

/// <summary>What the install flow needs from the screen: the question, the running install with its
/// progress, and the failure notice. A seam so the flow's decisions are testable without a window.</summary>
public interface IWebViewInstallUi
{
    /// <summary>Asks whether to install the missing runtime now; true = yes.</summary>
    bool ConfirmInstall();

    /// <summary>Runs the install with progress and a cancel option, returns once it is over.</summary>
    WebViewInstallResult RunInstall(WebViewRuntimeInstaller installer);

    /// <summary>Tells the user the install failed, with the technical line and the way to the
    /// download page.</summary>
    void ShowFailure(string? cause);
}

/// <summary>
/// What happens before a sign-in window may open: when the WebView2 Runtime is missing, offer to
/// install it, and only continue once it is really there. Declining or cancelling ends quietly; a
/// failed install says so and points at Microsoft's download page.
/// </summary>
public static class WebViewInstallFlow
{
    /// <summary>True when the runtime is available (it was, or the install just made it so).</summary>
    public static bool EnsureRuntime(Func<bool> isInstalled, WebViewRuntimeInstaller installer, IWebViewInstallUi ui)
    {
        if (isInstalled())
            return true;

        if (!ui.ConfirmInstall())
            return false;

        var result = ui.RunInstall(installer);
        switch (result.Outcome)
        {
            case WebViewInstallOutcome.Installed:
                return isInstalled();
            case WebViewInstallOutcome.Cancelled:
                return false;
            default:
                ui.ShowFailure(result.Cause);
                return false;
        }
    }
}
