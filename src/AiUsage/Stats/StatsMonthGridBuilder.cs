namespace AiUsage.Stats;

/// <summary>
/// Builds the year day-grid's own by-day list straight from the raw stats records - the one place
/// both the statistics window (<see cref="StatsViewModel"/>) and the widget's day-grid tile (<see
/// cref="ViewModels.DayGridTileViewModel"/>) compute it, so the two never disagree about a day's
/// color or total. Ends at today (local time) and starts at the rolling twelve-month start or at
/// the caller's earlier <c>from</c> (the statistics window and the widget tile both reach back as far
/// as their width fits, so the history is not capped), independent of any selected range or grouping.
/// The color scale is computed over the whole recorded history, never the returned slice, so a
/// resize that moves the start never shifts a day's shade.
/// </summary>
public static class StatsMonthGridBuilder
{
    public readonly record struct Result(
        IReadOnlyList<Views.Controls.StatsMonthGrid.DayValue> Days, DateOnly RangeStart, DateOnly RangeEnd,
        IReadOnlyList<long> ColorScaleTotals);

    /// <summary>The first day <see cref="Build"/> starts at: the twelve-month start, or <paramref
    /// name="from"/> when that is earlier.</summary>
    public static DateOnly FirstDay(DateOnly today, DateOnly? from = null)
    {
        var firstDay = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        return from is { } earlier && earlier < firstDay ? earlier : firstDay;
    }

    /// <summary>Every recorded day's total (up to <paramref name="today"/>), non-zero, ascending - the
    /// quantile boundaries of the color scale over the whole history.</summary>
    public static IReadOnlyList<long> ColorScale(IReadOnlyList<StatsRecord> all, DateOnly today) =>
        all.Where(record => record.Day <= today)
            .GroupBy(record => record.Day)
            .Select(group => group.Sum(record => record.TotalTokens))
            .Where(total => total > 0)
            .Order()
            .ToList();

    /// <param name="from">An earlier first day than the twelve-month start, for a caller that shows
    /// more days than that (the widget's week columns, the statistics window on a wide screen).</param>
    /// <param name="colorScale">An already computed <see cref="ColorScale"/> of the same records, so a
    /// re-slice on resize does not scan the whole history again.</param>
    public static Result Build(
        IReadOnlyList<StatsRecord> all, DateOnly today, DateOnly? from = null, IReadOnlyList<long>? colorScale = null)
    {
        var firstDay = FirstDay(today, from);
        var lastDay = today;
        var byDayByProvider = all
            .Where(record => record.Day >= firstDay && record.Day <= lastDay)
            .GroupBy(record => record.Day)
            .ToDictionary(group => group.Key, group => group
                .GroupBy(record => record.Provider)
                .ToDictionary(providerGroup => providerGroup.Key, providerGroup => providerGroup.Sum(record => record.TotalTokens)));

        var days = new List<Views.Controls.StatsMonthGrid.DayValue>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (byDayByProvider.TryGetValue(day, out var byProvider) && byProvider.Count > 0)
            {
                var ordered = byProvider
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new Views.Controls.StatsMonthGrid.ProviderTotal(entry.Key, entry.Value))
                    .ToList();
                days.Add(new Views.Controls.StatsMonthGrid.DayValue(day, ordered.Sum(entry => entry.Total), ordered[0].ProviderId, ordered));
            }
            else
            {
                days.Add(new Views.Controls.StatsMonthGrid.DayValue(day, 0, "", []));
            }
        }
        return new Result(days, firstDay, lastDay, colorScale ?? ColorScale(all, today));
    }
}
