using AiUsage.Providers;

namespace AiUsage.Tests;

/// <summary>
/// The Codex read script runs inside a signed-in chatgpt.com session, so what it may ask for is
/// fenced the same way Claude's is: a fixed candidate list, an allow-list built from that list, and
/// a direct fetch that refuses anything outside it.
/// </summary>
public class CodexDiscoveryScriptTests
{
    [Fact]
    public void Every_candidate_path_passes_its_own_allow_list()
    {
        Assert.All(CodexDiscoveryScript.CandidatePaths, path => Assert.True(CodexDiscoveryScript.IsAllowedUsagePath(path)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/backend-api/conversation")]
    [InlineData("/backend-api/codex/usage/../conversation")]
    [InlineData("https://elsewhere.test/backend-api/codex/usage")]
    [InlineData("/backend-api/codex/usage?x=1")]
    public void A_path_outside_the_candidate_list_is_refused(string? path)
    {
        Assert.False(CodexDiscoveryScript.IsAllowedUsagePath(path));
    }

    [Fact]
    public void Fetch_refuses_a_path_outside_the_allow_list_instead_of_running_it()
    {
        Assert.Throws<ArgumentException>(() => CodexDiscoveryScript.Fetch("/backend-api/conversation"));
    }

    [Fact]
    public void Fetch_runs_an_allowed_path_and_names_it_in_the_script()
    {
        var script = CodexDiscoveryScript.Fetch(CodexDiscoveryScript.CandidatePaths[0]);

        Assert.Contains(CodexDiscoveryScript.CandidatePaths[0], script, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_scripts_only_ever_read()
    {
        foreach (var script in new[] { CodexDiscoveryScript.Discover(), CodexDiscoveryScript.Fetch(CodexDiscoveryScript.CandidatePaths[0]) })
        {
            Assert.Contains("method: 'GET'", script, StringComparison.Ordinal);
            Assert.DoesNotContain("POST", script, StringComparison.Ordinal);
            Assert.DoesNotContain("PUT", script, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE", script, StringComparison.Ordinal);
        }
    }

    /// <summary>The whole point of the candidate walk is that a run against the live site says which
    /// address answered - without the attempts line that answer would only live in the browser's own
    /// devtools, where the app can never see it.</summary>
    [Fact]
    public void Discovery_reports_every_attempt_it_made()
    {
        var script = CodexDiscoveryScript.Discover();

        Assert.Contains("attempts", script, StringComparison.Ordinal);
        Assert.All(CodexDiscoveryScript.CandidatePaths, path => Assert.Contains(path, script, StringComparison.Ordinal));
    }
    /// <summary>Cookies alone answer every one of these addresses with 401 - the site's own web app
    /// puts a short-lived key from its session route on the request instead. Without it the tile said
    /// "failed" for an account that was plainly signed in, and the log filled with 401s.</summary>
    [Fact]
    public void BothScriptsAuthenticateTheirRequestsTheWayTheSitesOwnPageDoes()
    {
        foreach (var script in new[] { CodexDiscoveryScript.Discover(), CodexDiscoveryScript.Fetch("/backend-api/codex/usage") })
        {
            Assert.Contains("/api/auth/session", script, StringComparison.Ordinal);
            Assert.Contains("Authorization", script, StringComparison.Ordinal);
            Assert.Contains("headers: auth.headers", script, StringComparison.Ordinal);
        }
    }

    /// <summary>A session route that answers without an account is a signed-out session, not a
    /// broken read - saying so is what puts a sign-in button on the tile instead of a bare failure.</summary>
    [Fact]
    public void ASessionWithoutAnAccountIsReportedAsNotSignedIn()
    {
        var script = CodexDiscoveryScript.Discover();

        Assert.Contains("signedOut: !session || !session.user", script, StringComparison.Ordinal);
        Assert.Contains("if (auth.signedOut) return {status: 'not_signed_in'", script, StringComparison.Ordinal);
    }

    // Both scripts read /backend-api/me for the signed-in email alongside the real usage
    // read - never a usage endpoint of its own, never a candidate path.
    [Fact]
    public void Both_scripts_read_the_account_endpoint_for_an_email_with_an_explicit_GET()
    {
        foreach (var script in new[] { CodexDiscoveryScript.Discover(), CodexDiscoveryScript.Fetch("/backend-api/codex/usage") })
        {
            Assert.Contains("fetch('/backend-api/me', {method: 'GET', credentials: 'include', headers: headers})", script, StringComparison.Ordinal);
            Assert.Contains("email: email", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_account_endpoint_is_not_one_of_the_cacheable_usage_candidates()
    {
        Assert.False(CodexDiscoveryScript.IsAllowedUsagePath("/backend-api/me"));
    }
}
