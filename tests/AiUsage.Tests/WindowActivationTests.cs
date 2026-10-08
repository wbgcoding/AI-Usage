using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>
/// The pure decision behind <see cref="WindowActivation.TryActivate"/>: a refused activation
/// never gets a second attempt. Both facts below spy on the call count to prove that directly -
/// "does not escalate" means the underlying activation function is called exactly once, no matter
/// which way it answers.
/// </summary>
public class WindowActivationTests
{
    [Fact]
    public void A_successful_activation_calls_activate_exactly_once()
    {
        var callCount = 0;
        bool Activate()
        {
            callCount++;
            return true;
        }

        var result = WindowActivation.TryActivate(Activate);

        Assert.True(result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void A_refused_activation_calls_activate_exactly_once_and_does_not_escalate()
    {
        var callCount = 0;
        bool Activate()
        {
            callCount++;
            return false;
        }

        var result = WindowActivation.TryActivate(Activate);

        Assert.False(result);
        Assert.Equal(1, callCount);
    }
}
