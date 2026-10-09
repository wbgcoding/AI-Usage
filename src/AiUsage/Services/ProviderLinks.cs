namespace AiUsage.Services;

/// <summary>
/// Where each provider's own usage page lives on the web. These are facts about other people's
/// products, not this app's - the kind of thing a later redesign on their end quietly breaks, so a
/// dead link here is expected to need a fix someday, not a sign of a bug in this file.
/// </summary>
public static class ProviderLinks
{
    // The web-backed providers open the very address their session descriptor names (Claude's names the
    // site root, so its usage page hangs off that); only Copilot, which has no web session, is spelled
    // out here.
    private static readonly IReadOnlyDictionary<string, Uri> UsagePages = new Dictionary<string, Uri>
    {
        ["claude"] = new Uri(new Uri(ProviderRegistry.WebSessionFor("claude").BaseUrl), "settings/usage"),
        ["codex"] = new Uri(ProviderRegistry.CodexWebSession.BaseUrl),
        ["cursor"] = new Uri(ProviderRegistry.CursorWebSession.BaseUrl),
        ["gemini"] = new Uri(ProviderRegistry.GeminiWebSession.BaseUrl),
        ["copilot"] = new Uri("https://github.com/settings/copilot"),
    };

    // Opened in the browser only, never requested by the app itself.
    private static readonly IReadOnlyDictionary<string, Uri> StatusPages = new Dictionary<string, Uri>
    {
        ["claude"] = new Uri("https://status.anthropic.com"),
        ["codex"] = new Uri("https://status.openai.com"),
        ["cursor"] = new Uri("https://status.cursor.com"),
        ["gemini"] = new Uri("https://aistudio.google.com/status"),
        ["copilot"] = new Uri("https://www.githubstatus.com"),
    };

    /// <summary>The provider's own status page, null for an id this table does not know. Only ever
    /// opened in the user's browser, never fetched by the app.</summary>
    public static Uri? StatusPage(string providerId) => StatusPages.GetValueOrDefault(providerId);

    /// <summary>Null for a provider this table has no address for - the caller disables its menu
    /// entry rather than hiding it, so a future provider added without an entry here still explains
    /// itself instead of silently vanishing from the menu.</summary>
    public static Uri? UsagePage(string providerId) => UsagePages.GetValueOrDefault(providerId);
}
