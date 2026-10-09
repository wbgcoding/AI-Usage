namespace AiUsage.Models;

/// <summary>The three window levels the widget can sit on, as stored in <see cref="AppSettings.WindowLayer"/>.</summary>
public static class WindowLayers
{
    /// <summary>Above every other window.</summary>
    public const string OnTop = "OnTop";

    /// <summary>An ordinary window.</summary>
    public const string Normal = "Normal";

    /// <summary>Behind every other window, visible when you look at the desktop.</summary>
    public const string Desktop = "Desktop";

    public static readonly IReadOnlyList<string> All = [OnTop, Normal, Desktop];

    /// <summary>The stored value when it is one of the three, otherwise <see cref="Normal"/> (a missing
    /// or hand-edited value never leaves the widget without a level).</summary>
    public static string Normalize(string? value) =>
        All.FirstOrDefault(layer => string.Equals(layer, value, StringComparison.OrdinalIgnoreCase)) ?? Normal;

    /// <summary>What an old settings file's single "always on top" flag stands for.</summary>
    public static string FromLegacy(bool? alwaysOnTop) => alwaysOnTop == true ? OnTop : Normal;
}
