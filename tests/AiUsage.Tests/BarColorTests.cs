using System.Text.Json;
using System.Windows.Media;
using AiUsage.Models;
using AiUsage.Stats;
using AiUsage.Storage;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The bar color by token share: which modes a provider offers, how a window's token rows
/// become colored parts, and how those parts lay out across the filled width.</summary>
public class BarColorTests
{
    private const string Main = "Main agent";
    private const string Sub = "Subagents";

    private static readonly WindowShareRow[] ClaudeRows =
    [
        new("claude-opus-4-8", false, 620),
        new("claude-opus-4-8", true, 100),
        new("claude-sonnet-5", false, 200),
        new("claude-haiku-4-5", true, 80),
    ];

    [Theory]
    [InlineData("claude", "Level,Agent,Model")]
    [InlineData("codex", "Level,Model")]
    [InlineData("gemini", "Level,Model")]
    [InlineData("cursor", "Level")]
    [InlineData("copilot", "Level")]
    public void A_provider_offers_a_split_by_agent_only_when_the_index_tells_the_agents_apart(string provider, string expected) =>
        Assert.Equal(expected, string.Join(",", BarColorModes.OfferedFor(provider)));

    [Theory]
    [InlineData("agent", "claude", "Agent")]
    [InlineData("MODEL", "codex", "Model")]
    [InlineData("Agent", "codex", "Level")]
    [InlineData("Model", "copilot", "Level")]
    [InlineData("nonsense", "claude", "Level")]
    [InlineData(null, "claude", "Level")]
    public void A_mode_reads_back_in_its_canonical_spelling_or_as_Level(string? stored, string provider, string expected) =>
        Assert.Equal(expected, BarColorModes.Normalize(stored, provider));

    [Fact]
    public void A_settings_file_without_the_field_loads_every_provider_by_level_and_a_bad_one_falls_back()
    {
        var json = """{"providers":{"claude":{"barColorMode":"model"},"claude#2":{"barColorMode":"Agent"},"codex":{"barColorMode":"Agent"},"gemini":{}}}""";

        var (settings, refusal) = SettingsStore.Validate(json);

        Assert.Null(refusal);
        Assert.Equal("Model", settings!.Providers["claude"].BarColorMode);
        Assert.Equal("Level", settings.Providers["codex"].BarColorMode);
        Assert.Equal("Level", settings.Providers["gemini"].BarColorMode);
    }

    [Fact]
    public void The_mode_survives_a_save_and_load_round_trip()
    {
        var settings = new AppSettings();
        settings.Providers["claude"].BarColorMode = BarColorModes.Agent;
        settings.Providers["codex"].BarColorMode = BarColorModes.Model;

        var (loaded, _) = SettingsStore.Validate(JsonSerializer.Serialize(settings, SettingsStore.JsonOptions));

        Assert.Equal("Agent", loaded!.Providers["claude"].BarColorMode);
        Assert.Equal("Model", loaded.Providers["codex"].BarColorMode);
    }

    [Fact]
    public void Model_parts_are_ordered_by_share_and_take_the_colors_the_model_ring_gives_them()
    {
        var parts = WindowShares.Build(BarColorModes.Model, "claude", ClaudeRows, Main, Sub);

        Assert.Equal(["Opus 4.8", "Sonnet 5", "Haiku 4.5"], parts.Select(part => part.Label));
        Assert.Equal(100.0, parts.Sum(part => part.SharePercent), 6);
        Assert.Equal(72.0, parts[0].SharePercent, 6);

        // The statistics window colors the same records by the rank in its own share list.
        var records = ClaudeRows.Select(row => new StatsRecord("claude", new DateOnly(2026, 10, 15), row.Model, "p", row.Tokens, 0, 0, 0, Subagent: row.Subagent)).ToList();
        var ring = StatsAggregator.ShareByModel(records, 10, "Other");
        var ringRank = ring.Select((slice, rank) => (slice.Label, Color: ChartPalette.ForModel(slice.ProviderId, rank))).ToDictionary(entry => entry.Label, entry => entry.Color);
        Assert.All(parts, part => Assert.Equal(ringRank[part.Label], part.Color));
    }

    [Fact]
    public void Agent_parts_split_main_and_subagents_in_the_colors_of_the_agent_stack()
    {
        var parts = WindowShares.Build(BarColorModes.Agent, "claude", ClaudeRows, Main, Sub);

        Assert.Equal([Main, Sub], parts.Select(part => part.Label));
        Assert.Equal(82.0, parts[0].SharePercent, 6);
        Assert.Equal(18.0, parts[1].SharePercent, 6);
        Assert.Equal(ChartPalette.Categorical(0), parts[0].Color);
        Assert.Equal(ChartPalette.Categorical(1), parts[1].Color);
    }

