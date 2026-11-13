using AiUsage.Models;
using AiUsage.Services;
using CommunityToolkit.Mvvm.Input;

namespace AiUsage.ViewModels;

public partial class ProviderTileViewModel
{
    private StoredPlanChange? _planChange;

    /// <summary>"Plan changed: Max → Ultra" while the last plan change of this account is within its week
    /// and not dismissed; empty otherwise.</summary>
    public string PlanChangeText =>
        _planChangeShown && _planChange is { } change
            ? LocalizationService.Instance.Format("Tile.PlanChanged", change.From, change.To)
            : "";

    private bool _planChangeShown;

    /// <summary>The quiet line under the bars that names a recent plan change, with its dismiss link.</summary>
    public bool ShowPlanChangeNotice => ShowRows && _planChangeShown;

    /// <summary>Raised when the person hides the notice, so MainViewModel can persist it.</summary>
    public event EventHandler? PlanChangeDismissed;

    /// <summary>Takes the account's stored plan change (null = none) and shows it while <paramref
    /// name="now"/> is inside its week and it is not dismissed.</summary>
    public void SetPlanChange(StoredPlanChange? change, DateTimeOffset now)
    {
        _planChange = change;
        var shown = change?.IsShown(now) == true;
        if (shown == _planChangeShown)
        {
            OnPropertyChanged(nameof(PlanChangeText));
            return;
        }

        _planChangeShown = shown;
        OnPropertyChanged(nameof(PlanChangeText));
        OnPropertyChanged(nameof(ShowPlanChangeNotice));
    }

    [RelayCommand]
    private void DismissPlanChange()
    {
        if (_planChange is null)
            return;

        _planChange.Dismissed = true;
        SetPlanChange(_planChange, DateTimeOffset.UtcNow);
        PlanChangeDismissed?.Invoke(this, EventArgs.Empty);
    }
}
