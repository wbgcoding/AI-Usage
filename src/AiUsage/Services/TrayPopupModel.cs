using System.Globalization;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>One window line of the tray popup: "5 hours 62 % · 14:30". <see cref="Reset"/> is
/// null when the reset moment is unknown or already past.</summary>
public sealed record TrayPopupRow(string Label, string Percent, string? Reset)
{
    /// <summary>The whole line as one text, the way a screen reader gets it.</summary>
    public string Text => Label + " " + Percent + (Reset is null ? "" : " · " + Reset);
}

/// <summary>One provider's block: name, optional note, and a line per window it has a percentage for.</summary>
public sealed record TrayPopupBlock(string AutomationId, string Name, string? Note, IReadOnlyList<TrayPopupRow> Rows)
{
    public bool HasNote => !string.IsNullOrEmpty(Note);
}

/// <summary>
/// What the tray popup shows, without any UI type: the pause notice on top, then one block per
/// provider. Built from the same <see cref="TrayTooltipBuilder.ProviderLine"/> list as the shell
/// tooltip, but without its 127 character cap, so every reset time shows.
/// </summary>
public sealed record TrayPopupModel(string? PausedLine, IReadOnlyList<TrayPopupBlock> Blocks)
{
    public bool HasPausedLine => !string.IsNullOrEmpty(PausedLine);

    /// <summary>True with neither a pause notice nor a provider: the view then shows the product name.</summary>
    public bool IsEmpty => !HasPausedLine && Blocks.Count == 0;

    /// <summary>The popup as plain lines, for tests and the screen reader name.</summary>
    public string PlainText => string.Join('\n',
        (HasPausedLine ? new[] { PausedLine! } : [])
        .Concat(Blocks.SelectMany(b => new[] { b.Name + (b.HasNote ? " " + b.Note : "") }.Concat(b.Rows.Select(r => r.Text)))));

    /// <summary><paramref name="now"/> decides "today" and which reset moments are past (default: the
    /// clock).</summary>
    public static TrayPopupModel Build(IReadOnlyList<TrayTooltipBuilder.ProviderLine> providers, DateTimeOffset? pausedUntil = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var loc = LocalizationService.Instance;
        var fiveHour = loc["Window.FiveHour"];
        var week = loc["Window.Weekly"];

        var blocks = providers.Select((p, i) => new TrayPopupBlock(
            "TrayPopup.Provider." + (string.IsNullOrEmpty(p.ProviderId) ? i.ToString(CultureInfo.InvariantCulture) : p.ProviderId),
            p.DisplayName,
            string.IsNullOrEmpty(p.Note) ? null : p.Note,
            Rows(p, fiveHour, week, at))).ToList();

        return new TrayPopupModel(pausedUntil is { } until ? TrayTooltipBuilder.PausedLine(until) : null, blocks);
    }

    // A window the provider has no percentage for (no such limit on the plan) gets no line at all,
    // as in the shell tooltip.
    private static List<TrayPopupRow> Rows(TrayTooltipBuilder.ProviderLine p, string fiveHour, string week, DateTimeOffset now)
    {
        var rows = new List<TrayPopupRow>(2);
        if (p.FiveHourPercent is { } five)
            rows.Add(Row(fiveHour, five, p.FiveHourResetsAt, now));
        if (p.WeeklyPercent is { } weekly)
            rows.Add(Row(week, weekly, p.WeeklyResetsAt, now));
        return rows;
    }

    // The reset moment alone ("14:30", "Fri 14:30"); a reset already past says nothing.
    private static TrayPopupRow Row(string label, double percent, DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        var text = StatusTextMap.FormatUsagePercent(percent);
        if (resetsAt is not { } reset || reset <= now)
            return new TrayPopupRow(label, text, null);
        return new TrayPopupRow(label, text, TrayTooltipBuilder.ResetTime(reset, now));
    }
}
