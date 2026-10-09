namespace AiUsage.Stats;

/// <summary>
/// Short, human display names for the raw model identifiers that show up in the token usage index
/// (e.g. <c>claude-sonnet-5</c>, <c>gpt-5.6-sol</c>). A fixed lookup table plus Claude's fixed id
/// scheme, never a guess: a model that matches neither keeps its original, unmodified name.
/// </summary>
public static class ModelDisplayNames
{
    private static readonly Dictionary<string, string> Table = new()
    {
        ["claude-opus-5"] = "Opus 5",
        ["claude-opus-4-8"] = "Opus 4.8",
        ["claude-opus-4-6-thinking"] = "Opus 4.6",
        ["claude-sonnet-5"] = "Sonnet 5",
        ["claude-haiku-4-5"] = "Haiku 4.5",
        ["claude-fable-5"] = "Fable 5",
        ["claude-fable-5-1"] = "Fable 5.1",
        ["gpt-6-astra"] = "GPT-6 Astra",
        ["gpt-5.6-sol"] = "GPT-5.6 Sol",
        ["gpt-5.6-terra"] = "GPT-5.6 Terra",
        ["gpt-5.6-luna"] = "GPT-5.6 Luna",
        ["gpt-5-codex"] = "GPT-5 Codex",
        ["codex-auto-review"] = "Codex Review",
        ["glm-5.2"] = "GLM 5.2",
        ["minimax-m3"] = "MiniMax M3",
        ["minimax-m2.7"] = "MiniMax M2.7",
        ["kimi-k3"] = "Kimi K3",
        ["kimi-k2.5"] = "Kimi K2.5",
        ["kimi-k2.7-code"] = "Kimi K2.7 Code",
        ["gemini-3.6-flash"] = "Gemini 3.6 Flash",
        ["gemini-3.5-flash"] = "Gemini 3.5 Flash",
        ["gemini-3.1-flash-lite"] = "Gemini 3.1 Flash Lite",
        ["gemini-3-flash-preview"] = "Gemini 3 Flash",
        ["gemini-2.5-flash"] = "Gemini 2.5 Flash",
        ["gemini-2.5-flash-lite"] = "Gemini 2.5 Flash Lite",
    };

    /// <summary>Looks a raw model identifier up after normalizing it (provider prefix and
    /// <c>:free</c> suffix stripped, a trailing eight-digit date dropped, lowercased). A model that
    /// still does not match any table entry keeps its original name exactly as given - no derived or
    /// guessed shortening.</summary>
    public static string Resolve(string model)
    {
        if (string.IsNullOrEmpty(model))
            return model;

        var normalized = Normalize(model);
        if (Table.TryGetValue(normalized, out var displayName))
            return displayName;

        return ClaudeFamilyName(normalized) ?? model;
    }

    /// <summary>Claude model ids follow one fixed scheme, <c>claude-&lt;family&gt;-&lt;major&gt;[-&lt;minor&gt;][-thinking]</c>,
    /// so a new release (e.g. <c>claude-opus-5-5</c>) reads right without a table entry. Only the
    /// known families are accepted; anything else stays unmatched.</summary>
    private static string? ClaudeFamilyName(string normalized)
    {
        var match = ClaudeIdPattern.Match(normalized);
        if (!match.Success)
            return null;

        var family = match.Groups["family"].Value;
        var version = match.Groups["minor"].Success
            ? $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}"
            : match.Groups["major"].Value;
        return $"{char.ToUpperInvariant(family[0])}{family[1..]} {version}";
    }

    private static readonly System.Text.RegularExpressions.Regex ClaudeIdPattern = new(
        @"^claude-(?<family>opus|sonnet|haiku|fable)-(?<major>\d{1,2})(?:-(?<minor>\d{1,2}))?(?:-thinking)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string Normalize(string model)
    {
        var lastSlash = model.LastIndexOf('/');
        var afterPrefix = lastSlash >= 0 ? model[(lastSlash + 1)..] : model;

        const string freeSuffix = ":free";
        var withoutFreeSuffix = afterPrefix.EndsWith(freeSuffix, StringComparison.Ordinal)
            ? afterPrefix[..^freeSuffix.Length]
            : afterPrefix;

        return StripTrailingDate(withoutFreeSuffix).ToLowerInvariant();
    }

    /// <summary>Drops an appended eight-digit date (e.g. <c>-20251001</c>) and the separating hyphen
    /// right before it, if any - a run of any other length is left alone, since it is not a date.</summary>
    private static string StripTrailingDate(string value)
    {
        var digitsStart = value.Length;
        while (digitsStart > 0 && value[digitsStart - 1] is >= '0' and <= '9')
            digitsStart--;

        if (value.Length - digitsStart != 8)
            return value;

        var withoutDate = value[..digitsStart];
        return withoutDate.EndsWith('-') ? withoutDate[..^1] : withoutDate;
    }
}
