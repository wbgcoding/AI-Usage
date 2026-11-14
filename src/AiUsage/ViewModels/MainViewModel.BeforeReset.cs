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
    private readonly List<(string ProviderId, string Label, DateTimeOffset ResetsAt)> _beforeResetSent = [];

    // Reset times this close are the same period: Codex derives its reset from the read time and Claude's
    // jitters, so the exact instant differs from read to read (same tolerance as NotificationService).
    private static readonly TimeSpan BeforeResetPeriodTolerance = TimeSpan.FromMinutes(10);

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

        _beforeResetSent.RemoveAll(sent => sent.ResetsAt + BeforeResetPeriodTolerance <= now);
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

                if (_beforeResetSent.Any(sent => sent.ProviderId == tile.ProviderId && sent.Label == row.Label
                        && (sent.ResetsAt - resetsAt).Duration() <= BeforeResetPeriodTolerance))
                    continue;

                _beforeResetSent.Add((tile.ProviderId, row.Label, resetsAt));

                var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
                ForwardBeforeResetNotification(
                    new BeforeResetNotification(tile.ProviderId, tile.NotificationName, row.Kind, minutes, row.Label));
            }
        }
    }
}
