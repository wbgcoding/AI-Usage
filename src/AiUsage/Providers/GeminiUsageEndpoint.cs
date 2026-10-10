using AiUsage.Providers.Parsing;

namespace AiUsage.Providers;

/// <summary>
/// Gemini's half of a web-backed read: the scripts in <see cref="GeminiDiscoveryScript"/> and the
/// quota shape <see cref="GeminiUsageParser"/> knows. Unlike <see cref="CodexUsageEndpoint"/> this
/// needs no clock: the response's own reset instants already arrive as absolute timestamps.
/// </summary>
public sealed class GeminiUsageEndpoint : IWebUsageEndpoint
{
    public string Discover() => GeminiDiscoveryScript.Discover();

    public string Fetch(string path) => GeminiDiscoveryScript.Fetch(path);

    public bool IsAllowedUsagePath(string? path) => GeminiDiscoveryScript.IsAllowedUsagePath(path);

    public WebUsageResult ParseBody(string body)
    {
        var windows = GeminiUsageParser.Parse(body);
        return windows.Count == 0 ? WebUsageResult.Failed : new WebUsageResult(WebUsageOutcome.Ok, windows);
    }
}
