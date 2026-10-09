using System.Text.Json;
using System.Net.Http;
using AiUsage.Models;
using AiUsage.Providers.Parsing;

namespace AiUsage.Providers.LocalLogin;

/// <summary>Exactly why a Claude Code local-login read did not come back with usable windows, set at
/// one of <see cref="ClaudeCodeLogin.FetchAsync(CancellationToken)"/>'s four failure exits - never
/// guessed, never carrying a token or a path. <see cref="Ok"/> is the fifth, successful case.</summary>
internal enum ClaudeCodeLoginReason
{
    Ok,
    MissingFile,
    NoToken,
    TokenExpired,
    NonOkResponse,
}

/// <summary>The result of a Claude Code usage read: the windows to show when signed in, otherwise
/// just the outcome and why.</summary>
internal sealed record ClaudeCodeUsage(
    LocalLoginOutcome Outcome, IReadOnlyList<UsageWindow> Windows, ClaudeCodeLoginReason Reason = ClaudeCodeLoginReason.Ok,
    string? PlanType = null, FailureKind FailureKind = FailureKind.Other, int? HttpStatus = null)
{
    public static readonly ClaudeCodeUsage NotSignedIn = new(LocalLoginOutcome.NotSignedIn, [], ClaudeCodeLoginReason.MissingFile);
    public static readonly ClaudeCodeUsage Failed = new(LocalLoginOutcome.Failed, [], ClaudeCodeLoginReason.NonOkResponse);
}

/// <summary>
/// Reads Claude usage through the sign-in Claude Code already holds on this machine: the OAuth token
/// in <c>~/.claude/.credentials.json</c>, used only in memory to GET the usage endpoint. The widget
/// never refreshes or rewrites that token - rotating it would sign Claude Code itself out - so an
/// expired or missing token simply reads as "not signed in", and the user signs in again through
/// Claude Code. Reads usage numbers only; never a chat or message endpoint. Never throws.
/// </summary>
internal static class ClaudeCodeLogin
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";

    // Claude Code's own CLI always sends a "claude-code/<version>" User-Agent on this call; a request
    // with none of it lands in a separate, far more aggressively rate-limited bucket on the server
    // side (observed elsewhere as a persistent 429 on this exact endpoint). This local-only read
    // mimics that shape for the same reason AntigravityLocalLogin sends its own tool's User-Agent
    // rather than none.
    private const string UserAgent = "claude-code/1.0.0";

    private const string CredentialsFileName = ".credentials.json";

    private static string CredentialsPath() => ClaudeConfigRoot.FilePath(ClaudeConfigRoot.Candidates(), CredentialsFileName);

    /// <summary>Same as the default, from a given variable value and profile folder.</summary>
    internal static string CredentialsPathFor(string? configDirValue, string userProfile) =>
        ClaudeConfigRoot.FilePath(ClaudeConfigRoot.Candidates(configDirValue, userProfile), CredentialsFileName);

    public static Task<ClaudeCodeUsage> FetchAsync(CancellationToken ct) => FetchAsync(CredentialsPath(), ct);

    /// <summary>Test seam: a fixture credentials path instead of the real profile folder - everything
    /// past that point (the HTTP call, the parse) is identical to the production path.</summary>
    internal static async Task<ClaudeCodeUsage> FetchAsync(string credentialsPath, CancellationToken ct)
    {
        var (token, tokenReason, plan) = ReadAccessToken(credentialsPath);
        if (token is null)
            return new ClaudeCodeUsage(LocalLoginOutcome.NotSignedIn, [], tokenReason);

        var response = await LocalLoginHttp.SendAsync(
            HttpMethod.Get, UsageUrl, token, jsonBody: null, UserAgent, ct,
            extraHeaders: [new KeyValuePair<string, string>("anthropic-beta", BetaHeader)]);

        if (DeniedUsage(response.StatusCode) is { } denied)
            return denied;
        if (!response.Ok)
            return new ClaudeCodeUsage(
                LocalLoginOutcome.Failed, [], ClaudeCodeLoginReason.NonOkResponse,
                FailureKind: response.FailureKind, HttpStatus: response.StatusCode > 0 ? response.StatusCode : null);

        var parsed = ClaudeUsageParser.Parse(response.Body);
        return parsed.Outcome == ClaudeUsageOutcome.Ok
            ? new ClaudeCodeUsage(LocalLoginOutcome.Ok, parsed.Windows, ClaudeCodeLoginReason.Ok, plan)
            : new ClaudeCodeUsage(LocalLoginOutcome.Failed, [], ClaudeCodeLoginReason.NonOkResponse);
    }

    /// <summary>The answer for a refused request, null for any other status. A 401 on a token the file
    /// still calls valid means Claude Code rotated it since: it reads like an expired one, so the last
    /// numbers are held over until Claude Code renews the file. A 403 stays a plain refusal.</summary>
    internal static ClaudeCodeUsage? DeniedUsage(int statusCode) => statusCode switch
    {
        401 => new ClaudeCodeUsage(LocalLoginOutcome.NotSignedIn, [], ClaudeCodeLoginReason.TokenExpired),
        403 => new ClaudeCodeUsage(LocalLoginOutcome.NotSignedIn, [], ClaudeCodeLoginReason.NonOkResponse),
        _ => null,
    };

    /// <summary>A token this close to its expiry counts as expired: the request would arrive after it.</summary>
    internal static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    /// <summary>The current OAuth access token, or null (with the reason why) when there is none or
    /// it has already expired (the widget will not refresh it - that is Claude Code's own job).</summary>
    internal static (string? Token, ClaudeCodeLoginReason Reason, string? Plan) ReadAccessToken(string path)
    {
        if (!File.Exists(path))
            return (null, ClaudeCodeLoginReason.MissingFile, null);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, ClaudeCodeLoginReason.NoToken, null);
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
                return (null, ClaudeCodeLoginReason.NoToken, null);

            var accessToken = oauth.TryGetProperty("accessToken", out var accessEl) && accessEl.ValueKind == JsonValueKind.String
                ? accessEl.GetString()
                : null;
            if (string.IsNullOrEmpty(accessToken))
                return (null, ClaudeCodeLoginReason.NoToken, null);

            var plan = ReadPlan(oauth);

            // expiresAt is Unix milliseconds; a token already past it will only 401, so skip the call.
            if (oauth.TryGetProperty("expiresAt", out var expiresEl) && expiresEl.ValueKind == JsonValueKind.Number
                && expiresEl.TryGetInt64(out var expiresAtMs)
                && DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs) <= DateTimeOffset.UtcNow + ExpiryMargin)
                return (null, ClaudeCodeLoginReason.TokenExpired, plan);

            return (accessToken, ClaudeCodeLoginReason.Ok, plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
        {
            return (null, ClaudeCodeLoginReason.NoToken, null);
        }
    }

    /// <summary>The plan wording the sign-in already holds: <c>subscriptionType</c> (such as "max" or
    /// "pro") plus <c>rateLimitTier</c> (which carries the 5x/20x step of a Max plan), joined into one
    /// text for <see cref="Services.PlanTier"/>. Only these two string fields are read; the token next
    /// to them is never copied anywhere but the request header.</summary>
    private static string? ReadPlan(JsonElement oauth)
    {
        static string? Text(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 and <= 100 } value
                ? value
                : null;

        var parts = new[] { Text(oauth, "subscriptionType"), Text(oauth, "rateLimitTier") }.Where(part => part is not null);
        var plan = string.Join(' ', parts);
        return plan.Length > 0 ? plan : null;
    }
}
