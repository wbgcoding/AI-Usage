using System.Globalization;
using System.Text;
using AiUsage.Services;

namespace AiUsage.Stats;

/// <summary>
/// Turns the statistics table into a plain CSV file, the same conventions <see
/// cref="Services.HistoryExport"/> already settled on for the usage history export (comma
/// separator, "\r\n" line endings, UTF 8 without a byte order mark) and the same field escaping
/// (<see cref="Services.CsvField"/>: RFC 4180 quoting and a guard against spreadsheet formulas). The
/// two tables are shaped differently (a label plus one total here, several typed columns there), so
/// each has its own row writer.
/// </summary>
public static class StatsExport
{
    public static string ToCsv(IReadOnlyList<StatsRowViewModel> rows, string labelHeader, string totalHeader)
    {
        var builder = new StringBuilder();
        builder.Append(CsvField.Escape(labelHeader)).Append(',').Append(CsvField.Escape(totalHeader)).Append("\r\n");

        foreach (var row in rows)
            builder.Append(CsvField.Escape(row.Label)).Append(',').Append(row.TotalTokens.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

        return builder.ToString();
    }
}
