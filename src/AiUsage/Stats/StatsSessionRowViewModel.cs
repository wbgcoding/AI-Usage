namespace AiUsage.Stats;

/// <summary>One row of the session table, already formatted for display: the five columns and the
/// hover text that names the session in full.</summary>
public sealed record StatsSessionRowViewModel(
    string Project, string Start, string Duration, string Tokens, string Model, string ToolTip, string CostText = "")
{
    /// <summary>What a screen reader announces for the row: project and start, then the tokens, joined
    /// in the label-value order and punctuation of the current language.</summary>
    public string AutomationText => Services.LocalizationService.Instance.Format("Format.LabelValue", Project + " " + Start, Tokens);
}
