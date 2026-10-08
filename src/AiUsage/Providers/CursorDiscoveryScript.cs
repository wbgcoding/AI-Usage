using System.Text.Json;
using System.Text.RegularExpressions;
using AiUsage.Providers.Parsing;

namespace AiUsage.Providers;

/// <summary>
/// The JavaScript run inside the widget's own hidden, signed-in cursor.com session (see
/// Web/WebViewHost.cs) to find and then read Cursor's usage endpoint, plus this provider's own half
/// of <see cref="IWebUsageEndpoint"/> - the role <see cref="CodexUsageEndpoint"/> plays for Codex,
/// kept in one file here because nothing else needs the scripts on their own. <see cref="Discover"/>
/// reads the signed-in account id once, then walks a short list of candidate addresses and
/// remembers the first whose answer parses - a wrong guess here costs one request, never a wrong
/// number.
/// </summary>
public sealed class CursorDiscoveryScript : IWebUsageEndpoint
{
    // {0} is the account id read from /api/auth/me's "sub" field. The usage summary comes first: it
    // carries the plan's spend against its monthly allowance, which every current plan is billed
    // by. The request counters behind /api/usage only still carry a limit on the old request-based
    // plans, so they stay as the fallback for those.
    internal static readonly string[] CandidatePaths =
    [
        "/api/usage-summary",
        "/api/usage?user={0}",
        "/api/usage",
    ];

    private const string AccountIdPattern = "[A-Za-z0-9_|:-]{1,128}";

    // Built once from CandidatePaths' own segments, so the allow-list can never drift away from the
    // paths the app actually asks for. The one candidate carrying {0} accepts any id matching
    // AccountIdPattern there; every other candidate matches only itself.
    private static readonly Regex AllowedUsagePathPattern = new(
        // \A and \z, not ^ and $: $ also matches in front of a trailing newline, and this pattern is
        // the gate a cached path has to pass before it is fetched inside a signed-in session.
        @"\A(" + string.Join('|', CandidatePaths.Select(ToPathPattern)) + @")\z",
        RegexOptions.Compiled);

    private static string ToPathPattern(string candidate) =>
        string.Join(AccountIdPattern, candidate.Split("{0}").Select(Regex.Escape));

    /// <summary>True only for one of the app's own Cursor usage endpoints, with a well-shaped
    /// account id where the candidate carries one. A path that fails this must never reach
    /// <see cref="Fetch"/>.</summary>
    public static bool IsAllowedUsagePath(string? path) =>
        !string.IsNullOrEmpty(path) && AllowedUsagePathPattern.IsMatch(path);

    /// <summary>Full discovery: read the signed-in account id from /api/auth/me, then try every
    /// candidate path in turn, collecting every one that answers with JSON rather than stopping at
    /// the first - which one carries numbers depends on the account's plan, so
    /// <see cref="WebUsageSource.Interpret"/> is the one that picks a winner among them. Every attempt
    /// (path and status code, never a body or a cookie) comes back too, so a run against the live site
    /// says in the app's own log which address exists today - exactly how the Codex path already
    /// reports its own walk.</summary>
    // Shared by Discover and Fetch: merges the Grok Bot weekly bar onto an already-fetched usage
    // summary body. Its own path (/api/dashboard/get-sand-usage-status) is a fixed literal, never a
    // discovery candidate of its own and never added to CandidatePaths / the allow-list - that list
    // gates paths a *cached* answer gets re-fetched from later (see Fetch), and this one is always
    // called fresh, right alongside the summary read that needs it.
    // summary body. Any failure (network, non-200, non-JSON, unparsable) leaves the original body
    // untouched - the summary itself still counts even when this extra read does not answer. Copies
    // only the two fields the parser reads, never the upgrade links the endpoint also carries.
    private const string MergeGrokBotFunction = """
        const mergeGrokBot = async (text) => {
            try {
                const gRes = await fetch('/api/dashboard/get-sand-usage-status', {
                    method: 'POST', credentials: 'include',
                    headers: {'content-type': 'application/json'},
                    body: '{}',
                });
                if (gRes.status !== 200) return text;
                const gCt = gRes.headers.get('content-type') || '';
                if (!gCt.includes('json')) return text;
                const g = await gRes.json();
                const s = JSON.parse(text);
                s.grokBot = {usagePercent: g.usagePercent, nextResetTimestampUtc: g.nextResetTimestampUtc};
                return JSON.stringify(s);
            } catch (e) {
                return text;
            }
        };
        """;

