using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class PlanTierTests
{
    [Theory]
    [InlineData("max", "Max")]
    [InlineData("MAX", "Max")]
    [InlineData("max_5x", "Max 5x")]
    [InlineData("max_20x", "Max 20x")]
    [InlineData("max default_claude_max_5x", "Max 5x")]
    [InlineData("max default_claude_max_20x", "Max 20x")]
    [InlineData("default_claude_max_5x", "Max 5x")]
    [InlineData("pro", "Pro")]
    [InlineData("claude_pro chat", "Pro")]
    [InlineData("plus", "Plus")]
    [InlineData("Plus", "Plus")]
    [InlineData("team", "Team")]
    [InlineData("free", "Free")]
    [InlineData("business", "Business")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("individual", "Pro")]
    [InlineData("individual_pro", "Pro")]
    [InlineData("pro_plus", "Pro+")]
    [InlineData("Google AI Pro", "Pro")]
    [InlineData("Google AI Ultra", "Ultra")]
    public void A_known_tier_maps_to_its_product_name(string raw, string expected) =>
        Assert.Equal(expected, PlanTier.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("default_claude_ai")]
    [InlineData("mystery-tier")]
    [InlineData("unknown")]
    public void An_unknown_tier_shows_nothing(string? raw) =>
        Assert.Null(PlanTier.Normalize(raw));
}
