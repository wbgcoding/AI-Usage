using System.Text.Json;
using System.Text.Json.Serialization;
using AiUsage.Models;
using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>The stored section arrangement of the statistics window reads back leniently: a wrong
/// JSON shape becomes null (the default layout) and never breaks the file around it.</summary>
public class TolerantLayoutConverterTests
{
    private sealed class Holder
    {
        [JsonConverter(typeof(TolerantLayoutConverter))]
        public List<StatsLayoutRow>? Layout { get; set; } = [];

        public string After { get; set; } = "";
    }

    private static Holder Read(string layoutJson) =>
        JsonSerializer.Deserialize<Holder>($$"""{ "Layout": {{layoutJson}}, "After": "kept" }""")!;

    [Fact]
    public void A_well_formed_arrangement_reads_as_written()
    {
        var holder = Read("""[ { "Left": ["figures"], "Right": [] }, { "Left": ["provider", "model"], "Right": ["limits"] } ]""");

        Assert.NotNull(holder.Layout);
        Assert.Equal(2, holder.Layout.Count);
        Assert.Equal(["figures"], holder.Layout[0].Left);
        Assert.Empty(holder.Layout[0].Right);
        Assert.Equal(["provider", "model"], holder.Layout[1].Left);
        Assert.Equal(["limits"], holder.Layout[1].Right);
        Assert.Equal("kept", holder.After);
    }

    [Fact]
    public void An_empty_array_is_an_empty_list_not_null()
    {
        var holder = Read("[]");

        Assert.NotNull(holder.Layout);
        Assert.Empty(holder.Layout);
    }

    [Fact]
    public void Missing_columns_in_a_row_default_to_empty()
    {
        var holder = Read("""[ {} ]""");

        var row = Assert.Single(holder.Layout!);
        Assert.Empty(row.Left);
        Assert.Empty(row.Right);
    }

    [Theory]
    [InlineData("\"figures\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{ \"Left\": [\"figures\"] }")]
    [InlineData("[ \"figures\" ]")]
    [InlineData("[ 1, 2 ]")]
    [InlineData("[ { \"Left\": \"figures\" } ]")]
    [InlineData("[ { \"Left\": [1, 2] } ]")]
    [InlineData("[ { \"Left\": [\"a\"], \"Right\": { \"x\": 1 } } ]")]
    public void A_wrong_shape_reads_as_null_and_leaves_the_rest_of_the_file_intact(string json)
    {
        var holder = Read(json);

        Assert.Null(holder.Layout);
        Assert.Equal("kept", holder.After);
    }

    [Fact]
    public void A_json_null_stays_null()
    {
        Assert.Null(Read("null").Layout);
    }

    [Fact]
    public void A_wrong_shape_normalizes_to_the_default_arrangement()
    {
        var layout = StatsLayout.Normalize(Read("\"oops\"").Layout);

        Assert.Equal(StatsLayout.Default().Select(row => (row.Left, row.Right)), layout.Select(row => (row.Left, row.Right)));
    }

    [Fact]
    public void An_arrangement_round_trips_through_write_and_read()
    {
        var original = new Holder { Layout = StatsLayout.Default(), After = "x" };

        var copy = JsonSerializer.Deserialize<Holder>(JsonSerializer.Serialize(original))!;

        Assert.NotNull(copy.Layout);
        Assert.Equal(original.Layout!.Count, copy.Layout.Count);
        for (var i = 0; i < original.Layout.Count; i++)
        {
            Assert.Equal(original.Layout[i].Left, copy.Layout[i].Left);
            Assert.Equal(original.Layout[i].Right, copy.Layout[i].Right);
        }
    }

    [Fact]
    public void A_null_arrangement_is_written_as_null()
    {
        Assert.Contains("\"Layout\":null", JsonSerializer.Serialize(new Holder { Layout = null }));
    }

    [Fact]
    public void The_settings_file_survives_a_broken_arrangement()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "StatsSectionLayout": "broken", "StatsRange": "Month" }""")!;

        Assert.Null(settings.StatsSectionLayout);
        Assert.Equal("Month", settings.StatsRange);
    }
}
