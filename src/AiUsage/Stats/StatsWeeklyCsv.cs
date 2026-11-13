using System.Globalization;
using AiUsage.Stats.Pricing;

namespace AiUsage.Stats;

/// <summary>
/// The CSV of the previous ISO week that the token index saves on its own into a folder the user picked:
/// the same file the statistics window exports (all providers, the cost estimate as it is set), only for
/// the week Monday to Sunday that just ended and built without a window. One file per week, named by
/// <see cref="FileName"/>; a file that is already there is never written again.
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
