using System.Globalization;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// Builds the tray icon's tooltip text: one line per visible provider,
/// name, then each window's percentage. The shell's own <c>NOTIFYICONDATA.szTip</c> field hard-caps
/// the tooltip at 127 characters - this shortens provider names to three characters once the full
/// text would not fit, rather than losing a whole line.
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
    public readonly record struct ProviderLine(string DisplayName, double? FiveHourPercent, double? WeeklyPercent);

    /// <summary>The provider lines, with <paramref name="firstLine"/> (a notice such as the pause)
    /// above them when given. The notice stays whole; the provider lines give way to it, whole line by
    /// whole line.</summary>
    public static string Build(IReadOnlyList<ProviderLine> providers, string? firstLine = null)
    {
        if (string.IsNullOrEmpty(firstLine))
            return BuildLines(providers, MaxLength);

        var notice = firstLine.Length <= MaxLength ? firstLine : firstLine[..MaxLength];
        var body = BuildLines(providers, MaxLength - notice.Length - 1, cutLastLine: false);
        return body.Length == 0 ? notice : notice + '\n' + body;
    }

    private static string BuildLines(IReadOnlyList<ProviderLine> providers, int limit, bool cutLastLine = true)
    {
        const string noProviders = "AI-Usage";
        if (providers.Count == 0)
            return noProviders.Length <= limit ? noProviders : "";
        if (limit <= 0)
            return "";

        var full = string.Join('\n', Lines(providers, providers.Select(p => p.DisplayName).ToList()));
        if (full.Length <= limit)
            return full;

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
        var lines = Lines(providers, shortNames);
        while (lines.Count > 1 && string.Join('\n', lines).Length > limit)
            lines.RemoveAt(lines.Count - 1);

        var shortened = string.Join('\n', lines);
        return shortened.Length <= limit ? shortened : cutLastLine ? shortened[..limit] : "";
    }

    // The window names are the short Tray.Short.* tokens, not the full Window.FiveHour / Window.Weekly
    // labels: those are too long for the 127-character cap above.
    private static List<string> Lines(IReadOnlyList<ProviderLine> providers, List<string> names)
    {
        var loc = LocalizationService.Instance;
        var fiveHour = loc["Tray.Short.FiveHour"];
        var week = loc["Tray.Short.Week"];
        return providers.Select((p, i) =>
            $"{names[i]} {fiveHour} {FormatPercent(p.FiveHourPercent)} · {week} {FormatPercent(p.WeeklyPercent)}").ToList();
    }

    private static string FormatPercent(double? percent) =>
        percent is { } value ? StatusTextMap.FormatPercent(StatusTextMap.UsagePercent(value).ToString(CultureInfo.CurrentCulture)) : "–";
}
