using System.Diagnostics;
using System.Text.Json;
using AiUsage.Providers;

namespace AiUsage.Tests;

/// <summary>
/// Runs the discovery scripts against a canned site (a stubbed fetch) in Node, when Node is on the
/// path, and reads the envelope the way the app does. Without Node the behavioural checks have
/// nothing to run on and pass vacuously; the structural script tests next to them still apply.
/// </summary>
public class DiscoveryScriptBehaviourTests
{
    private const string Runner = """
        const fs = require('fs');
        const routes = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
        globalThis.location = {origin: 'https://site.test', hostname: 'site.test'};
        globalThis.navigator = {onLine: true};
        // The Gemini script reads its anti-forgery token, build label and language from the page itself.
        globalThis.window = {WIZ_global_data: JSON.parse(process.argv[4])};
        globalThis.document = {documentElement: {lang: 'de'}};
        globalThis.fetch = async (path, init) => {
            // A path with extra query parameters (the Gemini RPC adds the build label and language)
            // matches the route that names its fixed start.
            const key = routes[path] ? path : Object.keys(routes).find(k => path.startsWith(k + '&'));
            const r = routes[key] || {status: 404, body: ''};
            if (r.throws) throw new Error('network');
            // The Gemini usage RPC only answers a POST that carries the request literal and the token.
            if (key && key.includes('batchexecute')) {
                const body = String((init || {}).body || '');
                if ((init || {}).method !== 'POST' || !body.includes('f.req=') || !body.includes('at=tok')) return {status: 405, ok: false, text: async () => ''};
            }
            return {
                status: r.status,
                ok: r.status >= 200 && r.status < 300,
                headers: {get: (h) => h.toLowerCase() === 'content-type' ? (r.ct == null ? 'application/json' : r.ct) : null},
                json: async () => JSON.parse(r.body),
                text: async () => r.body,
            };
        };
        const script = fs.readFileSync(process.argv[2], 'utf8');
        Promise.resolve((0, eval)(script)).then(v => console.log(JSON.stringify(v)));
        """;

    private const string CodexOk =
        """{"rate_limits":{"primary":{"used_percent":12.5,"window_minutes":300},"secondary":{"used_percent":64,"window_minutes":10080}}}""";

    private const string ClaudeOk = """{"five_hour":{"utilization":30,"resets_at":1788624000}}""";

    private const string Uuid = "01234567-89ab-cdef-0123-456789abcdef";

    private static Route Json(string body) => new(200, body);

    private static Route Status(int status) => new(status, "");

    private sealed record Route(int Status, string Body, string? Ct = null);

    private const string SignedInPage = """{"SNlM0e":"tok","cfb2h":"boq_build"}""";

