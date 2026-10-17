using AiUsage.Models;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Which bar a Codex rate-limit window belongs to. The primary block is not always the five-hour
/// window: a weekly-only plan reports its week as the primary block and no second one, so the length
/// the window reports decides, and the position only answers when the length is missing.
/// </summary>
internal static class CodexWindowKinds
{
    /// <summary>A day or longer reads as the weekly window, anything shorter as the five-hour one.</summary>
    private const int WeeklyThresholdMinutes = 1440;

    /// <summary>The kind for a window of <paramref name="windowMinutes"/>, or <paramref name="byPosition"/>
    /// when the length is unknown.</summary>
    internal static WindowKind KindFor(int? windowMinutes, WindowKind byPosition) => windowMinutes switch
    {
        >= WeeklyThresholdMinutes => WindowKind.Weekly,
        > 0 => WindowKind.FiveHour,
        _ => byPosition,
    };

    /// <summary>The kind for a window that follows <paramref name="earlier"/>: when both would resolve to
    /// the same kind the later one keeps its position, so two bars never claim one slot.</summary>
    internal static WindowKind KindFor(int? windowMinutes, WindowKind byPosition, WindowKind? earlier)
    {
        var kind = KindFor(windowMinutes, byPosition);
        return earlier == kind ? byPosition : kind;
    }

    /// <summary>No usage window runs longer than about a year.</summary>
    private const long MaxWindowMinutes = 400L * 24 * 60;

    /// <summary>A reported length as a positive minute count no longer than a year, otherwise unknown -
    /// an absurd length would push the window's start before the first representable instant.</summary>
    internal static int? PlausibleMinutesOrNull(long? minutes) =>
        minutes is > 0 and <= MaxWindowMinutes ? (int)minutes.Value : null;

    internal static string LabelFor(WindowKind kind) => kind == WindowKind.Weekly ? "Window_Weekly" : "Window_FiveHour";
}
