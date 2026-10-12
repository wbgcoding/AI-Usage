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
        globalThis.fetch = async (path) => {
            const r = routes[path] || {status: 404, body: ''};
            if (r.throws) throw new Error('network');
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

    private static string RunInNode(string script, Dictionary<string, Route> routes)
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
    public void Gemini_discovery_returns_every_json_candidate_and_the_reader_takes_the_first_that_parses()
    {
        const string quota = """
            {"groups":[{"displayName":"Gemini Models","buckets":[
              {"window":"weekly","resetTime":"2026-09-18T05:21:33Z","remainingFraction":0.9},
              {"window":"5h","resetTime":"2026-09-13T20:40:15Z","remainingFraction":1}]}]}
            """;
        var routes = new Dictionary<string, Route>
        {
            [GeminiDiscoveryScript.CandidatePaths[0]] = Json("""{"unknown":true}"""),
            [GeminiDiscoveryScript.CandidatePaths[1]] = Json(quota),
        };
        var envelope = RunInNode(GeminiDiscoveryScript.Discover(), routes);
        var source = NewSource(new GeminiUsageEndpoint(), "gemini", "aistudio.google.com");
        var (result, path) = source.Interpret(envelope);
        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Equal(GeminiDiscoveryScript.CandidatePaths[1], path);
    }

    private static WebUsageSource NewSource(IWebUsageEndpoint endpoint, string id, string host) =>
        new(new AiUsage.Web.WebSessionDescriptor(
                ProviderId: id, BaseUrl: "https://" + host + "/", SignInUrl: "https://" + host + "/",
                AllowedHosts: [host], ProfileFolderName: id),
            endpoint, (_, _) => Task.FromResult(""));
}
