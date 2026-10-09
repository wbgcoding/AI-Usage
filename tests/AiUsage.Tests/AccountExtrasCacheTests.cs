using System.Text.Json;
using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The plan, e-mail and Grok Bot reads ride along with the usage read; each is a request of its own,
/// so a recent answer is kept for an hour and the script leaves that request out meanwhile.
/// </summary>
public class AccountExtrasCacheTests
{
    private const string OrgId = "2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc";
    private const string ClaudePath = $"/api/organizations/{OrgId}/usage";
    private const string ClaudeBody = """{"five_hour":{"utilization":42,"resets_at":1788624000}}""";
    private const string CodexBody = """{"rate_limit":{"primary_window":{"used_percent":23,"limit_window_seconds":18000}}}""";
    private const string CursorBody = """{"individualUsage":{"plan":{"totalPercentUsed":10}}}""";
    private const string PlanText = "default_claude_max_5x";

    // Built by concatenation: the ship-clean scan bans a real-looking address in tracked source.
    private const string FakeEmail = "user" + "@" + "example.com";

    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static WebSessionDescriptor DescriptorFor(string id, string host) => new(
        ProviderId: id, BaseUrl: $"https://{host}/", SignInUrl: $"https://{host}/login",
        AllowedHosts: [host], ProfileFolderName: id);

    private static string Envelope(string path, string body, string? plan = null, string? email = null, object? grokBot = null)
    {
        var fields = new Dictionary<string, object?> { ["status"] = "ok", ["path"] = path, ["body"] = body };
        if (plan is not null)
            fields["plan"] = plan;
        if (email is not null)
            fields["email"] = email;
        if (grokBot is not null)
            fields["grokBot"] = grokBot;
        return JsonSerializer.Serialize(fields);
    }

    /// <summary>A fake runner: answers every script with the envelope the real page would give, which
    /// carries the extras only when the script asked the site for them, and counts those asks.</summary>
    private sealed class FakeRunner(string path, string body, string askMarker, Func<string?, string> envelopeWhenAsked)
    {
        public int Asked { get; private set; }
        public int Runs { get; private set; }

        public Task<string> Run(string script, CancellationToken ct)
        {
            Runs++;
            if (script.Contains(askMarker, StringComparison.Ordinal))
            {
                Asked++;
                return Task.FromResult(envelopeWhenAsked(null));
            }

            return Task.FromResult(Envelope(path, body));
        }
    }

    [Fact]
    public async Task Twelve_claude_reads_in_one_hour_ask_for_the_plan_once()
    {
        var clock = new TestClock(Start);
        var runner = new FakeRunner(ClaudePath, ClaudeBody, "await readPlan()", _ => Envelope(ClaudePath, ClaudeBody, plan: PlanText));
        var source = new WebUsageSource(DescriptorFor("claude", "claude.ai"), new ClaudeUsageEndpoint(), runner.Run, timeProvider: clock);
        var settings = new AppSettings { WebUsagePaths = { ["claude"] = ClaudePath } };

        WebUsageResult? last = null;
        for (var read = 0; read < 12; read++)
        {
            last = await source.FetchAsync(settings, _ => { }, CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(5)); // 55 minutes at the last read
        }

        Assert.Equal(12, runner.Runs);
        Assert.Equal(1, runner.Asked); // the baseline asked 12 times
        Assert.Equal(PlanText, last!.PlanType); // the later reads still carry the plan, from the hold
    }

    [Fact]
    public async Task The_plan_is_asked_for_again_after_an_hour()
    {
        var clock = new TestClock(Start);
        var runner = new FakeRunner(ClaudePath, ClaudeBody, "await readPlan()", _ => Envelope(ClaudePath, ClaudeBody, plan: PlanText));
        var source = new WebUsageSource(DescriptorFor("claude", "claude.ai"), new ClaudeUsageEndpoint(), runner.Run, timeProvider: clock);
        var settings = new AppSettings { WebUsagePaths = { ["claude"] = ClaudePath } };

        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(59));
        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        Assert.Equal(1, runner.Asked);

