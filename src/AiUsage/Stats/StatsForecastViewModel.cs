using AiUsage.Services;
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

/// <summary>The month card: the API value of a month, how many times the plan price it is, and the plan
/// with its list price (the tooltip names where the price comes from and when it was checked).</summary>
public sealed record StatsForecastMonthViewModel(
    string ApiText, string PlanText, string PlanToolTip, string RatioText, string RatioCaption, string ToolTip, Brush Brush,
    string PriceDiffText = "")
{
    public bool HasPriceDiff => PriceDiffText.Length > 0;

    public bool HasPlan => PlanText.Length > 0;

    public bool HasRatio => RatioText.Length > 0;

    /// <summary>True when the right column (ratio or plan price) has anything to show.</summary>
    public bool HasSide => HasPlan || HasRatio;
}

/// <summary>The plan forecast of one provider: its plans and account count, one card per window and the month card.</summary>
public sealed record StatsForecastProviderViewModel(
    string ProviderId, string Name, Brush Brush, string PlanText, string AccountsText,
    IReadOnlyList<StatsForecastWindowViewModel> Windows, StatsForecastMonthViewModel? Month,
    string Key = "", string? Plan = null, bool IsCurrent = true, string OldLabel = "", string VsCurrentText = "")
{
    /// <summary>What the button above the forecast says: the provider's name, or for an old plan the label with its end date.</summary>
    public string ButtonName => IsCurrent ? Name : OldLabel;

    /// <summary>The line under the window cards comparing an old plan with the current one; empty for a current plan.</summary>
    public bool HasVsCurrent => VsCurrentText.Length > 0;

    /// <summary>The small text beside the provider name: plan(s), then the account count when there are several.</summary>
    public string Detail => PlanText.Length > 0 && AccountsText.Length > 0 ? TextCompose.Pair(PlanText, AccountsText) : PlanText + AccountsText;

    public bool HasMonth => Month is not null;
}

/// <summary>One provider button above the forecast: the provider's name, its plan beside it, its colour and whether it is the
/// shown one. <see cref="IsChecked"/> is what the radio button binds to, so a click or an arrow key both land in the owner.</summary>
public sealed partial class StatsForecastButtonViewModel(
    string providerId, string name, Brush brush, Action<string> pick, string? plan = null, string? key = null, bool isOld = false) : ObservableObject
{
    private bool _isChecked;

    public string ProviderId { get; } = providerId;

    /// <summary>The plan the button stands for; null when the plan is unknown or the button is no forecast button.</summary>
    public string? Plan { get; } = plan;

    /// <summary>What a pick hands to the owner: provider and plan (<c>claude|Max</c>); the provider alone for a button without a plan.</summary>
    public string Key { get; } = key ?? providerId;

    /// <summary>True for a plan the provider has left; its button is drawn muted.</summary>
    public bool IsOld { get; } = isOld;

    public double Opacity => IsOld ? 0.6 : 1;

    public string Name { get; } = name;

    public Brush Brush { get; } = brush;

    /// <summary>The plan (and account count) shown small beside the name; empty when unknown.</summary>
    public string Detail { get; private set; } = "";

    public bool HasDetail => Detail.Length > 0;

    internal void SetDetail(string detail)
    {
        if (Detail == detail)
            return;

        Detail = detail;
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasDetail));
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            // The radio group unchecks the previous button by itself; only a new pick is news.
            if (value && !_isChecked)
                pick(Key);
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
