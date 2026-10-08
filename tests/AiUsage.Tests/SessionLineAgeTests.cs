using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

public class SessionLineAgeTests
{
    private static readonly DateTimeOffset Cutoff = DateTimeOffset.Parse("2026-09-03T00:00:00Z");

    [Fact]
    public void A_top_level_timestamp_before_the_cutoff_is_old()
    {
        Assert.True(SessionLineAge.IsOlderThan("""{"timestamp":"2026-09-01T00:00:00Z","type":"x"}""", Cutoff));
    }

    [Fact]
    public void A_top_level_timestamp_after_the_cutoff_is_not_old()
    {
        Assert.False(SessionLineAge.IsOlderThan("""{"timestamp":"2026-09-04T00:00:00Z","type":"x"}""", Cutoff));
    }

    [Fact]
    public void A_line_without_a_timestamp_is_never_old()
    {
        Assert.False(SessionLineAge.IsOlderThan("""{"type":"x"}""", Cutoff));
    }

    [Fact]
    public void An_old_nested_timestamp_inside_a_recent_line_does_not_count()
    {
        const string line = """{"payload":{"timestamp":"2026-08-01T00:00:00Z"},"timestamp":"2026-09-04T00:00:00Z"}""";

        Assert.False(SessionLineAge.IsOlderThan(line, Cutoff));
    }

    [Fact]
    public void A_timestamp_quoted_inside_a_string_does_not_count()
    {
        const string line = """{"text":"{\"timestamp\":\"2026-08-01T00:00:00Z\"}","timestamp":"2026-09-04T00:00:00Z"}""";

        Assert.False(SessionLineAge.IsOlderThan(line, Cutoff));
    }

    [Fact]
    public void A_truncated_or_unparseable_line_is_not_old()
    {
        Assert.False(SessionLineAge.IsOlderThan("""{"timestamp":"2026-09-01T00:00:00Z","type":""", Cutoff));
        Assert.False(SessionLineAge.IsOlderThan("""{"timestamp":"not a date"}""", Cutoff));
    }
}
