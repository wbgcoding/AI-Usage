using System.Net.Http;
using System.Net.Http.Headers;

namespace AiUsage.Services;

/// <summary>
/// The one file download this app makes: plain GET requests over https to a fixed list of hosts. Redirects
/// are followed by hand so every hop is checked before a request goes out, the body is capped, and a
/// connection that goes quiet ends the download. Failures throw; the caller decides what that means.
/// This file is on the HTTP client allow-list in <c>TokenSafetyTests</c>.
/// </summary>
internal static class AllowListedDownloader
{
    internal const int MaxRedirects = 5;

    /// <summary>The largest body accepted. The files downloaded here (an installer, the WebView2
    /// bootstrapper) are a few megabytes; anything far beyond that is not what was asked for.</summary>
    internal const long MaxDownloadBytes = 32L * 1024 * 1024;

    /// <summary>A download ends when no byte has arrived for this long, so a slow link still finishes.</summary>
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The cap on the whole transfer, the last resort against a connection that trickles forever.</summary>
    internal static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(5);

    internal static Task DownloadAsync(
        string url, string destination, Func<Uri, bool> isAllowed, IProgress<double>? progress, CancellationToken ct) =>
        DownloadAsync(url, destination, isAllowed, null, StallTimeout, OverallTimeout, progress, ct);

    /// <summary>The handler and the limits are parameters so a test can stall the body: the stall limit
    /// restarts with the headers and after every chunk read. Without a handler the download uses one
    /// that does not follow redirects itself. The destination must not exist: a file or link already
    /// there is never written through.</summary>
    internal static async Task DownloadAsync(
        string url, string destination, Func<Uri, bool> isAllowed, HttpMessageHandler? handler,
        TimeSpan stallLimit, TimeSpan overallLimit, IProgress<double>? progress, CancellationToken ct)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(overallLimit);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.ProductName, AppInfo.Version));

        var current = new Uri(url);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (current.Scheme != Uri.UriSchemeHttps || !isAllowed(current))
                throw new InvalidOperationException("download address is not on the allow-list");

            limit.CancelAfter(stallLimit);
            using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total > MaxDownloadBytes)
                throw new InvalidOperationException("download is larger than allowed");

            await using var source = await response.Content.ReadAsStreamAsync(limit.Token);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            limit.CancelAfter(stallLimit);
            while ((read = await source.ReadAsync(buffer, limit.Token)) > 0)
            {
                limit.CancelAfter(stallLimit);
                received += read;
                if (received > MaxDownloadBytes)
                    throw new InvalidOperationException("download is larger than allowed");
                await target.WriteAsync(buffer.AsMemory(0, read), limit.Token);
                if (total is > 0)
                    progress?.Report(Math.Min(1.0, (double)received / total.Value));
            }

            if (received == 0)
                throw new InvalidOperationException("download was empty");

            progress?.Report(1.0);
            return;
        }

        throw new HttpRequestException("too many redirects");
    }
}
