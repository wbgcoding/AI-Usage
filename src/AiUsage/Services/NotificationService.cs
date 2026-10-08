using System.Text;
using System.Text.Json;
using AiUsage.Models;
using AiUsage.Storage;

namespace AiUsage.Services;

/// <summary>One threshold crossing, ready to show as a tray balloon. <paramref name="Label"/> is the
/// window's own resource key (see <see cref="Models.UsageWindow.Label"/>) - a provider with more than
/// one <see cref="WindowKind.Other"/> window (Cursor's per-model bars, its Grok Bot bar) needs its
/// own text for each, which <see cref="Kind"/> alone cannot tell apart. Left blank, this falls back
/// to the old Kind-only mapping, so a caller that only ever had one window per kind need not pass
/// it.</summary>
public sealed record ThresholdNotification(
    string ProviderId, string ProviderDisplayName, WindowKind Kind, double UsedPercent, DateTimeOffset? ResetsAt, string Label = "")
{
    /// <summary>What happened, for which provider and window, and when it resets - the four
    /// things the Notify.Threshold text is built from.</summary>
    public string Text(DateTimeOffset now)
    {
        var windowLabel = StatusTextMap.Resolve(Label.Length > 0 ? Label : FallbackLabel(Kind));
        var countdown = CountdownFormatter.Format(ResetsAt, now);
        // No reset time on the window at all (should not happen for a real threshold crossing, but
        // Notify.Threshold's own template always names one) - Notify.ThresholdNoReset is the same
        // sentence without that trailing clause, its own resource key rather than a literal, like
        // every other visible string.
        return countdown.Length > 0
            ? LocalizationService.Instance.Format("Notify.Threshold", ProviderDisplayName, windowLabel, StatusTextMap.UsagePercent(UsedPercent), countdown)
            : LocalizationService.Instance.Format("Notify.ThresholdNoReset", ProviderDisplayName, windowLabel, StatusTextMap.UsagePercent(UsedPercent));
    }

    internal static string FallbackLabel(WindowKind kind) => kind switch
    {
        WindowKind.Weekly => "Window_Weekly",
        WindowKind.Other => "Window_Other",
        _ => "Window_FiveHour",
    };
}

/// <summary>The good-news counterpart to <see cref="ThresholdNotification"/>: a window that had
/// actually crossed the threshold has reset and is free again. Opt-in via <see
/// cref="Models.AppSettings.NotifyOnReset"/> - a second stream of balloons must not arrive
/// unannounced. <paramref name="Label"/> plays the same role as on <see cref="ThresholdNotification"/>.</summary>
public sealed record ResetNotification(string ProviderId, string ProviderDisplayName, WindowKind Kind, string Label = "")
{
    public string Text()
    {
        var windowLabel = StatusTextMap.Resolve(Label.Length > 0 ? Label : ThresholdNotification.FallbackLabel(Kind));
        return LocalizationService.Instance.Format("Notify.Reset", ProviderDisplayName, windowLabel);
    }
}

/// <summary>
/// Threshold notifications: one rising-edge alert per window period,
/// independently per provider and per window (5 hours / weekly). A dip back below the threshold, or
/// the window's own reset passing, re-arms it for next time; a 30-minute lockout on top stops a
/// value oscillating right at the edge from re-notifying inside the same period even if it dips and
/// climbs back before the reset. Startup case: a window already above threshold on its very first
/// observation still notifies once (armed defaults to true) - the user should hear about an
/// already-critical value, not have it silently swallowed for lack of a prior "below" sample.
/// </summary>
public sealed class NotificationService
{
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    // Keyed by Label too, not only Kind: a provider can carry more than one Other window at once
    // (Cursor's per-model bars, its Grok Bot bar), and each needs its own arm/lockout state -
    // sharing one entry between them let one window's crossing silently arm or disarm another's.
    private readonly Dictionary<(string ProviderId, WindowKind Kind, string Label), State> _state = [];
    private string _filePath;

    public event Action<ThresholdNotification>? NotificationRaised;

    public event Action<ResetNotification>? ResetRaised;

    public NotificationService() : this(AppPaths.DataDirectory, DateTimeOffset.Now) { }

    /// <summary>Test seam: a directory of its own (never the real %APPDATA%) and an explicit "now"
    /// for the one-time decision of which loaded entries already reset and must be dropped.</summary>
    internal NotificationService(string dataDirectory) : this(dataDirectory, DateTimeOffset.Now) { }

    internal NotificationService(string dataDirectory, DateTimeOffset now)
    {
        _filePath = Path.Combine(dataDirectory, "notifications.json");
        LoadState(now);
    }

    /// <summary>The data folder moved (Settings window): later saves land there. The in-memory state
    /// stays as it is - the caller already copied the file across.</summary>
    internal void Redirect(string dataDirectory)
    {
        lock (_gate)
            _filePath = Path.Combine(dataDirectory, "notifications.json");
    }

