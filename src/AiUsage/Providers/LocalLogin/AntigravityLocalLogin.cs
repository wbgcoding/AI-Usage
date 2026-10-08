using System.Text.Json;
using System.Net.Http;
using AiUsage.Providers.Parsing;

namespace AiUsage.Providers.LocalLogin;

/// <summary>How a local-login fetch ended, independent of any one provider's response shape.</summary>
internal enum LocalLoginOutcome
{
    Ok,
    NotSignedIn,
    Failed,
}

/// <summary>The raw result of an Antigravity usage read: the quota-summary JSON to be parsed and the
/// plan-tier name to show, both empty/null unless <see cref="Outcome"/> is
/// <see cref="LocalLoginOutcome.Ok"/>.</summary>
internal sealed record AntigravityUsage(LocalLoginOutcome Outcome, string QuotaSummaryJson, string? PlanName)
{
    public static readonly AntigravityUsage NotSignedIn = new(LocalLoginOutcome.NotSignedIn, "", null);
    public static readonly AntigravityUsage Failed = new(LocalLoginOutcome.Failed, "", null);
}

/// <summary>
/// Reads Antigravity's usage numbers through the sign-in the Antigravity CLI already holds on this
/// machine: its OAuth token from the Windows Credential Manager, refreshed against Google's token
/// endpoint with the CLI's own client credentials only when it has actually expired. Never writes
/// the token back, never logs it, and calls only the two read endpoints on
/// <see cref="LocalLoginHttp"/>'s allow-list. The token lives in memory for the fetch and a short
/// in-process cache of the refreshed value, nothing more.
/// </summary>
internal static class AntigravityLocalLogin
{
    private const string CredentialTarget = "gemini:antigravity";
    private const string UserAgent = "antigravity/1.0.0 windows/amd64";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string LoadUrl = "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string QuotaUrl = "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";

    // Refreshed access tokens are valid ~1 hour; cache the one we minted so a fetch every few minutes
    // does not re-run the OAuth round trip (or the binary scan behind it) each time.
    private static readonly object Gate = new();
    private static string? _cachedRefreshToken;
    private static string? _cachedAccessToken;
    private static DateTimeOffset _cachedExpiry;

    public static async Task<AntigravityUsage> FetchAsync(CancellationToken ct)
    {
        var credentialJson = WindowsCredential.ReadUtf8(CredentialTarget);
        if (credentialJson is null)
            return AntigravityUsage.NotSignedIn;

        string? refreshToken;
        string? storedAccess;
        DateTimeOffset storedExpiry;
        try
        {
            using var document = JsonDocument.Parse(credentialJson);
            var token = document.RootElement.GetProperty("token");
            refreshToken = token.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            storedAccess = token.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            storedExpiry = token.TryGetProperty("expiry", out var ex) && ex.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ex.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return AntigravityUsage.NotSignedIn;
        }

        var (accessToken, rejected) = await ResolveAccessTokenAsync(refreshToken, storedAccess, storedExpiry, ct);
        if (accessToken is null)
        {
            if (rejected)
                return ForgetCachedTokenAndReportSignedOut();
            return refreshToken is null ? AntigravityUsage.NotSignedIn : AntigravityUsage.Failed;
        }

        var load = await LocalLoginHttp.SendAsync(
            HttpMethod.Post, LoadUrl, accessToken,
            "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}",
            UserAgent, ct);
        if (load.StatusCode is 401 or 403)
            return ForgetCachedTokenAndReportSignedOut();
        if (!load.Ok)
            return AntigravityUsage.Failed;

        var (project, planName) = ReadProjectAndPlan(load.Body);
        var quotaBody = project is null ? "{}" : $"{{\"project\":{JsonSerializer.Serialize(project)}}}";
        var quota = await LocalLoginHttp.SendAsync(HttpMethod.Post, QuotaUrl, accessToken, quotaBody, UserAgent, ct);
        if (quota.StatusCode is 401 or 403)
            return ForgetCachedTokenAndReportSignedOut();
        if (!quota.Ok)
            return AntigravityUsage.Failed;

        return new AntigravityUsage(LocalLoginOutcome.Ok, quota.Body, planName);
    }

