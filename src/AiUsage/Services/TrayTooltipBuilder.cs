using System.Globalization;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// The single highest usage row across a set of visible, numbered tiles: the one computation
/// behind both the title bar summary (<see cref="AiUsage.ViewModels.MainViewModel"/>) and the tray
/// icon/tooltip, pulled out so the two surfaces read the same provider, window kind, percent and
/// level instead of picking it independently and only agreeing by coincidence. Each caller keeps
/// its own row objects and formats the four fields its own way.
/// </summary>
public readonly record struct HighestUsageRow(string ProviderName, WindowKind Kind, double Percent, UsageLevel Level);

public static class UsageHighlight
{
    /// <summary>Highest by <see cref="HighestUsageRow.Percent"/>; a tie keeps whichever row was
    /// seen first, the same rule the previous separate implementations both used.</summary>
    public static HighestUsageRow? Highest(IEnumerable<HighestUsageRow> rows)
    {
        HighestUsageRow? best = null;
        foreach (var row in rows)
        {
            if (best is null || row.Percent > best.Value.Percent)
                best = row;
        }
        return best;
    }
}

/// <summary>
/// Builds the tray icon's tooltip text: one line per visible provider,
/// name, then each window's percentage. The shell's own <c>NOTIFYICONDATA.szTip</c> field hard-caps
/// the tooltip at 127 characters - this shortens provider names to three characters once the full
/// text would not fit, rather than losing a whole line.
/// </summary>
public static class TrayTooltipBuilder
{
    public const int MaxLength = 127;

    // Null, not 0: a provider that never delivered a given window (e.g. Claude's weekly slot) must
    // say so rather than show an invented "0 %".
    public readonly record struct ProviderLine(string DisplayName, double? FiveHourPercent, double? WeeklyPercent);

    public static string Build(IReadOnlyList<ProviderLine> providers)
    {
        if (providers.Count == 0)
            return "AI-Usage";

        var full = string.Join('\n', Lines(providers, providers.Select(p => p.DisplayName).ToList()));
        if (full.Length <= MaxLength)
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
        while (lines.Count > 1 && string.Join('\n', lines).Length > MaxLength)
            lines.RemoveAt(lines.Count - 1);

        var shortened = string.Join('\n', lines);
        return shortened.Length <= MaxLength ? shortened : shortened[..MaxLength];
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
