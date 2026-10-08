using AiUsage.Models;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class ThresholdNotificationTests
{
    [Fact]
    public void AThirdWindowKindRendersItsOwnLabelInsteadOfBeingMistakenForFiveHour()
    {
        var notification = new ThresholdNotification("codex", "Codex", WindowKind.Other, 92, DateTimeOffset.UtcNow.AddHours(1));

        var text = notification.Text(DateTimeOffset.UtcNow);

        Assert.Contains(LocalizationService.Instance["Window.Other"], text, StringComparison.Ordinal);
        Assert.DoesNotContain(LocalizationService.Instance["Window.FiveHour"], text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANotificationWithNoKnownResetTimeComesFromItsOwnResourceKey()
    {
        LocalizationService.Instance.SetLanguage("en");
        try
        {
            var notification = new ThresholdNotification("codex", "Codex", WindowKind.FiveHour, 92, ResetsAt: null);

            var text = notification.Text(DateTimeOffset.UtcNow);

            var expected = LocalizationService.Instance.Format("Notify.ThresholdNoReset", "Codex",
                LocalizationService.Instance["Window.FiveHour"], StatusTextMap.UsagePercent(92));
            Assert.Equal(expected, text);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void TheFormattedBalloonSaysResetsExactlyOnce()
    {
        LocalizationService.Instance.SetLanguage("en");
        try
        {
            var notification = new ThresholdNotification("claude", "Claude", WindowKind.FiveHour, 92, DateTimeOffset.UtcNow.AddHours(2).AddMinutes(10));

            var text = notification.Text(DateTimeOffset.UtcNow);

            var occurrences = text.Split("resets", StringSplitOptions.None).Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }
}