        clock.Advance(TimeSpan.FromMinutes(1));
        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        Assert.Equal(2, runner.Asked);
    }

    [Fact]
    public async Task A_read_that_got_no_plan_asks_again_next_time()
    {
        var clock = new TestClock(Start);
        var runner = new FakeRunner(ClaudePath, ClaudeBody, "await readPlan()", _ => Envelope(ClaudePath, ClaudeBody));
        var source = new WebUsageSource(DescriptorFor("claude", "claude.ai"), new ClaudeUsageEndpoint(), runner.Run, timeProvider: clock);
        var settings = new AppSettings { WebUsagePaths = { ["claude"] = ClaudePath } };

        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Equal(2, runner.Asked);
    }

    [Fact]
    public async Task A_new_discovery_and_a_signed_out_answer_drop_what_is_held()
    {
        var clock = new TestClock(Start);
        var answers = new Queue<string>();
        var scripts = new List<string>();
        var source = new WebUsageSource(
            DescriptorFor("claude", "claude.ai"), new ClaudeUsageEndpoint(),
            (script, _) =>
            {
                scripts.Add(script);
                return Task.FromResult(answers.Dequeue());
            },
            timeProvider: clock);
        var settings = new AppSettings { WebUsagePaths = { ["claude"] = ClaudePath } };

        answers.Enqueue(Envelope(ClaudePath, ClaudeBody, plan: PlanText));
        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        answers.Enqueue("""{"status":"not_signed_in"}""");
        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        answers.Enqueue(Envelope(ClaudePath, ClaudeBody, plan: PlanText));
        await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        // The second read still held the first answer; the signed-out answer cleared it, so the third asks again.
        Assert.Contains("await readPlan()", scripts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("await readPlan()", scripts[1], StringComparison.Ordinal);
        Assert.Contains("await readPlan()", scripts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rediscovery_drops_what_is_held()
    {
        var answers = new Queue<string>();
        var scripts = new List<string>();
        var source = new WebUsageSource(
            DescriptorFor("claude", "claude.ai"), new ClaudeUsageEndpoint(),
            (script, _) =>
            {
                scripts.Add(script);
                return Task.FromResult(answers.Dequeue());
            });
        var settings = new AppSettings { WebUsagePaths = { ["claude"] = ClaudePath } };

        answers.Enqueue(Envelope(ClaudePath, ClaudeBody, plan: PlanText)); // read 1 asks and holds
        answers.Enqueue("""{"status":"failed"}"""); // read 2: the cached path stopped answering ...
        answers.Enqueue(Envelope(ClaudePath, ClaudeBody)); // ... the walk finds it again
        answers.Enqueue(Envelope(ClaudePath, ClaudeBody)); // read 3
        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.DoesNotContain("await readPlan()", scripts[1], StringComparison.Ordinal);
        Assert.Contains("await readPlan()", scripts[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codex_asks_for_the_email_once_and_keeps_showing_it()
    {
        const string codexPath = "/backend-api/codex/usage";
        var clock = new TestClock(Start);
        var runner = new FakeRunner(codexPath, CodexBody, "await readEmail(auth.headers)", _ => Envelope(codexPath, CodexBody, email: FakeEmail));
        var source = new WebUsageSource(DescriptorFor("codex", "chatgpt.com"), new CodexUsageEndpoint(), runner.Run, timeProvider: clock);
        var settings = new AppSettings { WebUsagePaths = { ["codex"] = codexPath } };

        WebUsageResult? last = null;
        for (var read = 0; read < 12; read++)
            last = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Equal(1, runner.Asked);
        Assert.Equal(FakeEmail, last!.AccountLabel);
    }

    [Fact]
    public async Task Cursor_asks_for_the_email_and_the_grok_bot_once_and_keeps_both()
    {
        const string cursorPath = "/api/usage-summary";
        var clock = new TestClock(Start);
        var scripts = new List<string>();
        var source = new WebUsageSource(
            DescriptorFor("cursor", "cursor.com"), new CursorUsageEndpoint(),
            (script, _) =>
            {
                scripts.Add(script);
                var asked = script.Contains("await mergeGrokBot(text)", StringComparison.Ordinal);
                return Task.FromResult(asked
                    ? Envelope(cursorPath, CursorBody, email: FakeEmail, grokBot: new { usagePercent = 12.5, nextResetTimestampUtc = "2026-10-12T20:35:33.260Z" })
                    : Envelope(cursorPath, CursorBody));
            },
            timeProvider: clock);
        var settings = new AppSettings { WebUsagePaths = { ["cursor"] = cursorPath } };

        WebUsageResult? last = null;
        for (var read = 0; read < 12; read++)
            last = await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.Equal(1, scripts.Count(script => script.Contains("await mergeGrokBot(text)", StringComparison.Ordinal)));
        Assert.Equal(1, scripts.Count(script => script.Contains("await readEmail()", StringComparison.Ordinal)));
        Assert.Equal(FakeEmail, last!.AccountLabel);
        // From the second read on the held bar goes into the body as a literal the app wrote itself.
        Assert.Contains("applyGrokBot(text, {\"usagePercent\":12.5,\"nextResetTimestampUtc\":\"2026-10-12T20:35:33.260Z\"})", scripts[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"usagePercent":"1);alert(1);//","nextResetTimestampUtc":"x"}""")]
    [InlineData("""{"usagePercent":1e999}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""{"nextResetTimestampUtc":"x"}""")]
    public async Task A_grok_bot_answer_of_the_wrong_shape_is_never_held(string grokBotJson)
    {
        const string cursorPath = "/api/usage-summary";
        var scripts = new List<string>();
        var grokBot = JsonSerializer.Deserialize<JsonElement>(grokBotJson);
        var source = new WebUsageSource(
            DescriptorFor("cursor", "cursor.com"), new CursorUsageEndpoint(),
            (script, _) =>
            {
                scripts.Add(script);
                return Task.FromResult(Envelope(cursorPath, CursorBody, grokBot: grokBot));
            });
        var settings = new AppSettings { WebUsagePaths = { ["cursor"] = cursorPath } };

        await source.FetchAsync(settings, _ => { }, CancellationToken.None);
        await source.FetchAsync(settings, _ => { }, CancellationToken.None);

        Assert.DoesNotContain("alert(1)", scripts[1], StringComparison.Ordinal);
        Assert.Contains("await mergeGrokBot(text)", scripts[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_without_extras_reads_exactly_as_before()
    {
        var path = GeminiDiscoveryScript.CandidatePaths[0];
        IWebUsageEndpoint endpoint = new GeminiUsageEndpoint();

        Assert.Equal(endpoint.Fetch(path), endpoint.Fetch(path, new CachedAccountExtras("plan", FakeEmail, null)));
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
