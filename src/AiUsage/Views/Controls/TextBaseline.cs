using System.Windows.Controls;

namespace AiUsage.Views.Controls;

/// <summary>Where a single-line text's baseline sits below the top of its layout box, so texts of
/// different sizes in one row can share a baseline instead of a bottom edge (a smaller font has a
/// shorter descent, so bottom-aligned it sits visibly low).</summary>
internal static class TextBaseline
{
    public static double Of(TextBlock text) => text.Margin.Top + text.FontFamily.Baseline * text.FontSize;
}
