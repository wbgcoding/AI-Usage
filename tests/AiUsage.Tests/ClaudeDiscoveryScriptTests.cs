using AiUsage.Providers;
using Xunit;

namespace AiUsage.Tests;

public class ClaudeDiscoveryScriptTests
{
    private const string OrgId = "2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc";

    [Theory]
    [InlineData($"/api/organizations/{OrgId}/usage", true)]
    [InlineData($"/api/organizations/{OrgId}/usage_limits", true)]
    [InlineData($"/api/organizations/{OrgId}/rate_limits", true)]
    [InlineData($"/api/organizations/{OrgId}/usage');alert(1);//", false)]
    [InlineData($"/api/organizations/{OrgId}/usage/extra", false)]
    [InlineData("", false)]
    [InlineData("/api/organizations/2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21c/usage", false)] // 35 characters, one short
    [InlineData($"/api/organizations/{OrgId}/completion", false)] // well-formed but unknown endpoint
    public void IsAllowedUsagePath_accepts_only_the_apps_own_three_endpoints(string path, bool expected)
    {
        Assert.Equal(expected, ClaudeDiscoveryScript.IsAllowedUsagePath(path));
    }

    [Fact]
    public void IsAllowedUsagePath_never_drifts_away_from_CandidatePaths()
    {
        foreach (var candidate in ClaudeDiscoveryScript.CandidatePaths)
        {
            var resolved = candidate.Replace("{0}", OrgId);
            Assert.True(ClaudeDiscoveryScript.IsAllowedUsagePath(resolved), $"Expected {resolved} to be allowed.");
        }
    }

    [Fact]
    public void Fetch_throws_for_a_path_outside_the_allow_list()
    {
        Assert.Throws<ArgumentException>(() => ClaudeDiscoveryScript.Fetch("/api/x'+alert(1)+'"));
    }

    [Fact]
    public void Fetch_embeds_an_allowed_path_as_an_escaped_json_string_literal()
    {
        var path = $"/api/organizations/{OrgId}/usage";

        var script = ClaudeDiscoveryScript.Fetch(path);

        Assert.DoesNotContain($"fetch('{path}'", script);
        Assert.Contains($"fetch({System.Text.Json.JsonSerializer.Serialize(path)}", script);
    }

    [Fact]
    public void Discover_requires_a_successful_response_and_tries_the_next_candidate_otherwise()
    {
        // An error response (e.g. a 404) is usually JSON too, so the content-type check alone must
        // not be enough to accept a candidate - this is a thin assertion on the script text itself,
        // the real proof is a manual acceptance run against a live account.
        var script = ClaudeDiscoveryScript.Discover();

        Assert.Contains("res.ok", script);
        Assert.Contains("continue", script);
    }

    [Fact]
    public void Fetch_requires_a_successful_response_not_just_a_json_content_type()
    {
        var path = $"/api/organizations/{OrgId}/usage";

        var script = ClaudeDiscoveryScript.Fetch(path);

        Assert.Contains("res.ok", script);
    }
}
