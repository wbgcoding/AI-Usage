namespace AiUsage.Stats;

/// <summary>One plan's monthly price as the provider's own page advertises it (amount, currency and
/// whether VAT is in it), the page it comes from and when it was checked.</summary>
public sealed record PlanPrice(string Plan, double Amount, string Currency, bool IncludesVat, string Source, DateOnly CheckedOn)
{
    /// <summary>German VAT, the rate the gross figure in the tooltip uses.</summary>
    public const double GermanVat = 0.19;

    public bool IsEuro => Currency == "EUR";

    /// <summary>The price without VAT in its own currency.</summary>
    public double Net => IncludesVat ? Amount / (1 + GermanVat) : Amount;

    /// <summary>The price without VAT in US dollars, or null when it is in euros and no rate is known.</summary>
    public double? NetUsd(double? usdPerEur) => IsEuro ? usdPerEur is > 0 ? Net * usdPerEur.Value : null : Net;
}

/// <summary>
/// The monthly list prices of the plans the plan forecast compares against, taken only from each
/// provider's own pages (<see cref="PlanPrice.Source"/>). A tier the plan name cannot tell apart (a
/// provider selling several "Pro" or "Ultra" steps) is left out rather than guessed. Other countries'
/// VAT, yearly billing and promotions differ, so the ratio is a guide, not a bill. The figures are
/// checked by hand against the sources; <see cref="PlanPrice.CheckedOn"/> moves with every check.
/// </summary>
public static class PlanPriceList
{
    internal const string ClaudeSource = "https://claude.com/pricing";
    internal const string ChatGptPlusSource = "https://help.openai.com/en/articles/6950777-what-is-chatgpt-plus";
    internal const string ChatGptBusinessSource = "https://help.openai.com/en/articles/8792828-what-is-chatgpt-business";
    internal const string GeminiSource = "https://gemini.google/subscriptions/";
    private static readonly DateOnly Checked = new(2026, 10, 31);

    private static readonly Dictionary<(string Provider, string Plan), PlanPrice> Prices = new()
    {
        // Anthropic shows dollar prices without tax; its 200 dollar step is named Ultra on the page.
        [("claude", "pro")] = new("Pro", 20, "USD", false, ClaudeSource, Checked),
        [("claude", "max 5x")] = new("Max 5x", 100, "USD", false, ClaudeSource, Checked),
        [("claude", "max 20x")] = new("Max 20x", 200, "USD", false, ClaudeSource, Checked),
        [("claude", "ultra")] = new("Ultra", 200, "USD", false, ClaudeSource, Checked),
        [("codex", "plus")] = new("Plus", 20, "USD", false, ChatGptPlusSource, Checked),
        // Per seat with monthly billing.
        [("codex", "business")] = new("Business", 25, "USD", false, ChatGptBusinessSource, Checked),
        [("codex", "team")] = new("Team", 25, "USD", false, ChatGptBusinessSource, Checked),
        // Google shows consumer prices in euros with VAT.
        [("gemini", "plus")] = new("Plus", 4.99, "EUR", true, GeminiSource, Checked),
        [("gemini", "pro")] = new("Pro", 21.99, "EUR", true, GeminiSource, Checked),
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

    /// <summary>Every listed price, for the tests that check sources and dates.</summary>
    internal static IEnumerable<PlanPrice> All => Prices.Values;
}
