using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

[Trait("Layer", "Logic")]
public class ThresholdRowClampTests
{
    [Theory]
    [InlineData(20, SettingsRanges.MinThresholdPercent)]
    [InlineData(140, SettingsRanges.MaxThresholdPercent)]
    [InlineData(75, 75)]
    public void Every_window_stores_its_threshold_clamped_into_the_range(double typed, double expected)
    {
        var thresholds = new ThresholdSettings();
        var saves = 0;
        var row = new ThresholdRowViewModel("claude", "Claude", thresholds, () => saves++);

        row.FiveHour = typed;
        row.Weekly = typed;
        row.Other = typed;

        Assert.Equal((expected, expected, expected), (row.FiveHour, row.Weekly, row.Other));
        Assert.Equal((expected, expected, expected), (thresholds.FiveHour, thresholds.Weekly, thresholds.Other));
        Assert.Equal(3, saves);
    }
}
