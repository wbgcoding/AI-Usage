using System.Net.Http;
using System.Net.Http.Headers;
using AiUsage.Services;
using AiUsage.Storage;

namespace AiUsage.Providers.LocalLogin;

/// <summary>
/// The one HTTP surface the local-login providers use. It is one of the few files allowed an
/// <see cref="HttpClient"/>; <c>TokenSafetyTests</c> holds that allow-list. Every request must target one of a fixed allow-list of usage/quota and
/// token-refresh endpoints; a request to anything else is refused before it is sent. Redirects are
/// never followed (a 3xx counts as a failed read), so each request makes exactly one hop to the
/// URL that was checked and a poisoned response can never turn this into a general web client or
/// replay a form body elsewhere. Responses are read with a size cap. It reads usage numbers only and
/// never posts a prompt: no chat or completion endpoint is on the list.
/// </summary>
internal static class LocalLoginHttp
{
    // Exact URLs (no query string) the local-login providers are permitted to call. Each is a
    // read/telemetry-free endpoint: a Google token refresh, Antigravity's own usage/quota reads, and
    // Claude Code's own usage read. Never a chat, completion or message endpoint.
    private static readonly HashSet<string> AllowedUrls = new(StringComparer.Ordinal)
    {
        "https://oauth2.googleapis.com/token",
        "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
        "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
        "https://api.anthropic.com/api/oauth/usage",
    };

    /// <summary>Largest response body accepted; anything bigger is a failed read.</summary>
    internal const int MaxBodyBytes = 1024 * 1024;

    private static readonly HttpClient Client = CreateClient();

    /// <summary>How long reading a response body may take once the headers are in. The client's own
    /// timeout stops covering the body after the headers.</summary>
    internal static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(20);

    internal static HttpClient CreateClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    internal static bool IsAllowedUrl(string url) => AllowedUrls.Contains(url);

    /// <summary>True once the response is JSON worth parsing; false for HTML, an error status or no
    /// response at all. Kept separate so a caller can tell "server said no" from "wrong shape".</summary>
    internal readonly record struct Response(bool Ok, int StatusCode, string Body);

    /// <summary>Sends one request to an allow-listed URL and returns its body. Never throws: a
    /// blocked URL, a transport error or a non-success status all come back as a non-Ok
    /// <see cref="Response"/>. The <paramref name="bearer"/> token is attached only for the single
    /// call and is never stored or logged here.</summary>
    public static async Task<Response> SendAsync(
        HttpMethod method, string url, string? bearer, string? jsonBody, string? userAgent, CancellationToken ct,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null)
    {
        if (!IsAllowedUrl(url))
            return new Response(false, 0, "");

        using var request = new HttpRequestMessage(method, url);
        if (bearer is { Length: > 0 })
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (userAgent is { Length: > 0 })
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (extraHeaders is not null)
            foreach (var header in extraHeaders)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        return await ExecuteAsync(Client, request, ct);
    }

    /// <summary>Runs one request on <paramref name="client"/> without any allow-list check (the
    /// public entry points do that) and maps the outcome to a <see cref="Response"/>.</summary>
    internal static async Task<Response> ExecuteAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct, TimeSpan? bodyTimeout = null)
    {
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            // A redirect is never followed: report it as the failure it is, without a body.
            if (status is >= 300 and < 400)
                return new Response(false, status, "");

            using var bodyCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyCancel.CancelAfter(bodyTimeout ?? BodyTimeout);
            var body = await ReadBoundedAsync(response.Content, bodyCancel.Token);
            if (body is null)
            {
                LogService.Shared.LogError(
                    $"Local login: response from {request.RequestUri?.Host} over {MaxBodyBytes} bytes, dropped.");
                return new Response(false, status, "");
            }

            return new Response(status is >= 200 and < 300, status, body);
        }
        // IOException: a connection reset while the body is read; OperationCanceledException: the
        // body took longer than its own limit (the caller's token is excluded and propagates).
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return new Response(false, 0, "");
        }
    }

    /// <summary>Reads at most <see cref="MaxBodyBytes"/> bytes as UTF-8; null when the body is larger.</summary>
    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } declared && declared > MaxBodyBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>An <c>application/x-www-form-urlencoded</c> POST, used only for the OAuth token
    /// refresh. Same allow-list and never-throws contract as <see cref="SendAsync"/>.</summary>
    public static async Task<Response> PostFormAsync(
        string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        if (!IsAllowedUrl(url))
            return new Response(false, 0, "");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        return await ExecuteAsync(Client, request, ct);
    }
}
