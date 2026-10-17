using System.Globalization;
using System.Text;
using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>Fixture-based coverage for the universal long-format CSV export of the statistics window.</summary>
public class StatsExportTests
{
    internal static List<List<string>> Parse(string csv)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    field.Append(c);
            }
            else if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                records.Add(row);
                row = [];
                i++;
            }
            else
                field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            records.Add(row);
        }

        return records;
    }

    [Fact]
    public void The_header_row_names_the_seventeen_columns_in_order()
    {
        var csv = StatsExport.ToCsv([]);

        Assert.Equal(
            "section,label,day,hour,provider,model,project,project_merged,effort,input_tokens,output_tokens,"
            + "cache_creation_tokens,cache_read_tokens,total_tokens,cost_usd,cost,currency\r\n",
            csv);
        Assert.Equal(17, Parse(csv)[0].Count);
    }

    [Fact]
    public void The_file_encoding_writes_a_byte_order_mark()
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, StatsExport.FileEncoding.GetPreamble());
    }

    [Fact]
    public void A_row_round_trips_with_empty_cells_for_what_does_not_apply()
    {
        var csv = StatsExport.ToCsv([new StatsExportRow("by_day") { Day = "2026-01-01", TotalTokens = 12345, InputTokens = 5 }]);

        var cells = Parse(csv)[1];
        Assert.Equal(17, cells.Count);
        Assert.Equal("by_day", cells[0]);
        Assert.Equal("2026-01-01", cells[2]);
        Assert.Equal("", cells[3]);
        Assert.Equal("5", cells[9]);
        Assert.Equal("", cells[10]);
        Assert.Equal("12345", cells[13]);
        Assert.Equal("", cells[14]);
    }

    [Fact]
    public void A_text_with_quotes_line_breaks_or_a_formula_prefix_is_escaped()
    {
        var csv = StatsExport.ToCsv([
            new StatsExportRow("rows") { Project = "say \"hi\"" },
            new StatsExportRow("rows") { Project = "two\nlines" },
            new StatsExportRow("rows") { Project = "=HYPERLINK(\"x\")" },
        ]);

        var rows = Parse(csv);
        Assert.Equal(4, rows.Count);
        Assert.Equal("say \"hi\"", rows[1][6]);
        Assert.Equal("two\nlines", rows[2][6]);
        Assert.Equal("'=HYPERLINK(\"x\")", rows[3][6]);
        Assert.All(rows, cells => Assert.Equal(17, cells.Count));
    }

    [Fact]
    public void Numbers_use_a_dot_whatever_the_culture_is()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var csv = StatsExport.ToCsv([new StatsExportRow("total") { TotalTokens = 1234567, Cost = "1.5", Currency = "EUR" }]);
            var cells = Parse(csv)[1];
            Assert.Equal("1234567", cells[13]);
            Assert.Equal("1.5", cells[15]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