    // Up to four provider fetches complete on their own ThreadPool threads and can call this
    // concurrently for different (or the same) provider/window keys - the whole decision runs under
    // _gate so a concurrent Add/lookup on _state can never corrupt it; the notification itself is
    // only raised after the lock is released, so a slow subscriber can never hold the gate.
    // Threshold/enabled arrive already resolved (see SettingsRanges.ResolveThreshold) - this method
    // has no opinion on where a provider's number came from, global default or its own override, and
    // treats every WindowKind (including Other) exactly alike.
    public void Evaluate(string providerId, string providerDisplayName, UsageWindow window, double threshold, bool enabled, bool notifyOnReset, DateTimeOffset now)
    {
        if (!enabled)
            return;

        ThresholdNotification? notification = null;
        ResetNotification? resetNotification = null;
        bool changed;

        lock (_gate)
        {
            var key = (providerId, window.Kind, window.Label);
            var isNew = !_state.TryGetValue(key, out var state);
            if (state is null)
            {
                state = new State();
                _state[key] = state;
            }
            var before = (state.Armed, state.LastNotifiedAt, state.LastResetsAt);

            // A reset counts only once the window reports a different reset time. A stale snapshot
            // keeps repeating the old, already-passed one, and treating that as a fresh reset on every
            // fetch re-armed the window and repeated the balloon about once a minute.
            var resetPassed = state.LastResetsAt is { } lastReset && now >= lastReset && window.ResetsAt != lastReset;
            if (window.UsedPercent < threshold || resetPassed)
                state.Armed = true;

            // Only when this window had actually notified in the period that just ended - a window
            // that was never close to full stays quiet instead of gaining a balloon of its own.
            // Cleared regardless of notifyOnReset: the arming state must stay correct even for a
            // user who never opted into seeing the good news.
            if (resetPassed && state.LastNotifiedAt is not null)
            {
                if (notifyOnReset)
                    resetNotification = new ResetNotification(providerId, providerDisplayName, window.Kind, window.Label);
                state.LastNotifiedAt = null;
            }

            state.LastResetsAt = window.ResetsAt;

            if (state.Armed && window.UsedPercent >= threshold)
            {
                // Checked before disarming: a crossing suppressed only by the lockout must stay
                // armed, so it still notifies once the lockout expires instead of being silently
                // lost for the rest of this window period (only an actually-delivered notification
                // may consume the arm).
                if (state.LastNotifiedAt is not { } last || now - last >= Lockout)
                {
                    state.Armed = false;
                    state.LastNotifiedAt = now;
                    notification = new ThresholdNotification(providerId, providerDisplayName, window.Kind, window.UsedPercent, window.ResetsAt, window.Label);
                }
            }

            changed = isNew || before != (state.Armed, state.LastNotifiedAt, state.LastResetsAt);
        }

        // Every fetch of every provider lands here; rewriting an unchanged file each time is wasted work.
        if (changed)
            SaveState();

        if (notification is not null)
            NotificationRaised?.Invoke(notification);
        if (resetNotification is not null)
            ResetRaised?.Invoke(resetNotification);
    }

    /// <summary>Forgets every remembered window of one removed account and saves, so a later account
    /// that reuses the key never starts from its arm and lockout state.</summary>
    internal void RemoveAccount(string accountKey)
    {
        var removed = false;
        lock (_gate)
        {
            foreach (var key in _state.Keys.Where(key => key.ProviderId == accountKey).ToList())
                removed |= _state.Remove(key);
        }

        if (removed)
            SaveState();
    }

    /// <summary>Drops any loaded entry whose window has already reset since it was saved - a
    /// notification for a period that is long over must not linger and suppress the next real
    /// crossing. Missing/empty/unreadable file means "no state", the same tolerance
    /// <see cref="Storage.SettingsStore"/>'s own loader has, and never throws.</summary>
    private void LoadState(DateTimeOffset now)
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            var text = File.ReadAllText(_filePath);
            var entries = JsonSerializer.Deserialize<List<PersistedEntry?>>(text, JsonOptions);
            if (entries is null)
                return;

            foreach (var entry in entries)
            {
                if (entry?.ProviderId is null)
                    continue;
                if (entry.LastResetsAt is { } resetsAt && now >= resetsAt)
                    continue;

                // A label stored under an older spelling keys the same window it does today, so an
                // update never re-announces a crossing that was already announced. When the file holds
                // both spellings of one window, the current one is the truth whatever their order.
                var storedLabel = entry.Label ?? "";
                var currentLabel = LegacyWindowLabels.Migrate(entry.ProviderId, storedLabel);
                var key = (entry.ProviderId, entry.Kind, currentLabel);
                if (currentLabel != storedLabel && _state.ContainsKey(key))
                    continue;

                _state[key] = new State
                {
                    Armed = entry.Armed,
                    LastNotifiedAt = entry.LastNotifiedAt,
                    LastResetsAt = entry.LastResetsAt,
                };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable - start with no remembered state rather than take the app down.
        }
    }

    /// <summary>Atomic (tmp file + move), same as <see cref="Storage.SettingsStore.SaveNow"/> - and,
    /// like that one, never lets a save that cannot land take the app down with it. Runs entirely
    /// inside <see cref="_gate"/> (snapshot and file write together) so two threads saving at once
    /// can never race on the same temp file.</summary>
    private void SaveState()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

                var entries = _state.Select(kvp => new PersistedEntry(
                    kvp.Key.ProviderId, kvp.Key.Kind, kvp.Value.Armed, kvp.Value.LastNotifiedAt, kvp.Value.LastResetsAt, kvp.Key.Label)).ToList();
                var json = JsonSerializer.Serialize(entries, JsonOptions);

                var tempPath = $"{_filePath}.tmp";
                File.WriteAllBytes(tempPath, Utf8NoBom.GetBytes(json));
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A save that cannot land must not take the app down with it.
            }
        }
    }

    private sealed record PersistedEntry(
        string ProviderId, WindowKind Kind, bool Armed, DateTimeOffset? LastNotifiedAt, DateTimeOffset? LastResetsAt, string? Label = null);

    private sealed class State
    {
        public bool Armed = true;
        public DateTimeOffset? LastNotifiedAt;
        public DateTimeOffset? LastResetsAt;
    }
}
