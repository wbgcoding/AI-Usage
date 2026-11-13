using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The heads-up before a full window resets, routed like <see cref="ResetRaised"/> (quiet
    /// hours and a mute drop it). Only raised while <see cref="Models.AppSettings.NotifyBeforeReset"/> is on.</summary>
    public event Action<BeforeResetNotification>? BeforeResetRaised;

    // Which (account, window, reset instant) already got its heads-up. In memory only: a restart inside
    // the ten minutes may repeat one notice, which is accepted.
    private readonly HashSet<(string ProviderId, string Label, DateTimeOffset ResetsAt)> _beforeResetSent = [];

    private void ForwardBeforeResetNotification(BeforeResetNotification notification)
    {
        if (IsAlertAllowed(notification.ProviderId))
            BeforeResetRaised?.Invoke(notification);
    }

    /// <summary>Called by <see cref="Tick"/>: every row a tile shows that is at 90 % or more and resets
    /// within ten minutes gets one notice per reset, unless the tile's notifications are off.</summary>
    internal void EvaluateBeforeReset(DateTimeOffset now)
    {
        if (!_settings.NotifyBeforeReset)
        {
            _beforeResetSent.Clear();
            return;
        }

        _beforeResetSent.RemoveWhere(sent => sent.ResetsAt <= now);
        foreach (var tile in Tiles)
        {
            if (tile.IsHidden || !GetOrCreateProviderSettings(tile.ProviderId).NotificationsEnabled)
                continue;

            foreach (var row in tile.Rows)
            {
                if (row.UsedPercent < BeforeResetNotification.MinPercent || row.ResetsAt is not { } resetsAt)
                    continue;

                var remaining = resetsAt - now;
                if (remaining <= TimeSpan.Zero || remaining > BeforeResetNotification.Lead)
                    continue;

                if (!_beforeResetSent.Add((tile.ProviderId, row.Label, resetsAt)))
                    continue;

                var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
                ForwardBeforeResetNotification(
                    new BeforeResetNotification(tile.ProviderId, tile.NotificationName, row.Kind, minutes, row.Label));
            }
        }
    }
}
