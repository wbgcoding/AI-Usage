using System.Text.Json;
using AiUsage.Models;

namespace AiUsage.Tests;

/// <summary>Reading and writing the cost currency setting: names in any case, anything unknown or
/// misshapen falling back to dollars without rejecting the surrounding file.</summary>
public class CostCurrencyTests
{
    private sealed class Holder
    {
        public CostCurrency Currency { get; set; } = CostCurrency.Auto;

        public string After { get; set; } = "";
    }

    private static Holder Read(string currencyJson) =>
        JsonSerializer.Deserialize<Holder>($$"""{ "Currency": {{currencyJson}}, "After": "kept" }""")!;

    [Theory]
    [InlineData("\"Usd\"", CostCurrency.Usd)]
    [InlineData("\"Eur\"", CostCurrency.Eur)]
    [InlineData("\"Auto\"", CostCurrency.Auto)]
    [InlineData("\"eur\"", CostCurrency.Eur)]
    [InlineData("\"EUR\"", CostCurrency.Eur)]
    [InlineData("\"aUtO\"", CostCurrency.Auto)]
    [InlineData("\" Eur \"", CostCurrency.Eur)]
    public void A_known_name_reads_in_any_case(string json, CostCurrency expected) =>
        Assert.Equal(expected, Read(json).Currency);

    [Theory]
    [InlineData("\"Gbp\"")]
    [InlineData("\"\"")]
    [InlineData("\"7\"")]
    [InlineData("\"-1\"")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[\"Eur\"]")]
    [InlineData("{ \"a\": [1, 2] }")]
    public void Anything_unknown_or_misshapen_reads_as_dollars(string json) =>
        Assert.Equal(CostCurrency.Usd, Read(json).Currency);

    [Fact]
    public void A_skipped_value_does_not_disturb_the_properties_after_it()
    {
        Assert.Equal("kept", Read("{ \"deep\": { \"x\": [1, 2, 3] } }").After);
        Assert.Equal("kept", Read("[1, [2]]").After);
    }

    [Fact]
    public void An_undefined_numeric_name_is_not_accepted()
    {
        Assert.Equal(CostCurrency.Usd, Read("\"99\"").Currency);
    }

    [Theory]
    [InlineData(CostCurrency.Usd, "\"Usd\"")]
    [InlineData(CostCurrency.Eur, "\"Eur\"")]
    [InlineData(CostCurrency.Auto, "\"Auto\"")]
    public void The_value_is_written_as_its_name(CostCurrency value, string expected) =>
        Assert.Equal(expected, JsonSerializer.Serialize(value));

    [Theory]
    [InlineData(CostCurrency.Usd)]
    [InlineData(CostCurrency.Eur)]
    [InlineData(CostCurrency.Auto)]
    public void Every_value_round_trips(CostCurrency value) =>
        Assert.Equal(value, JsonSerializer.Deserialize<CostCurrency>(JsonSerializer.Serialize(value)));

    [Fact]
    public void A_bare_unknown_value_reads_as_dollars_outside_an_object_too()
    {
        Assert.Equal(CostCurrency.Usd, JsonSerializer.Deserialize<CostCurrency>("\"yen\""));
        Assert.Equal(CostCurrency.Usd, JsonSerializer.Deserialize<CostCurrency>("12"));
    }
}
