using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiUsage.Providers;

/// <summary>
/// The JavaScript run inside the widget's own hidden, signed-in gemini.google.com session (see
/// Web/WebViewHost.cs) to read the Gemini app's usage. Same two-script shape as
/// <see cref="CodexDiscoveryScript"/>: one walk over a list of candidates (here a single one), then a
/// direct re-read of whichever one answered. The one request is the usage call of gemini.google.com/usage,
/// a batchexecute RPC that only answers on POST - a plain read of the account's own usage data, with a
/// fixed RPC id and a fixed request literal. Nothing in the request is built from page data except the
/// page's own anti-forgery token, build label and language. This file must never name a chat or
/// completion path, which <c>TokenSafetyTests</c> enforces.
/// </summary>
internal static class GeminiDiscoveryScript
{
    // The one address the usage page itself calls. The walk keeps the candidate shape of the other web
    // reads so a second address could be added here; an address that stops answering is dropped again
    // on the next tick (see WebUsageSource), so a stale one costs one request, never a wrong number.
    internal static readonly string[] CandidatePaths =
    [
        "/_/BardChatUi/data/batchexecute?rpcids=jSf9Qc",
    ];

    // Built once from CandidatePaths itself, so the allow-list can never drift away from the paths
    // the app actually asks for.
    private static readonly Regex AllowedUsagePathPattern = new(
        // \z, not $: $ also matches in front of a trailing newline, and this pattern is the gate a
        // cached path has to pass before it is fetched inside a signed-in session.
        @"\A(" + string.Join('|', CandidatePaths.Select(Regex.Escape)) + @")\z",
        RegexOptions.Compiled);

    /// <summary>True only for one of the app's own Gemini usage endpoints. A path that fails this
    /// must never reach <see cref="Fetch"/>.</summary>
    internal static bool IsAllowedUsagePath(string? path) =>
        !string.IsNullOrEmpty(path) && AllowedUsagePathPattern.IsMatch(path);

    /// <summary>A thrown fetch is either the redirect of a signed-out session to the Google sign-in page
    /// or a plain network failure. Only a site whose own front page still answers (a redirect counts as
    /// an answer) makes it the former; offline, or no answer at all, is a failure. The probe never
    /// follows a redirect and sends no body.</summary>
    private const string ReachableFunction = """
        const reachable = async () => {
            if (navigator.onLine === false) return false;
            try {
                await fetch(location.origin + '/', {method: 'GET', credentials: 'include', redirect: 'manual'});
                return true;
            } catch (e) {
                return false;
            }
        };
        """;

