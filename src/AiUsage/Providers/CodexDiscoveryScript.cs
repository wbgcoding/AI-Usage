using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiUsage.Providers;

/// <summary>
/// The JavaScript run inside the widget's own hidden, signed-in ChatGPT session (see
/// Web/WebViewHost.cs) to find and then read Codex's usage endpoint. Same two-script shape as
/// <see cref="ClaudeDiscoveryScript"/>: one walk over a list of candidates, then a direct re-read of
/// whichever one answered. Every request is a plain GET against the account's own usage data - this
/// file must never name a chat or completion path, which <c>TokenSafetyTests</c> enforces.
/// </summary>
internal static class CodexDiscoveryScript
{
    // Which address actually answers is not documented anywhere the app can read, so the walk tries
    // the plausible ones in order and keeps the first that returns JSON - exactly how the Claude path
    // works. A candidate that no longer answers is dropped again on the next tick (see
    // WebUsageSource), so a wrong guess here costs one request, never a wrong number.
    internal static readonly string[] CandidatePaths =
    [
        "/backend-api/codex/usage",
        "/backend-api/codex/rate_limits",
        "/backend-api/wham/usage",
        "/backend-api/usage",
    ];

    // Built once from CandidatePaths itself, so the allow-list can never drift away from the paths
    // the app actually asks for.
    private static readonly Regex AllowedUsagePathPattern = new(
        // \z, not $: $ also matches in front of a trailing newline, and this pattern is the gate a
        // cached path has to pass before it is fetched inside a signed-in session.
        @"\A(" + string.Join('|', CandidatePaths.Select(Regex.Escape)) + @")\z",
        RegexOptions.Compiled);

    /// <summary>True only for one of the app's own Codex usage endpoints. A path that fails this
    /// must never reach <see cref="Fetch"/>.</summary>
    internal static bool IsAllowedUsagePath(string? path) =>
        !string.IsNullOrEmpty(path) && AllowedUsagePathPattern.IsMatch(path);

    /// <summary>The snippet both scripts start with: the site's own web app authenticates its
    /// backend calls with a short-lived key it reads from its session route, and that route is also
    /// the only honest answer to "is this session signed in at all" - cookies alone answer every
    /// usage address with 401, which is what left the tile saying "failed" with the account clearly
    /// signed in. The key goes on these requests and nowhere else: never stored, never logged, never
    /// part of an answer this script returns. The session route itself carries no usage data and no
    /// message content, so reading it can never cost the account anything.</summary>
    private const string AuthHeadersSnippet = """
        async function authHeaders() {
            try {
                const res = await fetch('/api/auth/session', {method: 'GET', credentials: 'include'});
                if (res.status === 401 || res.status === 403) return {signedOut: true, headers: {}};
                if (!res.ok) return {signedOut: false, headers: {}};
                if (!(res.headers.get('content-type') || '').includes('json')) return {signedOut: false, headers: {}};
                const session = await res.json();
                if (session && typeof session.accessToken === 'string' && session.accessToken.length > 0)
                    return {signedOut: false, headers: {Authorization: 'Bearer ' + session.accessToken}};
                return {signedOut: !session || !session.user, headers: {}};
            } catch (e) {
                return {signedOut: false, headers: {}};
            }
        }
        """;

    // Cheap, best-effort account read alongside the real usage fetch: this account endpoint carries
    // no usage data at all, only the signed-in user's own profile, and any failure here simply leaves
    // email null rather than failing the usage read it rides along with.
    private const string ReadEmailSnippet = """
        async function readEmail(headers) {
            try {
                const res = await fetch('/backend-api/me', {method: 'GET', credentials: 'include', headers: headers});
                if (!res.ok) return null;
                const ct = res.headers.get('content-type') || '';
                if (!ct.includes('json')) return null;
                const me = await res.json();
                return (me && typeof me.email === 'string') ? me.email : null;
            } catch (e) {
                return null;
            }
        }
        """;

    /// <summary>Full discovery: try every candidate path in order, return the first one that answers
    /// with JSON. The answer also carries what each candidate replied with (path and status code
    /// only, never a body, a cookie or a token), so a run against the live site says in the app's own
    /// log which address exists today instead of leaving that to guesswork.</summary>
    public static string Discover()
    {
        var candidates = string.Join(",", CandidatePaths.Select(p => $"'{p}'"));
        return AuthHeadersSnippet + ReadEmailSnippet + $$"""
            (async () => {
                const attempts = [];
                try {
                    const auth = await authHeaders();
                    if (auth.signedOut) return {status: 'not_signed_in', attempts: attempts};
                    let sawAuthFailure = false;
                    for (const path of [{{candidates}}]) {
                        const res = await fetch(path, {method: 'GET', credentials: 'include', headers: auth.headers});
                        attempts.push(path + ' ' + res.status);
                        if (res.status === 401 || res.status === 403) { sawAuthFailure = true; continue; }
                        if (res.status === 429) return {status: 'blocked', attempts: attempts};
                        if (!res.ok) continue;
                        const ct = res.headers.get('content-type') || '';
                        const text = await res.text();
                        if (ct.includes('json')) {
                            const email = await readEmail(auth.headers);
                            return {status: 'ok', path: path, body: text, attempts: attempts, email: email};
                        }
                    }
                    if (sawAuthFailure) return {status: 'not_signed_in', attempts: attempts};
                    return {status: 'failed', attempts: attempts};
                } catch (e) {
                    return {status: 'failed', attempts: attempts};
                }
            })()
            """;
    }

    /// <summary>Re-fetches one already-discovered path directly. Throws for any path outside
    /// <see cref="IsAllowedUsagePath"/>: the caller must never hand this an unvalidated value,
    /// because it runs inside a signed-in chatgpt.com session.</summary>
    public static string Fetch(string path)
    {
        if (!IsAllowedUsagePath(path))
            throw new ArgumentException("Path is not an allowed Codex usage endpoint.", nameof(path));

        // Belt and braces: the allow-list above is the real gate, and this serializes the value as a
        // proper JSON string literal so nothing in it can close the literal early even so.
        var pathJson = JsonSerializer.Serialize(path);
        return AuthHeadersSnippet + ReadEmailSnippet + $$"""
            (async () => {
                try {
                    const auth = await authHeaders();
                    if (auth.signedOut) return {status: 'not_signed_in'};
                    const res = await fetch({{pathJson}}, {method: 'GET', credentials: 'include', headers: auth.headers});
                    if (res.status === 401 || res.status === 403) return {status: 'not_signed_in'};
                    if (res.status === 429) return {status: 'blocked'};
                    if (!res.ok) return {status: 'failed'};
                    const ct = res.headers.get('content-type') || '';
                    const text = await res.text();
                    if (!ct.includes('json')) return {status: 'blocked'};
                    const email = await readEmail(auth.headers);
                    return {status: 'ok', path: {{pathJson}}, body: text, email: email};
                } catch (e) {
                    return {status: 'failed'};
                }
            })()
            """;
    }
}
