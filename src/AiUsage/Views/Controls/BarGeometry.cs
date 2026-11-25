namespace AiUsage.Views.Controls;

/// <summary>The one filled-width rule of every gauge drawn by hand (<see cref="FractionBar"/>,
/// <see cref="SegmentedBarFill"/>) and of the plain usage bar fill in UsageBar.xaml.</summary>
public static class BarGeometry
{
    /// <summary>Narrowest fill a bar draws, so a barely used window still shows a mark.</summary>
    public const double MinFilledWidth = 3;

    /// <summary>The width of the filled part on a track <paramref name="trackWidth"/> wide: nothing for
    /// an empty or unknown fraction, otherwise at least <see cref="MinFilledWidth"/> and at most the
    /// whole track. A fraction above 1 counts as 1.</summary>
    internal static double FilledWidth(double fraction, double trackWidth)
    {
        if (!(fraction > 0) || trackWidth <= 0)
            return 0;
        return Math.Min(trackWidth, Math.Max(MinFilledWidth, trackWidth * Math.Min(fraction, 1)));
    }
}