    public static string Discover()
    {
        var candidates = string.Join(",", CandidatePaths.Select(p => $"'{p}'"));
        return $$"""
            (async () => {
                {{MergeGrokBotFunction}}
                const attempts = [];
                try {
                    const meRes = await fetch('/api/auth/me', {method: 'GET', credentials: 'include'});
                    if (meRes.status === 401 || meRes.status === 403) return {status: 'not_signed_in', attempts: attempts};
                    if (meRes.status === 429) return {status: 'blocked', attempts: attempts};
                    const meCt = meRes.headers.get('content-type') || '';
                    if (!meCt.includes('json')) return {status: 'blocked', attempts: attempts};
                    const me = await meRes.json();
                    const sub = me && me.sub;
                    // An account endpoint that answers without an account id is a session that is not
                    // signed in, not a broken read: the signed-out site answers this route with an
                    // empty body rather than a 401. Reporting it as a plain failure is what left the
                    // tile saying "failed" with no way to sign in.
                    if (typeof sub !== 'string' || !/^{{AccountIdPattern}}$/.test(sub)) return {status: 'not_signed_in', attempts: attempts};
                    const email = (me && typeof me.email === 'string') ? me.email : null;
                    let sawAuthFailure = false;
                    const results = [];
                    const paths = [{{candidates}}].map(p => p.replace('{0}', sub));
                    for (const path of paths) {
                        const res = await fetch(path, {method: 'GET', credentials: 'include'});
                        attempts.push(path + ' ' + res.status);
                        if (res.status === 401 || res.status === 403) { sawAuthFailure = true; continue; }
                        if (res.status === 429) return {status: 'blocked', attempts: attempts};
                        if (!res.ok) continue;
                        const ct = res.headers.get('content-type') || '';
                        if (ct.includes('json')) {
                            let body = await res.text();
                            if (path === '/api/usage-summary') body = await mergeGrokBot(body);
                            results.push({path: path, body: body});
                        }
                    }
                    if (results.length > 0) return {status: 'ok', results: results, attempts: attempts, email: email};
                    if (sawAuthFailure) return {status: 'not_signed_in', attempts: attempts};
                    return {status: 'failed', attempts: attempts};
                } catch (e) {
                    return {status: 'failed', attempts: attempts};
                }
            })()
            """;
    }

    /// <summary>Re-fetches one already-discovered path directly - one request, not the account-id
    /// read plus the whole candidate walk. Throws for any path outside <see
    /// cref="IsAllowedUsagePath"/>: the caller must never hand this an unvalidated value, because it
    /// runs inside a signed-in cursor.com session.</summary>
    public static string Fetch(string path)
    {
        if (!IsAllowedUsagePath(path))
            throw new ArgumentException("Path is not an allowed Cursor usage endpoint.", nameof(path));

        // Belt and braces: the allow-list above is the real gate, and this serializes the value as a
        // proper JSON string literal so nothing in it can close the literal early even so.
        var pathJson = JsonSerializer.Serialize(path);
        // path is fixed at this point (already validated above), so whether to merge the Grok Bot
        // read is decided here rather than by a runtime check in the generated script.
        var mergeCall = path == "/api/usage-summary" ? "await mergeGrokBot(text)" : "text";
        return $$"""
            (async () => {
                {{MergeGrokBotFunction}}
                // Cheap, best-effort account read alongside the real fetch below: any failure here
                // simply leaves email null rather than failing the usage read it rides along with.
                const readEmail = async () => {
                    try {
                        const meRes = await fetch('/api/auth/me', {method: 'GET', credentials: 'include'});
                        if (!meRes.ok) return null;
                        const meCt = meRes.headers.get('content-type') || '';
                        if (!meCt.includes('json')) return null;
                        const me = await meRes.json();
                        return (me && typeof me.email === 'string') ? me.email : null;
                    } catch (e) {
                        return null;
                    }
                };
                try {
                    const res = await fetch({{pathJson}}, {method: 'GET', credentials: 'include'});
                    if (res.status === 401 || res.status === 403) return {status: 'not_signed_in'};
                    if (res.status === 429) return {status: 'blocked'};
                    if (!res.ok) return {status: 'failed'};
                    const ct = res.headers.get('content-type') || '';
                    const text = await res.text();
                    if (!ct.includes('json')) return {status: 'blocked'};
                    const email = await readEmail();
                    return {status: 'ok', path: {{pathJson}}, body: {{mergeCall}}, email: email};
                } catch (e) {
                    return {status: 'failed'};
                }
            })()
            """;
    }

    string IWebUsageEndpoint.Discover() => Discover();

    string IWebUsageEndpoint.Fetch(string path) => Fetch(path);

    bool IWebUsageEndpoint.IsAllowedUsagePath(string? path) => IsAllowedUsagePath(path);

    public WebUsageResult ParseBody(string body)
    {
        var windows = CursorUsageParser.Parse(body);
        return windows.Count == 0
            ? WebUsageResult.Failed
            : new WebUsageResult(WebUsageOutcome.Ok, windows, PlanType: CursorUsageParser.ParsePlan(body));
    }
}
