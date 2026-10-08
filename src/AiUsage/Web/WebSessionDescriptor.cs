namespace AiUsage.Web;

/// <summary>
/// Everything a web-backed provider's own session needs that used to be hardcoded to one provider's
/// name throughout this folder: the page its hidden reader session starts from, the page its
/// sign-in window opens, which hosts that window may navigate to, and which subfolder of the shared
/// webview profile root is its own. One instance per provider that has a web session; a further
/// provider brings its own instance instead of a copy of this plumbing.
/// </summary>
/// <param name="BaseUrl">Where the hidden reader session starts - a usage page, so the fetch script
/// runs on the provider's own origin.</param>
/// <param name="SignInUrl">Where the sign-in window starts, spelled out in full rather than derived
/// from <paramref name="BaseUrl"/>: a usage page is a deep link that a signed-out visitor may well
/// answer with a 404, while this one is the provider's own front door for a signed-out user.</param>
public sealed record WebSessionDescriptor(
    string ProviderId,
    string BaseUrl,
    string SignInUrl,
    IReadOnlyList<string> AllowedHosts,
    string ProfileFolderName);
