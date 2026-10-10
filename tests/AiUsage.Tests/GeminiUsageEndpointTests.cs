using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

/// <summary>
/// The Gemini web endpoint reads the same groups/buckets/remainingFraction shape as
/// <see cref="GeminiUsageParser"/>: a real-shaped body yields both windows, an unrecognised shape
/// fails instead of showing a guessed number.
/// </summary>
public class GeminiUsageEndpointTests
{
    private static readonly GeminiUsageEndpoint Endpoint = new();

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
        var result = Endpoint.ParseBody(QuotaSummary);
        var windows = result.Windows;

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
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
        var result = Endpoint.ParseBody(json);

        Assert.Equal(WebUsageOutcome.Failed, result.Outcome);
        Assert.Empty(result.Windows);
    }
}
