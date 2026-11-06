namespace AiUsage.Services;

/// <summary>
/// Turns whatever plan wording a provider's own response carries ("max", "default_claude_max_5x",
/// "plus", "individual_pro", "Google AI Pro", ...) into the one short product name the tile shows
/// after the provider name. A value that names no known tier gives null: nothing is shown rather
/// than a raw or guessed label.
/// </summary>
public static class PlanTier
{
    // Checked in this order, so a response that carries two words ("team" and "pro") shows the
    // broader one.
    private static readonly (string Token, string Name)[] Known =
    [
        ("enterprise", "Enterprise"),
        ("team", "Team"),
        ("business", "Business"),
        ("ultra", "Ultra"),
        ("pro", "Pro"),
        ("plus", "Plus"),
        ("individual", "Pro"),
        ("free", "Free"),
    ];

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var tokens = Tokenize(raw);

        // Anthropic renamed its plans: the old "Max 5x" step is now "Max", "Max 20x" is "Ultra".
        // The tier strings in its responses still carry the old step names.
        if (tokens.Contains("max20x"))
            return "Ultra";
        if (tokens.Contains("max5x"))
            return "Max";

        var maxAt = tokens.IndexOf("max");
        if (maxAt >= 0)
        {
            for (var i = maxAt; i + 1 < tokens.Count; i++)
            {
                if (tokens[i] == "max" && tokens[i + 1] == "20x")
                    return "Ultra";
            }
            return "Max";
        }

        if (tokens.Contains("pro") && tokens.Contains("plus"))
            return "Pro+";

        foreach (var (token, name) in Known)
        {
            if (tokens.Contains(token))
                return name;
        }

        return null;
    }

    private static List<string> Tokenize(string raw)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }
}
