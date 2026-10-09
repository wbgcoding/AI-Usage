using System.Text.Json;
using AiUsage.Models;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class NotificationServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static UsageWindow FiveHour(double percent, DateTimeOffset resetsAt) =>
        new("Window_FiveHour", WindowKind.FiveHour, percent, resetsAt, 300);

    private static UsageWindow Other(double percent, DateTimeOffset resetsAt) =>
        new("Window_Other", WindowKind.Other, percent, resetsAt, 300);

    private readonly List<DisposableTestDirectory> _tempDirectories = [];

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-notifications");
        _tempDirectories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
            directory.Dispose();
    }

    [Fact]
    public void Several_new_windows_of_one_snapshot_are_written_once_when_the_batch_ends()
    {
        var service = new NotificationService(TempDirectory());

        using (service.BatchSaves())
        {
            service.Evaluate("codex", "Codex", FiveHour(10, Start.AddHours(1)), threshold: 85, enabled: true, notifyOnReset: false, Start);
            service.Evaluate("codex", "Codex", Other(20, Start.AddHours(2)), threshold: 85, enabled: true, notifyOnReset: false, Start);
            service.Evaluate("codex", "Codex", new UsageWindow("Window_Weekly", WindowKind.Weekly, 30, Start.AddDays(2), 300),
                threshold: 85, enabled: true, notifyOnReset: false, Start);
            Assert.Equal(0, service.SavesWritten);
        }

        Assert.Equal(1, service.SavesWritten);
    }

    [Fact]
    public void Without_a_batch_every_changed_window_still_saves_on_its_own()
    {
        var service = new NotificationService(TempDirectory());

        service.Evaluate("codex", "Codex", FiveHour(10, Start.AddHours(1)), threshold: 85, enabled: true, notifyOnReset: false, Start);
        service.Evaluate("codex", "Codex", Other(20, Start.AddHours(2)), threshold: 85, enabled: true, notifyOnReset: false, Start);

        Assert.Equal(2, service.SavesWritten);
    }

    [Fact]
    public void A_batch_in_which_nothing_changed_writes_nothing_and_a_batched_state_reloads()
    {
        var directory = TempDirectory();
        var service = new NotificationService(directory);
        using (service.BatchSaves())
            service.Evaluate("codex", "Codex", FiveHour(90, Start.AddHours(1)), threshold: 85, enabled: true, notifyOnReset: false, Start);
        Assert.Equal(1, service.SavesWritten);

        using (service.BatchSaves())
            service.Evaluate("codex", "Codex", FiveHour(90, Start.AddHours(1)), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(1));
        Assert.Equal(1, service.SavesWritten);

        var raised = new List<ThresholdNotification>();
        var reloaded = new NotificationService(directory, Start.AddMinutes(2));
        reloaded.NotificationRaised += raised.Add;
        reloaded.Evaluate("codex", "Codex", FiveHour(90, Start.AddHours(1)), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(2));
        Assert.Empty(raised); // the first run already notified and disarmed; the saved state remembers it
    }

    [Fact]
    public void A_stale_snapshot_repeating_an_already_passed_reset_time_does_not_repeat_the_notification()
    {
        var service = new NotificationService(TempDirectory());
        var raised = new List<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);
        var resetsAt = Start.AddHours(1);

        service.Evaluate("codex", "Codex", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start);
        // Past the reset time, but the provider keeps reporting the same old window.
        for (var minutes = 61; minutes < 300; minutes += 60)
            service.Evaluate("codex", "Codex", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(minutes));

        Assert.Single(raised);
    }

    [Fact]
    public void PendulumAroundThresholdRaisesExactlyTwoNotifications()
    {
        var service = new NotificationService(TempDirectory());
        var raised = new List<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);
        var resetsAt = Start.AddHours(5);
        var now = Start;

        foreach (var percent in new[] { 80.0, 86.0, 88.0, 84.0, 87.0 })
        {
            service.Evaluate("claude", "Claude", FiveHour(percent, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, now);
            now = now.AddMinutes(35); // clear of the 30-minute lockout between every sample
        }

        Assert.Equal(2, raised.Count);
        Assert.All(raised, n => Assert.Equal("claude", n.ProviderId));
    }

    [Fact]
    public void StayingAboveThresholdDoesNotRepeat()
    {
        var service = new NotificationService(TempDirectory());
        var raised = new List<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);
        var resetsAt = Start.AddHours(5);

        service.Evaluate("claude", "Claude", FiveHour(86, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start);
        service.Evaluate("claude", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(1));
        service.Evaluate("claude", "Claude", FiveHour(99, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(2));

        Assert.Single(raised);
    }

    [Fact]
    public void ResetPassingRearmsEvenWithoutDippingBelow()
    {
        var service = new NotificationService(TempDirectory());
        var raised = new List<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);

        service.Evaluate("claude", "Claude", FiveHour(90, Start.AddHours(1)), threshold: 85, enabled: true, notifyOnReset: false, Start);
        // The window resets and immediately reports high usage again in the next period.
        service.Evaluate("claude", "Claude", FiveHour(90, Start.AddHours(6)), threshold: 85, enabled: true, notifyOnReset: false, Start.AddHours(2));

        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void ACrossingRearmedByADipButSuppressedByTheLockoutStillFiresOnceItExpires()
    {
        var service = new NotificationService(TempDirectory());
        var raised = new List<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);
        var resetsAt = Start.AddHours(5);

        service.Evaluate("claude", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start);
        Assert.Single(raised); // first crossing notifies and disarms

        // Dips back below threshold - re-arms, per the class's own documented contract.
        service.Evaluate("claude", "Claude", FiveHour(80, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(5));
        // Crosses back above threshold while still inside the 30-minute lockout from the first
        // notification - must stay quiet AND stay armed (only an actually-delivered notification
        // may consume the arm; a crossing the lockout alone suppressed must not be lost for good).
        service.Evaluate("claude", "Claude", FiveHour(92, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(10));
        Assert.Single(raised);

        // Lockout has now expired, still above threshold, no further dip - the deferred crossing
        // must notify now instead of staying silently disarmed for the rest of the window.
        service.Evaluate("claude", "Claude", FiveHour(93, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(40));

        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void DisabledThresholdNeverNotifies()
    {
        var service = new NotificationService(TempDirectory());
        var raised = new List<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);

        service.Evaluate("claude", "Claude", FiveHour(99, Start.AddHours(1)), threshold: 85, enabled: false, notifyOnReset: false, Start);

        Assert.Empty(raised);
    }

    [Fact]
    public void TwoProvidersWithDifferentThresholdsAreIndependent()
    {
        var service = new NotificationService(TempDirectory());
        var raised = new List<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);
        var resetsAt = Start.AddHours(5);

        service.Evaluate("claude", "Claude", FiveHour(70, resetsAt), threshold: 65, enabled: true, notifyOnReset: false, Start);
        service.Evaluate("codex", "Codex", FiveHour(70, resetsAt), threshold: 90, enabled: true, notifyOnReset: false, Start);

        Assert.Single(raised);
        Assert.Equal("claude", raised[0].ProviderId);
    }

    [Fact]
    public void AnOtherWindowAboveThresholdNowRaisesANotification()
    {
        // Evaluate used to resolve its own threshold/enabled pair from WindowKind, hardcoding
        // WindowKind.Other to (0.0, false) - Gemini's, Copilot's and any per-model window could
        // never raise a notification no matter how the settings were tuned. The method now takes
        // the resolved pair from its caller and treats every WindowKind alike.
        var service = new NotificationService(TempDirectory());
        var raised = new List<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);
        var resetsAt = Start.AddHours(5);

        service.Evaluate("gemini", "Gemini", Other(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start);

        Assert.Single(raised);
        Assert.Equal(WindowKind.Other, raised[0].Kind);
    }

    [Fact]
    public void AThresholdCrossingFollowedByTheResetRaisesExactlyOneResetNotification()
    {
        var service = new NotificationService(TempDirectory());
        var resetRaised = new List<ResetNotification>();
        service.ResetRaised += n => resetRaised.Add(n);
        var resetsAt = Start.AddHours(1);

        service.Evaluate("claude", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: true, Start);
        Assert.Empty(resetRaised); // the crossing itself is not a reset

        // The window's own reset passes, well below threshold in the fresh period.
        service.Evaluate("claude", "Claude", FiveHour(10, Start.AddHours(6)), threshold: 85, enabled: true, notifyOnReset: true, Start.AddHours(2));

        Assert.Single(resetRaised);
        Assert.Equal("claude", resetRaised[0].ProviderId);
        Assert.Equal(WindowKind.FiveHour, resetRaised[0].Kind);
    }

    [Fact]
    public void AWindowNeverAboveThresholdRaisesNoResetNotificationWhenItResets()
    {
        var service = new NotificationService(TempDirectory());
        var resetRaised = new List<ResetNotification>();
        service.ResetRaised += n => resetRaised.Add(n);
        var resetsAt = Start.AddHours(1);

        service.Evaluate("claude", "Claude", FiveHour(40, resetsAt), threshold: 85, enabled: true, notifyOnReset: true, Start);
        service.Evaluate("claude", "Claude", FiveHour(30, Start.AddHours(6)), threshold: 85, enabled: true, notifyOnReset: true, Start.AddHours(2));

        Assert.Empty(resetRaised);
    }

    [Fact]
    public void NotifyOnResetOffNeverRaisesAResetNotificationEvenAfterACrossing()
    {
        var service = new NotificationService(TempDirectory());
        var resetRaised = new List<ResetNotification>();
        service.ResetRaised += n => resetRaised.Add(n);
        var resetsAt = Start.AddHours(1);

        service.Evaluate("claude", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start);
        service.Evaluate("claude", "Claude", FiveHour(10, Start.AddHours(6)), threshold: 85, enabled: true, notifyOnReset: false, Start.AddHours(2));

        Assert.Empty(resetRaised);
    }

    [Fact]
    public async Task SixteenTasksRacingTheFirstEvaluateForTheSameKeyNeverThrowAndNotifyOnce()
    {
        // The narrowest version of the race the lock exists for: many threads hitting
        // _state.TryGetValue/Add for the exact same (providerId, kind) key at once, before any
        // entry exists yet - an unlocked Dictionary can corrupt itself or throw here.
        var service = new NotificationService(TempDirectory());
        var raised = new System.Collections.Concurrent.ConcurrentBag<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);
        var resetsAt = Start.AddHours(5);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => service.Evaluate("claude", "Claude", FiveHour(90, resetsAt), threshold: 50, enabled: true, notifyOnReset: false, Start)))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Single(raised);
    }

    [Fact]
    public async Task EightConcurrentTasksHammeringEvaluateRaiseExactlyOneNotificationPerProviderAndWindow()
    {
        var service = new NotificationService(TempDirectory());
        var raised = new System.Collections.Concurrent.ConcurrentBag<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);

        var resetsAt = Start.AddHours(5);
        var providerIds = new[] { "codex", "claude", "gemini", "copilot" };
        var kinds = new[] { WindowKind.FiveHour, WindowKind.Weekly };

        // Same fixed "now" and an already-above-threshold percent on every call: whichever thread
        // wins the race is the only one that can ever notify (the 30-minute lockout keeps every
        // later call - same or different thread - quiet), so a correct lock yields exactly one
        // notification per key no matter how the 1,600 calls interleave; a broken lock either throws
        // (corrupted dictionary) or lets more than one call through.
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                foreach (var providerId in providerIds)
                {
                    foreach (var kind in kinds)
                    {
                        var label = kind == WindowKind.Weekly ? "Window_Weekly" : "Window_FiveHour";
                        service.Evaluate(providerId, providerId, new UsageWindow(label, kind, 90, resetsAt, 300), threshold: 50, enabled: true, notifyOnReset: false, Start);
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(providerIds.Length * kinds.Length, raised.Count);
        foreach (var providerId in providerIds)
            foreach (var kind in kinds)
                Assert.Single(raised, n => n.ProviderId == providerId && n.Kind == kind);
    }

    [Fact]
    public void RestartingAgainstTheSameDirectoryDoesNotRepeatANotification()
    {
        var directory = TempDirectory();
        var resetsAt = Start.AddHours(5);

        var first = new NotificationService(directory, Start);
        var raisedByFirst = new List<ThresholdNotification>();
        first.NotificationRaised += n => raisedByFirst.Add(n);
        first.Evaluate("claude", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start);
        Assert.Single(raisedByFirst);

        // Stands in for a process restart: a fresh instance reads the same directory shortly
        // afterwards, well before the window's own reset time - it must pick up the disarmed,
        // already-notified state rather than starting fresh.
        var second = new NotificationService(directory, Start.AddMinutes(1));
        var raisedBySecond = new List<ThresholdNotification>();
        second.NotificationRaised += n => raisedBySecond.Add(n);
        second.Evaluate("claude", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(1));

        Assert.Empty(raisedBySecond);
    }

    [Fact]
    public void AnEntryWhoseWindowAlreadyResetIsDroppedOnLoadAndNotifiesAgain()
    {
        var directory = TempDirectory();
        var resetsAt = Start.AddHours(5);

        var first = new NotificationService(directory, Start);
        first.Evaluate("claude", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start);

        // Recreated well after that saved window's own reset time - the loaded entry must be
        // dropped instead of leaving the notification disarmed forever.
        var reloadNow = resetsAt.AddDays(1);
        var second = new NotificationService(directory, reloadNow);
        var raisedBySecond = new List<ThresholdNotification>();
        second.NotificationRaised += n => raisedBySecond.Add(n);
        second.Evaluate("claude", "Claude", FiveHour(90, reloadNow.AddHours(5)), threshold: 85, enabled: true, notifyOnReset: false, reloadNow);

        Assert.Single(raisedBySecond);
    }

    [Fact]
    public void AStateSavedUnderALegacyCopilotLabelStillSuppressesTheRepeat()
    {
        var directory = TempDirectory();
        var resetsAt = Start.AddDays(20);
        var first = new NotificationService(directory, Start);
        first.Evaluate("copilot", "Copilot", new UsageWindow("Premium", WindowKind.Other, 90, resetsAt, windowMinutes: null), threshold: 85, enabled: true, notifyOnReset: false, Start);

        var second = new NotificationService(directory, Start.AddMinutes(1));
        var raised = new List<ThresholdNotification>();
        second.NotificationRaised += n => raised.Add(n);
        second.Evaluate("copilot", "Copilot", new UsageWindow("Window_CopilotPremium", WindowKind.Other, 90, resetsAt, windowMinutes: null), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(1));

        Assert.Empty(raised);
    }

    [Fact]
    public void RemovingAnAccountForgetsItsStateOnDiskButKeepsTheOthers()
    {
        var directory = TempDirectory();
        var resetsAt = Start.AddDays(20);
        var first = new NotificationService(directory, Start);
        first.Evaluate("claude#2", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start);
        first.Evaluate("claude", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start);

        first.RemoveAccount("claude#2");

        var second = new NotificationService(directory, Start.AddMinutes(1));
        var raised = new List<ThresholdNotification>();
        second.NotificationRaised += n => raised.Add(n);
        second.Evaluate("claude#2", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(1));
        second.Evaluate("claude", "Claude", FiveHour(90, resetsAt), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(1));

        Assert.Equal(["claude#2"], raised.Select(n => n.ProviderId).ToList());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACurrentCopilotEntryWinsOverALegacyOneWhateverTheFileOrder(bool legacyFirst)
    {
        var directory = TempDirectory();
        var resetsAt = Start.AddDays(20);
        // The legacy entry says "already notified", the current one says "armed again": the current one is the truth.
        var legacy = new { ProviderId = "copilot", Kind = WindowKind.Other, Armed = false, LastNotifiedAt = (DateTimeOffset?)Start, LastResetsAt = (DateTimeOffset?)resetsAt, Label = "Premium" };
        var current = new { ProviderId = "copilot", Kind = WindowKind.Other, Armed = true, LastNotifiedAt = (DateTimeOffset?)null, LastResetsAt = (DateTimeOffset?)resetsAt, Label = "Window_CopilotPremium" };
        object[] entries = legacyFirst ? [legacy, current] : [current, legacy];
        File.WriteAllText(Path.Combine(directory, "notifications.json"), JsonSerializer.Serialize(entries));

        var service = new NotificationService(directory, Start.AddMinutes(1));
        var raised = new List<ThresholdNotification>();
        service.NotificationRaised += n => raised.Add(n);
        service.Evaluate("copilot", "Copilot", new UsageWindow("Window_CopilotPremium", WindowKind.Other, 90, resetsAt, windowMinutes: null), threshold: 85, enabled: true, notifyOnReset: false, Start.AddMinutes(1));

        Assert.Single(raised);
    }
}
