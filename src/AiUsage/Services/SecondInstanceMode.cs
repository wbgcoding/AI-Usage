using AiUsage.Storage;
using AiUsage.Web;

namespace AiUsage.Services;

/// <summary>
/// The "--second-instance" switch: starts another copy of the widget alongside the one already
/// running, for trying a fresh build without closing the everyday one. Everything that could collide
/// between the two moves into its own subfolder - settings, history, logs and the browser sign-in
/// profiles - so the running copy's data is never written by the test copy, and the single-instance
/// check is skipped for that start only. Nothing about this switch changes the ordinary start.
/// </summary>
public static class SecondInstanceMode
{
    internal const string Switch = "--second-instance";

    /// <summary>The subfolder both the data directory and the browser profile root get in this mode.
    /// Named plainly, because a user who finds it should be able to tell what it is.</summary>
    internal const string FolderName = "second-instance";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>Points this process at its own data directory and its own browser profile root.
    /// Called once, before anything opens a file - a later call would leave stores already pointed at
    /// the everyday folders.</summary>
    public static void Redirect()
    {
        AppPaths.SetOverride(Path.Combine(AppPaths.DataDirectory, FolderName));
        WebViewHost.SetRootOverride(Path.Combine(WebViewHost.SharedRootFolder(), FolderName));
    }
}
