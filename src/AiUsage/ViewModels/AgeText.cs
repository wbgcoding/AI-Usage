using AiUsage.Services;

namespace AiUsage.ViewModels;

/// <summary>The bare "vor ..." text for a <see cref="ProviderTileViewModel.LastSuccessAt"/>, as the
/// Settings window's "last successful refresh" list shows it. Deliberately not <see
/// cref="CountdownFormatter.FormatAge"/>: that phrase already opens with the words the list's own
/// heading says once.</summary>
public static class AgeText
{
    /// <summary>The elapsed text for <paramref name="timestamp"/>, or the "never updated" text when
    /// there is none. A view model property re-raised by a timer calls this so the age keeps moving.</summary>
    public static string Describe(DateTimeOffset? timestamp) =>
        timestamp is { } at
            ? LocalizationService.Instance.Format("Age.Bare", CountdownFormatter.FormatElapsed(DateTimeOffset.Now - at))
            : LocalizationService.Instance["Settings.NeverUpdated"];
}
