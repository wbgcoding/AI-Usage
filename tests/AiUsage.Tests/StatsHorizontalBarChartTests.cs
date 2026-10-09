using System.Globalization;
using System.Windows;
using AiUsage.Stats;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>The horizontal bar chart asks for exactly the height its rows need, so a panel with a
/// single project is not a large block of empty space.</summary>
public class StatsHorizontalBarChartTests
{
    [Fact]
    public void Desired_height_follows_the_row_count()
    {
        var (one, three, none) = RunOnSta(() =>
        {
            var day = new DateOnly(2026, 9, 1);
            double Measure(int rows)
            {
                var chart = new StatsHorizontalBarChart
                {
                    Rows = Enumerable.Range(0, rows)
                        .Select(i => new StatsProjectRow($"p{i}", $"p{i}", 10, 10, day, day))
                        .ToList(),
                };
                chart.Measure(new Size(400, double.PositiveInfinity));
                return chart.DesiredSize.Height;
            }
            return (Measure(1), Measure(3), Measure(0));
        });

        Assert.Equal(3 * one, three, 3);
        Assert.Equal(one, none, 3);
        Assert.True(one < 40);
    }

    /// <summary>The hover tooltip's own text builder is pure, so every line - and the way an absent
    /// optional field simply drops its own line rather than printing a blank one - is checked here
    /// without a visual tree at all.</summary>
    [Fact]
    public void BuildProjectDetailTooltipLinesIncludesEveryLineWhenAllDataIsPresent()
    {
        var culture = CultureInfo.InvariantCulture;
        var row = new StatsProjectRow(
            "proj", @"C:\code\proj", Total: 1500, Percent: 42.5,
            FirstActivity: new DateOnly(2026, 1, 1), LastActivity: new DateOnly(2026, 1, 3),
            InputTokens: 900, OutputTokens: 500, CachedTokens: 100,
            ProviderIds: ["claude", "codex"], MainModel: "claude-sonnet-5",
            SessionCount: 7);
        var providerDisplayNames = new Dictionary<string, string> { ["claude"] = "Claude", ["codex"] = "Codex" };

        var lines = StatsHorizontalBarChart.BuildProjectDetailTooltipLines(
            row, providerDisplayNames,
            folderFormat: "Folder: {0}", tokensFormat: "Tokens: {0} ({1} %)",
            inputOutputCacheFormat: "Input {0} \u00b7 Output {1} \u00b7 Cache {2}",
            providersFormat: "Providers: {0}", mainModelFormat: "Main model: {0}",
            activeFormat: "Active: {0}", sessionsFormat: "Sessions: {0}",
            tokenWord: "tokens", culture);

        Assert.Equal(
        [
            "proj",
            @"Folder: C:\code\proj",
            "Tokens: 1,500 tokens (42.5 %)",
            "Input 900 \u00b7 Output 500 \u00b7 Cache 100",
            "Providers: Claude, Codex",
            "Main model: Sonnet 5",
            "Active: 01/01-01/03/2026",
            "Sessions: 7",
        ], lines);
    }

    [Fact]
    public void BuildProjectDetailTooltipLinesOmitsEveryLineWhoseDataIsMissing()
    {
        var culture = CultureInfo.InvariantCulture;
        var row = new StatsProjectRow(
            "proj", @"C:\code\proj", Total: 1500, Percent: 42.5,
            FirstActivity: new DateOnly(2026, 1, 1), LastActivity: new DateOnly(2026, 1, 3));

        var lines = StatsHorizontalBarChart.BuildProjectDetailTooltipLines(
            row, providerDisplayNames: new Dictionary<string, string>(),
            folderFormat: "Folder: {0}", tokensFormat: "Tokens: {0} ({1} %)",
            inputOutputCacheFormat: "Input {0} \u00b7 Output {1} \u00b7 Cache {2}",
            providersFormat: "Providers: {0}", mainModelFormat: "Main model: {0}",
            activeFormat: "Active: {0}", sessionsFormat: "Sessions: {0}",
            tokenWord: "tokens", culture);

        Assert.Equal(
        [
            "proj",
            @"Folder: C:\code\proj",
            "Tokens: 1,500 tokens (42.5 %)",
            "Input 0 \u00b7 Output 0 \u00b7 Cache 0",
            "Active: 01/01-01/03/2026",
        ], lines);
    }

    [Fact]
    public void Cache_share_bar_accessible_name_follows_label_changes()
    {
        var name = RunOnSta(() =>
        {
            var bar = new StatsCacheShareBar { Values = [50L, 50L, 0L, 0L] };
            bar.SegmentLabels = ["Input", "Write", "Read", "Output"];
            return System.Windows.Automation.AutomationProperties.GetName(bar);
        });

        Assert.StartsWith("Input: 50", name);
        Assert.DoesNotContain("?", name);
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "measure did not finish");
        if (failure is not null)
            throw failure;
        return result!;
    }

    [Fact]
    public void DisplayNamesShowTheShortLabelAndOnlyAddTheParentFolderWhereTwoRowsReadTheSame()
    {
        var day = new DateOnly(2026, 1, 1);
        StatsProjectRow Row(string label, string path) => new(label, path, 100, 10, day, day);
        var rows = new[]
        {
            Row("app", @"C:\work\app"),
            Row("web", @"C:\work\web"),
            Row("App", @"D:\clients\acme\App"),
            Row("No project", "No project"),
        };

        var names = StatsHorizontalBarChart.DisplayNames(rows);

        Assert.Equal(new[] { "app (work)", "web", "App (acme)", "No project" }, names.ToArray());
    }

    [Fact]
    public void BuildProjectDetailTooltipLinesUsesTheGivenDisplayNameAsItsFirstLine()
    {
        var day = new DateOnly(2026, 1, 1);
        var row = new StatsProjectRow("app", @"C:\work\app", 100, 10, day, day);

        var lines = StatsHorizontalBarChart.BuildProjectDetailTooltipLines(
            row, new Dictionary<string, string>(), "Folder: {0}", "", "", "", "", "", "", "tokens",
            CultureInfo.InvariantCulture, displayName: "app (work)");

        Assert.Equal("app (work)", lines[0]);
        Assert.Equal(@"Folder: C:\work\app", lines[1]);
    }
}
