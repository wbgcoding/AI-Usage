namespace AiUsage.Stats;

/// <summary>One plan's monthly list price: US dollars, euros as a German customer pays them (with 19 %
/// VAT; null when no euro price is published), where the figures come from and when they were checked.</summary>
public sealed record PlanPrice(string Plan, double Usd, double? Eur, string Source, DateOnly CheckedOn);

/// <summary>
/// The monthly list prices of the plans the plan forecast compares against. Public list prices only;
/// other countries' VAT, regional prices, yearly billing and promotions differ, so the ratio is a guide,
/// not a bill. The figures are checked by hand against the sources and <see cref="PlanPrice.CheckedOn"/>
/// moves with every check.
/// </summary>
public static class PlanPriceList
{
    private const string ClaudeSource = "claude.com/pricing";
    private const string ChatGptSource = "chatgpt.com/pricing, vodafone.de";
    private const string GoogleSource = "one.google.com, gptprompts.ai";
    private static readonly DateOnly Checked = new(2026, 10, 31);

    private static readonly Dictionary<(string Provider, string Plan), PlanPrice> Prices = new()
    {
        // Anthropic lists 18, 90 and 180 euros before VAT.
        [("claude", "pro")] = new("Pro", 20, 21.42, ClaudeSource, Checked),
        [("claude", "max 5x")] = new("Max 5x", 100, 107.10, ClaudeSource, Checked),
        [("claude", "max 20x")] = new("Max 20x", 200, 214.20, ClaudeSource, Checked),
        [("codex", "plus")] = new("Plus", 20, 23, ChatGptSource, Checked),
        [("codex", "pro")] = new("Pro", 200, 229, ChatGptSource, Checked),
        // Per seat and month; no euro list price is published.
        [("codex", "business")] = new("Business", 30, null, ChatGptSource, Checked),
        [("codex", "team")] = new("Team", 30, null, ChatGptSource, Checked),
        [("gemini", "pro")] = new("Pro", 19.99, 21.99, GoogleSource, Checked),
    };

    /// <summary>The price of a plan name (any case; <c>PlanTier.Normalize</c> writes them) of a provider
    /// or one of its account keys, or null when the plan is not in the list.</summary>
    public static PlanPrice? Find(string providerId, string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return null;

        var hash = providerId.IndexOf('#');
        var provider = (hash >= 0 ? providerId[..hash] : providerId).ToLowerInvariant();
        return Prices.TryGetValue((provider, plan.Trim().ToLowerInvariant()), out var price) ? price : null;
    }

    /// <summary>The monthly dollar price of a plan, or null when the plan is not in the list.</summary>
    public static double? MonthlyUsd(string providerId, string? plan) => Find(providerId, plan)?.Usd;
}
