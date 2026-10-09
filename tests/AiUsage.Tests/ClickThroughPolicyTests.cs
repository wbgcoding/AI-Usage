using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Tests;

public class ClickThroughPolicyTests
{
    [Fact]
    public void Switching_click_through_off_leaves_the_window_level_and_opacity_untouched()
    {
        var resolved = new ClickThroughPolicy().Resolve(clickThrough: false, windowLayer: WindowLayers.Normal, opacity: 100);

        Assert.Equal(WindowLayers.Normal, resolved.WindowLayer);
        Assert.Equal(100, resolved.WindowOpacityPercent);
    }

    [Fact]
    public void Switching_click_through_on_forces_the_on_top_level()
    {
        var resolved = new ClickThroughPolicy().Resolve(clickThrough: true, windowLayer: WindowLayers.Normal, opacity: 90);

        Assert.Equal(WindowLayers.OnTop, resolved.WindowLayer);
    }

    [Fact]
    public void Click_through_at_full_opacity_drops_to_the_fallback()
    {
        var resolved = new ClickThroughPolicy().Resolve(clickThrough: true, windowLayer: WindowLayers.OnTop, opacity: 100);

        Assert.Equal(ClickThroughPolicy.FallbackOpacityPercent, resolved.WindowOpacityPercent);
    }

    [Fact]
    public void Click_through_below_full_opacity_leaves_it_unchanged()
    {
        var resolved = new ClickThroughPolicy().Resolve(clickThrough: true, windowLayer: WindowLayers.OnTop, opacity: 80);

        Assert.Equal(80, resolved.WindowOpacityPercent);
    }

    [Fact]
    public void Click_through_when_already_on_top_stays_on_top()
    {
        var resolved = new ClickThroughPolicy().Resolve(clickThrough: true, windowLayer: WindowLayers.OnTop, opacity: 90);

        Assert.Equal(WindowLayers.OnTop, resolved.WindowLayer);
        Assert.Equal(90, resolved.WindowOpacityPercent);
    }

    [Fact]
    public void Turning_click_through_off_restores_the_opacity_active_before_it_was_turned_on()
    {
        var policy = new ClickThroughPolicy();

        var turnedOn = policy.Resolve(clickThrough: true, windowLayer: WindowLayers.Normal, opacity: 100);
        Assert.Equal(ClickThroughPolicy.FallbackOpacityPercent, turnedOn.WindowOpacityPercent);

        var turnedOff = policy.Resolve(clickThrough: false, windowLayer: WindowLayers.OnTop, opacity: turnedOn.WindowOpacityPercent);
        Assert.Equal(100, turnedOff.WindowOpacityPercent);
    }

    [Fact]
    public void Turning_click_through_off_keeps_an_opacity_the_user_picked_while_it_was_on()
    {
        var policy = new ClickThroughPolicy();

        var turnedOn = policy.Resolve(clickThrough: true, windowLayer: WindowLayers.Normal, opacity: 100);
        Assert.Equal(ClickThroughPolicy.FallbackOpacityPercent, turnedOn.WindowOpacityPercent);

        // The user moves the opacity slider themselves while click-through is still on.
        const double userChosenOpacity = 60;

        var turnedOff = policy.Resolve(clickThrough: false, windowLayer: WindowLayers.OnTop, opacity: userChosenOpacity);
        Assert.Equal(userChosenOpacity, turnedOff.WindowOpacityPercent);
    }
}
