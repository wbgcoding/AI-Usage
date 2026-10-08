using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class HideToTrayFlowTests
{
    [Fact]
    public void The_hint_fires_the_first_time_and_never_again()
    {
        var hideCalls = 0;
        var balloonCalls = 0;
        var markCalls = 0;

        var firedFirst = HideToTrayFlow.Run(hintAlreadyShown: false, () => hideCalls++, () => balloonCalls++, () => markCalls++);
        var firedSecond = HideToTrayFlow.Run(hintAlreadyShown: true, () => hideCalls++, () => balloonCalls++, () => markCalls++);

        Assert.True(firedFirst);
        Assert.False(firedSecond);
        Assert.Equal(2, hideCalls);
        Assert.Equal(1, balloonCalls);
        Assert.Equal(1, markCalls);
    }

    [Fact]
    public void The_window_is_always_hidden_even_when_the_hint_is_skipped()
    {
        var hidden = false;

        HideToTrayFlow.Run(hintAlreadyShown: true, () => hidden = true, () => { }, () => { });

        Assert.True(hidden);
    }
}