    /// <summary>A refused token must not keep being served from the cache for up to an hour after the
    /// CLI has already signed in again.</summary>
    private static AntigravityUsage ForgetCachedTokenAndReportSignedOut()
    {
        lock (Gate)
        {
            _cachedRefreshToken = null;
            _cachedAccessToken = null;
        }
        return AntigravityUsage.NotSignedIn;
    }

    /// <summary><c>Rejected</c> is true when the token endpoint refused the refresh token itself
    /// (no pair succeeded, at least one answered <c>invalid_grant</c> and none failed in transit): the CLI
    /// has signed out, which is not a network failure. The two optional parameters are the test seams for the endpoint and
    /// the discovered client pairs.</summary>
    internal static async Task<(string? Token, bool Rejected)> ResolveAccessTokenAsync(
        string? refreshToken, string? storedAccess, DateTimeOffset storedExpiry, CancellationToken ct,
        Func<string, IEnumerable<KeyValuePair<string, string>>, CancellationToken, Task<LocalLoginHttp.Response>>? post = null,
        Func<IReadOnlyList<(string ClientId, string ClientSecret)>>? discoverPairs = null)
    {
        var now = DateTimeOffset.UtcNow;

        lock (Gate)
        {
            if (refreshToken is not null && _cachedRefreshToken == refreshToken
                && _cachedAccessToken is not null && _cachedExpiry > now.AddSeconds(30))
                return (_cachedAccessToken, false);
        }

        // The stored token is refreshed by the CLI/IDE itself while it runs; trust it whenever it is
        // still comfortably in date, so the common case needs no network at all.
        if (storedAccess is { Length: > 0 } && storedExpiry > now.AddSeconds(30))
            return (storedAccess, false);

        if (refreshToken is null)
            return (null, false);

        return await RefreshAsync(
            refreshToken,
            post ?? LocalLoginHttp.PostFormAsync,
            discoverPairs ?? AntigravityOAuthClient.DiscoverPairs,
            ct);
    }

    private static async Task<(string? Token, bool Rejected)> RefreshAsync(
        string refreshToken,
        Func<string, IEnumerable<KeyValuePair<string, string>>, CancellationToken, Task<LocalLoginHttp.Response>> post,
        Func<IReadOnlyList<(string ClientId, string ClientSecret)>> discoverPairs,
        CancellationToken ct)
    {
        var transportFailures = 0;
        var invalidGrants = 0;
        foreach (var (clientId, clientSecret) in discoverPairs())
        {
            ct.ThrowIfCancellationRequested();
            var response = await post(TokenUrl, new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            }, ct);

            if (!response.Ok)
            {
                if (response.StatusCode == 0)
                    transportFailures++;
                else if (IsInvalidGrant(response))
                    invalidGrants++;
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(response.Body);
                if (!document.RootElement.TryGetProperty("access_token", out var accessEl)
                    || accessEl.GetString() is not { Length: > 0 } accessToken)
                    continue;

                var lifetime = document.RootElement.TryGetProperty("expires_in", out var expiresEl)
                    && expiresEl.TryGetInt32(out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : TimeSpan.FromMinutes(55);

                lock (Gate)
                {
                    _cachedRefreshToken = refreshToken;
                    _cachedAccessToken = accessToken;
                    _cachedExpiry = DateTimeOffset.UtcNow + lifetime;
                }
                return (accessToken, false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Try the next pair. InvalidOperationException: a field of an unexpected JSON type.
            }
        }

        // The client is authenticated before the grant is checked, so an invalid_grant proves its pair
        // was accepted and the token itself is dead; the other pairs may simply be look-alikes that
        // answer invalid_client. Without any answer from a pair (transport failure) nothing is certain.
        return (null, invalidGrants > 0 && transportFailures == 0);
    }

    private static bool IsInvalidGrant(LocalLoginHttp.Response response)
    {
        if (response.StatusCode is not (400 or 401))
            return false;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() == "invalid_grant";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (string? Project, string? PlanName) ReadProjectAndPlan(string loadBody)
    {
        try
        {
            using var document = JsonDocument.Parse(loadBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);
            var project = root.TryGetProperty("cloudaicompanionProject", out var projectEl) && projectEl.ValueKind == JsonValueKind.String
                ? projectEl.GetString()
                : null;
            var planName = root.TryGetProperty("paidTier", out var paidEl) && paidEl.ValueKind == JsonValueKind.Object
                && paidEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()
                : null;
            return (project, AntigravityStateReader.ShortenPlanName(planName));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
