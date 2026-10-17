using System.Globalization;
using System.Text;
using AiUsage.Services;

namespace AiUsage.Stats;

/// <summary>
/// One line of the statistics export. Every line belongs to a <see cref="Section"/>; a column that does
/// not apply to the section stays empty. <see cref="CostUsd"/> is always dollars, <see cref="Cost"/> is
/// the same amount in the currency the window shows (<see cref="Currency"/>); all three are empty when
/// the cost estimate is off or the line has no price.
/// </summary>
public sealed record StatsExportRow(string Section)
{
    public string Label { get; init; } = "";
    public string Day { get; init; } = "";
    public string Hour { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public string Project { get; init; } = "";
    public string ProjectMerged { get; init; } = "";
    public string Effort { get; init; } = "";
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheCreationTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? TotalTokens { get; init; }
    public string CostUsd { get; init; } = "";
    public string Cost { get; init; } = "";
    public string Currency { get; init; } = "";
}

/// <summary>
/// Turns the rows of the statistics export into one long-format CSV file: UTF 8 with a byte order mark so
/// a spreadsheet reads the umlauts of a project path, "\r\n" line ends, RFC 4180 quoting through <see
/// cref="CsvField"/> (which also guards against spreadsheet formulas), a dot as decimal separator and ISO
/// 8601 dates, whatever the interface language is.
/// </summary>
public static class StatsExport
{
    public const string Header =
        "section,label,day,hour,provider,model,project,project_merged,effort,input_tokens,output_tokens,"
        + "cache_creation_tokens,cache_read_tokens,total_tokens,cost_usd,cost,currency";

    /// <summary>The file encoding: UTF 8 with a byte order mark.</summary>
    public static UTF8Encoding FileEncoding { get; } = new(encoderShouldEmitUTF8Identifier: true);

    public static string ToCsv(IEnumerable<StatsExportRow> rows)
    {
        var builder = new StringBuilder();
        builder.Append(Header).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(row.Section).Append(',')
                .Append(CsvField.Escape(row.Label)).Append(',')
                .Append(row.Day).Append(',')
                .Append(row.Hour).Append(',')
                .Append(CsvField.Escape(row.Provider)).Append(',')
                .Append(CsvField.Escape(row.Model)).Append(',')
                .Append(CsvField.Escape(row.Project)).Append(',')
                .Append(CsvField.Escape(row.ProjectMerged)).Append(',')
                .Append(CsvField.Escape(row.Effort)).Append(',');
            AppendNumber(builder, row.InputTokens).Append(',');
            AppendNumber(builder, row.OutputTokens).Append(',');
            AppendNumber(builder, row.CacheCreationTokens).Append(',');
            AppendNumber(builder, row.CacheReadTokens).Append(',');
            AppendNumber(builder, row.TotalTokens).Append(',');
            builder.Append(row.CostUsd).Append(',').Append(row.Cost).Append(',').Append(row.Currency).Append("\r\n");
        }

        return builder.ToString();
    }

    private static StringBuilder AppendNumber(StringBuilder builder, long? value) =>
        value is { } number ? builder.Append(number.ToString(CultureInfo.InvariantCulture)) : builder;
}
