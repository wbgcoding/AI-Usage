using System.Text.RegularExpressions;

namespace AiUsage.Stats;

/// <summary>
/// Tells which model ids are a provider's own. A coding tool can be pointed at another vendor's API
/// (Claude Code against a third-party endpoint), so its usage history holds models the provider's quota
/// never paid for; the plan forecast leaves those out. A row without a model name stays, and so does
/// every row of a provider with no known family.
/// </summary>
public static class ProviderModelFilter
{
    /// <summary>OpenAI's reasoning series by id: o1, o3-mini, o4-mini-high.</summary>
    private static readonly Regex OpenAiReasoning = new(@"^o\d+(-|$)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True when <paramref name="model"/> belongs to the model family of <paramref name="providerId"/>.</summary>
    public static bool Belongs(string providerId, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return true;

        // A vendor prefix ("anthropic/claude-sonnet-5", "openai/gpt-5") is not part of the family.
        var name = model.Trim().ToLowerInvariant();
        name = name[(name.LastIndexOf('/') + 1)..];
        return providerId switch
        {
            "claude" => name.Contains("claude") || name.Contains("opus") || name.Contains("sonnet") || name.Contains("haiku"),
            "codex" => (name.Contains("gpt") && !name.Contains("gpt-oss")) || name.StartsWith("codex", StringComparison.Ordinal) || OpenAiReasoning.IsMatch(name),
            "gemini" => name.Contains("gemini"),
            _ => true,
        };
    }

    /// <summary>The rows of <paramref name="records"/> whose model is the provider's own.</summary>
    public static List<StatsRecord> Own(string providerId, IEnumerable<StatsRecord> records) =>
        [.. records.Where(record => Belongs(providerId, record.Model))];
}
