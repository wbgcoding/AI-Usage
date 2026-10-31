using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiUsage.Providers;

/// <summary>
/// The JavaScript run inside a hidden, signed-in session (claude.ai's or the Claude Console's) to read
/// the console's API credit balance: first the organisation list, then the one prepaid credit address
/// of the organisation C# picked. Both return one plain object, parsed in C#
/// (<see cref="Parsing.ClaudeConsoleCreditParser"/>). Billing numbers only; no other address is ever asked.
/// </summary>
internal static class ClaudeConsoleCreditScript
{
    // The console's own pages name their client on every API call; a call without it can be refused.
    private const string ClientHeaders =
        "{'anthropic-client-platform': location.hostname === 'platform.claude.com' ? 'web_console' : 'web_claude_ai'}";

    // \A and \z, not ^ and $: $ also matches in front of a trailing newline, and this pattern is the
    // gate a picked path has to pass before it is fetched inside a signed-in session.
    private static readonly Regex AllowedCreditPathPattern = new(
        @"\A/api/organizations/[0-9a-fA-F-]{36}/prepaid/credits\z", RegexOptions.Compiled);

    /// <summary>The fixed organisation-list address.</summary>
    internal const string OrganizationsPath = "/api/organizations";

    internal static bool IsAllowedCreditPath(string? path) =>
        !string.IsNullOrEmpty(path) && AllowedCreditPathPattern.IsMatch(path);

    internal static string CreditPath(string organizationId) => $"/api/organizations/{organizationId}/prepaid/credits";

    /// <summary>Asks the organisation list and reads the console's "last active organisation" cookie
    /// when the page exposes it. Also the sign-in window's session check: "ok" means signed in.</summary>
    internal static string ListOrganizations() => $$"""
        (async () => {
            try {
                const res = await fetch('/api/organizations', {method: 'GET', credentials: 'include', headers: {{ClientHeaders}}});
                if (res.status === 401 || res.status === 403) return {status: 'not_signed_in', code: String(res.status)};
                if (res.status === 429) return {status: 'blocked', code: '429'};
                if (!res.ok) return {status: 'failed', code: String(res.status)};
                if (!(res.headers.get('content-type') || '').includes('json')) return {status: 'blocked', code: 'html'};
                const body = await res.text();
                let lastActiveOrg = null;
                try {
                    const match = document.cookie.match(/(?:^|;\s*)lastActiveOrg=([^;]*)/);
                    if (match && /^[0-9a-fA-F-]{36}$/.test(match[1])) lastActiveOrg = match[1];
                } catch (e) {
                }
                return {status: 'ok', body: body, lastActiveOrg: lastActiveOrg};
            } catch (e) {
                return {status: 'failed', code: 'network'};
            }
        })()
        """;

    /// <summary>Reads the credit balance at <paramref name="path"/>. Throws for any path outside <see
    /// cref="IsAllowedCreditPath"/>: it runs inside a signed-in session, so the caller must never hand
    /// it an unvalidated value. A refusal here is not a sign-out: the list call before it already
    /// proved the session, so a 401 or 403 means this organisation has no balance to show.</summary>
    internal static string FetchCredits(string path)
    {
        if (!IsAllowedCreditPath(path))
            throw new ArgumentException("Path is not the Claude Console credit endpoint.", nameof(path));

        // The allow-list above is the real gate; this makes the value a proper JSON string literal too.
        var pathJson = JsonSerializer.Serialize(path);
        return $$"""
            (async () => {
                try {
                    const res = await fetch({{pathJson}}, {method: 'GET', credentials: 'include', headers: {{ClientHeaders}}});
                    if (res.status === 429) return {status: 'blocked', code: '429'};
                    if (!res.ok) return {status: 'failed', code: String(res.status)};
                    if (!(res.headers.get('content-type') || '').includes('json')) return {status: 'blocked', code: 'html'};
                    return {status: 'ok', body: await res.text()};
                } catch (e) {
                    return {status: 'failed', code: 'network'};
                }
            })()
            """;
    }
}
