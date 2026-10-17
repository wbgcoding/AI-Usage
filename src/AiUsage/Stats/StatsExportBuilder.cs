using System.Globalization;
using System.Text;
using AiUsage.Stats.Pricing;

namespace AiUsage.Stats;

/// <summary>Everything the statistics window computed for its current range and provider selection.</summary>
internal sealed record StatsExportData(
    IReadOnlyList<StatsRecord> InRange,
    IReadOnlyList<StatsRecord> Previous,
    IReadOnlyList<StatsRecord> Raw,
    IReadOnlyList<StatsSessionRecord> Sessions,
    StatsHeadlineFigures Figures,
    int PeriodDayCount,
    IReadOnlyList<StatsLimitGroupViewModel> Limits,
    ProjectAliases Aliases,
    StatsCostView Cost);

/// <summary>
/// Lays the figures of the statistics window out as the lines of the universal export (see <see
/// cref="StatsExport"/>). Section names, labels and the cost currency are the same in every interface
/// language, so a file can be read by a script without knowing which language wrote it.
/// </summary>
internal static class StatsExportBuilder
{
    private const int TopProjectCount = 12;

    public static IReadOnlyList<StatsExportRow> Build(StatsExportData data)
    {
        var rows = new List<StatsExportRow>();
        var records = data.InRange;
        var cost = data.Cost;

        StatsExportRow Priced(StatsExportRow row, IReadOnlyCollection<StatsRecord> group) =>
            Costed(Tokens(row, group), cost, cost.Active ? cost.Sum(group) : CostSummary.Empty);

        rows.Add(Priced(new StatsExportRow("total"), records));

        foreach (var group in OrderedByTotal(records.GroupBy(record => record.Provider, StringComparer.Ordinal)))
            rows.Add(Priced(new StatsExportRow("by_provider") { Provider = group.Key }, group.ToList()));

        foreach (var group in OrderedByTotal(records.GroupBy(record => record.Model, StringComparer.Ordinal)))
            rows.Add(Priced(new StatsExportRow("by_model") { Model = group.Key }, group.ToList()));

        var projects = ProjectGroups(records);
        foreach (var group in projects)
            rows.Add(Priced(new StatsExportRow("by_project") { Project = ProjectOf(group) }, group.ToList()));

        foreach (var group in OrderedByTotal(records.GroupBy(record => record.Effort, StringComparer.Ordinal)))
            rows.Add(Priced(new StatsExportRow("by_effort") { Effort = group.Key }, group.ToList()));

        foreach (var group in records.GroupBy(record => record.Day).OrderBy(group => group.Key))
            rows.Add(Priced(new StatsExportRow("by_day") { Day = Iso(group.Key) }, group.ToList()));

        foreach (var group in records.GroupBy(record => record.Hour).OrderBy(group => group.Key))
            rows.Add(Priced(new StatsExportRow("by_hour") { Hour = Number(group.Key) }, group.ToList()));

        foreach (var group in records
            .GroupBy(record => (Weekday: ((int)record.Day.DayOfWeek + 6) % 7, record.Hour))
            .OrderBy(group => group.Key.Weekday).ThenBy(group => group.Key.Hour))
        {
            var weekday = (DayOfWeek)((group.Key.Weekday + 1) % 7);
            rows.Add(Priced(
                new StatsExportRow("by_weekday_hour") { Label = weekday.ToString().ToLowerInvariant(), Hour = Number(group.Key.Hour) },
                group.ToList()));
        }

        var rank = 0;
        foreach (var group in projects.Take(TopProjectCount))
            rows.Add(Priced(new StatsExportRow("top_projects") { Label = Number(++rank), Project = ProjectOf(group) }, group.ToList()));

        AddCacheSplit(rows, records, cost);
        AddFigures(rows, data);
        AddLimits(rows, data);

        foreach (var session in data.Sessions)
        {
            var start = session.FirstUtc.ToLocalTime();
            var row = new StatsExportRow("sessions")
            {
                Label = session.SessionId,
                Day = Iso(session.StartDay),
                Hour = Number(start.Hour),
                Provider = session.Provider,
                Model = session.MainModel,
                Project = session.Project,
                InputTokens = session.InputTokens,
                OutputTokens = session.OutputTokens,
                CacheCreationTokens = session.CacheCreationTokens,
                CacheReadTokens = session.CacheReadTokens,
                TotalTokens = session.TotalTokens,
            };
            var summary = cost.Active && session.MainModel.Length > 0
                ? cost.Sum([new StatsRecord(
                    session.Provider, session.StartDay, session.MainModel, session.Project,
                    session.InputTokens, session.OutputTokens, session.CacheCreationTokens, session.CacheReadTokens)])
                : CostSummary.Empty;
            rows.Add(Costed(row, cost, summary));
        }

        foreach (var record in data.Raw)
        {
            var row = new StatsExportRow("rows")
            {
                Day = Iso(record.Day),
                Hour = Number(record.Hour),
                Provider = record.Provider,
                Model = record.Model,
                Project = record.Project,
                ProjectMerged = data.Aliases.Resolve(record.Project),
                Effort = record.Effort,
            };
            rows.Add(Costed(Tokens(row, [record]), cost, cost.Active ? cost.Sum([record]) : CostSummary.Empty));
        }

        return rows;
    }

