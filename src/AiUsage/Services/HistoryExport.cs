using System.Globalization;
using System.Text;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>
/// Turns the recorded history into a plain CSV file for another program to read, not display
/// text - every field is written in a stable, locale-independent shape regardless of the app's own
/// UI language or the machine's regional number format.
/// </summary>
public static class HistoryExport
{
    private const string Header = "provider,window,timestamp_utc,percent,resets_at_utc,tokens";

    public static string ToCsv(IReadOnlyDictionary<string, IReadOnlyList<HistoryPoint>> byProvider)
    {
        var builder = new StringBuilder();
        builder.Append(Header).Append("\r\n");

        foreach (var (providerId, points) in byProvider)
        {
            foreach (var point in points)
            {
                builder
                    .Append(CsvField.Escape(providerId)).Append(',')
                    .Append(point.Window).Append(',')
                    .Append(point.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.Percent.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.ResetsAt is { } resetsAt ? resetsAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : "").Append(',')
                    .Append(point.Tokens?.ToString(CultureInfo.InvariantCulture) ?? "")
                    .Append("\r\n");
            }
        }

        return builder.ToString();
    }
}
