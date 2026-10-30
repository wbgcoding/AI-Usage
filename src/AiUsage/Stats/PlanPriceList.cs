namespace AiUsage.Stats;

/// <summary>
/// The monthly list price in US dollars of the plans the plan forecast compares against. These are the
/// public list prices; VAT, regional prices and promotions differ, so the ratio is a guide, not a bill.
/// </summary>
public static class PlanPriceList
{
    private static readonly Dictionary<(string Provider, string Plan), double> Prices = new()
    {
        [("claude", "pro")] = 20,
        [("claude", "max 5x")] = 100,
        [("claude", "max 20x")] = 200,
        [("codex", "plus")] = 20,
        [("codex", "pro")] = 200,
        [("codex", "business")] = 30,
        [("codex", "team")] = 30,
        [("gemini", "pro")] = 19.99,
        [("gemini", "ultra")] = 249.99,
    };

    /// <summary>The monthly price of a normalized plan name (as <c>PlanTier.Normalize</c> writes it) of a
    /// provider or one of its account keys, or null when the plan is not in the list.</summary>
    public static double? MonthlyUsd(string providerId, string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return null;

        var hash = providerId.IndexOf('#');
        var provider = (hash >= 0 ? providerId[..hash] : providerId).ToLowerInvariant();
        return Prices.TryGetValue((provider, plan.Trim().ToLowerInvariant()), out var price) ? price : null;
    }
}
