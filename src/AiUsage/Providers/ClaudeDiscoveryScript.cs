using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiUsage.Providers;

/// <summary>
/// The JavaScript run inside the widget's own hidden, signed-in WebView2 session (see
/// Web/WebViewHost.cs) to find and then read Claude's usage endpoint. Both scripts return one
/// plain object - WebView2's ExecuteScriptAsync hands that back to C# as its JSON text directly,
/// so the caller parses it with the same JsonDocument it would use for any other response.
/// </summary>
internal static class ClaudeDiscoveryScript
{
    // {0} in each candidate is the organisation uuid discovered from /api/organizations.
    internal static readonly string[] CandidatePaths =
    [
        "/api/organizations/{0}/usage",
        "/api/organizations/{0}/usage_limits",
        "/api/organizations/{0}/rate_limits",
    ];

    // Built once from CandidatePaths' own last segments, so the allow-list can never drift away
    // from the paths the app actually asks for.
    private static readonly Regex AllowedUsagePathPattern = new(
        // \A and \z, not ^ and $: $ also matches in front of a trailing newline, and this pattern is
        // the gate a cached path has to pass before it is fetched inside a signed-in session.
        @"\A/api/organizations/[0-9a-fA-F-]{36}/(" +
        string.Join('|', CandidatePaths.Select(p => Regex.Escape(p[(p.LastIndexOf('/') + 1)..]))) +
        @")\z",
        RegexOptions.Compiled);

    /// <summary>True only for one of the app's own three usage endpoints under a 36-character
    /// organisation id. A path that fails this must never reach <see cref="Fetch"/>.</summary>
    internal static bool IsAllowedUsagePath(string? path) =>
        !string.IsNullOrEmpty(path) && AllowedUsagePathPattern.IsMatch(path);

    // The tier lives on the organisation record /api/organizations already answers for discovery: its
    // rate limit tier (carries the Max 5x/20x step) and capability names ("claude_pro", ...). Only
    // those two fields are read, joined into one string; any failure leaves plan null and never
    // fails the usage read it rides along with.
    private const string PlanFromOrgSnippet = """
        function planOf(org) {
            try {
                const parts = [];
                if (org && typeof org.rate_limit_tier === 'string') parts.push(org.rate_limit_tier);
                if (org && Array.isArray(org.capabilities))
                    for (const c of org.capabilities) if (typeof c === 'string') parts.push(c);
                const text = parts.join(' ');
                return text.length > 0 && text.length <= 200 ? text : null;
            } catch (e) {
                return null;
            }
        }
        async function readPlan() {
            try {
                const res = await fetch('/api/organizations', {method: 'GET', credentials: 'include'});
                if (!res.ok) return null;
                if (!(res.headers.get('content-type') || '').includes('json')) return null;
                const orgs = await res.json();
                return Array.isArray(orgs) && orgs.length > 0 ? planOf(orgs[0]) : null;
            } catch (e) {
                return null;
            }
        }
        """;

    /// <summary>Full discovery: find the organisation, try every candidate path in order, return
    /// the first one that answers with JSON. Only needed once - <see cref="Fetch"/> covers every
    /// later tick once a path is cached in settings.</summary>
    public static string Discover()
    {
        var candidates = string.Join(",", CandidatePaths.Select(p => $"'{p}'"));
        return PlanFromOrgSnippet + $$"""
            (async () => {
                try {
                    const orgRes = await fetch('/api/organizations', {method: 'GET', credentials: 'include'});
                    if (orgRes.status === 401 || orgRes.status === 403) return {status: 'not_signed_in'};
                    if (orgRes.status === 429) return {status: 'blocked'};
                    const orgCt = orgRes.headers.get('content-type') || '';
                    if (!orgCt.includes('json')) return {status: 'blocked'};
                    const orgs = await orgRes.json();
                    if (!Array.isArray(orgs) || orgs.length === 0 || !orgs[0].uuid) return {status: 'failed'};
                    const uuid = orgs[0].uuid;
                    if (typeof uuid !== 'string' || !/^[0-9a-fA-F-]{36}$/.test(uuid)) return {status: 'failed'};
                    const candidates = [{{candidates}}].map(p => p.replace('{0}', uuid));
                    for (const path of candidates) {
                        const res = await fetch(path, {method: 'GET', credentials: 'include'});
                        if (!res.ok) continue;
                        const ct = res.headers.get('content-type') || '';
                        const text = await res.text();
                        if (ct.includes('json')) return {status: 'ok', path: path, body: text, plan: planOf(orgs[0])};
                    }
                    return {status: 'failed'};
                } catch (e) {
                    return {status: 'failed'};
                }
            })()
            """;
    }

    /// <summary>Re-fetches one already-discovered path directly - one request, not the whole
    /// organisation-then-candidates walk. Throws for any path outside <see
    /// cref="IsAllowedUsagePath"/>: the caller must never hand this an unvalidated value, because
    /// it runs inside a signed-in claude.ai session.</summary>
    public static string Fetch(string path, bool planHeld = false)
    {
        if (!IsAllowedUsagePath(path))
            throw new ArgumentException("Path is not an allowed Claude usage endpoint.", nameof(path));

        // Belt and braces: the allow-list above is the real gate, and this serializes the value as
        // a proper JSON string literal so nothing in it can close the literal early even so.
        var pathJson = JsonSerializer.Serialize(path);
        // The plan comes from a request of its own; while a recent answer is held the script leaves it out.
        var planCall = planHeld ? "null" : "await readPlan()";
        return PlanFromOrgSnippet + $$"""
            (async () => {
                try {
                    const res = await fetch({{pathJson}}, {method: 'GET', credentials: 'include'});
                    if (res.status === 401 || res.status === 403) return {status: 'not_signed_in'};
                    if (res.status === 429) return {status: 'blocked'};
                    const ct = res.headers.get('content-type') || '';
                    const text = await res.text();
                    if (!res.ok) return {status: 'failed'};
                    if (ct.includes('json')) return {status: 'ok', path: {{pathJson}}, body: text, plan: {{planCall}}};
                    return {status: 'blocked'};
                } catch (e) {
                    return {status: 'failed'};
                }
            })()
            """;
    }
}
