using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiUsage.Stats.Pricing;

/// <summary>What the two downloaded price tables (<see cref="ModelPriceList"/>,
/// <see cref="FallbackPriceList"/>) read the same way.</summary>
internal static class PriceListKeys
{
    /// <summary>A per-token price at or above this is not a real price (that would be 10 dollars per
    /// 1000 tokens) and the field is dropped.</summary>
    internal const double MaxTokenPrice = 0.01;

    /// <summary>The trimmed cache is far smaller than the download; a bigger file is not ours.</summary>
    internal const long MaxCacheBytes = 16L * 1024 * 1024;

    internal static readonly ModelPrice FreePrice = new(0, 0, null, null);

    internal static readonly Regex DateSuffix = new(@"-(\d{8}|\d{4}-\d{2}-\d{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A per-token price field of a price table entry: a finite number from zero up to
    /// <see cref="MaxTokenPrice"/>, else null.</summary>
    internal static double? PriceField(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number))
            return null;
        return double.IsFinite(number) && number >= 0 && number < MaxTokenPrice ? number : null;
    }

    /// <summary>Writes a cache file whole or not at all: the bytes go to a temporary file that then
    /// replaces the cache file.</summary>
    internal static void WriteCache(string directory, string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

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
