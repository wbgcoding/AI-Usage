using System.Globalization;
using AiUsage.Models;
using AiUsage.Stats.Pricing;

namespace AiUsage.Stats;

/// <summary>
/// The weekly CSV files that the token index saves on its own into a folder the user picked: the same file
/// the statistics window exports (all providers, the cost estimate as it is set), for a finished week
/// Monday to Sunday and built without a window. One file per week, named by <see cref="FileName"/>; a file
/// that is already there is never written again. A week missed because the PC was off is written at the
/// next walk (see <see cref="DueWeeks"/>).
/// </summary>
internal static class StatsWeeklyCsv
{
    /// <summary>How long a run waits for the cached price list before it writes the file without costs.</summary>
    private static readonly TimeSpan PriceWait = TimeSpan.FromSeconds(10);

    internal enum Outcome
    {
        /// <summary>The file for the previous week was written.</summary>
        Written,

        /// <summary>The file for the previous week was already there; nothing was touched.</summary>
        AlreadyThere,

        /// <summary>The folder does not exist (an unplugged drive, a deleted folder); nothing was written.</summary>
        FolderMissing,
    }

    /// <summary>The name of the file for ISO week <paramref name="week"/> of <paramref name="isoYear"/>.</summary>
    internal static string FileName(int isoYear, int week) =>
        string.Create(CultureInfo.InvariantCulture, $"AI-Usage-{isoYear}-W{week:00}.csv");

    /// <summary>The previous ISO week as of <paramref name="today"/>: its Monday, its Sunday and the file name.</summary>
    internal static (DateOnly From, DateOnly To, string FileName) PreviousWeek(DateOnly today)
    {
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var from = thisMonday.AddDays(-7);
        var date = from.ToDateTime(TimeOnly.MinValue);
        return (from, from.AddDays(6), FileName(ISOWeek.GetYear(date), ISOWeek.GetWeekOfYear(date)));
    }

    /// <summary>The moment the CSV of the previous week falls due in the Monday to Sunday week that contains
    /// <paramref name="now"/>: <paramref name="day"/> of that week at the full <paramref name="hour"/>.</summary>
    internal static DateTime DueMoment(DateTime now, DayOfWeek day, int hour)
    {
        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        return monday.AddDays(((int)day + 6) % 7).AddHours(AppSettings.NormalizeWeeklyCsvHour(hour));
    }

    /// <summary>Whether the file of the previous week is due at <paramref name="now"/>: the chosen day and hour
    /// of the current week have come. A start after that moment catches up, because the due moment stays
    /// passed for the rest of the week.</summary>
    internal static bool IsDue(DateTime now, DayOfWeek day, int hour) => now >= DueMoment(now, day, hour);

    /// <summary>The most weeks back a catch-up reaches, the previous week included.</summary>
    internal const int CatchUpWeeks = 12;

    private static readonly System.Text.RegularExpressions.Regex NamePattern =
        new(@"^AI-Usage-(\d{4})-W(\d{2})\.csv$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The weeks whose file is due and still missing at <paramref name="now"/>, oldest first, each as a day
    /// of the week after it (pass that day to <see cref="Write"/>). The file of week W is due at the chosen day and
    /// hour of week W+1. Candidates are the weeks after the newest file in <paramref name="existingNames"/> up to
    /// the previous week, at most <see cref="CatchUpWeeks"/>; with no such file yet only the previous week counts.</summary>
    internal static IReadOnlyList<DateOnly> DueWeeks(DateTime now, DayOfWeek day, int hour, IEnumerable<string> existingNames)
    {
        var previousMonday = PreviousWeek(DateOnly.FromDateTime(now)).From;
        var first = previousMonday;
        DateOnly? newest = null;
        foreach (var name in existingNames)
        {
            var match = NamePattern.Match(name);
            if (!match.Success)
                continue;

            var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var week = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (year < 1 || week < 1 || week > ISOWeek.GetWeeksInYear(year))
                continue;

            var monday = DateOnly.FromDateTime(ISOWeek.ToDateTime(year, week, DayOfWeek.Monday));
            if (newest is null || monday > newest)
                newest = monday;
        }

        if (newest is { } latest)
            first = latest.AddDays(7);
        var oldest = previousMonday.AddDays(-7 * (CatchUpWeeks - 1));
        if (first < oldest)
            first = oldest;

        var due = new List<DateOnly>();
        for (var monday = first; monday <= previousMonday; monday = monday.AddDays(7))
        {
            var nextWeek = monday.AddDays(7);
            if (now >= DueMoment(nextWeek.ToDateTime(TimeOnly.MinValue), day, hour))
                due.Add(nextWeek);
        }

        return due;
    }

    /// <summary>Saves the CSV of the previous week into <paramref name="folder"/> unless it is already there.
    /// May block while the price list loads, so call it off the UI thread. IO failures are thrown.</summary>
    internal static Outcome Write(string folder, DateOnly today, StatsStore store, CostService? cost)
    {
        if (!Directory.Exists(folder))
            return Outcome.FolderMissing;

        var (from, to, fileName) = PreviousWeek(today);
        var path = Path.Combine(folder, fileName);
        if (File.Exists(path))
            return Outcome.AlreadyThere;

        var rows = StatsExportBuilder.Build(Collect(store, from, to, cost));
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, StatsExport.ToCsv(rows), StatsExport.FileEncoding);
            File.Move(temporary, path);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        return Outcome.Written;
    }

    private static StatsExportData Collect(StatsStore store, DateOnly from, DateOnly to, CostService? cost)
    {
        var all = StatsAggregator.ResolveBareProjectNames(store.LoadAll());
        var allSessions = store.LoadSessions();
        var inRange = all.Where(record => record.Day >= from && record.Day <= to).ToList();
        var sessions = StatsAggregator.SessionsInRange(allSessions, from, to);
        var figures = StatsAggregator.ComputeHeadlineFigures(inRange, [], 7);
        return new StatsExportData(
            inRange, [], inRange, sessions, figures, 7, [], ProjectAliasResolver.Build(all, allSessions), new StatsCostView(WaitForPrices(cost)));
    }

    // The price list loads in the background after the start; the first walk can be over before it is.
    private static CostService? WaitForPrices(CostService? cost)
    {
        if (cost is not { Enabled: true })
            return cost;

        try
        {
            cost.PricesLoaded.Wait(PriceWait);
        }
        catch (AggregateException)
        {
            // A cache that failed to load is the same as no prices: the file simply has no costs.
        }

        return cost;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
