using System.Text.RegularExpressions;
using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>
/// The widget must never spend a provider's own quota: every network call it makes has to be a
/// read. These guards scan the shipped source as text, the same way the ship-clean guards do, so a
/// later change that adds an HTTP client, an unlabelled fetch, a write verb or a fourth Claude
/// endpoint fails here instead of shipping unnoticed. Each check is a pure function over text, so
/// it is proven against a planted violation here, not just eyeballed once by hand.
///
/// One narrow, named exception: <c>Services/UpdateCheck.cs</c> uses <see cref="System.Net.Http.HttpClient"/>
/// for a GET against this app's own public release metadata - it never touches a provider, never
/// anything but GET, and is proven safe on its own terms below rather than simply let through.
/// </summary>
public class TokenSafetyTests
{
    private const string UpdateCheckRelativePath = "src/AiUsage/Services/UpdateCheck.cs";

    // The second, and only other, file permitted an HttpClient: the local-login providers' single
    // HTTP surface. It enforces its own fixed allow-list of read-only usage/quota and token-refresh
    // URLs (proven below), so a bug can never turn it into a general web client.
    private const string LocalLoginHttpRelativePath = "src/AiUsage/Providers/LocalLogin/LocalLoginHttp.cs";

    // The third: the one-time download of Microsoft's WebView2 bootstrapper. It follows its redirects
    // by hand and refuses every address but Microsoft's two download hosts (proven below).
    private const string WebViewInstallerRelativePath = "src/AiUsage/Web/WebViewRuntimeInstaller.cs";

    // The fourth and last: the update download loads release files from a fixed host list only.
    private const string UpdateHostRelativePath = "src/AiUsage/Services/UpdateHost.cs";

    private static readonly string[] HttpClientAllowedFiles =
        [UpdateCheckRelativePath, LocalLoginHttpRelativePath, WebViewInstallerRelativePath, UpdateHostRelativePath];

    private static readonly Regex HttpClientType = new(
        @"\b(?:HttpClient|WebRequest|WebClient|TcpClient|SocketsHttpHandler)\b", RegexOptions.Compiled);

    private static readonly Regex FetchCall = new(@"fetch\(", RegexOptions.Compiled);

    private static readonly Regex ForbiddenRequest = new(
        // One alternative per chat surface the app must never address: the first group is Claude's,
        // the second ChatGPT's own conversation endpoints, which the Codex read route would otherwise
        // be able to name without this guard noticing. The POST verb is checked separately, below -
        // see HasForbiddenPostVerb - because one endpoint (Cursor's Grok Bot usage read) only ever
        // answers on POST.
        @"/completion|/completions|/messages|/chat_conversations|/append_message"
        + @"|/conversation|/backend-api/f/conversation",
        RegexOptions.Compiled);

    private static readonly Regex PostVerb = new(@"method:\s*'POST'", RegexOptions.Compiled);

    // The one deliberate exception to "every request is a GET": Cursor's own Grok Bot usage bar
    // only answers on POST (see CursorDiscoveryScript's own doc comment) - still a plain read, body
    // always the literal '{}', never a candidate path and never built from anything the discovered
    // account carries. Every other POST anywhere under src still fails this guard; this one is
    // recognised only by its exact endpoint appearing near the verb, not merely by being POST.
    private const string AllowedPostEndpoint = "/api/dashboard/get-sand-usage-status";

    internal static bool HasForbiddenHttpClientType(string text) => HttpClientType.IsMatch(text);

