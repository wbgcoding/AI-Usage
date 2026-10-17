using AiUsage.Providers.Parsing;
using AiUsage.Services;

namespace AiUsage.Providers;

/// <summary>
/// Gemini's half of a web-backed read: the scripts in <see cref="GeminiDiscoveryScript"/> and the
/// usage payload of the Gemini app <see cref="GeminiAppUsageParser"/> knows. Unlike
/// <see cref="CodexUsageEndpoint"/> this needs no clock: the response's own reset instants already
/// arrive as absolute timestamps. (<see cref="GeminiUsageParser"/> stays with the Antigravity read.)
/// </summary>
public sealed class GeminiUsageEndpoint : IWebUsageEndpoint
{
    public string Discover() => GeminiDiscoveryScript.Discover();

    public string Fetch(string path) => GeminiDiscoveryScript.Fetch(path);

    public bool IsAllowedUsagePath(string? path) => GeminiDiscoveryScript.IsAllowedUsagePath(path);

    public WebUsageResult ParseBody(string body)
    {
        foreach (var note in GeminiAppUsageParser.ChangeNotes(body))
            ReadChangeLog.Report("Gemini", note);
        var windows = GeminiAppUsageParser.Parse(body);
        return windows.Count == 0 ? WebUsageResult.Failed : new WebUsageResult(WebUsageOutcome.Ok, windows);
    }
}
