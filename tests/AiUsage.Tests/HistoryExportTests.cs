using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class HistoryExportTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 3, 15, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ToCsv_writes_the_header_row_first() =>
        Assert.StartsWith(
            "provider,window,timestamp_utc,percent,resets_at_utc,tokens\r\n",
            HistoryExport.ToCsv(new Dictionary<string, IReadOnlyList<HistoryPoint>>()));

    [Fact]
    public void ToCsv_writes_one_fully_populated_row()
    {
        var byProvider = new Dictionary<string, IReadOnlyList<HistoryPoint>>
        {
            ["codex"] = [new HistoryPoint(1, Timestamp, WindowKind.FiveHour, 42.5, Timestamp.AddHours(2), Tokens: 12345)],
        };

        var row = HistoryExport.ToCsv(byProvider).Split("\r\n")[1];

        Assert.Equal("codex,FiveHour,2026-03-15T14:30:00.0000000Z,42.5,2026-03-15T16:30:00.0000000Z,12345", row);
    }

    [Fact]
    public void ToCsv_leaves_reset_and_tokens_empty_rather_than_the_word_null()
    {
        var byProvider = new Dictionary<string, IReadOnlyList<HistoryPoint>>
        {
            ["codex"] = [new HistoryPoint(1, Timestamp, WindowKind.Weekly, 10, null)],
        };

        var csv = HistoryExport.ToCsv(byProvider);
        var row = csv.Split("\r\n")[1];

        Assert.Equal("codex,Weekly,2026-03-15T14:30:00.0000000Z,10,,", row);
        Assert.DoesNotContain("null", csv);
    }

    [Fact]
    public void ToCsv_writes_percent_with_a_dot_even_under_a_german_test_culture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var byProvider = new Dictionary<string, IReadOnlyList<HistoryPoint>>
            {
                ["codex"] = [new HistoryPoint(1, Timestamp, WindowKind.FiveHour, 42.5, null)],
            };

            var csv = HistoryExport.ToCsv(byProvider);

            Assert.Contains("42.5", csv);
            Assert.DoesNotContain("42,5", csv);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToCsv_escapes_a_provider_id_with_quotes_and_a_formula_prefix()
    {
        var byProvider = new Dictionary<string, IReadOnlyList<HistoryPoint>>
        {
            ["=cmd\"x"] = [new HistoryPoint(1, Timestamp, WindowKind.Weekly, 10, null)],
        };

        var row = HistoryExport.ToCsv(byProvider).Split("\r\n")[1];

        Assert.StartsWith("\"'=cmd\"\"x\",Weekly,", row);
    }
}
