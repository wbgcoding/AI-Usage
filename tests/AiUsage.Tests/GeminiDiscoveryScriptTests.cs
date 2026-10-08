using AiUsage.Providers;

namespace AiUsage.Tests;

/// <summary>
/// The Gemini read script runs inside a signed-in aistudio.google.com session, so what it may ask
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
    [InlineData("/api/usage/../generateContent")]
    [InlineData("https://elsewhere.test/api/usage")]
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

    [Fact]
    public void Both_scripts_only_ever_read()
    {
        foreach (var script in new[] { GeminiDiscoveryScript.Discover(), GeminiDiscoveryScript.Fetch(GeminiDiscoveryScript.CandidatePaths[0]) })
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
        var script = GeminiDiscoveryScript.Discover();

        Assert.Contains("attempts", script, StringComparison.Ordinal);
        Assert.All(GeminiDiscoveryScript.CandidatePaths, path => Assert.Contains(path, script, StringComparison.Ordinal));
    }
}
