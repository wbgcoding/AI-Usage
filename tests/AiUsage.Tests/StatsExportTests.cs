using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>Fixture-based coverage for the statistics table's CSV export.</summary>
public class StatsExportTests
{
    [Fact]
    public void The_header_row_matches_the_shown_table_s_own_columns()
    {
        var csv = StatsExport.ToCsv([], "Nach Tag", "Gesamt");
        var firstLine = csv.Split("\r\n")[0];

        Assert.Equal("Nach Tag,Gesamt", firstLine);
    }

    [Fact]
    public void A_row_round_trips_through_the_written_csv()
    {
        var rows = new[] { new StatsRowViewModel("2026-01-01", 12345, "12.345") };

        var csv = StatsExport.ToCsv(rows, "Nach Tag", "Gesamt");
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var dataLine = lines[1].Split(',');

        Assert.Equal("2026-01-01", dataLine[0]);
        Assert.Equal(12345, long.Parse(dataLine[1]));
    }

    [Fact]
    public void A_label_with_quotes_line_breaks_or_a_formula_prefix_is_escaped()
    {
        var rows = new[]
        {
            new StatsRowViewModel("say \"hi\"", 1, "1"),
            new StatsRowViewModel("two\nlines", 2, "2"),
            new StatsRowViewModel("=HYPERLINK(\"x\")", 3, "3"),
        };

        var csv = StatsExport.ToCsv(rows, "Label", "Total");

        Assert.Contains("\"say \"\"hi\"\"\",1\r\n", csv);
        Assert.Contains("\"two\nlines\",2\r\n", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\",3\r\n", csv);
    }
}
