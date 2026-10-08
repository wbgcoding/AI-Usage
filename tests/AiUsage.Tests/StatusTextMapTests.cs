using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class StatusTextMapTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(62.5, 63)]
    [InlineData(63.5, 64)]
    [InlineData(0.5, 1)]
    [InlineData(99.5, 100)]
    public void UsagePercentRoundsAwayFromZero(double percent, int expected) =>
        Assert.Equal(expected, StatusTextMap.UsagePercent(percent));

    [Fact]
    public void WindowNamesAreDurationsAndCopilotBucketsAreTranslated()
    {
        try
        {
            LocalizationService.Instance.SetLanguage("en");
            Assert.Equal("7 days", StatusTextMap.Resolve("Window_Weekly"));
            Assert.Equal("Chat", StatusTextMap.Resolve("Window_CopilotChat"));
            Assert.Equal("Completions", StatusTextMap.Resolve("Window_CopilotCompletions"));
            Assert.Equal("Premium requests", StatusTextMap.Resolve("Window_CopilotPremium"));

            LocalizationService.Instance.SetLanguage("de");
            Assert.Equal("7 Tage", StatusTextMap.Resolve("Window_Weekly"));
            Assert.Equal("Chat", StatusTextMap.Resolve("Window_CopilotChat"));
            Assert.Equal("Code-Vervollständigungen", StatusTextMap.Resolve("Window_CopilotCompletions"));
            Assert.Equal("Premium-Anfragen", StatusTextMap.Resolve("Window_CopilotPremium"));
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void PercentFollowsTheLanguage()
    {
        try
        {
            LocalizationService.Instance.SetLanguage("en");
            Assert.Equal("42%", StatusTextMap.FormatPercent("42"));
            Assert.Equal("+13%", StatusTextMap.FormatPercent("+13"));

            LocalizationService.Instance.SetLanguage("de");
            Assert.Equal("42 %", StatusTextMap.FormatPercent("42"));
            Assert.Equal("+13 %", StatusTextMap.FormatPercent("+13"));
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    // The tile used to round with the interpolation format "{0:0}", the tray used
    // Math.Round(..., AwayFromZero) and the notification used bare Math.Round (to even) - at 62.5%
    // the tile and tray said 63 while the balloon said 62. All three now go through the same
    // StatusTextMap.UsagePercent helper.
    [Theory]
    [InlineData(62.5, 63)]
    [InlineData(63.5, 64)]
    [InlineData(0.5, 1)]
    [InlineData(99.5, 100)]
    public void TileTrayAndNotificationAgreeOnTheRoundedPercent(double percent, int expected)
    {
        // English explicitly: the notification path below now goes through the localisation
        // service (Notify.ThresholdNoReset) rather than a bare interpolated string, so this must
        // not depend on whatever language another test left the shared instance in.
        LocalizationService.Instance.SetLanguage("en");
        try
        {
            var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, percent, resetsAt: null, windowMinutes: 300);

            var row = new UsageRowViewModel(window, Now);
            Assert.Contains($"{expected}%", row.RightLabelText);

            var tray = TrayTooltipBuilder.Build([new TrayTooltipBuilder.ProviderLine("Claude", percent, null)]);
            Assert.Contains($"5h {expected}%", tray);

            var notification = new ThresholdNotification("claude", "Claude", WindowKind.FiveHour, percent, ResetsAt: null);
            Assert.Contains($"{expected}%", notification.Text(Now));
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void AKeyNotShapedLikeAnInternalKeyIsReturnedVerbatim()
    {
        Assert.Equal("Claude Sonnet 4.5", StatusTextMap.Resolve("Claude Sonnet 4.5"));
    }

    [Fact]
    public void AnUnknownInternalShapedKeyStillResolvesToEmpty()
    {
        Assert.Equal("", StatusTextMap.Resolve("Window_DoesNotExist"));
    }

    [Fact]
    public void NoOtherCodeRoundsAPercentageOutsideTheSharedHelper()
    {
        var offenders = System.IO.Directory
            .EnumerateFiles(FindSourceRoot(), "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}")
                     && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}")
                     && !f.EndsWith("StatusTextMap.cs")
                     // These round something that is not a usage percentage (a duration in
                     // years, a chart's mid-gridline pixel, a chart's column count, the tray icon's
                     // digit box in whole pixels) and legitimately stay on bare Math.Round.
                     && !f.EndsWith("DurationFormatter.cs")
                     && !f.EndsWith("HistoryChart.cs")
                     && !f.EndsWith("TrayIconRenderer.cs"))
            .SelectMany(f => System.IO.File.ReadLines(f).Select((line, i) => (file: f, line: i + 1, text: line)))
            .Where(l => l.text.Contains("Math.Round"))
            .ToList();

        Assert.Empty(offenders);
    }

    // A provider may hand a resource key straight to ProviderError instead of one of this class's
    // own internal words. Those keys carry dots, so they never matched the internal key shape and
    // used to be handed back verbatim - the Claude tile showed the bare text
    // "State.NotSignedIn.ClaudeCode" where the sentence belongs.
    [Theory]
    [InlineData("State.NotSignedIn.ClaudeCode")]
    [InlineData("State.NotSignedIn.WebSession")]
    [InlineData("State.NotSignedIn.ClaudeExpired")]
    public void AResourceKeyHandedOverDirectlyIsResolved(string key)
    {
        var text = StatusTextMap.Resolve(key);

        Assert.NotEqual(key, text);
        Assert.NotEmpty(text);
    }

    // Anything that is genuinely not a resource key - a model display name out of a provider's
    // answer, for instance - still comes back untouched rather than being swallowed to "".
    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("claude-sonnet-5")]
    [InlineData("Some sentence a provider sent.")]
    public void AValueThatIsNoResourceKeyComesBackUntouched(string value) =>
        Assert.Equal(value, StatusTextMap.Resolve(value));

    // The regression guard for the defect above: every literal any provider hands to ProviderError
    // has to end up as real text, in both languages.
    [Fact]
    public void EveryProviderErrorKeyResolvesToText()
    {
        var providers = System.IO.Path.Combine(FindSourceRoot(), "Providers");
        var literals = System.IO.Directory
            .EnumerateFiles(providers, "*.cs", System.IO.SearchOption.AllDirectories)
            .SelectMany(System.IO.File.ReadLines)
            .SelectMany(line => System.Text.RegularExpressions.Regex
                .Matches(line, "ProviderError\\(\\s*\"([^\"]+)\"(?:\\s*,\\s*\"([^\"]+)\")?")
                .SelectMany(m => m.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1)
                    .Where(g => g.Success).Select(g => g.Value)))
            .Distinct()
            .ToList();

        Assert.NotEmpty(literals);

        foreach (var language in new[] { "en", "de" })
        {
            LocalizationService.Instance.SetLanguage(language);
            var unresolved = literals.Where(k => StatusTextMap.Resolve(k) == k).ToList();
            Assert.Empty(unresolved);
        }
    }

    private static string FindSourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "src", "AiUsage")))
            dir = System.IO.Path.GetDirectoryName(dir);
        return dir is null
            ? throw new InvalidOperationException("Could not locate the src/AiUsage directory from the test output path.")
            : System.IO.Path.Combine(dir, "src", "AiUsage");
    }
}
