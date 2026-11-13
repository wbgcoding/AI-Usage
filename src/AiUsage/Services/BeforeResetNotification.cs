using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>A window at 90 % or more is about to reset: the short heads-up that comes before the
/// "free again" notice. <paramref name="Minutes"/> is the whole minutes left, at least 1.
/// <paramref name="Label"/> plays the same role as on <see cref="ThresholdNotification"/>.</summary>
public sealed record BeforeResetNotification(
    string ProviderId, string ProviderDisplayName, WindowKind Kind, int Minutes, string Label = "")
{
    /// <summary>The usage a window must have reached for the heads-up.</summary>
    public const double MinPercent = 90;

    /// <summary>How long before the reset the heads-up comes.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(10);

    public string Text()
    {
        var windowLabel = StatusTextMap.Resolve(Label.Length > 0 ? Label : ThresholdNotification.FallbackLabel(Kind));
        return LocalizationService.Instance.Format("Notify.BeforeReset", ProviderDisplayName, windowLabel, Minutes);
    }
}
