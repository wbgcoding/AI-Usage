using System.Text.Json;
using AiUsage.Models;
using AiUsage.Services;
using static AiUsage.Providers.Parsing.JsonReading;

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

    // The two window lengths a Codex plan reports; any other length is mapped onto one of the two bars.
    private static readonly int[] KnownWindowMinutes = [300, 10080];

    /// <summary>
    /// What in one rate-limit container (a block that holds the windows side by side) no longer
    /// matches what the Codex reads were written for: a window under a name they do not know, a known
    /// window without a <c>used_percent</c> field, a window length that is neither five hours nor a
    /// week (it still lands on the closer bar). A window that is simply null is the normal shape for
    /// "no number" and says nothing. Names and lengths only, never a usage figure.
    /// </summary>
    internal static void CollectNotes(JsonElement container, string containerName, string[] knownWindows, List<string> notes)
    {
        if (container.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in container.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            var name = ReadChangeLog.SafeName(property.Name);
            var hasPercent = property.Value.TryGetProperty("used_percent", out var percentEl);
            if (!knownWindows.Contains(property.Name))
            {
                if (hasPercent && percentEl.ValueKind == JsonValueKind.Number)
                    notes.Add($"'{ReadChangeLog.SafeName(containerName)}' holds an unknown window '{name}', which is not shown.");
                continue;
            }

            if (!hasPercent)
            {
                notes.Add($"the window '{name}' has no used_percent field (keys: {ReadChangeLog.SafeNames(property.Value.EnumerateObject().Select(p => p.Name))}).");
                continue;
            }

            long? minutes = TryGetInt32(property.Value, "window_minutes");
            minutes ??= TryGetInt64(property.Value, "limit_window_seconds") is { } seconds ? seconds / 60 : null;
            if (PlausibleMinutesOrNull(minutes) is { } length && !KnownWindowMinutes.Contains(length))
                notes.Add($"the window '{name}' runs {length} minutes, neither 5 hours nor 7 days; it is shown as the bar its length is closer to.");
        }
    }

    internal static string LabelFor(WindowKind kind) => kind == WindowKind.Weekly ? "Window_Weekly" : "Window_FiveHour";
}