    private static string RunInNode(string script, Dictionary<string, Route> routes, string pageData = SignedInPage)
    {
        var node = NodeLocator.Find() ?? throw new InvalidOperationException("node.exe not on PATH");

        using var directory = TestPaths.CreateDisposableDirectory("ai-usage-discovery-script");
        var runnerPath = Path.Combine(directory.Path, "runner.js");
        var scriptPath = Path.Combine(directory.Path, "script.js");
        var routesPath = Path.Combine(directory.Path, "routes.json");
        File.WriteAllText(runnerPath, Runner);
        File.WriteAllText(scriptPath, script);
        File.WriteAllText(routesPath, JsonSerializer.Serialize(routes.ToDictionary(
            pair => pair.Key,
            pair => new { status = pair.Value.Status, body = pair.Value.Body, ct = pair.Value.Ct })));

        var info = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(runnerPath);
        info.ArgumentList.Add(scriptPath);
        info.ArgumentList.Add(routesPath);
        info.ArgumentList.Add(pageData);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20_000));
        return output.Trim();
    }

    private static string Status_(string? envelope)
    {
        using var document = JsonDocument.Parse(envelope!);
        return document.RootElement.GetProperty("status").GetString()!;
    }

    [NodeFact]
    public void Codex_session_route_answering_500_reads_as_failed_not_signed_out()
    {
        var envelope = RunInNode(CodexDiscoveryScript.Discover(), new() { ["/api/auth/session"] = Status(500) });
        Assert.Equal("failed", Status_(envelope));
    }

    [NodeFact]
    public void Codex_session_route_answering_429_reads_as_blocked()
    {
        var envelope = RunInNode(CodexDiscoveryScript.Discover(), new() { ["/api/auth/session"] = Status(429) });
        Assert.Equal("blocked", Status_(envelope));
    }

    [NodeFact]
    public void Codex_session_route_answering_401_still_reads_as_signed_out()
    {
        var envelope = RunInNode(CodexDiscoveryScript.Discover(), new() { ["/api/auth/session"] = Status(401) });
        Assert.Equal("not_signed_in", Status_(envelope));
    }

    [NodeFact]
    public void Codex_session_route_answering_500_fails_the_direct_read_too()
    {
        var envelope = RunInNode(CodexDiscoveryScript.Fetch("/backend-api/codex/usage"), new() { ["/api/auth/session"] = Status(500) });
        Assert.Equal("failed", Status_(envelope));
    }

    [NodeFact]
    public void Codex_discovery_returns_every_json_candidate_and_the_reader_takes_the_first_that_parses()
    {
        var routes = new Dictionary<string, Route>
        {
            ["/api/auth/session"] = Json("""{"accessToken":"t","user":{}}"""),
            ["/backend-api/codex/usage"] = Json("""{"error":"new schema"}"""),
            ["/backend-api/codex/rate_limits"] = Json(CodexOk),
        };
        var envelope = RunInNode(CodexDiscoveryScript.Discover(), routes);
        using (var document = JsonDocument.Parse(envelope))
            Assert.Equal(2, document.RootElement.GetProperty("results").GetArrayLength());

        var source = NewSource(new CodexUsageEndpoint(() => DateTimeOffset.Parse("2026-09-18T20:00:00Z")), "codex", "chatgpt.com");
        var (result, path) = source.Interpret(envelope);
        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Equal("/backend-api/codex/rate_limits", path);
    }

    [NodeFact]
    public void Claude_discovery_returns_every_json_candidate_and_the_reader_takes_the_first_that_parses()
    {
        var routes = new Dictionary<string, Route>
        {
            ["/api/organizations"] = Json($$"""[{"uuid":"{{Uuid}}","capabilities":["chat"]}]"""),
            [$"/api/organizations/{Uuid}/usage"] = Json("""{"unknown":true}"""),
            [$"/api/organizations/{Uuid}/usage_limits"] = Json(ClaudeOk),
        };
        var envelope = RunInNode(ClaudeDiscoveryScript.Discover(), routes);
        var source = NewSource(new ClaudeUsageEndpoint(), "claude", "claude.ai");
        var (result, path) = source.Interpret(envelope);
        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Equal($"/api/organizations/{Uuid}/usage_limits", path);
    }

    [NodeFact]
    public void Claude_discovery_with_every_candidate_rate_limited_reads_as_blocked()
    {
        var routes = new Dictionary<string, Route>
        {
            ["/api/organizations"] = Json($$"""[{"uuid":"{{Uuid}}","capabilities":["chat"]}]"""),
            [$"/api/organizations/{Uuid}/usage"] = Status(429),
            [$"/api/organizations/{Uuid}/usage_limits"] = Status(429),
        };
        Assert.Equal("blocked", Status_(RunInNode(ClaudeDiscoveryScript.Discover(), routes)));
    }

    /// <summary>The answer of the usage RPC as the app's batch envelope: a length-prefixed JSON line whose
    /// <c>jSf9Qc</c> entry carries the usage payload as a JSON string.</summary>
    private static string GeminiEnvelope(string payload)
    {
        var line = "[[\"wrb.fr\",\"jSf9Qc\"," + JsonSerializer.Serialize(payload) + ",null,null,null,\"generic\"],[\"di\",117]]";
        return ")]}'\n\n" + line.Length + "\n" + line + "\n25\n[[\"e\",4,null,null,237]]";
    }

    [NodeFact]
    public void Gemini_discovery_posts_the_usage_rpc_and_the_reader_takes_the_payload()
    {
        var routes = new Dictionary<string, Route>
        {
            [GeminiDiscoveryScript.CandidatePaths[0]] = new(200, GeminiEnvelope("[2,[[2181,0.09,1,[[1792246090,287070000]]],[45607,0.05737659,2,[[1792652890,287184000]]]],false]"), "text/plain"),
        };
        var envelope = RunInNode(GeminiDiscoveryScript.Discover(), routes);
        var source = NewSource(new GeminiUsageEndpoint(), "gemini", "gemini.google.com");
        var (result, path) = source.Interpret(envelope);
        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Equal(GeminiDiscoveryScript.CandidatePaths[0], path);
    }

    [NodeFact]
    public void Gemini_direct_read_answers_the_same_payload()
    {
        var routes = new Dictionary<string, Route>
        {
            [GeminiDiscoveryScript.CandidatePaths[0]] = new(200, GeminiEnvelope("[2,[],false]"), "text/plain"),
        };
        using var document = JsonDocument.Parse(RunInNode(GeminiDiscoveryScript.Fetch(GeminiDiscoveryScript.CandidatePaths[0]), routes));
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("[2,[],false]", document.RootElement.GetProperty("body").GetString());
    }

    [NodeFact]
    public void Gemini_page_without_an_anti_forgery_token_reads_as_signed_out_without_a_request()
    {
        var routes = new Dictionary<string, Route>
        {
            [GeminiDiscoveryScript.CandidatePaths[0]] = new(200, GeminiEnvelope("[2,[],false]"), "text/plain"),
        };
        Assert.Equal("not_signed_in", Status_(RunInNode(GeminiDiscoveryScript.Discover(), routes, pageData: "{}")));
        Assert.Equal("not_signed_in", Status_(RunInNode(GeminiDiscoveryScript.Fetch(GeminiDiscoveryScript.CandidatePaths[0]), routes, pageData: """{"SNlM0e":""}""")));
    }

    [NodeFact]
    public void Gemini_answer_without_the_usage_entry_reads_as_failed()
    {
        var routes = new Dictionary<string, Route>
        {
            [GeminiDiscoveryScript.CandidatePaths[0]] = new(200, ")]}'\n\n25\n[[\"e\",4,null,null,237]]", "text/plain"),
        };
        Assert.Equal("failed", Status_(RunInNode(GeminiDiscoveryScript.Discover(), routes)));
    }

    [NodeFact]
    public void Gemini_rpc_answering_401_reads_as_signed_out_and_429_as_blocked()
    {
        Assert.Equal("not_signed_in", Status_(RunInNode(GeminiDiscoveryScript.Discover(), new() { [GeminiDiscoveryScript.CandidatePaths[0]] = Status(401) })));
        Assert.Equal("blocked", Status_(RunInNode(GeminiDiscoveryScript.Discover(), new() { [GeminiDiscoveryScript.CandidatePaths[0]] = Status(429) })));
    }

    private static WebUsageSource NewSource(IWebUsageEndpoint endpoint, string id, string host) =>
        new(new AiUsage.Web.WebSessionDescriptor(
                ProviderId: id, BaseUrl: "https://" + host + "/", SignInUrl: "https://" + host + "/",
                AllowedHosts: [host], ProfileFolderName: id),
            endpoint, (_, _) => Task.FromResult(""));
}
