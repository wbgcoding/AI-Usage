using System.Windows;
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
}
