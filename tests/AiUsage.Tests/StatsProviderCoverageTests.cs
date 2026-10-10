using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>Fixture-based coverage for which providers the statistics window can ever show a real
/// number for versus the honest "no local data" line.</summary>
public class StatsProviderCoverageTests
{
    [Fact]
    public void Exactly_claude_codex_and_gemini_have_local_token_data()
    {
        Assert.True(ProviderCoverage.HasLocalTokenData("claude"));
        Assert.True(ProviderCoverage.HasLocalTokenData("codex"));
        Assert.True(ProviderCoverage.HasLocalTokenData("gemini"));
        Assert.False(ProviderCoverage.HasLocalTokenData("cursor"));
        Assert.False(ProviderCoverage.HasLocalTokenData("copilot"));

        Assert.Equal(["claude", "codex", "cursor", "gemini", "copilot"], ProviderCoverage.AllProviderIds);
    }
}
