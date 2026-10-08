namespace AiUsage.Services;

/// <summary>
/// Picks which density stage the tile list renders in. Single source of truth for the stage order
/// and for the height model <see cref="WindowPlacementService"/> uses as a first estimate before the
/// real tiles can be measured.
///
/// A hand-choice from the eye menu wins unconditionally. Otherwise the largest stage whose real,
/// measured content height fits the available height is taken - measured, never estimated, so a tile
/// that turns out taller than the model assumed can never leave the content cut off. The choice
/// is a plain function of the available height, so there is no feedback loop to damp and no
/// hysteresis: the window's height does not depend on the stage picked for it.
/// </summary>
public static class TileDensitySelector
{
    /// <summary>Rounding slack, so content that fits to within a pixel never counts as overflowing.</summary>
    public const double FitTolerance = 1;

    private static readonly TileDensity[] Order = [TileDensity.Full, TileDensity.Mini];

    // First-estimate heights per tile including the gap below it. Full is a tile with two bar rows and
    // the history chart (about 196 px) plus the 4 px gap.
    private static readonly Dictionary<TileDensity, double> Heights = new()
    {
        [TileDensity.Full] = 200,
        [TileDensity.Mini] = 44,
    };

    public static double HeightFor(TileDensity density) => Heights[density];

    /// <summary>The hand-chosen stage if there is one, else the largest stage whose
    /// <paramref name="contentHeight"/> fits <paramref name="availableHeight"/>, else Mini (the
    /// scroll viewer takes over from there). <paramref name="contentHeight"/> is asked from the
    /// largest stage down and never past the first that fits.</summary>
    public static TileDensity SelectFitting(Func<TileDensity, double> contentHeight, double availableHeight, TileDensity? manualOverride)
    {
        if (manualOverride is { } chosen)
            return chosen;

        foreach (var density in Order)
        {
            if (contentHeight(density) <= availableHeight + FitTolerance)
                return density;
        }

        return TileDensity.Mini;
    }
}
