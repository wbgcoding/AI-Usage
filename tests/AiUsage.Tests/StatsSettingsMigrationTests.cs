using AiUsage.Storage;

namespace AiUsage.Tests;

public class StatsSettingsMigrationTests
{
    [Theory]
    [InlineData("{\"schemaVersion\":1,\"statsGrouping\":\"Day\",\"statsPerDayView\":\"Weekday\"}", "Weekday")]
    [InlineData("{\"schemaVersion\":1,\"statsGrouping\":\"Day\",\"statsPerDayView\":\"Hour\"}", "Hour")]
    [InlineData("{\"schemaVersion\":1,\"statsGrouping\":\"Day\",\"statsPerDayView\":\"Day\"}", "Day")]
    [InlineData("{\"schemaVersion\":1,\"statsGrouping\":\"Model\",\"statsPerDayView\":\"Hour\"}", "Model")]
    [InlineData("{\"schemaVersion\":1,\"statsPerDayView\":\"Weekday\"}", "Weekday")]
    [InlineData("{\"schemaVersion\":1,\"statsGrouping\":\"Week\"}", "Week")]
    public void The_old_chart_switch_carries_into_the_grouping(string json, string expected)
    {
        var (settings, refusal) = SettingsStore.Validate(json);

        Assert.Null(refusal);
        Assert.Equal(expected, settings!.StatsGrouping);
        Assert.Null(settings.LegacyStatsPerDayView);
    }
}
