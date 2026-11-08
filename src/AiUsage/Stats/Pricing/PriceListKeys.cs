using System.Text.RegularExpressions;

namespace AiUsage.Stats.Pricing;

/// <summary>What the two downloaded price tables (<see cref="ModelPriceList"/>,
/// <see cref="FallbackPriceList"/>) read the same way.</summary>
internal static class PriceListKeys
{
    /// <summary>A per-token price at or above this is not a real price (that would be 10 dollars per
    /// 1000 tokens) and the field is dropped.</summary>
    internal const double MaxTokenPrice = 0.01;

    internal static readonly ModelPrice FreePrice = new(0, 0, null, null);

    internal static readonly Regex DateSuffix = new(@"-(\d{8}|\d{4}-\d{2}-\d{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The path part just before the model name in a list key ("minimax" in
    /// "minimax/minimax-m3"); empty when the key has no path.</summary>
    internal static string MakerOf(string key)
    {
        var slash = key.LastIndexOf('/');
        if (slash < 0)
            return "";
        var before = key[..slash];
        return before[(before.LastIndexOf('/') + 1)..];
    }
}
