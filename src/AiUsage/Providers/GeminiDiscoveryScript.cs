using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiUsage.Providers;

/// <summary>
/// The JavaScript run inside the widget's own hidden, signed-in aistudio.google.com session (see
/// Web/WebViewHost.cs) to find and then read Gemini's usage endpoint. Same two-script shape as
/// <see cref="CodexDiscoveryScript"/>: one walk over a list of candidates, then a direct re-read of
/// whichever one answered. Every request is a plain GET against the account's own usage data - this
/// file must never name a chat or completion path, which <c>TokenSafetyTests</c> enforces.
/// </summary>
internal static class GeminiDiscoveryScript
{
    // Which address actually answers is not documented anywhere the app can read, so the walk tries
    // the plausible ones in order and keeps the first that returns JSON - exactly how the Codex and
    // Cursor paths work. A candidate that no longer answers is dropped again on the next tick (see
    // WebUsageSource), so a wrong guess here costs one request, never a wrong number.
    internal static readonly string[] CandidatePaths =
    [
        "/api/usage",
        "/api/quota",
        "/api/user/usage",
        "/api/billing/usage",
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

    /// <summary>Full discovery: try every candidate path in order, return the first one that answers
    /// with JSON. The answer also carries what each candidate replied with (path and status code
    /// only, never a body, a cookie or a token), so a run against the live site says in the app's own
    /// log which address exists today instead of leaving that to guesswork.</summary>
    public static string Discover()
    {
        var candidates = string.Join(",", CandidatePaths.Select(p => $"'{p}'"));
        return $$"""
            (async () => {
                const attempts = [];
                try {
                    let sawAuthFailure = false;
                    for (const path of [{{candidates}}]) {
                        const res = await fetch(path, {method: 'GET', credentials: 'include'});
                        attempts.push(path + ' ' + res.status);
                        if (res.status === 401 || res.status === 403) { sawAuthFailure = true; continue; }
                        if (res.status === 429) return {status: 'blocked', attempts: attempts};
                        if (!res.ok) continue;
                        const ct = res.headers.get('content-type') || '';
                        const text = await res.text();
                        if (ct.includes('json')) return {status: 'ok', path: path, body: text, attempts: attempts};
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
    /// because it runs inside a signed-in aistudio.google.com session.</summary>
    public static string Fetch(string path)
    {
        if (!IsAllowedUsagePath(path))
            throw new ArgumentException("Path is not an allowed Gemini usage endpoint.", nameof(path));

        // Belt and braces: the allow-list above is the real gate, and this serializes the value as a
        // proper JSON string literal so nothing in it can close the literal early even so.
        var pathJson = JsonSerializer.Serialize(path);
        return $$"""
            (async () => {
                try {
                    const res = await fetch({{pathJson}}, {method: 'GET', credentials: 'include'});
                    if (res.status === 401 || res.status === 403) return {status: 'not_signed_in'};
                    if (res.status === 429) return {status: 'blocked'};
                    if (!res.ok) return {status: 'failed'};
                    const ct = res.headers.get('content-type') || '';
                    const text = await res.text();
                    if (!ct.includes('json')) return {status: 'blocked'};
                    return {status: 'ok', path: {{pathJson}}, body: text};
                } catch (e) {
                    return {status: 'failed'};
                }
            })()
            """;
    }
}
