using System.Text;
using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Providers.Parsing;
using AiUsage.ViewModels;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Every provider hands the subscription tier it already finds in an answer it fetched anyway to the
/// tile, which shows it after the provider name. The raw wording differs per provider; these tests
/// cover the read per source and the header text it ends up as.
/// </summary>
public class PlanTierReadTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T10:00:00Z");
    private const string FakeToken = "sk-ant-oat01-FAKE-TOKEN-MARKER-DO-NOT-LEAK";

    private static string Header(string providerId, string displayName, string? planType)
    {
        var tile = new ProviderTileViewModel(providerId, displayName);
        tile.Apply(new ProviderSnapshot(
            providerId, [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 10, null, null)], planType,
            SourceKind.LocalLogin, Now, Now, ProviderStatus.Ok, null), Now);
        return tile.PlanText ?? "";
    }

    // Claude Code: the sign-in file carries the tier next to the token.

    [Theory]
    [InlineData("""{"claudeAiOauth":{"accessToken":"__T__","subscriptionType":"max","rateLimitTier":"default_claude_max_5x"}}""", "Max 5x")]
    [InlineData("""{"claudeAiOauth":{"accessToken":"__T__","subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}}""", "Max 20x")]
    [InlineData("""{"claudeAiOauth":{"accessToken":"__T__","subscriptionType":"pro","rateLimitTier":"default_claude_ai"}}""", "Pro")]
    [InlineData("""{"claudeAiOauth":{"accessToken":"__T__","subscriptionType":"team"}}""", "Team")]
    public void The_claude_code_login_reads_the_tier_from_the_credentials_file(string json, string expectedHeader)
    {
        using var directory = TestPaths.CreateDisposableDirectory("plan-tier-claude-code");
        var path = Path.Combine(directory, "credentials.json");
        File.WriteAllText(path, json.Replace("__T__", FakeToken), new UTF8Encoding(false));

        var (token, _, plan) = ClaudeCodeLogin.ReadAccessToken(path);

        Assert.Equal(FakeToken, token);
        Assert.NotNull(plan);
        Assert.DoesNotContain(FakeToken, plan);
        Assert.Equal(expectedHeader, Header("claude", "Claude", plan));
    }

    [Fact]
    public void A_credentials_file_without_a_tier_gives_no_plan_text()
    {
        using var directory = TestPaths.CreateDisposableDirectory("plan-tier-claude-code-none");
        var path = Path.Combine(directory, "credentials.json");
        File.WriteAllText(path, "{\"claudeAiOauth\":{\"accessToken\":\"" + FakeToken + "\"}}", new UTF8Encoding(false));

        var (_, _, plan) = ClaudeCodeLogin.ReadAccessToken(path);

        Assert.Null(plan);
        Assert.Equal("", Header("claude", "Claude", plan));
    }

    [Fact]
    public async Task The_claude_provider_hands_the_local_logins_tier_to_its_snapshot()
    {
        var windows = new[] { new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42, null, null) };
        var provider = new ClaudeProvider(
            projectsRoot: Path.Combine(AppContext.BaseDirectory, "Fixtures", "does-not-exist"), now: () => Now,
            localLogin: _ => Task.FromResult(new ClaudeCodeUsage(LocalLoginOutcome.Ok, windows, ClaudeCodeLoginReason.Ok, "max default_claude_max_5x")));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("max default_claude_max_5x", snapshot.PlanType);
        Assert.Equal("Max 5x", Header("claude", "Claude", snapshot.PlanType));
    }

    // Claude web: the organisation record the discovery script reads carries the tier, handed over as
    // a plain "plan" string on the script's answer.

    private static readonly WebSessionDescriptor ClaudeDescriptor = new(
        ProviderId: "claude", BaseUrl: "https://claude.ai/",
        SignInUrl: "https://claude.ai/login", AllowedHosts: ["claude.ai"], ProfileFolderName: "claude");

    private const string OrgId = "2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc";

    [Fact]
    public void A_claude_web_answer_carries_the_organisations_tier()
    {
        var envelope = """{"status":"ok","path":"/api/organizations/__O__/usage","plan":"default_claude_max_20x claude_max chat","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}"""
            .Replace("__O__", OrgId);
        var source = new WebUsageSource(ClaudeDescriptor, new ClaudeUsageEndpoint(), (_, _) => Task.FromResult(envelope));

        var (result, _) = source.Interpret(envelope);

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Equal("default_claude_max_20x claude_max chat", result.PlanType);
        Assert.Equal("Max 20x", Header("claude", "Claude", result.PlanType));
    }

    [Fact]
    public void A_web_answer_without_a_plan_field_has_no_tier()
    {
        var envelope = """{"status":"ok","path":"/api/organizations/__O__/usage","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}"""
            .Replace("__O__", OrgId);
        var source = new WebUsageSource(ClaudeDescriptor, new ClaudeUsageEndpoint(), (_, _) => Task.FromResult(envelope));

        var (result, _) = source.Interpret(envelope);

        Assert.Null(result.PlanType);
    }

    [Fact]
    public void An_oversized_plan_field_is_dropped()
    {
        var envelope = """{"status":"ok","path":"/api/organizations/__O__/usage","plan":"__P__","body":"{\"five_hour\":{\"utilization\":42,\"resets_at\":1788624000}}"}"""
            .Replace("__O__", OrgId).Replace("__P__", new string('x', 500));
        var source = new WebUsageSource(ClaudeDescriptor, new ClaudeUsageEndpoint(), (_, _) => Task.FromResult(envelope));

        var (result, _) = source.Interpret(envelope);

        Assert.Null(result.PlanType);
    }

    // Codex: plan_type sits at the root of the usage answer the web session already reads.

    [Fact]
    public void The_codex_web_usage_answer_names_the_plan()
    {
        const string body = """
            {"plan_type":"pro","rate_limit":{"allowed":true,"limit_reached":false,
            "primary_window":{"used_percent":23,"limit_window_seconds":18000,"reset_after_seconds":1800},
            "secondary_window":{"used_percent":41,"limit_window_seconds":604800,"reset_after_seconds":90000}}}
            """;

        var result = new CodexUsageEndpoint(() => Now).ParseBody(body);

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Equal("pro", result.PlanType);
        Assert.Equal("Pro", Header("codex", "Codex", result.PlanType));
    }

    [Fact]
    public void A_codex_answer_without_plan_type_has_no_tier() =>
        Assert.Null(CodexWebUsageParser.ParsePlan("""{"primary":{"used_percent":7}}"""));

    // Cursor: membershipType at the root of the usage summary.

    [Fact]
    public void The_cursor_usage_summary_names_the_membership()
    {
        const string body = """
            {"billingCycleStart": "2026-09-05T10:00:00.000Z", "billingCycleEnd": "2026-10-05T10:00:00.000Z",
             "membershipType": "pro", "isUnlimited": false,
             "individualUsage": {"plan": {"enabled": true, "used": 1250, "limit": 2000, "remaining": 750,
                                          "totalPercentUsed": 62.5}}}
            """;

        Assert.Equal("pro", CursorUsageParser.ParsePlan(body));
        Assert.Equal("pro", new CursorUsageEndpoint().ParseBody(body).PlanType);
        Assert.Equal("Pro", Header("cursor", "Cursor", CursorUsageParser.ParsePlan(body)));
    }

    [Fact]
    public void A_cursor_answer_without_membership_has_no_tier() =>
        Assert.Null(CursorUsageParser.ParsePlan("""{"individualUsage":{}}"""));

    // Copilot: copilot_plan in the user answer the CLI read already returns.

    private const string CopilotJson = """
        {"login":"octocat","copilot_plan":"individual_pro","quota_reset_date_utc":"2026-10-01T00:00:00.000Z",
         "quota_snapshots":{"chat":{"percent_remaining":80.0,"unlimited":false,"has_quota":true}}}
        """;

    [Fact]
    public async Task The_copilot_provider_hands_the_users_plan_to_its_snapshot()
    {
        var provider = new CopilotProvider(_ => Task.FromResult(new CopilotFetch(GitHubCliOutcome.Ok, CopilotJson)), () => Now);

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("individual_pro", snapshot.PlanType);
        Assert.Equal("Pro", Header("copilot", "Copilot", snapshot.PlanType));
    }

    [Theory]
    [InlineData("business", "Business")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("free", "Free")]
    public void Other_copilot_plans_map_to_their_product_names(string plan, string expected) =>
        Assert.Equal(expected, Header("copilot", "Copilot", CopilotUserParser.ParsePlan("{\"copilot_plan\":\"" + plan + "\"}")));

    [Fact]
    public void A_copilot_answer_without_a_plan_has_no_tier() =>
        Assert.Null(CopilotUserParser.ParsePlan("""{"login":"octocat"}"""));

    // Gemini: the Antigravity read's plan name moves to the same display.

    [Theory]
    [InlineData("Pro", "Pro")]
    [InlineData("Ultra", "Ultra")]
    [InlineData("Google AI Pro", "Pro")]
    public void The_gemini_plan_name_shows_through_the_same_display(string planName, string expected) =>
        Assert.Equal(expected, Header("gemini", "Gemini", planName));
}