    internal static bool HasFetchWithoutGetNearby(string text)
    {
        foreach (Match match in FetchCall.Matches(text))
        {
            var window = text.Substring(match.Index, Math.Min(200, text.Length - match.Index));
            if (window.Contains("method: 'GET'", StringComparison.Ordinal))
                continue;
            // The one exception (see HasForbiddenPostVerb): a POST is only ever tolerated for the
            // fixed Grok Bot endpoint, never for any other fetch.
            if (window.Contains("method: 'POST'", StringComparison.Ordinal) && window.Contains(AllowedPostEndpoint, StringComparison.Ordinal))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>True for a POST verb anywhere in <paramref name="text"/> that is not immediately
    /// explained by <see cref="AllowedPostEndpoint"/> appearing nearby - the same windowed-proximity
    /// idea as <see cref="HasFetchWithoutGetNearby"/>, just inverted (forbidden unless nearby, rather
    /// than required nearby).</summary>
    internal static bool HasForbiddenPostVerb(string text)
    {
        foreach (Match match in PostVerb.Matches(text))
        {
            var start = Math.Max(0, match.Index - 200);
            var window = text.Substring(start, Math.Min(400, text.Length - start));
            if (!window.Contains(AllowedPostEndpoint, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static bool HasForbiddenRequestShape(string text) => ForbiddenRequest.IsMatch(text) || HasForbiddenPostVerb(text);

    [Fact]
    public void No_own_http_client_exists_anywhere_under_src_outside_the_named_update_check_exception()
    {
        var offenders = SourceFiles()
            .Where(f => !HttpClientAllowedFiles.Contains(RelativePath(f)))
            .Where(f => HasForbiddenHttpClientType(File.ReadAllText(f)))
            .Select(RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0, "HTTP client type(s) found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void UpdateCheck_never_names_a_provider_endpoint_or_a_write_verb()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), UpdateCheckRelativePath));

        Assert.False(HasForbiddenRequestShape(text));
        Assert.Contains("GetAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PostAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PutAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SendAsync", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateHost_only_reads_and_checks_every_address_against_the_host_list()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), UpdateHostRelativePath));

        Assert.False(HasForbiddenRequestShape(text));
        Assert.Contains("GetAsync", text, StringComparison.Ordinal);
        Assert.Contains("UpdateInstaller.IsAllowedUrl", text, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PostAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PutAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SendAsync", text, StringComparison.Ordinal);
        Assert.False(ReferencesAProviderOrigin(text));
    }

    // Checked as full origins, not bare hostnames: a bare "github.com" would also match this app's
    // own "api.github.com" release feed, which is exactly the address this file is supposed to use.
    private static readonly string[] ProviderOrigins =
    [
        "https://claude.ai",
        "https://chatgpt.com",
        "https://cursor.com",
        "https://aistudio.google.com",
        "https://github.com",
    ];

    internal static bool ReferencesAProviderOrigin(string text) =>
        ProviderOrigins.Any(origin => text.Contains(origin, StringComparison.Ordinal));

    [Fact]
    public void UpdateCheck_points_only_at_this_project_own_release_feed()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), UpdateCheckRelativePath));

        Assert.Contains(
            "https://api.github.com/repos/wbgcoding/AI-Usage/releases/latest",
            text,
            StringComparison.Ordinal);
        Assert.False(ReferencesAProviderOrigin(text));
    }

    [Fact]
    public void ReferencesAProviderOrigin_check_fires_on_a_planted_provider_host_in_place_of_the_release_feed()
    {
        Assert.True(ReferencesAProviderOrigin(
            "private const string ReleasesUrl = \"https://claude.ai/repos/x/y/releases/latest\";"));
        Assert.False(ReferencesAProviderOrigin(
            "private const string ReleasesUrl = \"https://api.github.com/repos/x/y/releases/latest\";"));
    }

    [Fact]
    public void The_webview_installer_names_only_the_two_microsoft_download_hosts_and_only_ever_reads()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), WebViewInstallerRelativePath));

        Assert.False(HasForbiddenRequestShape(text));
        Assert.False(ReferencesAProviderOrigin(text));
        Assert.Contains("HttpMethod.Get", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMethod.Post", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PostAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PutAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAsync", text, StringComparison.Ordinal);

        // Every https address written in the file belongs to Microsoft's go. or delivery hosts.
        foreach (Match url in Regex.Matches(text, @"https://[A-Za-z0-9.\-]+"))
        {
            Assert.True(
                url.Value is "https://go.microsoft.com" or "https://msedge.sf.dl.delivery.mp.microsoft.com",
                $"unexpected address in the installer: {url.Value}");
        }
    }

    [Fact]
    public void LocalLoginHttp_allows_only_read_only_usage_and_token_urls_never_a_chat_endpoint()
    {
        // The exact reads the local-login providers make.
        Assert.True(LocalLoginHttp.IsAllowedUrl("https://oauth2.googleapis.com/token"));
        Assert.True(LocalLoginHttp.IsAllowedUrl("https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"));
        Assert.True(LocalLoginHttp.IsAllowedUrl("https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"));
        Assert.True(LocalLoginHttp.IsAllowedUrl("https://api.anthropic.com/api/oauth/usage"));

        // A chat/message/completion endpoint, or anything else, is refused before a socket opens.
        Assert.False(LocalLoginHttp.IsAllowedUrl("https://api.anthropic.com/v1/messages"));
        Assert.False(LocalLoginHttp.IsAllowedUrl("https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent"));
        Assert.False(LocalLoginHttp.IsAllowedUrl("https://example.com/"));
    }

    [Fact]
    public void HttpClientType_check_fires_on_a_planted_HttpClient_field()
    {
        Assert.True(HasForbiddenHttpClientType("private readonly HttpClient _client = new();"));
        Assert.False(HasForbiddenHttpClientType("private readonly int _retries = 3;"));
    }

    [Fact]
    public void Every_fetch_call_names_the_GET_method_within_200_characters()
    {
        var offenders = SourceFiles()
            .Where(f => HasFetchWithoutGetNearby(File.ReadAllText(f)))
            .Select(RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0, "fetch( call(s) missing an explicit GET:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void FetchWithoutGet_check_fires_on_a_planted_unlabelled_fetch()
    {
        Assert.True(HasFetchWithoutGetNearby("const res = await fetch(path, {credentials: 'include'});"));
        Assert.False(HasFetchWithoutGetNearby("const res = await fetch(path, {method: 'GET', credentials: 'include'});"));
    }

    [Fact]
    public void HasForbiddenPostVerb_allows_only_the_fixed_grok_bot_endpoint()
    {
        Assert.False(HasForbiddenPostVerb(
            "await fetch('/api/dashboard/get-sand-usage-status', {method: 'POST', credentials: 'include', body: '{}'})"));
        Assert.True(HasForbiddenPostVerb("await fetch('/api/chat', {method: 'POST', credentials: 'include'})"));
    }

    [Fact]
    public void No_write_verb_or_chat_endpoint_appears_anywhere_under_src()
    {
        // body: is deliberately not checked for: the correct script already returns its answer as
        // "body: text", so forbidding it would fail on untouched, correct code.
        var offenders = SourceFiles()
            .Where(f => HasForbiddenRequestShape(File.ReadAllText(f)))
            .Select(RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0, "Forbidden request shape(s) found:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void ForbiddenRequestShape_check_fires_on_a_planted_POST_or_chat_endpoint()
    {
        Assert.True(HasForbiddenRequestShape("fetch(path, {method: 'POST', credentials: 'include'})"));
        Assert.True(HasForbiddenRequestShape("await fetch('/api/organizations/x/messages')"));
        Assert.False(HasForbiddenRequestShape("if (ct.includes('json')) return {status: 'ok', path: path, body: text};"));
    }

    // Every provider must own up to where it actually looks (IUsageProvider.ReadLocations), for the
    // About window's "what this program reads" section - the default interface member returns an
    // empty list, which is exactly what a provider added later without filling this in would show,
    // so this is proven red against a planted empty one rather than only eyeballed once by hand.
    private sealed class EmptyReadLocationsProvider : IUsageProvider
    {
        public string Id => "empty";
        public string DisplayName => "Empty";
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void Every_shipped_provider_names_at_least_one_read_location()
    {
        IReadOnlyList<IUsageProvider> providers = [new ClaudeProvider(), new CodexProvider(), new CursorProvider(), new GeminiProvider(), new CopilotProvider()];

        Assert.All(providers, provider => Assert.NotEmpty(provider.ReadLocations));
    }

    [Fact]
    public void ReadLocations_check_fires_on_a_provider_that_never_filled_it_in()
    {
        Assert.Empty(((IUsageProvider)new EmptyReadLocationsProvider()).ReadLocations);
    }

    // A local read location that is not a quota endpoint: the index walks every Claude and Codex
    // session transcript on this machine, not just the three quota endpoints above. It is a plain
    // file read (Directory.EnumerateFiles/FileStream), already covered by the HTTP-client and
    // fetch-shape guards above like every other file under src - this fact only pins the two roots
    // down by name, so a future rename of either root is caught here rather than silently drifting
    // from what was measured here.
    [Fact]
    public void StatsIndexer_walks_only_the_two_measured_local_session_log_roots()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/AiUsage/Stats/StatsIndexer.cs"));
        var claudeRoot = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/AiUsage/Providers/ClaudeConfigRoot.cs"));

        Assert.Contains("ClaudeConfigRoot.", text, StringComparison.Ordinal);
        Assert.Contains("\".claude\"", claudeRoot, StringComparison.Ordinal);
        Assert.Contains("\"projects\"", claudeRoot, StringComparison.Ordinal);
        var codexPaths = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/AiUsage/Providers/CodexPaths.cs"));
        Assert.Contains("CodexPaths.Sessions", text, StringComparison.Ordinal);
        Assert.Contains("CodexPaths.ArchivedSessions", text, StringComparison.Ordinal);
        Assert.Contains("\".codex\"", codexPaths, StringComparison.Ordinal);
        Assert.Contains("\"sessions\"", codexPaths, StringComparison.Ordinal);
        Assert.Contains("\"archived_sessions\"", codexPaths, StringComparison.Ordinal);
    }

    // Settings.SignOut disconnects only this app - it must never sign the user out of
    // another tool. "gh auth logout" is the one command that would do exactly that for the GitHub
    // CLI (Copilot's own read route); this guard fails the moment any shipped source file names it,
    // the same static-scan style every other guard in this file already uses.
    [Fact]
    public void No_source_file_ever_runs_gh_auth_logout()
    {
        var offenders = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("auth logout", StringComparison.OrdinalIgnoreCase))
            .Select(RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0, "'auth logout' found in:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void CandidatePaths_has_exactly_the_three_read_only_usage_endpoints()
    {
        // The three names are written out literally here, not read back out of CandidatePaths -
        // this test exists precisely to catch a fourth path being added, and a check derived from
        // the same constant could never see one.
        Assert.Equal(3, ClaudeDiscoveryScript.CandidatePaths.Length);
        Assert.Contains(ClaudeDiscoveryScript.CandidatePaths, p => p.EndsWith("/usage", StringComparison.Ordinal));
        Assert.Contains(ClaudeDiscoveryScript.CandidatePaths, p => p.EndsWith("/usage_limits", StringComparison.Ordinal));
        Assert.Contains(ClaudeDiscoveryScript.CandidatePaths, p => p.EndsWith("/rate_limits", StringComparison.Ordinal));
    }

    private static IEnumerable<string> SourceFiles()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsUnderBinOrObj(f));
    }

    private static bool IsUnderBinOrObj(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string RelativePath(string fullPath) =>
        Path.GetRelativePath(FindRepoRoot(), fullPath).Replace('\\', '/');

    // A worktree's own root has a ".git" FILE (pointing at the real repo's .git/worktrees/<name>),
    // not a ".git" directory - checking only Directory.Exists (as elsewhere in this test project)
    // walks straight past a worktree root and finds the main checkout's .git directory instead,
    // silently scanning the wrong copy of the source. Checking either keeps this correct in both.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the repository root (.git) above " + AppContext.BaseDirectory);
    }
}
