using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Storage;

/// <summary>Window labels a provider used to store as plain English words and now stores as resource
/// keys. Everything persisted under the old label (hidden windows, the tray window choice, recorded
/// history points) is renamed on load so it keeps matching the live windows.</summary>
internal static class LegacyWindowLabels
{
    private static readonly Dictionary<string, string> Copilot = new(StringComparer.Ordinal)
    {
        ["Chat"] = "Window_CopilotChat",
        ["Completions"] = "Window_CopilotCompletions",
        ["Premium"] = "Window_CopilotPremium",
    };

    /// <summary>The current label for one stored label of the account <paramref name="accountKey"/>;
    /// the label unchanged when it is not a legacy one or the account is not a Copilot one.</summary>
    internal static string Migrate(string accountKey, string label) =>
        ProviderRegistry.BaseProviderId(accountKey) == "copilot" && Copilot.TryGetValue(label, out var current)
            ? current
            : label;

    /// <summary>The tray window choice ("Label:&lt;label&gt;") with a legacy Copilot label renamed.</summary>
    internal static string MigrateTrayWindow(string trayWindow, string labelPrefix)
    {
        if (!trayWindow.StartsWith(labelPrefix, StringComparison.Ordinal))
            return trayWindow;
        return Copilot.TryGetValue(trayWindow[labelPrefix.Length..], out var current) ? labelPrefix + current : trayWindow;
    }

    /// <summary>Renames the legacy labels in a loaded settings object, dropping a duplicate a
    /// rename would create.</summary>
    internal static void Migrate(AppSettings settings, string trayLabelPrefix)
    {
        foreach (var (accountKey, provider) in settings.Providers)
        {
            if (provider.HiddenWindows is { Count: > 0 } hidden)
                provider.HiddenWindows = hidden.Select(label => Migrate(accountKey, label)).Distinct(StringComparer.Ordinal).ToList();
        }

        // A "Label:Chat" choice belongs to Copilot only when the icon follows a Copilot account or the
        // highest tile; for any other pinned provider the same word is that provider's own label.
        var trayFollowsCopilot = settings.TrayProvider is { } trayProvider
            && (trayProvider == "Auto" || ProviderRegistry.BaseProviderId(trayProvider) == "copilot");
        if (trayFollowsCopilot && settings.TrayWindow is { } trayWindow)
            settings.TrayWindow = MigrateTrayWindow(trayWindow, trayLabelPrefix);
    }
}
