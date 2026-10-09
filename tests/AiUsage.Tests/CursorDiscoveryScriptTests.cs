using AiUsage.Providers;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The Cursor read script runs inside a signed-in cursor.com session, so what it may ask for is
/// fenced the same way Claude's and Codex's are: a fixed candidate list, an allow-list built from
/// that list, and a direct fetch that refuses anything outside it.
/// </summary>
public class CursorDiscoveryScriptTests
{
    private const string AccountId = "user_2f1c8a904b3e4c1a9d770b6a5e3f21cc";

    [Theory]
    [InlineData($"/api/usage?user={AccountId}", true)]
    [InlineData("/api/usage-summary", true)]
    [InlineData("/api/dashboard/get-monthly-spending", false)]
    [InlineData("/api/dashboard/usage-summary", false)]
    [InlineData("/api/usage", true)]
    [InlineData("/api/chat", false)]
    [InlineData($"/api/usage?user={AccountId}');alert(1);//", false)]
    [InlineData("", false)]
    [InlineData("/api/auth/me", false)] // used only inside Discover(), never a cacheable usage path
    [InlineData("/api/usage?user=", false)] // an empty account id must not slip through
    public void IsAllowedUsagePath_accepts_only_the_apps_own_endpoints(string path, bool expected)
    {
        Assert.Equal(expected, CursorDiscoveryScript.IsAllowedUsagePath(path));
    }

    [Fact]
    public void IsAllowedUsagePath_never_drifts_away_from_CandidatePaths()
    {
        foreach (var candidate in CursorDiscoveryScript.CandidatePaths)
        {
            var resolved = candidate.Replace("{0}", AccountId);
            Assert.True(CursorDiscoveryScript.IsAllowedUsagePath(resolved), $"Expected {resolved} to be allowed.");
        }
    }

    [Fact]
    public void Fetch_throws_for_a_path_outside_the_allow_list()
    {
        Assert.Throws<ArgumentException>(() => CursorDiscoveryScript.Fetch("/api/chat"));
    }

    [Fact]
    public void Fetch_embeds_an_allowed_path_as_an_escaped_json_string_literal()
    {
        const string path = "/api/usage";

        var script = CursorDiscoveryScript.Fetch(path);

        Assert.DoesNotContain($"fetch('{path}'", script);
        Assert.Contains($"fetch({System.Text.Json.JsonSerializer.Serialize(path)}", script);
    }

    [Fact]
    public void Discover_reads_the_account_id_before_walking_every_candidate()
    {
        var script = CursorDiscoveryScript.Discover();

        Assert.Contains("/api/auth/me", script, StringComparison.Ordinal);
        Assert.Contains("sub", script, StringComparison.Ordinal);
        Assert.Contains("attempts", script, StringComparison.Ordinal);
        Assert.All(CursorDiscoveryScript.CandidatePaths, path => Assert.Contains(path, script, StringComparison.Ordinal));
    }

    [Fact]
    public void Neither_script_writes_anywhere_but_the_fixed_grok_bot_merge_call()
    {
        foreach (var script in new[] { CursorDiscoveryScript.Discover(), CursorDiscoveryScript.Fetch(CursorDiscoveryScript.CandidatePaths[2]) })
        {
            Assert.DoesNotContain("PUT", script, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE", script, StringComparison.Ordinal);
            // The one POST both scripts carry is the fixed Grok Bot merge call - a literal path,
            // never a candidate and never built from the discovered account id.
            var postCount = System.Text.RegularExpressions.Regex.Matches(script, "method: 'POST'").Count;
            Assert.Equal(1, postCount);
            Assert.Contains("'/api/dashboard/get-sand-usage-status'", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_grok_bot_merge_copies_only_its_own_two_fields()
    {
        foreach (var script in new[] { CursorDiscoveryScript.Discover(), CursorDiscoveryScript.Fetch(CursorDiscoveryScript.CandidatePaths[0]) })
        {
            Assert.Contains("s.grokBot = {usagePercent: g.usagePercent, nextResetTimestampUtc: g.nextResetTimestampUtc}", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Fetch_only_merges_the_grok_bot_read_for_the_usage_summary_path()
    {
        var summaryScript = CursorDiscoveryScript.Fetch("/api/usage-summary");
        var requestScript = CursorDiscoveryScript.Fetch(CursorDiscoveryScript.CandidatePaths[2]);

        Assert.Contains("body: await mergeGrokBot(text)", summaryScript, StringComparison.Ordinal);
        Assert.Contains("body: text", requestScript, StringComparison.Ordinal);
        Assert.DoesNotContain("mergeGrokBot(text)", requestScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_scripts_only_ever_read()
    {
        foreach (var script in new[] { CursorDiscoveryScript.Discover(), CursorDiscoveryScript.Fetch(CursorDiscoveryScript.CandidatePaths[2]) })
        {
            Assert.Contains("method: 'GET'", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Implements_the_web_usage_endpoint_contract_through_the_interface()
    {
        IWebUsageEndpoint endpoint = new CursorUsageEndpoint();

        Assert.Equal(CursorDiscoveryScript.Discover(), endpoint.Discover());
        Assert.True(endpoint.IsAllowedUsagePath(CursorDiscoveryScript.CandidatePaths[2]));
        Assert.Throws<ArgumentException>(() => endpoint.Fetch("/api/chat"));
    }

    // Both scripts already read /api/auth/me (Discover for the account id, Fetch as a
    // dedicated best-effort read) - both carry the signed-in email back on the envelope.
    [Fact]
    public void Discover_reports_the_signed_in_email_on_the_envelope()
    {
        var script = CursorDiscoveryScript.Discover();

        Assert.Contains("const email = (me && typeof me.email === 'string') ? me.email : null;", script, StringComparison.Ordinal);
        Assert.Contains("email: email", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Fetch_also_reads_the_signed_in_email_with_an_explicit_GET()
    {
        var script = CursorDiscoveryScript.Fetch(CursorDiscoveryScript.CandidatePaths[2]);

        Assert.Contains("fetch('/api/auth/me', {method: 'GET', credentials: 'include'})", script, StringComparison.Ordinal);
        Assert.Contains("email: email", script, StringComparison.Ordinal);
    }
}