    /// <summary>One read of the usage RPC, shared by both scripts. The page carries its anti-forgery
    /// token ("at") and build label ("bl") in <c>window.WIZ_global_data</c>; a signed-out page has no
    /// token, which is the sign-in answer without a request. The RPC answers only on POST, with the
    /// fixed request literal below and the token as the only page value in the body; the answer is a
    /// batch envelope whose <c>jSf9Qc</c> entry carries the usage payload as a JSON string. Resolves to
    /// <c>{status, code, body}</c>: <c>code</c> is the HTTP status (000 when none was received), and
    /// <c>body</c> the payload string when <c>status</c> is 'ok'.</summary>
    private const string ReadFunction = """
        const pageValues = async () => {
            const data = window.WIZ_global_data || {};
            if (data.SNlM0e) return {at: data.SNlM0e, bl: data.cfb2h || ''};
            // A page that no longer hands its values to the window still serves them in its own HTML:
            // read this same page once more, on this origin only, and take them from there.
            try {
                const res = await fetch(location.origin + location.pathname, {method: 'GET', credentials: 'include'});
                if (!res.ok || new URL(res.url).origin !== location.origin) return null;
                const html = await res.text();
                const at = (html.match(/"SNlM0e":"([^"]+)"/) || [])[1];
                const bl = (html.match(/"cfb2h":"([^"]+)"/) || [])[1];
                return at ? {at: at, bl: bl || ''} : null;
            } catch (e) {
                return null;
            }
        };
        const readUsage = async (path) => {
            const values = await pageValues();
            if (!values) return {status: 'not_signed_in', code: '000', reason: 'no_page_token'};
            const url = path + '&source-path=%2Fusage&bl=' + encodeURIComponent(values.bl)
                + '&hl=' + encodeURIComponent(document.documentElement.lang || 'en') + '&rt=c';
            const form = new URLSearchParams({'f.req': '[[["jSf9Qc","[]",null,"generic"]]]', 'at': values.at});
            let res;
            try {
                res = await fetch(url, {method: 'POST', credentials: 'include',
                    // The usage RPC /_/BardChatUi/data/batchexecute?rpcids=jSf9Qc only answers on POST.
                    headers: {'content-type': 'application/x-www-form-urlencoded;charset=UTF-8'},
                    body: form.toString()});
            } catch (e) {
                // A signed-out session is redirected to the Google sign-in page, which the browser
                // refuses to hand to this page: the fetch throws instead of answering. A network
                // failure throws the same way and is not a sign-in problem.
                return {status: await reachable() ? 'not_signed_in' : 'failed', code: '000', reason: 'fetch_refused'};
            }
            const code = String(res.status);
            try {
                if (res.type === 'opaqueredirect' || (res.redirected && new URL(res.url).origin !== location.origin)) return {status: 'not_signed_in', code: code, reason: 'redirect'};
                if (res.status === 401 || res.status === 403) return {status: 'not_signed_in', code: code, reason: 'http_' + code};
                if (res.status === 429) return {status: 'blocked', code: code};
                if (!res.ok) return {status: 'failed', code: code, reason: 'http_' + code};
                const text = (await res.text()).replace(/^\)\]\}'/, '');
                for (const line of text.split('\n')) {
                    const trimmed = line.trim();
                    if (!trimmed.startsWith('[')) continue;
                    let parsed;
                    try { parsed = JSON.parse(trimmed); } catch (e) { continue; }
                    if (!Array.isArray(parsed)) continue;
                    for (const e of parsed) {
                        if (Array.isArray(e) && e[0] === 'wrb.fr' && e[1] === 'jSf9Qc' && typeof e[2] === 'string') return {status: 'ok', code: code, body: e[2]};
                    }
                }
                return {status: 'failed', code: code, reason: 'no_usage_entry'};
            } catch (e) {
                return {status: 'failed', code: code, reason: 'unreadable'};
            }
        };
        """;

    /// <summary>Full discovery: try every candidate path in order and return every one that answers
    /// with a usage payload (the reader takes the first that parses). The answer also carries what each
    /// candidate replied with (path and status code only, never a body, a cookie or a token), so a run
    /// against the live site says in the app's own log what the address answered instead of leaving that
    /// to guesswork.</summary>
    public static string Discover()
    {
        var candidates = string.Join(",", CandidatePaths.Select(p => $"'{p}'"));
        return $$"""
            (async () => {
                {{ReachableFunction}}
                {{ReadFunction}}
                const attempts = [];
                try {
                    let sawAuthFailure = false;
                    let reason = null;
                    const results = [];
                    for (const path of [{{candidates}}]) {
                        const r = await readUsage(path);
                        attempts.push(path + ' ' + r.code);
                        if (r.status === 'blocked') return {status: 'blocked', attempts: attempts};
                        if (r.status === 'not_signed_in') { sawAuthFailure = true; reason = r.reason || reason; continue; }
                        if (r.status === 'ok') results.push({path: path, body: r.body});
                    }
                    if (results.length > 0) return {status: 'ok', results: results, attempts: attempts};
                    if (sawAuthFailure) return {status: 'not_signed_in', reason: reason, attempts: attempts};
                    return {status: 'failed', attempts: attempts};
                } catch (e) {
                    return {status: 'failed', attempts: attempts};
                }
            })()
            """;
    }

    /// <summary>Re-fetches one already-discovered path directly. Throws for any path outside
    /// <see cref="IsAllowedUsagePath"/>: the caller must never hand this an unvalidated value,
    /// because it runs inside a signed-in gemini.google.com session.</summary>
    public static string Fetch(string path)
    {
        if (!IsAllowedUsagePath(path))
            throw new ArgumentException("Path is not an allowed Gemini usage endpoint.", nameof(path));

        // Belt and braces: the allow-list above is the real gate, and this serializes the value as a
        // proper JSON string literal so nothing in it can close the literal early even so.
        var pathJson = JsonSerializer.Serialize(path);
        return $$"""
            (async () => {
                {{ReachableFunction}}
                {{ReadFunction}}
                try {
                    const r = await readUsage({{pathJson}});
                    if (r.status === 'ok') return {status: 'ok', path: {{pathJson}}, body: r.body};
                    return {status: r.status, reason: r.reason};
                } catch (e) {
                    return {status: 'failed'};
                }
            })()
            """;
    }
}
