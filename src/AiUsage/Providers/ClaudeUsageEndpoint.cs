using AiUsage.Providers.Parsing;

namespace AiUsage.Providers;

/// <summary>
/// Claude's half of a web-backed read: the scripts in <see cref="ClaudeDiscoveryScript"/> and the
/// response shape <see cref="ClaudeUsageParser"/> knows. Holds no state, so one instance per
/// provider instance costs nothing.
/// </summary>
public sealed class ClaudeUsageEndpoint : IWebUsageEndpoint
{
    public string Discover() => ClaudeDiscoveryScript.Discover();

    public string Fetch(string path) => ClaudeDiscoveryScript.Fetch(path);

    public bool IsAllowedUsagePath(string? path) => ClaudeDiscoveryScript.IsAllowedUsagePath(path);

    public WebUsageResult ParseBody(string body)
    {
        var parsed = ClaudeUsageParser.Parse(body);
        return parsed.Outcome switch
        {
            ClaudeUsageOutcome.Ok => new WebUsageResult(WebUsageOutcome.Ok, parsed.Windows),
            ClaudeUsageOutcome.Blocked => WebUsageResult.Blocked,
            _ => WebUsageResult.Failed,
        };
    }
}