    private static void AddCacheSplit(List<StatsExportRow> rows, IReadOnlyList<StatsRecord> records, StatsCostView cost)
    {
        var share = StatsAggregator.CacheShare(records);
        var split = cost.Active ? cost.Split(records) : CostSplit.Empty;
        string[] labels = ["new_input", "cache_write", "cache_read", "output"];
        var values = share.AsValues;
        for (var i = 0; i < labels.Length; i++)
        {
            var row = new StatsExportRow("cache_split") { Label = labels[i], TotalTokens = values[i] };
            row = i switch
            {
                0 => row with { InputTokens = values[i] },
                1 => row with { CacheCreationTokens = values[i] },
                2 => row with { CacheReadTokens = values[i] },
                _ => row with { OutputTokens = values[i] },
            };
            // A part without tokens gets no cost: the formatter would still show the one cent floor.
            rows.Add(split.AnyPriced && values[i] > 0 ? Costed(row, cost, new CostSummary(split.AsParts[i], true, [])) : row);
        }
    }

    private static void AddFigures(List<StatsExportRow> rows, StatsExportData data)
    {
        var cost = data.Cost;
        var total = cost.Active ? cost.Sum(data.InRange) : CostSummary.Empty;
        var days = Math.Max(1, data.PeriodDayCount);

        CostSummary Share(int count) => total.AnyPriced ? total with { Usd = total.Usd / count } : total;

        rows.Add(Costed(
            new StatsExportRow("figures") { Label = "average_per_day", TotalTokens = (long)(data.Figures.PerDayAverage + 0.5) },
            cost, Share(days)));
        if (data.Figures.ActiveDayCount > 0)
            rows.Add(Costed(
                new StatsExportRow("figures")
                {
                    Label = "average_per_active_day",
                    TotalTokens = (data.Figures.Total + data.Figures.ActiveDayCount / 2) / data.Figures.ActiveDayCount,
                },
                cost, Share(data.Figures.ActiveDayCount)));

        if (data.Figures.BusiestDay is { } busiest)
        {
            var onDay = data.InRange.Where(record => record.Day == busiest).ToList();
            rows.Add(Costed(
                new StatsExportRow("figures") { Label = "busiest_day", Day = Iso(busiest), TotalTokens = data.Figures.BusiestDayTotal },
                cost, cost.Active ? cost.Sum(onDay) : CostSummary.Empty));
        }

        rows.Add(PreviousPeriod(data.Previous, cost));
    }

    private static StatsExportRow PreviousPeriod(IReadOnlyList<StatsRecord> records, StatsCostView cost) =>
        Costed(
            new StatsExportRow("figures") { Label = "previous_period", TotalTokens = records.Sum(record => record.TotalTokens) },
            cost, cost.Active ? cost.Sum(records) : CostSummary.Empty);

    /// <summary>One line per window of the limits section. The window's own figures are percentages, which
    /// the token columns cannot hold; the line carries the tokens its provider used in the range.</summary>
    private static void AddLimits(List<StatsExportRow> rows, StatsExportData data)
    {
        foreach (var group in data.Limits)
        {
            var used = data.InRange.Where(record => record.Provider == group.ProviderId).Sum(record => record.TotalTokens);
            foreach (var limit in group.Rows)
                rows.Add(new StatsExportRow("limits")
                {
                    Label = WindowName(limit.WindowKey.Length > 0 ? limit.WindowKey : limit.Label),
                    Provider = group.ProviderId,
                    TotalTokens = used,
                });
        }
    }

    /// <summary>"5h" and "7d" for the two common windows, else the resource key as snake case.</summary>
    internal static string WindowName(string key)
    {
        switch (key)
        {
            case "Window_FiveHour":
                return "5h";
            case "Window_Weekly":
                return "7d";
        }

        var text = key.StartsWith("Window_", StringComparison.Ordinal) ? key["Window_".Length..] : key;
        var builder = new StringBuilder();
        foreach (var letter in text)
        {
            if (char.IsUpper(letter) && builder.Length > 0)
                builder.Append('_');
            builder.Append(letter is ' ' or '-' ? '_' : char.ToLowerInvariant(letter));
        }

        return builder.ToString();
    }

    private static List<IGrouping<string, StatsRecord>> ProjectGroups(IReadOnlyList<StatsRecord> records) =>
        OrderedByTotal(records.GroupBy(record => StatsAggregator.ProjectGroupKey(record.Project), StringComparer.OrdinalIgnoreCase));

    private static string ProjectOf(IGrouping<string, StatsRecord> group) =>
        group.Key == StatsAggregator.NoProjectKey ? "" : group.Key;

    private static List<IGrouping<string, StatsRecord>> OrderedByTotal(IEnumerable<IGrouping<string, StatsRecord>> groups) =>
        groups.OrderByDescending(group => group.Sum(record => record.TotalTokens)).ThenBy(group => group.Key, StringComparer.Ordinal).ToList();

    private static StatsExportRow Tokens(StatsExportRow row, IReadOnlyCollection<StatsRecord> records) => row with
    {
        InputTokens = records.Sum(record => record.InputTokens),
        OutputTokens = records.Sum(record => record.OutputTokens),
        CacheCreationTokens = records.Sum(record => record.CacheCreationTokens),
        CacheReadTokens = records.Sum(record => record.CacheReadTokens),
        TotalTokens = records.Sum(record => record.TotalTokens),
    };

    private static StatsExportRow Costed(StatsExportRow row, StatsCostView cost, CostSummary summary)
    {
        var (usd, shown, currency) = cost.ForExport(summary);
        return row with { CostUsd = usd, Cost = shown, Currency = currency };
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
