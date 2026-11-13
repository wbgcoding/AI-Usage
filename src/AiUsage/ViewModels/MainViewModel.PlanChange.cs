using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Compares a read's plan with the one stored for the account: the first plan is only
    /// stored, a different one is stored as a <see cref="StoredPlanChange"/> and becomes the new last
    /// plan. A read without a plan (failed, signed out) counts for nothing. True when a change was recorded.</summary>
    internal static bool ObservePlan(ProviderSettings settings, string? rawPlan, DateTimeOffset now)
    {
        var plan = PlanTier.Normalize(rawPlan);
        if (string.IsNullOrEmpty(plan))
            return false;

        var previous = settings.LastPlan;
        if (previous == plan)
            return false;

        settings.LastPlan = plan;
        if (string.IsNullOrEmpty(previous))
            return true;

        settings.PlanChange = new StoredPlanChange { From = previous, To = plan, AtUtc = now.ToUniversalTime() };
        return true;
    }

    /// <summary>Feeds a fresh read's plan to <see cref="ObservePlan"/>, saves what it changed and shows
    /// the notice on the account's tile.</summary>
    private void TrackPlan(ProviderTileViewModel tile, ProviderSnapshot snapshot, DateTimeOffset now)
    {
        var providerSettings = GetOrCreateProviderSettings(tile.ProviderId);
        if (!ObservePlan(providerSettings, snapshot.PlanType, now))
            return;

        _settingsStore.RequestSave(_settings);
        tile.SetPlanChange(providerSettings.PlanChange, now);
    }

    /// <summary>The tile holds the stored change itself, so its dismissal only needs saving.</summary>
    private void SavePlanChangeDismissed() => _settingsStore.RequestSave(_settings);
}
