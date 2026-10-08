using AiUsage.Models;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class SettingsRangesTests
{
    [Theory]
    [InlineData(14, 15)]
    [InlineData(16 * 60, 15 * 60)]
    [InlineData(60, 60)]
    public void ClampRefreshSecondsMatchesTheLiteralExamples(int input, int expected) =>
        Assert.Equal(expected, SettingsRanges.ClampRefreshSeconds(input));

    [Theory]
    [InlineData(40, 50)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void ClampThresholdMatchesTheLiteralExamples(double input, double expected) =>
        Assert.Equal(expected, SettingsRanges.ClampThreshold(input));

    [Theory]
    [InlineData(6, 7)]
    [InlineData(4000, 3650)]
    [InlineData(365, 365)]
    [InlineData(1460, 1460)]
    [InlineData(1600, 1460)]
    public void SnapRetentionDaysMatchesTheLiteralExamples(int input, int expected) =>
        Assert.Equal(expected, SettingsRanges.SnapRetentionDays(input));

    [Theory]
    [InlineData(19, 20)]
    [InlineData(20, 20)]
    [InlineData(100, 100)]
    [InlineData(200, 100)]
    [InlineData(65, 65)] // between the old minimum (70) and the new one (20): loads unchanged
    public void ClampWindowOpacityPercentMatchesTheLiteralExamples(int input, int expected) =>
        Assert.Equal(expected, SettingsRanges.ClampWindowOpacityPercent(input));

    [Fact]
    public void At30DaysRetentionOnlyYearIsNotOffered()
    {
        var available = SettingsRanges.AvailableChartRanges(30);

        Assert.Contains(available, o => o.Value == "Day");
        Assert.Contains(available, o => o.Value == "Week");
        Assert.Contains(available, o => o.Value == "Month");
        Assert.DoesNotContain(available, o => o.Value == "Year");
    }

    [Fact]
    public void AtMaxRetentionEveryRangeIsOffered()
    {
        var available = SettingsRanges.AvailableChartRanges(3650);

        Assert.Equal(5, available.Count);
    }

    // "All" means whatever history actually exists, so - unlike "Year" - it stays offered no
    // matter how short the retention setting is.
    [Fact]
    public void AllIsOfferedAtTheDefaultRetentionOfAYear()
    {
        var available = SettingsRanges.AvailableChartRanges(365);

        Assert.Contains(available, o => o.Value == "All");
    }

    [Fact]
    public void At30DaysRetentionYearIsExcludedButAllIsStillOffered()
    {
        var available = SettingsRanges.AvailableChartRanges(30);

        Assert.DoesNotContain(available, o => o.Value == "Year");
        Assert.Contains(available, o => o.Value == "All");
    }

    [Fact]
    public void ResolveThresholdReturnsTheGlobalPairWhenNotUsingCustom()
    {
        var settings = new AppSettings { DefaultThreshold = 77, DefaultThresholdEnabled = false };

        var (threshold, enabled) = SettingsRanges.ResolveThreshold(settings, "claude", WindowKind.FiveHour);

        Assert.Equal(77, threshold);
        Assert.False(enabled);
    }

    [Fact]
    public void ResolveThresholdReturnsTheGlobalPairForEveryWindowKindIncludingOther()
    {
        var settings = new AppSettings { DefaultThreshold = 90, DefaultThresholdEnabled = true };

        Assert.Equal((90.0, true), SettingsRanges.ResolveThreshold(settings, "gemini", WindowKind.FiveHour));
        Assert.Equal((90.0, true), SettingsRanges.ResolveThreshold(settings, "gemini", WindowKind.Weekly));
        Assert.Equal((90.0, true), SettingsRanges.ResolveThreshold(settings, "gemini", WindowKind.Other));
    }

    [Fact]
    public void ResolveThresholdReturnsThePerProviderValuePerWindowKindWhenUsingCustom()
    {
        var settings = new AppSettings();
        var thresholds = settings.Providers["claude"].Thresholds;
        thresholds.UseCustom = true;
        thresholds.FiveHour = 60;
        thresholds.FiveHourEnabled = false;
        thresholds.Weekly = 70;
        thresholds.WeeklyEnabled = true;
        thresholds.Other = 80;
        thresholds.OtherEnabled = false;

        Assert.Equal((60.0, false), SettingsRanges.ResolveThreshold(settings, "claude", WindowKind.FiveHour));
        Assert.Equal((70.0, true), SettingsRanges.ResolveThreshold(settings, "claude", WindowKind.Weekly));
        Assert.Equal((80.0, false), SettingsRanges.ResolveThreshold(settings, "claude", WindowKind.Other));
    }

    [Fact]
    public void ResolveThresholdFallsBackToTheGlobalPairForAProviderWithNoOwnSettingsEntry()
    {
        var settings = new AppSettings { DefaultThreshold = 42, DefaultThresholdEnabled = true };

        var (threshold, enabled) = SettingsRanges.ResolveThreshold(settings, "not-registered", WindowKind.Weekly);

        Assert.Equal(42, threshold);
        Assert.True(enabled);
    }
}
