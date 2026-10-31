using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.Stats;

/// <summary>One bar of the per-model capacities: its label and its length as a share (0..1) of the longest.</summary>
public sealed record StatsForecastModelBar(string Label, double Fraction);

/// <summary>One window card (5-hour or week) of a provider's plan forecast. Without data
/// (<see cref="HasData"/> false) the card only says so.</summary>
public sealed record StatsForecastWindowViewModel(
    string Caption, bool HasData, string CapacityText, string ApiText, double AverageFraction, string AverageText,
    string ModelsCaption, IReadOnlyList<StatsForecastModelBar> Models, string ToolTip, string AverageToolTip, Brush Brush)
{
    public bool HasApi => ApiText.Length > 0;

    public bool HasAverage => AverageText.Length > 0;

    /// <summary>True when the capacity differs per model, so the small bars are drawn.</summary>
    public bool HasModels => Models.Count > 0;

    /// <summary>True when the capacity is one figure for all models together (the micro text says so).</summary>
    public bool IsAllModels => HasData && Models.Count == 0;
}

/// <summary>The month card: the API value of a month against the plan price on one scale (0..1 of the
/// longer one), and how many times the plan price the value is.</summary>
public sealed record StatsForecastMonthViewModel(
    string ApiText, double ApiFraction, string PlanText, double PlanFraction, string RatioText, string RatioCaption, string ToolTip, Brush Brush)
{
    public bool HasPlan => PlanText.Length > 0;

    public bool HasRatio => RatioText.Length > 0;
}

/// <summary>The plan forecast of one provider: its plans and account count, one card per window and the month card.</summary>
public sealed record StatsForecastProviderViewModel(
    string ProviderId, string Name, Brush Brush, string PlanText, string AccountsText,
    IReadOnlyList<StatsForecastWindowViewModel> Windows, StatsForecastMonthViewModel? Month)
{
    public bool HasPlan => PlanText.Length > 0;

    public bool HasAccounts => AccountsText.Length > 0;

    public bool HasPlanLine => HasPlan || HasAccounts;

    public bool HasMonth => Month is not null;
}

/// <summary>One provider button above the forecast: the provider's name and colour and whether it is the
/// shown one. <see cref="IsChecked"/> is what the radio button binds to, so a click or an arrow key both land in the owner.</summary>
public sealed partial class StatsForecastButtonViewModel(string providerId, string name, Brush brush, Action<string> pick) : ObservableObject
{
    private bool _isChecked;

    public string ProviderId { get; } = providerId;

    public string Name { get; } = name;

    public Brush Brush { get; } = brush;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            // The radio group unchecks the previous button by itself; only a new pick is news.
            if (value && !_isChecked)
                pick(ProviderId);
        }
    }

    /// <summary>Sets the checked state from the owner, without calling it back.</summary>
    internal void Mark(bool isChecked)
    {
        if (_isChecked == isChecked)
            return;

        _isChecked = isChecked;
        OnPropertyChanged(nameof(IsChecked));
    }
}
