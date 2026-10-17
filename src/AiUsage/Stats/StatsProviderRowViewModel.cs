namespace AiUsage.Stats;

/// <summary>One row of the "every provider" list: a display name, its token total shortened for the
/// row and the same total written out in full for the tooltip, already resolved so the view stays a
/// plain binding with no branching of its own.</summary>
public sealed record StatsProviderRowViewModel(string DisplayName, string ValueText, string ExactText, string CostText = "", string CostLine = "")
{
    /// <summary>The tooltip: the exact token count, then the cost lines (with the unpriced models) when known.</summary>
    public string TooltipText => CostLine.Length == 0 ? ExactText : ExactText + "\n" + CostLine;

    /// <summary>"Claude: 1.2M" in the order and punctuation of the current language, followed by the
    /// cost figure when there is one.</summary>
    public string LabelValueText => Services.LocalizationService.Instance.Format(
        "Format.LabelValue", DisplayName, CostText.Length == 0 ? ValueText : ValueText + "  " + CostText);
}
