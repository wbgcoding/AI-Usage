using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// The one deliberate network call that has nothing to do with any provider's own usage numbers: an
/// opt-out, at-most-daily read of this app's own GitHub releases feed, purely to tell the user a
/// newer version exists. This class only reads; loading and running a newer version is
/// <see cref="UpdateInstaller"/>'s job, and only behind a valid signature. <c>TokenSafetyTests</c> carries a
/// narrow, explicit exception naming this one file, because this request is a read of this app's own
/// public release metadata, never a provider endpoint, and never anything but GET.
/// </summary>
public static class UpdateCheck
{
    public static readonly TimeSpan MinCheckInterval = TimeSpan.FromHours(24);

    private const string ReleasesUrl = "https://api.github.com/repos/wbgcoding/AI-Usage/releases/latest";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One file attached to a release: its name and the address it is loaded from.</summary>
    public sealed record ReleaseAsset(string Name, string DownloadUrl);

    public sealed record Release(string TagName, string HtmlUrl, IReadOnlyList<ReleaseAsset>? Assets = null);

    /// <summary>The version a tag names, without the leading "v" - what the notices show.</summary>
    public static string DisplayVersion(string tagName) =>
        tagName.Length > 0 && (tagName[0] == 'v' || tagName[0] == 'V') ? tagName[1..] : tagName;

    /// <summary>
    /// Strips one leading "v"/"V" off <paramref name="tagName"/>, parses both sides with
    /// <see cref="Version.TryParse(string?, out Version?)"/> and compares - anything either side
    /// cannot parse is simply never "newer", never an exception. Pure, so every case (newer, older,
    /// equal, v-prefixed, unparseable) is a plain fact test with no network involved at all.
    /// </summary>
    public static bool IsNewer(string runningVersion, string tagName)
    {
        return Version.TryParse(runningVersion, out var running) &&
               Version.TryParse(DisplayVersion(tagName), out var tag) &&
               tag > running;
    }

    /// <summary>
    /// Runs <paramref name="fetch"/> only once <see cref="MinCheckInterval"/> has actually passed
    /// since <see cref="AppSettings.LastUpdateCheckUtc"/> (or never ran at all), and only while
    /// <see cref="AppSettings.CheckForUpdates"/> is on - otherwise returns null without ever calling
    /// it. The timestamp is written on every real attempt, successful or not, so a persistent
    /// failure (offline, or no release published yet and the API answering 404) is retried at most
    /// once a day, never hammered on every About window open.
    /// </summary>
    public static async Task<Release?> CheckIfDueAsync(
        AppSettings settings, Action<AppSettings> save, Func<CancellationToken, Task<Release?>> fetchRelease,
        DateTimeOffset now, CancellationToken ct)
    {
        if (!settings.CheckForUpdates)
            return null;
        if (settings.LastUpdateCheckUtc is { } last && now - last < MinCheckInterval)
            return null;

        var release = await fetchRelease(ct);
        settings.LastUpdateCheckUtc = now;
        save(settings);
        return release;
    }

    /// <summary>The page link is handed to the shell when the user clicks it, so only an https page
    /// on github.com is ever accepted - never a file path, a program or another scheme.</summary>
    internal static bool IsReleasePageUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    private static List<ReleaseAsset> ReadAssets(JsonElement release)
    {
        var assets = new List<ReleaseAsset>();
        if (!release.TryGetProperty("assets", out var list) || list.ValueKind != JsonValueKind.Array)
            return assets;

        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } fileName
                && item.TryGetProperty("browser_download_url", out var url) && url.GetString() is { Length: > 0 } address)
                assets.Add(new ReleaseAsset(fileName, address));
        }

        return assets;
    }

    /// <summary>The real fetch: reads only <c>tag_name</c>, <c>html_url</c> and the name and download
    /// address of each attached file from the response, nothing else. Any failure whatsoever - offline,
    /// timeout, a 404 (no release has been published yet, which is simply "no newer version"), an
    /// unexpected response shape - becomes null, never an exception the caller has to guard against
    /// and never a tile-style error state.</summary>
    public static async Task<Release?> FetchLatestAsync(CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = RequestTimeout };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AI-Usage", AppInfo.Version));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await client.GetAsync(ReleasesUrl, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var stream = await response.Content.ReadAsStreamAsync(ct);
            await using (stream.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var tagName = document.RootElement.GetProperty("tag_name").GetString();
                var htmlUrl = document.RootElement.GetProperty("html_url").GetString();
                return tagName is null || !IsReleasePageUrl(htmlUrl) ? null : new Release(tagName, htmlUrl!, ReadAssets(document.RootElement));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
}
