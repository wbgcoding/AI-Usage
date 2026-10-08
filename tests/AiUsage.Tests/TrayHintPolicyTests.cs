using AiUsage.Services;

namespace AiUsage.Tests;

public class TrayHintPolicyTests
{
    [Fact]
    public void Shows_the_hint_when_it_has_never_been_shown()
    {
        Assert.True(TrayHintPolicy.ShouldShowHint(alreadyShown: false));
    }

    [Fact]
    public void Does_not_show_the_hint_again_once_it_has_been_shown()
    {
        Assert.False(TrayHintPolicy.ShouldShowHint(alreadyShown: true));
    }
}
