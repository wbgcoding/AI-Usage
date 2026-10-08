using AiUsage.Providers.Parsing;

namespace AiUsage.Providers;

/// <summary>
/// Codex's half of a web-backed read: the scripts in <see cref="CodexDiscoveryScript"/> and the
/// rate-limit shape <see cref="CodexWebUsageParser"/> knows. The clock is injectable because the
/// response states its resets relative to the moment it was read.
/// </summary>
public sealed class CodexUsageEndpoint : IWebUsageEndpoint
{
    private readonly Func<DateTimeOffset> _now;

    public CodexUsageEndpoint(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.Now);

    public string Discover() => CodexDiscoveryScript.Discover();

    public string Fetch(string path) => CodexDiscoveryScript.Fetch(path);

    public bool IsAllowedUsagePath(string? path) => CodexDiscoveryScript.IsAllowedUsagePath(path);

    public WebUsageResult ParseBody(string body)
    {
        var windows = CodexWebUsageParser.Parse(body, _now());
        return windows.Count == 0
            ? WebUsageResult.Failed
            : new WebUsageResult(WebUsageOutcome.Ok, windows, PlanType: CodexWebUsageParser.ParsePlan(body));
    }
}
