using System.Globalization;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// Builds the tray icon's tooltip text: one line per visible provider,
/// name, then each window's percentage. The shell's own <c>NOTIFYICONDATA.szTip</c> field hard-caps
/// the tooltip at 127 characters - once the full text would not fit, this drops the reset times, then
/// the notes, then shortens provider names to three characters, rather than losing a whole line.
/// </summary>
public static class TrayTooltipBuilder
{
    public const int MaxLength = 127;

    /// <summary>The short clock time of the regional format ("14:30" or "2:30 PM"), as the pause
    /// texts show it.</summary>
    internal static string ShortTime(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>The first tooltip line while fetching is paused.</summary>
    internal static string PausedLine(DateTimeOffset until) =>
        LocalizationService.Instance.Format("Tray.PausedUntil", ShortTime(until));

    // Null, not 0: a provider that never delivered a given window (e.g. Claude's weekly slot) must
    // say so rather than show an invented "0 %".
    // Note: short text after the percentages when the numbers are not current (sign-in needed, as of a time).
    // The reset times follow their window's percentage when known and still ahead: "62 % (14:30)".
    // ProviderId names the line for the popup's automation ids; the shell tooltip ignores it.
    public readonly record struct ProviderLine(
        string DisplayName, double? FiveHourPercent, double? WeeklyPercent, string? Note = null,
        DateTimeOffset? FiveHourResetsAt = null, DateTimeOffset? WeeklyResetsAt = null, string? ProviderId = null);

    /// <summary>A reset moment in few characters: the clock time when it is today, else the short
    /// weekday and the clock time ("Mo 14:30"), both in the regional format.</summary>
    internal static string ResetTime(DateTimeOffset moment, DateTimeOffset now)
    {
        var local = moment.ToLocalTime();
        var clock = ShortTime(moment);
        return local.Date == now.ToLocalTime().Date
            ? clock
            : local.ToString("ddd", CultureInfo.CurrentCulture) + " " + clock;
    }

    /// <summary>The provider lines, with <paramref name="firstLine"/> (a notice such as the pause)
    /// above them when given. The notice stays whole; the provider lines give way to it, whole line by
    /// whole line. <paramref name="now"/> decides "today" for the reset times (default: the
    /// clock).</summary>
    public static string Build(IReadOnlyList<ProviderLine> providers, string? firstLine = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        if (string.IsNullOrEmpty(firstLine))
            return BuildLines(providers, MaxLength, at);

        var notice = firstLine.Length <= MaxLength ? firstLine : firstLine[..MaxLength];
        var body = BuildLines(providers, MaxLength - notice.Length - 1, at, cutLastLine: false);
        return body.Length == 0 ? notice : notice + '\n' + body;
    }

    private static string BuildLines(IReadOnlyList<ProviderLine> providers, int limit, DateTimeOffset now, bool cutLastLine = true)
    {
        const string noProviders = AppInfo.ProductName;
        if (providers.Count == 0)
            return noProviders.Length <= limit ? noProviders : "";
        if (limit <= 0)
            return "";

        // Overflow order: all reset times, then the notes (the last line's first), then names cut
        // short, then whole lines.
        var fullNames = providers.Select(p => p.DisplayName).ToList();
        var withTimes = string.Join('\n', Lines(providers, fullNames, providers.Count, now));
        if (withTimes.Length <= limit)
            return withTimes;

        for (var notes = providers.Count; notes >= 0; notes--)
        {
            var text = string.Join('\n', Lines(providers, fullNames, notes, null));
            if (text.Length <= limit)
                return text;
        }

        // Names cut to three characters; two that end up alike get a number so they stay apart.
        var cut = providers.Select(p => p.DisplayName.Length <= 3 ? p.DisplayName : p.DisplayName[..3]).ToList();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var shortNames = cut.Select(name =>
        {
            if (cut.Count(other => other == name) < 2)
                return name;
            seen[name] = seen.GetValueOrDefault(name) + 1;
            return name + seen[name].ToString(CultureInfo.InvariantCulture);
        }).ToList();

        // Whole lines go, last first, rather than a line cut in the middle.
        var lines = Lines(providers, shortNames, 0, null);
        while (lines.Count > 1 && string.Join('\n', lines).Length > limit)
            lines.RemoveAt(lines.Count - 1);

        var shortened = string.Join('\n', lines);
        return shortened.Length <= limit ? shortened : cutLastLine ? shortened[..limit] : "";
    }

    // The window names are the short Tray.Short.* tokens, not the full Window.FiveHour / Window.Weekly
    // labels: those are too long for the 127-character cap above.
    // Only the first <paramref name="notes"/> lines keep their note; reset times show only when
    // <paramref name="timesNow"/> is given.
    private static List<string> Lines(IReadOnlyList<ProviderLine> providers, List<string> names, int notes, DateTimeOffset? timesNow)
    {
        var loc = LocalizationService.Instance;
        var fiveHour = loc["Tray.Short.FiveHour"];
        var week = loc["Tray.Short.Week"];
        return providers.Select((p, i) =>
            $"{names[i]} {fiveHour} {WindowPart(p.FiveHourPercent, p.FiveHourResetsAt, timesNow)} · {week} {WindowPart(p.WeeklyPercent, p.WeeklyResetsAt, timesNow)}"
            + (i >= notes || string.IsNullOrEmpty(p.Note) ? "" : " " + p.Note)).ToList();
    }

    // "62 % (14:30)"; a window without a percentage has no time, and a reset already past says nothing.
    private static string WindowPart(double? percent, DateTimeOffset? resetsAt, DateTimeOffset? now)
    {
        var text = FormatPercent(percent);
        return percent is not null && now is { } at && resetsAt is { } reset && reset > at
            ? text + " (" + ResetTime(reset, at) + ")"
            : text;
    }

    private static string FormatPercent(double? percent) =>
        percent is { } value ? StatusTextMap.FormatUsagePercent(value) : "–";
}
