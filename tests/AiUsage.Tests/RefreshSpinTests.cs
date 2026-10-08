using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

public class RefreshSpinTests
{
    private const double Frame = 1.0 / 60;

    private static RefreshSpinState SpinFor(double seconds)
    {
        var state = RefreshSpinState.Rest;
        for (var t = 0.0; t < seconds; t += Frame)
            state = RefreshSpin.Step(state, Frame, spinning: true);
        return state;
    }

    [Fact]
    public void The_start_gains_speed_steadily_up_to_the_full_speed()
    {
        var state = RefreshSpinState.Rest;
        var previous = 0.0;
        var steps = (int)Math.Ceiling(RefreshSpin.RampSeconds / Frame);
        for (var i = 0; i < steps; i++)
        {
            state = RefreshSpin.Step(state, Frame, spinning: true);
            Assert.True(state.Speed > previous, $"speed did not rise at step {i}");
            Assert.True(state.Speed - previous < RefreshSpin.MaxSpeed / 4, $"speed jumped at step {i}");
            previous = state.Speed;
        }

        Assert.Equal(RefreshSpin.MaxSpeed, state.Speed, 6);
        state = RefreshSpin.Step(state, Frame, spinning: true);
        Assert.Equal(RefreshSpin.MaxSpeed, state.Speed, 6);
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.1)]
    [InlineData(0.25)]
    [InlineData(0.9)]
    [InlineData(1.3)]
    [InlineData(2.75)]
    public void Stopping_always_ends_on_a_multiple_of_360_after_a_full_turn(double spinSeconds)
    {
        var state = SpinFor(spinSeconds);

        var guard = 0;
        while (!state.IsAtRest && guard++ < 10_000)
        {
            var next = RefreshSpin.Step(state, Frame, spinning: false);
            Assert.True(next.Speed <= state.Speed, "speed rose while stopping");
            state = next;
        }

        Assert.True(state.IsAtRest);
        Assert.Equal(0, state.Angle % 360);
        Assert.True(state.Angle >= 360, $"stopped after {state.Angle} degrees");
    }

    // One more turn at full speed plus the firm brake: the glyph has a single arrow head, so a stop
    // can only rest on a whole turn and the worst case is bounded by the speed, not by the braking.
    [Fact]
    public void Stopping_from_full_speed_finishes_quickly_on_a_whole_turn_without_speeding_up()
    {
        var worst = 0.0;
        for (var offset = 0.0; offset < Frame; offset += Frame / 12)
        {
            for (var extraFrames = 0; extraFrames < 60; extraFrames++)
            {
                var state = SpinFor(RefreshSpin.RampSeconds + 0.1 + extraFrames * Frame);
                state = RefreshSpin.Step(state, offset + 1e-9, spinning: true);
                Assert.Equal(RefreshSpin.MaxSpeed, state.Speed, 6);

                var frames = 0;
                while (!state.IsAtRest && frames < 600)
                {
                    var next = RefreshSpin.Step(state, Frame, spinning: false);
                    Assert.True(next.Speed <= state.Speed, "speed rose while stopping");
                    state = next;
                    frames++;
                }

                Assert.True(state.IsAtRest);
                Assert.Equal(0, state.Angle % 360);
                worst = Math.Max(worst, frames * Frame);
            }
        }

        Assert.True(worst <= 1.2, $"worst stop took {worst:F3} s");
    }

    [Fact]
    public void Stopping_keeps_full_speed_while_a_whole_turn_is_still_ahead()
    {
        var running = new RefreshSpinState(10, RefreshSpin.MaxSpeed);

        var next = RefreshSpin.Step(running, Frame, spinning: false);

        Assert.Equal(RefreshSpin.MaxSpeed, next.Speed, 6);
        Assert.Equal(10 + RefreshSpin.MaxSpeed * Frame, next.Angle, 6);
    }

    [Fact]
    public void A_resting_glyph_stays_at_rest_and_a_new_start_counts_from_zero()
    {
        var rest = RefreshSpin.Step(RefreshSpinState.Rest, Frame, spinning: false);
        Assert.Equal(RefreshSpinState.Rest, rest);

        var restedAt720 = new RefreshSpinState(720, 0);
        var started = RefreshSpin.Step(restedAt720, Frame, spinning: true);
        Assert.True(started.Angle < 5);
    }
}
