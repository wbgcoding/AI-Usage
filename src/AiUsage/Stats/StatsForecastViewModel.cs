namespace AiUsage.Stats;

/// <summary>One line of the plan forecast: the main text, an optional smaller line under it and the tooltip.</summary>
public sealed record StatsForecastRowViewModel(string Text, string DetailText, string ToolTip)
{
    public bool HasDetail => DetailText.Length > 0;
}

/// <summary>The plan forecast of one provider: the heading (provider, plans, account count) and its rows.</summary>
public sealed record StatsForecastGroupViewModel(string ProviderId, string Heading, IReadOnlyList<StatsForecastRowViewModel> Rows);
