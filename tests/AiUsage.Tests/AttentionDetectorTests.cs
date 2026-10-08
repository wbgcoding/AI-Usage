using AiUsage.Providers.Parsing;
using Xunit;

namespace AiUsage.Tests;

public class AttentionDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TwelveHours = TimeSpan.FromHours(12);

    [Fact]
    public void AssistantTurnThirtySecondsOldIsWaiting() =>
        Assert.True(AttentionDetector.IsWaiting(SessionRecordKind.AssistantTurn, Now - TimeSpan.FromSeconds(30), Now, TwelveHours));

    [Fact]
    public void AssistantTurnFiveSecondsOldIsNotWaitingYet() =>
        Assert.False(AttentionDetector.IsWaiting(SessionRecordKind.AssistantTurn, Now - TimeSpan.FromSeconds(5), Now, TwelveHours));

    [Fact]
    public void AssistantTurnTwentyHoursOldIsNoLongerWaiting() =>
        Assert.False(AttentionDetector.IsWaiting(SessionRecordKind.AssistantTurn, Now - TimeSpan.FromHours(20), Now, TwelveHours));

    [Fact]
    public void UserTurnIsNeverWaiting() =>
        Assert.False(AttentionDetector.IsWaiting(SessionRecordKind.UserTurn, Now - TimeSpan.FromSeconds(30), Now, TwelveHours));

    [Fact]
    public void ToolCallIsNeverWaiting() =>
        Assert.False(AttentionDetector.IsWaiting(SessionRecordKind.ToolCall, Now - TimeSpan.FromSeconds(30), Now, TwelveHours));

    [Fact]
    public void NoRecordAtAllIsNotWaiting() =>
        Assert.False(AttentionDetector.IsWaiting(null, null, Now, TwelveHours));

    [Fact]
    public void ExactlyTwentySecondsOldIsWaiting() =>
        Assert.True(AttentionDetector.IsWaiting(SessionRecordKind.AssistantTurn, Now - TimeSpan.FromSeconds(20), Now, TwelveHours));

    [Fact]
    public void ExactlyTwelveHoursOldIsStillWaiting() =>
        Assert.True(AttentionDetector.IsWaiting(SessionRecordKind.AssistantTurn, Now - TimeSpan.FromHours(12), Now, TwelveHours));

    [Fact]
    public void JustOverTwelveHoursOldIsNoLongerWaiting() =>
        Assert.False(AttentionDetector.IsWaiting(SessionRecordKind.AssistantTurn, Now - TimeSpan.FromHours(12) - TimeSpan.FromMinutes(1), Now, TwelveHours));

    [Fact]
    public void AThreeHourOldTurnIsNotWaitingAtTheDefaultMaxAge() =>
        Assert.False(AttentionDetector.IsWaiting(SessionRecordKind.AssistantTurn, Now - TimeSpan.FromHours(3), Now, AttentionDetector.DefaultMaxAge));

    [Fact]
    public void AThreeHourOldTurnIsWaitingAtAFourHourSetting() =>
        Assert.True(AttentionDetector.IsWaiting(SessionRecordKind.AssistantTurn, Now - TimeSpan.FromHours(3), Now, TimeSpan.FromHours(4)));
}
