using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using AiUsage.Providers.LocalLogin;
using AiUsage.Services;
using AiUsage.Web;

namespace AiUsage.Views;

/// <summary>
/// The one "sign in to a web-backed provider" flow, reused by the Settings window's own button and
/// by a tile's action button on the main display alike - opens the real sign-in window for
/// whichever provider <paramref name="descriptor"/> names, or offers to install the WebView2 Runtime
/// first when it is missing (see <see cref="WebViewInstallFlow"/>). Never duplicated per caller, and
/// never per provider either.
/// </summary>
internal static class WebSignInFlow
{
    /// <param name="onClosed">Called once the window is gone, with whether the sign-in actually
    /// finished (see <see cref="SignInWindow.SignedIn"/>) rather than being closed part-way.</param>
    public static void Open(Window owner, WebSessionDescriptor descriptor, Action<bool> onClosed)
    {
        if (!WebViewInstallFlow.EnsureRuntime(WebViewAvailability.IsInstalled, WebViewRuntimeInstaller.CreateDefault(), new WebViewInstallUi(owner)))
            return;

        var window = new SignInWindow(descriptor);
        OwnerWindowResolver.ApplyOwner(window, owner);
        // Closed fires synchronously before ShowDialog returns - no guessed settle time needed, the
        // session is already written to disk by the time the just-closed window is gone.
        window.Closed += (_, _) => onClosed(window.SignedIn);
        if (window.Owner is null)
        {
            // No owner to stack it above - a freshly created top-level window is not guaranteed to
            // land in the foreground on its own, so ask for activation the same narrow way every
            // other forced-forward call in this app does (one attempt, no escalation on refusal).
            window.Loaded += (_, _) => WindowActivation.TryActivate(window.Activate);
        }
        window.ShowDialog();
    }

    /// <summary>For a Copilot tile whose GitHub CLI is missing: the same themed offer
    /// <see cref="Open"/> makes for a missing WebView2 runtime, here for the CLI Copilot reads through
    /// instead. Returns true when the tile was handled that way and the caller must not reconnect.</summary>
    public static bool OfferGitHubCliIfMissing(Window owner, string realProviderId)
    {
        if (realProviderId != "copilot" || GitHubCliUsage.IsCliInstalled())
            return false;

        var loc = LocalizationService.Instance;
        var openDownloadPage = ConfirmWindow.Show(
            owner,
            loc["State.GitHubCliMissing.Head"],
            loc["State.GitHubCliMissing.Reason"],
            loc["State.GitHubCliMissing.Action"],
            loc["Action.Cancel"]);

        if (!openDownloadPage)
            return true;

        try
        {
            Process.Start(new ProcessStartInfo(GitHubCliUsage.DownloadUrl) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser registered - nothing sensible to recover into.
        }
        return true;
    }
}
