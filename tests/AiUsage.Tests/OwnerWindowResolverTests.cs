using AiUsage.Views;

namespace AiUsage.Tests;

/// <summary>
/// The pure decision behind <see cref="OwnerWindowResolver"/>, exercised through its generic
/// <c>Resolve</c> overload with plain string stand-ins instead of real <c>Window</c> instances -
/// <see cref="AccessibilityTests"/> documents why constructing real WPF windows outside a dedicated,
/// timeout-guarded thread is unsafe in this suite, and the generic signature exists precisely so this
/// decision never needs one.
/// </summary>
public class OwnerWindowResolverTests
{
    [Fact]
    public void PreferredWindowShownWins()
    {
        var result = OwnerWindowResolver.Resolve<string>("preferred", preferredIsShown: true, null, activeIsShown: false);

        Assert.Equal("preferred", result);
    }

    [Fact]
    public void FallsBackToActiveWindowWhenPreferredNotShown()
    {
        var result = OwnerWindowResolver.Resolve("preferred", preferredIsShown: false, "active", activeIsShown: true);

        Assert.Equal("active", result);
    }

    [Fact]
    public void ReturnsNullWhenNothingIsShown()
    {
        var result = OwnerWindowResolver.Resolve("preferred", preferredIsShown: false, "active", activeIsShown: false);

        Assert.Null(result);
    }

    [Fact]
    public void PreferredWindowShownButNotActiveStillWins()
    {
        // A different window is currently active ("active" here stands in for whatever
        // Application.Current reports as its active window) - the preferred candidate still wins,
        // because winning only depends on being shown, never on being the active one.
        var result = OwnerWindowResolver.Resolve("preferred", preferredIsShown: true, "active", activeIsShown: true);

        Assert.Equal("preferred", result);
    }

    [Fact]
    public void ReturnsNullWhenNoCandidatesAtAll()
    {
        var result = OwnerWindowResolver.Resolve<string>(null, preferredIsShown: false, null, activeIsShown: false);

        Assert.Null(result);
    }

    [Fact]
    public void NeedsCenterOnScreenWhenNoOwnerWasResolved()
    {
        Assert.True(OwnerWindowResolver.NeedsCenterOnScreen<string>(null));
    }

    [Fact]
    public void DoesNotNeedCenterOnScreenWhenAnOwnerWasResolved()
    {
        Assert.False(OwnerWindowResolver.NeedsCenterOnScreen("owner"));
    }
}