    [Fact]
    public void Nothing_is_split_for_the_level_mode_a_mode_the_provider_lacks_or_an_empty_window()
    {
        Assert.Empty(WindowShares.Build(BarColorModes.Level, "claude", ClaudeRows, Main, Sub));
        Assert.Empty(WindowShares.Build(BarColorModes.Agent, "codex", ClaudeRows, Main, Sub));
        Assert.Empty(WindowShares.Build(BarColorModes.Model, "claude", [], Main, Sub));
    }

    [Fact]
    public void Two_raw_ids_of_one_model_share_a_part()
    {
        WindowShareRow[] rows = [new("claude-opus-4-8", false, 30), new("claude-opus-4-8-20260101", false, 70)];

        var parts = WindowShares.Build(BarColorModes.Model, "claude", rows, Main, Sub);

        Assert.Single(parts);
        Assert.Equal(100.0, parts[0].SharePercent, 6);
    }

    private static BarSegment Part(double share, byte shade) => new("p", share, Color.FromRgb(shade, shade, shade));

    [Fact]
    public void Part_widths_are_proportional_to_the_shares_and_add_up_to_the_filled_width()
    {
        var placed = WindowShares.Layout([Part(50, 1), Part(30, 2), Part(20, 3)], 200);

        Assert.Equal(3, placed.Count);
        Assert.Equal([100.0, 60.0, 40.0], placed.Select(part => part.Width));
        Assert.Equal([0.0, 100.0, 160.0], placed.Select(part => part.Left));
        Assert.InRange(placed.Sum(part => part.Width), 199, 201);
    }

    [Fact]
    public void Parts_narrower_than_two_pixels_merge_into_the_last_one()
    {
        // On 300 px every part is at least 3 px wide, so none merges.
        var wide = WindowShares.Layout([Part(90, 1), Part(8, 2), Part(1, 3), Part(1, 4)], 300);
        Assert.Equal(4, wide.Count);

        // On 100 px the last two are 1 px each and fold into the final part, which keeps its color.
        var narrow = WindowShares.Layout([Part(90, 1), Part(8, 2), Part(1, 3), Part(1, 4)], 100);

        Assert.Equal(3, narrow.Count);
        Assert.Equal(Color.FromRgb(4, 4, 4), narrow[^1].Color);
        Assert.Equal(2.0, narrow[^1].Width, 6);
        Assert.InRange(narrow.Sum(part => part.Width), 99, 101);
        Assert.Equal(100.0, narrow[^1].Left + narrow[^1].Width, 6);
    }

    [Fact]
    public void A_single_part_fills_the_whole_width_even_below_two_pixels()
    {
        var placed = WindowShares.Layout([Part(100, 9)], 1.5);

        Assert.Single(placed);
        Assert.Equal(1.5, placed[0].Width, 6);
    }

    [Theory]
    [InlineData(0, 300, 0)]
    [InlineData(50, 300, 150)]
    [InlineData(0.2, 300, 3)] // the floor the plain fill keeps
    [InlineData(100, 300, 300)]
    public void The_filled_width_is_the_used_percent_of_the_track_with_a_floor(double used, double track, double expected) =>
        Assert.Equal(expected, AiUsage.Views.Controls.SegmentedBarFill.FilledWidth(used, track), 6);

    [Fact]
    public void The_store_returns_the_window_slice_by_model_and_agent_and_their_sum_equals_the_plain_sum()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("bar-color-store");
        var store = new StatsStore(dataDir);
        var day = new DateOnly(2026, 10, 15);
        store.AddDelta([
            new StatsRecord("claude", day, "claude-opus-4-8", "p", 100, 0, 0, 0, Hour: 10),
            new StatsRecord("claude", day, "claude-opus-4-8", "p", 40, 0, 0, 0, Hour: 11, Subagent: true),
            new StatsRecord("claude", day, "claude-sonnet-5", "p", 10, 0, 0, 0, Hour: 12),
            new StatsRecord("claude", day, "claude-sonnet-5", "p", 999, 0, 0, 0, Hour: 8), // before the window
            new StatsRecord("codex", day, "gpt-5.6-sol", "p", 777, 0, 0, 0, Hour: 12), // another provider
        ]);
        var start = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Local);

        var rows = store.SumTokensByModelSince("claude", start);

        Assert.NotNull(rows);
        Assert.Equal(3, rows!.Count);
        Assert.Contains(new WindowShareRow("claude-opus-4-8", false, 100), rows);
        Assert.Contains(new WindowShareRow("claude-opus-4-8", true, 40), rows);
        Assert.Contains(new WindowShareRow("claude-sonnet-5", false, 10), rows);
        Assert.Equal(store.SumTokensSince("claude", start), rows.Sum(row => row.Tokens));

        var onlyOpus = store.SumTokensByModelSince("claude", start, "opus");
        Assert.Equal(2, onlyOpus!.Count);
    }
}
