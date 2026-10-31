using AiUsage.Stats;

namespace AiUsage.Tests;

public class ProviderModelFilterTests
{
    [Theory]
    [InlineData("claude", "claude-opus-4-8")]
    [InlineData("claude", "Claude-Sonnet-5")]
    [InlineData("claude", "anthropic/claude-haiku-4-5")]
    [InlineData("claude", "opus-4-8")]
    [InlineData("claude", "sonnet")]
    [InlineData("codex", "gpt-5.5")]
    [InlineData("codex", "gpt-5-codex")]
    [InlineData("codex", "codex-mini-latest")]
    [InlineData("codex", "openai/gpt-5")]
    [InlineData("codex", "o3")]
    [InlineData("codex", "o4-mini-high")]
    [InlineData("codex", "o1")]
    [InlineData("gemini", "gemini-3-pro")]
    [InlineData("gemini", "models/gemini-3.1-flash-lite")]
    [InlineData("gemini", "google/gemini-3-flash")]
    public void A_provider_keeps_its_own_family(string provider, string model) =>
        Assert.True(ProviderModelFilter.Belongs(provider, model));

    [Theory]
    [InlineData("claude", "minimax-m3")]
    [InlineData("claude", "big-pickle")]
    [InlineData("claude", "opencode-zen/big-pickle")]
    [InlineData("claude", "inclusionai/ling-3.0-flash-vl:free")]
    [InlineData("claude", "nemotron-3-ultra-free")]
    [InlineData("claude", "gemini-3.1-flash-lite")]
    [InlineData("claude", "gpt-5")]
    [InlineData("codex", "claude-opus-5-5")]
    [InlineData("codex", "gemini-3-pro")]
    [InlineData("codex", "gpt-oss-120b")]
    [InlineData("codex", "oracle-x")]
    [InlineData("gemini", "claude-opus-5-5-medium")]
    [InlineData("gemini", "gpt-oss-120b-medium")]
    public void Another_vendors_models_are_not_the_providers(string provider, string model) =>
        Assert.False(ProviderModelFilter.Belongs(provider, model));

    [Theory]
    [InlineData("claude", "")]
    [InlineData("codex", "  ")]
    [InlineData("gemini", "")]
    public void A_row_without_a_model_name_stays(string provider, string model) =>
        Assert.True(ProviderModelFilter.Belongs(provider, model));

    [Theory]
    [InlineData("copilot", "whatever-model")]
    [InlineData("", "minimax-m3")]
    public void A_provider_without_a_known_family_keeps_everything(string provider, string model) =>
        Assert.True(ProviderModelFilter.Belongs(provider, model));

    [Fact]
    public void Own_drops_only_the_foreign_rows()
    {
        var day = new DateOnly(2026, 10, 1);
        var records = new[]
        {
            new StatsRecord("claude", day, "claude-opus-4-8", "p", 1, 1, 0, 0, 1),
            new StatsRecord("claude", day, "minimax-m3", "p", 5, 5, 0, 0, 1),
            new StatsRecord("claude", day, "", "p", 2, 2, 0, 0, 2),
        };

        var own = ProviderModelFilter.Own("claude", records);

        Assert.Equal(["claude-opus-4-8", ""], own.Select(record => record.Model));
    }
}
