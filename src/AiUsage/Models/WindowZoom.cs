namespace AiUsage.Models;

/// <summary>The zoom steps the widget offers, on top of the Windows display scaling of the monitor it is on.</summary>
public static class WindowZoom
{
    public const int DefaultPercent = 100;

    public static readonly IReadOnlyList<int> AllowedPercents = [90, 100, 125, 150];

    /// <summary>The smallest factor on offer, the floor for remembered window sizes.</summary>
    public static double MinFactor => AllowedPercents.Min() / 100.0;

    /// <summary>The stored percent when it is one of the offered steps, otherwise 100.</summary>
    public static int Normalize(int percent) => AllowedPercents.Contains(percent) ? percent : DefaultPercent;

    /// <summary>The scale factor for a stored percent (1.25 for 125).</summary>
    public static double Factor(int percent) => Normalize(percent) / 100.0;
}
