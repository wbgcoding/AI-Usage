using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The extended style click-through writes in one step, so the window never passes through
/// an intermediate look (transparent but not yet layered, or layered at the wrong alpha).</summary>
public class ClickThroughStyleTests
{
    private const long Layered = 0x80000;
    private const long Transparent = 0x20;
    private const long Other = 0x100;

    [Fact]
    public void Switching_on_sets_transparent_and_layered_together()
    {
        Assert.Equal(Other | Transparent | Layered, NativeWindowStyle.ClickThroughExStyle(Other, true, 85));
    }

    [Fact]
    public void Switching_on_at_full_opacity_still_layers_the_window()
    {
        Assert.Equal(Transparent | Layered, NativeWindowStyle.ClickThroughExStyle(0, true, 100));
    }

    [Fact]
    public void Switching_off_on_an_opaque_window_makes_it_ordinary_again()
    {
        Assert.Equal(Other, NativeWindowStyle.ClickThroughExStyle(Other | Transparent | Layered, false, 100));
    }

    [Fact]
    public void Switching_off_on_a_translucent_window_keeps_it_layered()
    {
        Assert.Equal(Other | Layered, NativeWindowStyle.ClickThroughExStyle(Other | Transparent | Layered, false, 85));
    }

    [Fact]
    public void An_unchanged_state_returns_the_style_unchanged()
    {
        var on = Other | Transparent | Layered;
        Assert.Equal(on, NativeWindowStyle.ClickThroughExStyle(on, true, 85));
    }
}
