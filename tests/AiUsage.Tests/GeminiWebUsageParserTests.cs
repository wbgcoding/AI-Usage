using AiUsage.Models;
using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

/// <summary>
/// GeminiWebUsageParser reads the same groups/buckets/remainingFraction shape
/// <see cref="GeminiUsageParser"/> already reads from the Antigravity CLI's own sign-in (see that
/// class's own remarks on why) - these tests prove the same real-shaped body, reached through the
/// app's own browser session instead, still yields the two windows, and that an unrecognised shape
/// yields none rather than a guessed number.
/// </summary>
public class GeminiWebUsageParserTests
{
    private const string QuotaSummary = """
        {
          "groups": [
            {
              "displayName": "Gemini Models",
              "buckets": [
                { "window": "weekly", "resetTime": "2026-09-18T05:21:33Z", "remainingFraction": 0.9 },
                { "window": "5h", "resetTime": "2026-09-13T20:40:15Z", "remainingFraction": 1 }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void A_real_shaped_body_becomes_both_windows()
    {
        var windows = GeminiWebUsageParser.Parse(QuotaSummary);

        Assert.Equal(2, windows.Count);
        Assert.Equal(0, windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent);
        Assert.Equal(10, windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent, precision: 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"error":"unauthorized"}""")]
    public void An_unknown_shape_yields_no_window_instead_of_a_guess(string json)
    {
        Assert.Empty(GeminiWebUsageParser.Parse(json));
    }
}
