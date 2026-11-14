using AiUsage.Views;

namespace AiUsage.Tests;

public class WebSignInFlowShutdownTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_sign_in_window_closed_while_the_app_keeps_running_reports_its_result(bool signedIn)
    {
        bool? reported = null;
        var handler = WebSignInFlow.SkipDuringShutdown(() => signedIn, value => reported = value, () => false);

        handler(null, EventArgs.Empty);

        Assert.Equal(signedIn, reported);
    }

    [Fact]
    public void A_sign_in_window_closed_by_the_app_exit_starts_no_follow_up()
    {
        var calls = 0;
        var handler = WebSignInFlow.SkipDuringShutdown(() => true, _ => calls++, () => true);

        handler(null, EventArgs.Empty);

        Assert.Equal(0, calls);
    }
}
