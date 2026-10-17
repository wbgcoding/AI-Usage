using AiUsage.Providers.Parsing;
using AiUsage.Services;

namespace AiUsage.Providers;

/// <summary>
/// Cursor's half of a web-backed read: the scripts in <see cref="CursorDiscoveryScript"/> and the
/// response shape <see cref="CursorUsageParser"/> knows. Holds no state, so one instance per
/// provider instance costs nothing.
/// </summary>
public sealed class CursorUsageEndpoint : IWebUsageEndpoint
{
    public string Discover() => CursorDiscoveryScript.Discover();

    public string Fetch(string path) => CursorDiscoveryScript.Fetch(path);

    public string Fetch(string path, CachedAccountExtras cached) =>
        CursorDiscoveryScript.Fetch(path, cached.Email is not null, cached.GrokBotJson);

    public bool IsAllowedUsagePath(string? path) => CursorDiscoveryScript.IsAllowedUsagePath(path);

    public WebUsageResult ParseBody(string body)
    {
        foreach (var note in CursorUsageParser.ChangeNotes(body))
            ReadChangeLog.Report("Cursor", note);
        var windows = CursorUsageParser.Parse(body);
        return windows.Count == 0
            ? WebUsageResult.Failed
            : new WebUsageResult(WebUsageOutcome.Ok, windows, PlanType: CursorUsageParser.ParsePlan(body));
    }
}
