using AiUsage.Providers;

namespace AiUsage.Tests;

/// <summary>
/// The Gemini read script runs inside a signed-in gemini.google.com session, so what it may ask
/// for is fenced the same way Codex's is: a fixed candidate list, an allow-list built from that
/// list, and a direct fetch that refuses anything outside it.
/// </summary>
public class GeminiDiscoveryScriptTests
{
    [Fact]
    public void Every_candidate_path_passes_its_own_allow_list()
    {
        Assert.All(GeminiDiscoveryScript.CandidatePaths, path => Assert.True(GeminiDiscoveryScript.IsAllowedUsagePath(path)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/api/generateContent")]
    [InlineData("/_/BardChatUi/data/batchexecute?rpcids=OTHER")]
    [InlineData("/_/BardChatUi/data/batchexecute?rpcids=jSf9Qc&x=1")]
    [InlineData("https://elsewhere.test/_/BardChatUi/data/batchexecute?rpcids=jSf9Qc")]
    [InlineData("/api/usage?x=1")]
    public void A_path_outside_the_candidate_list_is_refused(string? path)
    {
        Assert.False(GeminiDiscoveryScript.IsAllowedUsagePath(path));
    }

    [Fact]
    public void Fetch_refuses_a_path_outside_the_allow_list_instead_of_running_it()
    {
        Assert.Throws<ArgumentException>(() => GeminiDiscoveryScript.Fetch("/api/generateContent"));
    }

    [Fact]
    public void Fetch_runs_an_allowed_path_and_names_it_in_the_script()
    {
        var script = GeminiDiscoveryScript.Fetch(GeminiDiscoveryScript.CandidatePaths[0]);

        Assert.Contains(GeminiDiscoveryScript.CandidatePaths[0], script, StringComparison.Ordinal);
    }

    /// <summary>The usage RPC answers only on POST, so the scripts post, but only the one fixed request:
    /// the rpc id and the request literal are constants, the body holds no other page data than the
    /// page's own anti-forgery token, and no other verb appears.</summary>
    [Fact]
    public void Both_scripts_post_only_the_fixed_usage_request()
    {
        foreach (var script in new[] { GeminiDiscoveryScript.Discover(), GeminiDiscoveryScript.Fetch(GeminiDiscoveryScript.CandidatePaths[0]) })
        {
            Assert.Contains("method: 'POST'", script, StringComparison.Ordinal);
            Assert.Contains("""[[["jSf9Qc","[]",null,"generic"]]]""", script, StringComparison.Ordinal);
            Assert.Contains("/_/BardChatUi/data/batchexecute?rpcids=jSf9Qc", script, StringComparison.Ordinal);
            Assert.Contains("WIZ_global_data", script, StringComparison.Ordinal);
            Assert.DoesNotContain("PUT", script, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE", script, StringComparison.Ordinal);
            Assert.DoesNotContain("PATCH", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_page_without_an_anti_forgery_token_answers_not_signed_in_without_a_request()
    {
        var script = GeminiDiscoveryScript.Discover();
        var tokenCheck = script.IndexOf("SNlM0e", StringComparison.Ordinal);
        var firstFetch = script.IndexOf("fetch(url", StringComparison.Ordinal);

        Assert.True(tokenCheck >= 0 && tokenCheck < firstFetch, "the token check has to come before the usage request");
    }

    /// <summary>The whole point of the candidate walk is that a run against the live site says which
    /// address answered - without the attempts line that answer would only live in the browser's own
    /// devtools, where the app can never see it.</summary>
    [Fact]
    public void Discovery_reports_every_attempt_it_made()
    {
        var script = GeminiDiscoveryScript.Discover();

        Assert.Contains("attempts", script, StringComparison.Ordinal);
        Assert.All(GeminiDiscoveryScript.CandidatePaths, path => Assert.Contains(path, script, StringComparison.Ordinal));
    }

    /// <summary>A signed-out session is redirected to the Google sign-in page, which the browser will not
    /// hand to the page: the fetch throws. That has to read as "signed out", not as a plain failure.</summary>
    [Fact]
    public void A_thrown_fetch_or_a_redirect_to_another_host_answers_not_signed_in()
    {
        foreach (var script in new[] { GeminiDiscoveryScript.Discover(), GeminiDiscoveryScript.Fetch(GeminiDiscoveryScript.CandidatePaths[0]) })
        {
            Assert.Contains("catch (e)", script, StringComparison.Ordinal);
            Assert.Contains("opaqueredirect", script, StringComparison.Ordinal);
            Assert.Contains("res.redirected", script, StringComparison.Ordinal);
            Assert.Contains("not_signed_in", script, StringComparison.Ordinal);
        }
    }

    /// <summary>A plain network failure throws the same error as the sign-in redirect. It must read as a
    /// failure: offline, or when the site's own front page does not answer either.</summary>
    [Fact]
    public void A_network_failure_answers_failed_not_signed_out()
    {
        foreach (var script in new[] { GeminiDiscoveryScript.Discover(), GeminiDiscoveryScript.Fetch(GeminiDiscoveryScript.CandidatePaths[0]) })
        {
            Assert.Contains("navigator.onLine === false", script, StringComparison.Ordinal);
            Assert.Contains("redirect: 'manual'", script, StringComparison.Ordinal);
            Assert.Contains("location.origin + '/'", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_thrown_fetch_while_offline_is_not_counted_as_a_sign_in_failure_in_the_discovery()
    {
        var script = GeminiDiscoveryScript.Discover();
        var offline = script.IndexOf("navigator.onLine === false", StringComparison.Ordinal);
        var firstAuthFlag = script.IndexOf("sawAuthFailure = true", StringComparison.Ordinal);

        Assert.True(offline >= 0 && offline < firstAuthFlag, "the offline check has to come before the first sign-in flag");
    }
}
