using System.Windows;
using System.Windows.Media;

namespace AiUsage.Services;

/// <summary>
/// Puts a CJK font into a theme's font families. Japanese and Simplified Chinese share code points
/// but need different glyph forms, so the font follows the UI language, never one static list.
/// </summary>
public static class CjkFontFallback
{
    private const string BaseKey = "AiUsage.CjkFontFallback.Base.";
    private static readonly string[] FontKeys = ["Font.Ui", "Font.Numeric"];

    /// <summary>The family string with the CJK fonts for <paramref name="language"/> inserted before
    /// the last generic fallback; any other language returns it unchanged.</summary>
    public static string Apply(string family, string language)
    {
        var mono = family.Contains("Consolas", StringComparison.OrdinalIgnoreCase);
        string[] added = language switch
        {
            "ja" => mono ? ["MS Gothic"] : ["Yu Gothic UI", "Meiryo UI"],
            "zh-Hans" => mono ? ["Microsoft YaHei UI"] : ["Microsoft YaHei UI", "Microsoft YaHei"],
            _ => [],
        };
        if (added.Length == 0)
            return family;

        var parts = family.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0)
            return family;

        parts.InsertRange(parts.Count > 1 ? parts.Count - 1 : parts.Count, added);
        return string.Join(", ", parts);
    }

    /// <summary>Rewrites <c>Font.Ui</c> and <c>Font.Numeric</c> in a theme dictionary for the
    /// language. The theme's own family is remembered, so a repeat call never stacks fonts.</summary>
    public static void ApplyTo(ResourceDictionary theme, string language)
    {
        foreach (var key in FontKeys)
        {
            var baseKey = BaseKey + key;
            if (!theme.Contains(baseKey))
            {
                if (theme[key] is not FontFamily current)
                    continue;
                theme[baseKey] = current.Source;
            }

            var family = Apply((string)theme[baseKey], language);
            theme[key] = new FontFamily(family);
        }
    }
}
