using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class TileDensitySelectorTests
{
    // Measured content heights of five tiles per stage, deliberately taller than the fixed model.
    private static double Measured(TileDensity density) => density switch
    {
        TileDensity.Full => 910,
        _ => 260,
    };

    [Theory]
    [InlineData(2000, TileDensity.Full)]
    [InlineData(910, TileDensity.Full)]
    [InlineData(909.5, TileDensity.Full)]
    [InlineData(700, TileDensity.Mini)]
    [InlineData(300, TileDensity.Mini)]
    [InlineData(100, TileDensity.Mini)]
    public void Automatic_takes_the_largest_stage_whose_measured_content_fits(double availableHeight, TileDensity expected) =>
        Assert.Equal(expected, TileDensitySelector.SelectFitting(Measured, availableHeight, manualOverride: null));

    // Coming from a small stage must never hold the tiles small: the pick depends on the height alone.
    [Fact]
    public void Automatic_jumps_straight_to_full_when_the_window_is_tall_enough()
    {
        var small = TileDensitySelector.SelectFitting(Measured, 300, manualOverride: null);
        var tall = TileDensitySelector.SelectFitting(Measured, 1000, manualOverride: null);

        Assert.Equal(TileDensity.Mini, small);
        Assert.Equal(TileDensity.Full, tall);
    }

    [Fact]
    public void Automatic_stops_measuring_at_the_first_stage_that_fits()
    {
        var asked = new List<TileDensity>();

        TileDensitySelector.SelectFitting(d => { asked.Add(d); return Measured(d); }, 2000, manualOverride: null);

        Assert.Equal([TileDensity.Full], asked);
    }

    [Theory]
    [InlineData(TileDensity.Full, 0)]
    [InlineData(TileDensity.Full, 10_000)]
    [InlineData(TileDensity.Mini, 0)]
    [InlineData(TileDensity.Mini, 10_000)]
    public void A_hand_chosen_stage_wins_across_the_full_height_range(TileDensity manual, double availableHeight) =>
        Assert.Equal(manual, TileDensitySelector.SelectFitting(Measured, availableHeight, manualOverride: manual));
}
