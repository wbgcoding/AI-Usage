using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

/// <summary>
/// The Gemini web endpoint reads the usage payload of the Gemini app (<see cref="GeminiAppUsageParser"/>):
/// a real-shaped body yields both windows, an unrecognised shape fails instead of showing a guessed
/// number.
/// </summary>
public class GeminiUsageEndpointTests
{
    private static readonly GeminiUsageEndpoint Endpoint = new();

    private const string QuotaSummary =
        "[2,[[2181,0.09,1,[[1792246090,287070000]]],[45607,0.05737659,2,[[1792652890,287184000]]]],false]";

    [Fact]
    public void A_real_shaped_body_becomes_both_windows()
    {
        var result = Endpoint.ParseBody(QuotaSummary);
        var windows = result.Windows;

        Assert.Equal(WebUsageOutcome.Ok, result.Outcome);
        Assert.Equal(2, windows.Count);
        Assert.Equal(9, windows.Single(w => w.Kind == WindowKind.FiveHour).UsedPercent, precision: 3);
        Assert.Equal(5.737659, windows.Single(w => w.Kind == WindowKind.Weekly).UsedPercent, precision: 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[2,[],false]")]
    [InlineData("""{"error":"unauthorized"}""")]
    public void An_unknown_shape_yields_no_window_instead_of_a_guess(string json)
    {
        var result = Endpoint.ParseBody(json);

        Assert.Equal(WebUsageOutcome.Failed, result.Outcome);
        Assert.Empty(result.Windows);
    }
}
