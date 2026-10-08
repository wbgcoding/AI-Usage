using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

public class ModelDisplayNamesTests
{
    [Theory]
    [InlineData("claude-opus-5", "Opus 5")]
    [InlineData("claude-opus-4-8", "Opus 4.8")]
    [InlineData("claude-opus-4-6-thinking", "Opus 4.6")]
    [InlineData("claude-sonnet-5", "Sonnet 5")]
    [InlineData("claude-haiku-4-5", "Haiku 4.5")]
    [InlineData("claude-fable-5", "Fable 5")]
    [InlineData("claude-fable-5-1", "Fable 5.1")]
    [InlineData("gpt-6-astra", "GPT-6 Astra")]
    [InlineData("gpt-5.6-sol", "GPT-5.6 Sol")]
    [InlineData("gpt-5.6-terra", "GPT-5.6 Terra")]
    [InlineData("gpt-5.6-luna", "GPT-5.6 Luna")]
    [InlineData("codex-auto-review", "Codex Review")]
    [InlineData("glm-5.2", "GLM 5.2")]
    [InlineData("minimax-m3", "MiniMax M3")]
    [InlineData("minimax-m2.7", "MiniMax M2.7")]
    [InlineData("kimi-k3", "Kimi K3")]
    [InlineData("kimi-k2.5", "Kimi K2.5")]
    [InlineData("kimi-k2.7-code", "Kimi K2.7 Code")]
    [InlineData("gemini-3.6-flash", "Gemini 3.6 Flash")]
    [InlineData("gemini-3.5-flash", "Gemini 3.5 Flash")]
    [InlineData("gemini-3.1-flash-lite", "Gemini 3.1 Flash Lite")]
    [InlineData("gemini-3-flash-preview", "Gemini 3 Flash")]
    [InlineData("gemini-2.5-flash", "Gemini 2.5 Flash")]
    [InlineData("gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite")]
    public void Resolve_maps_every_table_entry_to_its_short_name(string model, string expected) =>
        Assert.Equal(expected, ModelDisplayNames.Resolve(model));

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-sonnet-5-1-20260101", "Sonnet 5.1")]
    [InlineData("anthropic/claude-haiku-6", "Haiku 6")]
    [InlineData("claude-fable-6-2-thinking", "Fable 6.2")]
    public void Resolve_reads_new_claude_releases_from_their_id_scheme(string model, string expected) =>
        Assert.Equal(expected, ModelDisplayNames.Resolve(model));

    [Theory]
    [InlineData("claude-mystery-5")]
    [InlineData("claude-opus-5-5-extra")]
    public void Resolve_leaves_ids_outside_the_claude_scheme_unchanged(string model) =>
        Assert.Equal(model, ModelDisplayNames.Resolve(model));

    [Fact]
    public void Resolve_keeps_an_unknown_model_unchanged() =>
        Assert.Equal("nvidia/nemotron-3-ultra-550b-a55b:free", ModelDisplayNames.Resolve("nvidia/nemotron-3-ultra-550b-a55b:free"));

    [Fact]
    public void Resolve_strips_a_trailing_date_before_matching() =>
        Assert.Equal("Haiku 4.5", ModelDisplayNames.Resolve("claude-haiku-4-5-20251001"));

    [Fact]
    public void Resolve_keeps_an_empty_model_unchanged() =>
        Assert.Equal("", ModelDisplayNames.Resolve(""));
}
