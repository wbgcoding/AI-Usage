namespace AiUsage.Services;

/// <summary>
/// Where each provider's own usage page lives on the web. These are facts about other people's
/// products, not this app's - the kind of thing a later redesign on their end quietly breaks, so a
/// dead link here is expected to need a fix someday, not a sign of a bug in this file.
/// </summary>
public static class ProviderLinks
{
    private static readonly IReadOnlyDictionary<string, Uri> UsagePages = new Dictionary<string, Uri>
    {
        ["claude"] = new Uri("https://claude.ai/settings/usage"),
        ["codex"] = new Uri("https://chatgpt.com/codex/settings/usage"),
        ["cursor"] = new Uri("https://cursor.com/dashboard/usage"),
        ["gemini"] = new Uri("https://aistudio.google.com/usage"),
        ["copilot"] = new Uri("https://github.com/settings/copilot"),
    };

    /// <summary>Null for a provider this table has no address for - the caller disables its menu
    /// entry rather than hiding it, so a future provider added without an entry here still explains
    /// itself instead of silently vanishing from the menu.</summary>
    public static Uri? UsagePage(string providerId) => UsagePages.GetValueOrDefault(providerId);
}
