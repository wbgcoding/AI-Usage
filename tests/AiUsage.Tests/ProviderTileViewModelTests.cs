using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class ProviderTileViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static string ProviderTileXamlPath([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        var repoRoot = Path.GetDirectoryName(testsDir)!;
        return Path.Combine(repoRoot, "src", "AiUsage", "Views", "Controls", "ProviderTile.xaml");
    }

    // A plain text scan over the XAML (see WindowTitleBindingTests for why not a live visual tree)
    // proves the header's own name button opens the details and the refresh moved into the tile menu.
    [Fact]
    public void TheHeaderNameButtonIsBoundToToggleDetailsCommand()
    {
        var text = File.ReadAllText(ProviderTileXamlPath());

        var match = Regex.Match(text, @"<Button Style=""\{StaticResource TileNameButtonStyle\}""[^>]*Command=""\{Binding ToggleDetailsCommand\}""", RegexOptions.Singleline);
        Assert.True(match.Success, "ProviderTile.xaml: the header name button is not bound to ToggleDetailsCommand.");
    }

    [Fact]
    public void TheTileMenuStartsWithRefreshThenASeparator()
    {
        var text = File.ReadAllText(ProviderTileXamlPath());

        var match = Regex.Match(
            text, @"<ContextMenu>\s*<MenuItem Header=""[^""]*Action\.RefreshNow\][^>]*Command=""\{Binding RefreshCommand\}""\s*/>\s*<Separator/>", RegexOptions.Singleline);
        Assert.True(match.Success, "ProviderTile.xaml: the tile menu does not start with the refresh item and a separator.");
    }

    [Fact]
    public void TheNameCommandTogglesTheDetailsOnlyWhileThereAreDetails()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10) with { Diagnostics = [] }, Now);
        Assert.False(tile.ToggleDetailsCommand.CanExecute(null));

        tile.Apply(Snapshot(ProviderStatus.Ok, 10) with { Diagnostics = ["a line"] }, Now);
        Assert.True(tile.ToggleDetailsCommand.CanExecute(null));
        tile.ToggleDetailsCommand.Execute(null);
        Assert.True(tile.DetailsExpanded);
        tile.ToggleDetailsCommand.Execute(null);
        Assert.False(tile.DetailsExpanded);
    }

    [Fact]
    public void TheNameTooltipSaysClickForDetailsOnlyWhileThereAreDetails()
    {
        var loc = LocalizationService.Instance;
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10, sourceKind: SourceKind.None) with { Diagnostics = [] }, Now);
        Assert.DoesNotContain(loc["Tile.NameTooltip"], tile.NameTooltipText);

        tile.Apply(Snapshot(ProviderStatus.Ok, 10, sourceKind: SourceKind.None) with { Diagnostics = ["a line"] }, Now);
        Assert.Equal(loc["Tile.NameTooltip"], tile.NameTooltipText);
    }

    // The account name no longer has a dedicated header column, so the name button's tooltip is the
    // only place left that shows it - these two cases prove the composition itself (hint alone, hint
    // plus account on its own line), not just that a value exists.
    [Fact]
    public void NameTooltipTextIsEmptyWithoutDetailsAccountOrSource()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        Assert.Equal("", tile.NameTooltipText);
    }

    [Fact]
    public void NameTooltipTextAddsTheAccountOnASecondLineWhenKnown()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, sourceKind: SourceKind.None) with { AccountLabel = "Work account", Diagnostics = ["a line"] }, Now);

        var expected = $"{LocalizationService.Instance["Tile.NameTooltip"]}{Environment.NewLine}Work account";
        Assert.Equal(expected, tile.NameTooltipText);
    }

    [Fact]
    public void SourceShowsInTheNameTooltip()
    {
        var loc = LocalizationService.Instance;
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, sourceKind: SourceKind.WebSession) with { AccountLabel = "Work account" }, Now);

        var expected = $"Work account{Environment.NewLine}Quelle: Websitzung";
        Assert.Equal(expected, tile.NameTooltipText);
    }

    [Theory]
    [InlineData("de", "Claude: nicht verbunden")]
    [InlineData("en", "Claude: not connected")]
    public void NoTileTextEverReadsNoSource(string language, string expectedDiagnostic)
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(language);
        try
        {
            var tile = new ProviderTileViewModel("claude", "Claude");
            tile.Apply(Snapshot(ProviderStatus.NotSignedIn, sourceKind: SourceKind.None), Now);

            Assert.Equal("", tile.NameTooltipText);
            Assert.Equal(expectedDiagnostic, tile.SourceDiagnosticText);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void SourceTipFollowsALaterSourceChange()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, sourceKind: SourceKind.LocalFile), Now);
        Assert.EndsWith("Quelle: lokale Dateien", tile.NameTooltipText, StringComparison.Ordinal);

        tile.Apply(Snapshot(ProviderStatus.Ok, sourceKind: SourceKind.LocalLogin), Now);
        Assert.EndsWith("Quelle: CLI-Anmeldung", tile.NameTooltipText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTileHeaderHasNoSourceTextBlock() =>
        Assert.DoesNotContain("SourceBadgeText", File.ReadAllText(ProviderTileXamlPath()), StringComparison.Ordinal);

    [Fact]
    public void AFailedReadAfterGoodNumbersKeepsTheRowsAndFlagsLastValues()
    {
        var loc = LocalizationService.Instance;
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, percent: 42), Now);

        tile.Apply(FailedSnapshot(FailureKind.ServerError, 502), Now.AddMinutes(5));

        Assert.True(tile.IsShowingLastValues);
        Assert.Single(tile.Rows);
        Assert.Equal(42, tile.Rows[0].UsedPercent);
        Assert.True(tile.HasNumbers);
        Assert.False(tile.ShowPlaceholder);
        Assert.Contains(loc.Format("State.Failed.Server.Head", "Claude"), tile.FailureNoticeText);
        Assert.Contains("502", tile.FailureNoticeText);
        Assert.Equal(Now, tile.LastSuccessAt);
    }

    [Fact]
    public void LastValuesEndWithTheNextGoodSnapshot()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, percent: 42), Now);
        tile.Apply(FailedSnapshot(FailureKind.Timeout), Now.AddMinutes(5));

        tile.Apply(Snapshot(ProviderStatus.Ok, percent: 50) with { FetchedAt = Now.AddMinutes(10), DataTimestamp = Now.AddMinutes(10) }, Now.AddMinutes(10));

        Assert.False(tile.IsShowingLastValues);
        Assert.Equal(50, tile.Rows[0].UsedPercent);
        Assert.Equal("", tile.FailureNoticeText);
    }

    [Fact]
    public void AFailedReadWithNoEarlierDataShowsThePlaceholder()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.Apply(FailedSnapshot(FailureKind.ServerError, 502), Now);

        Assert.False(tile.IsShowingLastValues);
        Assert.True(tile.ShowPlaceholder);
        Assert.Empty(tile.Rows);
    }

    [Fact]
    public void AFailedReadAfterNumbersOlderThanTheStaleLimitShowsThePlaceholder()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, percent: 42), Now);

        tile.Apply(FailedSnapshot(FailureKind.ServerError, 502), Now + ProviderFreshness.StaleAfter + TimeSpan.FromMinutes(1));

        Assert.False(tile.IsShowingLastValues);
        Assert.True(tile.ShowPlaceholder);
        Assert.Empty(tile.Rows);
    }

    [Fact]
    public void AStaleTileKeepsItsRowsThroughAFailureToo()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Stale, percent: 42), Now);

        tile.Apply(FailedSnapshot(FailureKind.Network), Now.AddMinutes(5));

        Assert.True(tile.IsShowingLastValues);
        Assert.Single(tile.Rows);
        Assert.False(tile.ShowStaleNotice);
    }

    [Fact]
    public void ANotSignedInSnapshotAfterGoodNumbersBehavesAsBefore()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };
        tile.Apply(Snapshot(ProviderStatus.Ok, percent: 42), Now);

        tile.Apply(Snapshot(ProviderStatus.NotSignedIn) with { Windows = [] }, Now.AddMinutes(5));

        Assert.False(tile.IsShowingLastValues);
        Assert.Empty(tile.Rows);
        Assert.True(tile.ShowPlaceholder);
    }

    [Theory]
    [InlineData(FailureKind.ServerError, true)]
    [InlineData(FailureKind.Timeout, true)]
    [InlineData(FailureKind.Network, false)]
    [InlineData(FailureKind.Refused, false)]
    [InlineData(FailureKind.Other, false)]
    public void TheStatusLinkShowsOnlyForServerErrorsAndTimeouts(FailureKind kind, bool shown)
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.Apply(FailedSnapshot(kind, kind == FailureKind.ServerError ? 502 : null), Now);

        Assert.Equal(shown, tile.ShowStatusLink);
        Assert.Equal(LocalizationService.Instance.Format("Tile.CheckStatus", "Claude"), tile.StatusLinkText);
    }

    [Fact]
    public void TheStatusLinkAlsoShowsWhileTheLastValuesStayOnShow()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, percent: 42), Now);

        tile.Apply(FailedSnapshot(FailureKind.Timeout), Now.AddMinutes(5));

        Assert.True(tile.IsShowingLastValues);
        Assert.True(tile.ShowStatusLink);
        Assert.True(tile.ShowFailureLink);
    }

    [Fact]
    public void TheStatusLinkGoesAwayWithTheFailure()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(FailedSnapshot(FailureKind.ServerError, 503), Now);

        tile.Apply(Snapshot(ProviderStatus.Ok, percent: 42), Now.AddMinutes(5));

        Assert.False(tile.ShowStatusLink);
    }

    [Fact]
    public void ACodexTileWithStaleLocalFileNumbersShowsTheHintAndTheWebSignInLink()
    {
        var loc = LocalizationService.Instance;
        var tile = new ProviderTileViewModel("codex", "Codex") { SupportsInAppSignIn = true };

        tile.Apply(Snapshot(ProviderStatus.Stale, 42, sourceKind: SourceKind.LocalFile), Now);

        Assert.Equal(loc["Tile.CodexLocalHint"], tile.ReasonText);
        Assert.True(tile.ShowStaleNotice);
        Assert.True(tile.ShowCodexSignInLink);
    }

    [Theory]
    [InlineData("codex", ProviderStatus.Stale, SourceKind.WebSession)]
    [InlineData("codex", ProviderStatus.Ok, SourceKind.LocalFile)]
    [InlineData("claude", ProviderStatus.Stale, SourceKind.LocalFile)]
    public void TheCodexHintShowsOnlyForCodexStaleOnLocalFiles(string providerId, ProviderStatus status, SourceKind source)
    {
        var tile = new ProviderTileViewModel(providerId, providerId) { SupportsInAppSignIn = true };

        tile.Apply(Snapshot(status, 42, sourceKind: source), Now);

        Assert.NotEqual(LocalizationService.Instance["Tile.CodexLocalHint"], tile.ReasonText);
        Assert.False(tile.ShowCodexSignInLink);
    }

    [Fact]
    public void TheCodexWebSignInLinkRunsTheTilesSignInAction()
    {
        var tile = new ProviderTileViewModel("codex", "Codex") { SupportsInAppSignIn = true };
        tile.Apply(Snapshot(ProviderStatus.Stale, 42, sourceKind: SourceKind.LocalFile), Now);
        var requested = 0;
        tile.ActionRequested += (_, _) => requested++;

        tile.RunActionCommand.Execute(null);

        Assert.Equal(1, requested);
    }

    [Fact]
    public void ACodexTileThatIsAlreadySignedInOnTheWebGetsNoSignInLink()
    {
        var tile = new ProviderTileViewModel("codex", "Codex") { SupportsInAppSignIn = true };
        tile.MarkSignedIn(Now.AddMinutes(-1));

        tile.Apply(Snapshot(ProviderStatus.Stale, 42, sourceKind: SourceKind.LocalFile), Now);

        Assert.False(tile.ShowCodexSignInLink);
    }

    private static ProviderSnapshot FailedSnapshot(FailureKind kind, int? httpStatus = null, string reasonKey = "Status_Failed_Reason") =>
        Snapshot(ProviderStatus.Failed, error: new ProviderError(reasonKey, "Action_Retry", Kind: kind, HttpStatus: httpStatus))
            with { Windows = [], SourceKind = SourceKind.None, DataTimestamp = null };

    [Fact]
    public void AServerErrorNamesTheProviderAndTheStatusAndOffersNoSignIn()
    {
        var loc = LocalizationService.Instance;
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };

        tile.Apply(FailedSnapshot(FailureKind.ServerError, 502), Now);

        Assert.Equal(loc.Format("State.Failed.Server.Head", "Claude"), tile.HeadlineText);
        Assert.Equal(loc.Format("State.Failed.Server.Reason", "Claude", 502), tile.ReasonText);
        Assert.Contains("502", tile.ReasonText);
        Assert.False(tile.ShowSignIn);
        Assert.False(tile.ShowHeaderSignIn);
    }

    [Fact]
    public void ATimeoutAndALostConnectionHaveTheirOwnWordingAndNoSignIn()
    {
        var loc = LocalizationService.Instance;
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };

        tile.Apply(FailedSnapshot(FailureKind.Timeout), Now);
        Assert.Equal(loc.Format("State.Failed.Timeout.Head", "Claude"), tile.HeadlineText);
        Assert.Equal(loc["State.Failed.Timeout.Reason"], tile.ReasonText);
        Assert.False(tile.ShowSignIn);

        tile.Apply(FailedSnapshot(FailureKind.Network), Now);
        Assert.Equal(loc["State.Failed.Network.Head"], tile.HeadlineText);
        Assert.Equal(loc.Format("State.Failed.Network.Reason", "Claude"), tile.ReasonText);
        Assert.False(tile.ShowSignIn);
    }

    [Fact]
    public void ARefusedRequestNamesTheStatusButKeepsTheSignInButton()
    {
        var loc = LocalizationService.Instance;
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };

        tile.Apply(FailedSnapshot(FailureKind.Refused, 404), Now);

        Assert.Equal(loc.Format("State.Failed.Refused.Head", "Claude"), tile.HeadlineText);
        Assert.Equal(loc.Format("State.Failed.Refused.Reason", "Claude", 404), tile.ReasonText);
        Assert.True(tile.ShowSignIn);
    }

    [Fact]
    public void AnUnknownFailureKeepsTheGenericWordingAndTheSignInButton()
    {
        var loc = LocalizationService.Instance;
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };

        tile.Apply(FailedSnapshot(FailureKind.Other), Now);

        Assert.Equal(loc["State.Failed.Head"], tile.HeadlineText);
        Assert.Equal(loc["State.Failed.Reason"], tile.ReasonText);
        Assert.True(tile.ShowSignIn);
    }

    [Fact]
    public void ANetworkFailureKeepsAMoreSpecificReasonTheProviderGave()
    {
        var loc = LocalizationService.Instance;
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.Apply(FailedSnapshot(FailureKind.Network, reasonKey: "Status_Offline_Reason"), Now);

        Assert.Equal(loc["State.Failed.Network.Head"], tile.HeadlineText);
        Assert.Equal(loc["State.Offline.Reason"], tile.ReasonText);
    }

    [Fact]
    public void NotSignedInStillShowsTheSignInButton()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };

        tile.Apply(Snapshot(ProviderStatus.NotSignedIn) with { Windows = [] }, Now);

        Assert.True(tile.ShowSignIn);
    }

    private static ProviderSnapshot Snapshot(ProviderStatus status, double percent = 0, ProviderError? error = null, SourceKind sourceKind = SourceKind.LocalFile) =>
        new(
            ProviderId: "claude",
            Windows: [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, percent, Now.AddHours(2), 300)],
            PlanType: "Plus",
            SourceKind: sourceKind,
            FetchedAt: Now,
            DataTimestamp: Now,
            Status: status,
            Error: error);

    [Theory]
    [InlineData(0, UsageLevel.Ok)]
    [InlineData(59.9, UsageLevel.Ok)]
    [InlineData(60, UsageLevel.Ok)]
    [InlineData(85, UsageLevel.Warn)]
    [InlineData(85.1, UsageLevel.Crit)]
    [InlineData(100, UsageLevel.Crit)]
    public void PercentClassifiesToTheCorrectLevel(double percent, UsageLevel expected)
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, percent), Now);

        Assert.Equal(expected, tile.Rows[0].Level);
    }

    [Theory]
    [InlineData(95, false)]
    [InlineData(99.4, false)]
    [InlineData(99.5, true)]
    [InlineData(100, true)]
    public void IsLimitReachedOnlyOnceTheDisplayedPercentWouldReadOneHundred(double percent, bool expected)
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, percent), Now);

        Assert.Equal(expected, tile.IsLimitReached);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(5, true)]
    public void HasChartDataNeedsTwoReadingsInOneSeries(int count, bool expected)
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10), Now);
        var points = Enumerable.Range(0, count)
            .Select(i => new HistoryPoint(1, Now.AddMinutes(i), WindowKind.FiveHour, 10 + i, null)).ToArray();

        tile.UpdateHistory(points, [], Now.AddDays(-1), Now);

        Assert.Equal(expected, tile.HasChartData);
        Assert.Equal(expected, tile.ShowChartBox);
        Assert.Equal(!expected, tile.ShowChartHint);
    }

    [Fact]
    public void OneReadingInEachSeriesIsStillNotAChart()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10), Now);
        var points = new[]
        {
            new HistoryPoint(1, Now, WindowKind.FiveHour, 10, null),
            new HistoryPoint(1, Now, WindowKind.Weekly, 20, null),
        };

        tile.UpdateHistory(points, [], Now.AddDays(-1), Now);

        Assert.False(tile.HasChartData);
    }

    [Fact]
    public void NeitherTheChartBoxNorTheHintShowsWhenTheChartIsOffOrTheTileIsMini()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10), Now);
        Assert.True(tile.ShowChartHint);

        tile.ChartHidden = true;
        Assert.False(tile.ShowChartHint);
        Assert.False(tile.ShowChartBox);

        tile.ChartHidden = false;
        tile.Density = TileDensity.Mini;
        Assert.False(tile.ShowChartHint);
    }

    [Fact]
    public void TheEmptyChartTextNamesTheTwoReadingsRule()
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage("en");
        try
        {
            Assert.Equal("The chart appears once there are two readings.", loc["Chart.Empty"]);
        }
        finally
        {
            loc.SetLanguage("de");
        }

        Assert.Equal("Das Diagramm erscheint ab zwei Messpunkten.", loc["Chart.Empty"]);
    }

    [Fact]
    public void UpdateHistorySplitsPointsByWindowKindInTimeOrder()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var points = new[]
        {
            new HistoryPoint(1, Now, WindowKind.FiveHour, 10, null),
            new HistoryPoint(1, Now.AddMinutes(5), WindowKind.Weekly, 20, null),
            new HistoryPoint(1, Now.AddMinutes(10), WindowKind.FiveHour, 30, null),
        };

        tile.UpdateHistory(points, [], Now.AddDays(-1), Now);

        Assert.Equal([10, 30], tile.FiveHourValues.Select(p => p.Percent));
        Assert.Equal([20], tile.WeeklyValues.Select(p => p.Percent));
        Assert.Equal(Now.AddDays(-1), tile.RangeStart);
        Assert.Equal(Now, tile.RangeEnd);
    }

    [Fact]
    public void AProviderWithOnlyOtherWindowsFillsBothChartSlotsFromDistinctLabels()
    {
        // Cursor's shape: no FiveHour/Weekly points at all, two distinct Other labels.
        var tile = new ProviderTileViewModel("cursor", "Cursor");
        var points = new[]
        {
            new HistoryPoint(1, Now, WindowKind.Other, 10, null, Label: "Window_CursorModels"),
            new HistoryPoint(1, Now, WindowKind.Other, 90, null, Label: "Window_OtherModels"),
            new HistoryPoint(1, Now.AddMinutes(5), WindowKind.Other, 15, null, Label: "Window_CursorModels"),
        };

        tile.UpdateHistory(points, [], Now.AddDays(-1), Now);

        Assert.Equal([10, 15], tile.FiveHourValues.Select(p => p.Percent));
        Assert.Equal([90], tile.WeeklyValues.Select(p => p.Percent));
        Assert.Equal(LocalizationService.Instance["Window.CursorModels"], tile.FiveHourLabelText);
        Assert.Equal(LocalizationService.Instance["Window.OtherModels"], tile.WeeklyLabelText);
    }

    [Fact]
    public void ASingleOtherWindowProviderFillsOnlyTheFirstChartSlot()
    {
        // Copilot/Gemini/Antigravity's shape: one Other label, no FiveHour/Weekly points.
        var tile = new ProviderTileViewModel("copilot", "Copilot");
        var points = new[]
        {
            new HistoryPoint(1, Now, WindowKind.Other, 40, null, Label: "Window_Other"),
        };

        tile.UpdateHistory(points, [], Now.AddDays(-1), Now);

        Assert.Equal([40], tile.FiveHourValues.Select(p => p.Percent));
        Assert.Empty(tile.WeeklyValues);
        Assert.Equal(LocalizationService.Instance["Window.Other"], tile.FiveHourLabelText);
        Assert.Equal(LocalizationService.Instance["Window.Weekly"], tile.WeeklyLabelText);
    }

    [Fact]
    public void ARegularFiveHourAndWeeklyProviderKeepsTheStandardLegendLabels()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var points = new[]
        {
            new HistoryPoint(1, Now, WindowKind.FiveHour, 10, null),
            new HistoryPoint(1, Now, WindowKind.Weekly, 20, null),
        };

        tile.UpdateHistory(points, [], Now.AddDays(-1), Now);

        Assert.Equal(LocalizationService.Instance["Window.FiveHour"], tile.FiveHourLabelText);
        Assert.Equal(LocalizationService.Instance["Window.Weekly"], tile.WeeklyLabelText);
    }

    [Fact]
    public void OldHistoryLinesWithoutALabelStillReadAsAnUnlabelledOtherSeries()
    {
        var tile = new ProviderTileViewModel("copilot", "Copilot");
        var points = new[]
        {
            new HistoryPoint(1, Now, WindowKind.Other, 40, null),
        };

        tile.UpdateHistory(points, [], Now.AddDays(-1), Now);

        Assert.Empty(tile.FiveHourValues);
        Assert.Empty(tile.WeeklyValues);
    }

    [Fact]
    public void PreviousWeekComparisonNeverBorrowsAnOtherSlot()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor");
        var points = new[] { new HistoryPoint(1, Now, WindowKind.Other, 10, null, Label: "Window_Month") };
        var previousWeek = new[] { new HistoryPoint(1, Now.AddDays(-7), WindowKind.Other, 50, null, Label: "Window_Month") };

        tile.UpdateHistory(points, previousWeek, Now.AddDays(-1), Now);

        Assert.Empty(tile.PreviousWeekValues);
    }

    [Fact]
    public void OkStatusShowsBarsNotPlaceholder()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);

        Assert.True(tile.IsOk);
        Assert.True(tile.ShowRows);
        Assert.False(tile.ShowPlaceholder);
        Assert.True(tile.ShowDiagram); // Full density by default
    }

    [Theory]
    [InlineData(ProviderStatus.NoLocalData)]
    [InlineData(ProviderStatus.SourceUnavailable)]
    [InlineData(ProviderStatus.Blocked)]
    [InlineData(ProviderStatus.Failed)]
    public void NonOkStatusShowsPlaceholderNotBars(ProviderStatus status)
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(status), Now);

        Assert.False(tile.IsOk);
        Assert.False(tile.ShowRows);
        Assert.True(tile.ShowPlaceholder);
        Assert.False(string.IsNullOrEmpty(tile.HeadlineText));
    }

    [Fact]
    public void NotSignedInExposesSignInAction()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.NotSignedIn), Now);

        Assert.True(tile.HasAction);
        Assert.Equal("Anmelden", tile.ActionLabelText);
    }

    [Fact]
    public void FreshTileHasNoSignInStateYet()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        Assert.Equal(SignInState.Unknown, tile.SignInState);
        Assert.False(tile.ShowSignIn);
        Assert.False(tile.ShowSignOut);
    }

    // A sign-in that just finished is newer than any read that started before it: such a read can
    // land afterwards (a slow web read started while the window was still open) and says "signed out"
    // only because it looked before the sign-in existed. It must not undo the finished sign-in.
    private static readonly DateTimeOffset SignedInAt = Now.AddMinutes(1);

    [Fact]
    public void ALocalReadThatStartedBeforeTheSignInFinishedDoesNotBringTheButtonBack()
    {
        var tile = new ProviderTileViewModel("gemini", "Gemini") { SupportsInAppSignIn = true };
        tile.MarkSignedIn(SignedInAt);

        tile.Apply(Snapshot(ProviderStatus.Ok, sourceKind: SourceKind.LocalLogin) with { WebSessionSignedIn = false }, SignedInAt.AddSeconds(5));

        Assert.Equal(SignInState.SignedIn, tile.SignInState);
        Assert.False(tile.ShowSignIn);
        Assert.True(tile.ShowSignOut);
    }

    [Fact]
    public void AnEmptyNotSignedInAnswerFromBeforeTheSignInIsIgnored()
    {
        var tile = new ProviderTileViewModel("gemini", "Gemini") { SupportsInAppSignIn = true };
        tile.MarkSignedIn(SignedInAt);

        tile.Apply(Snapshot(ProviderStatus.NotSignedIn) with { Windows = [] }, SignedInAt.AddSeconds(5));

        Assert.Equal(SignInState.SignedIn, tile.SignInState);
        Assert.NotEqual(ProviderStatus.NotSignedIn, tile.Status);
        Assert.False(tile.ShowSignIn);
    }

    [Fact]
    public void ARealSignedOutAnswerFromAfterTheSignInStillShowsTheButton()
    {
        var tile = new ProviderTileViewModel("gemini", "Gemini") { SupportsInAppSignIn = true };
        tile.MarkSignedIn(SignedInAt);

        tile.Apply(Snapshot(ProviderStatus.NotSignedIn) with { Windows = [], FetchedAt = SignedInAt.AddSeconds(1) }, SignedInAt.AddSeconds(5));

        Assert.Equal(SignInState.SignedOut, tile.SignInState);
        Assert.True(tile.ShowSignIn);
    }

    [Fact]
    public void ASuccessfulReadAfterTheSignInKeepsTheTileSignedIn()
    {
        var tile = new ProviderTileViewModel("gemini", "Gemini") { SupportsInAppSignIn = true };
        tile.MarkSignedIn(SignedInAt);

        tile.Apply(
            Snapshot(ProviderStatus.Ok, sourceKind: SourceKind.WebSession) with { WebSessionSignedIn = true, FetchedAt = SignedInAt.AddSeconds(1) },
            SignedInAt.AddSeconds(5));

        Assert.Equal(SignInState.SignedIn, tile.SignInState);
        Assert.Equal(ProviderStatus.Ok, tile.Status);
        Assert.False(tile.ShowSignIn);
    }

    // The settings row names the source that delivers and, next to a local source, whether the
    // browser session is signed in as well.

    private static string Via(string key) => LocalizationService.Instance[key];

    private static ProviderTileViewModel GeminiTile(ProviderSnapshot? snapshot)
    {
        var tile = new ProviderTileViewModel("gemini", "Gemini") { SupportsInAppSignIn = true };
        if (snapshot is not null)
            tile.Apply(snapshot, Now);
        return tile;
    }

    private static ProviderSnapshot GeminiSnapshot(SourceKind source, bool? webSignedIn) =>
        Snapshot(ProviderStatus.Ok, sourceKind: source) with { ProviderId = "gemini", AccountLabel = "Work account", WebSessionSignedIn = webSignedIn };

    [Fact]
    public void TheRowOfAnAccountThatNothingHasReadYetSaysNotConnected() =>
        Assert.Equal(Via("Settings.Via.None"), GeminiTile(null).SettingsRowText);

    [Fact]
    public void TheRowOfALocalOnlyAccountNamesOnlyTheLocalSource() =>
        Assert.Equal($"Work account · {Via("Settings.Via.Antigravity")}",
            GeminiTile(GeminiSnapshot(SourceKind.LocalLogin, webSignedIn: false)).SettingsRowText);

    [Fact]
    public void TheRowOfAWebOnlyAccountNamesTheWebSessionWithoutAnExtraSuffix() =>
        Assert.Equal($"Work account · {Via("Settings.Via.WebSession")}",
            GeminiTile(GeminiSnapshot(SourceKind.WebSession, webSignedIn: true)).SettingsRowText);

    [Fact]
    public void TheRowOfAnAccountSignedInBothWaysNamesTheDeliveringSourceAndTheWebSignIn() =>
        Assert.Equal($"Work account · {Via("Settings.Via.Antigravity")} · {Via("Settings.Via.WebAlsoSignedIn")}",
            GeminiTile(GeminiSnapshot(SourceKind.LocalLogin, webSignedIn: true)).SettingsRowText);

    [Fact]
    public void AFinishedSignInWindowAddsTheWebSignInToTheRowUntilASignOutClearsIt()
    {
        var tile = GeminiTile(GeminiSnapshot(SourceKind.LocalLogin, webSignedIn: null));
        Assert.DoesNotContain(Via("Settings.Via.WebAlsoSignedIn"), tile.SettingsRowText);

        tile.MarkSignedIn(SignedInAt);
        Assert.EndsWith(Via("Settings.Via.WebAlsoSignedIn"), tile.SettingsRowText);

        tile.Apply(Snapshot(ProviderStatus.NotSignedIn) with { Windows = [], SourceKind = SourceKind.None, FetchedAt = SignedInAt.AddMinutes(1) }, SignedInAt.AddMinutes(1));
        Assert.Equal(Via("Settings.Via.None"), tile.SettingsRowText);
    }

    [Fact]
    public void TheDeliveringSourceInTheRowIsTheOneOfTheLastSnapshotThatDelivered()
    {
        var tile = GeminiTile(GeminiSnapshot(SourceKind.WebSession, webSignedIn: true));
        // A tick where the local read wins: the row follows, with the web sign-in beside it.
        tile.Apply(GeminiSnapshot(SourceKind.LocalLogin, webSignedIn: true), Now);
        Assert.Contains(Via("Settings.Via.Antigravity"), tile.SettingsRowText);
        // A failed tick delivers nothing and leaves the row as it was.
        tile.Apply(Snapshot(ProviderStatus.Failed) with { Windows = [], SourceKind = SourceKind.None }, Now);
        Assert.Contains(Via("Settings.Via.Antigravity"), tile.SettingsRowText);
    }

    [Fact]
    public void NotSignedInSnapshotShowsTheSignInButtonOnly()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };
        tile.Apply(Snapshot(ProviderStatus.NotSignedIn), Now);

        Assert.True(tile.ShowSignIn);
        Assert.False(tile.ShowSignOut);
    }

    [Theory]
    [InlineData(ProviderStatus.NotSignedIn)]
    [InlineData(ProviderStatus.Blocked)]
    public void ABodyThatAlreadyOffersSignInLeavesNoSecondButtonInTheHeader(ProviderStatus status)
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };
        tile.Apply(Snapshot(status) with { Windows = [] }, Now);

        Assert.True(tile.HasAction);
        Assert.True(tile.ShowSignIn);
        Assert.False(tile.ShowHeaderSignIn);
    }

    [Fact]
    public void AFailedReadWhoseBodyOffersARetryKeepsTheHeaderSignIn()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };
        tile.Apply(Snapshot(ProviderStatus.Failed) with { Windows = [], Error = new ProviderError("Status_Offline_Reason", "Action_Retry") }, Now);

        Assert.True(tile.ShowSignIn);
        Assert.True(tile.ShowHeaderSignIn);
    }

    [Fact]
    public void TheHeaderSignInFollowsTheDensityOfTheBody()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };
        tile.Apply(Snapshot(ProviderStatus.NotSignedIn) with { Windows = [] }, Now);
        var changed = new List<string?>();
        tile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tile.Density = TileDensity.Mini;

        Assert.Contains(nameof(ProviderTileViewModel.ShowHeaderSignIn), changed);
        Assert.True(tile.ShowHeaderSignIn);
    }

    [Fact]
    public void SwitchingEveryRowOffDoesNotTurnAWorkingReadIntoASignInButton()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);
        Assert.False(tile.ShowSignIn);

        tile.ShowFiveHour = false;

        Assert.Empty(tile.Rows);
        Assert.False(tile.ShowSignIn);
    }

    private static ProviderTileViewModel TileWithARisingFiveHourForecast()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, percent: 56), Now);
        HistoryPoint[] points =
        [
            new(1, Now.AddMinutes(-90), WindowKind.FiveHour, 20, null), new(1, Now.AddMinutes(-75), WindowKind.FiveHour, 26, null),
            new(1, Now.AddMinutes(-60), WindowKind.FiveHour, 32, null), new(1, Now.AddMinutes(-45), WindowKind.FiveHour, 38, null),
            new(1, Now.AddMinutes(-30), WindowKind.FiveHour, 44, null), new(1, Now.AddMinutes(-15), WindowKind.FiveHour, 50, null),
            new(1, Now.AddMinutes(-1), WindowKind.FiveHour, 56, null),
        ];
        tile.UpdateHistory(points, [], Now.AddHours(-2), Now);
        return tile;
    }

    [Fact]
    public void AForecastSurvivesTheRowsBeingRebuiltByAWindowToggle()
    {
        var tile = TileWithARisingFiveHourForecast();
        Assert.NotEqual("", tile.Rows.Single().ForecastText);

        tile.ShowFiveHour = false;
        tile.ShowFiveHour = true;

        Assert.NotEqual("", tile.Rows.Single().ForecastText);
    }

    [Fact]
    public void AForecastFollowsTheDensityBackAndForthWithoutANewHistoryRead()
    {
        var tile = TileWithARisingFiveHourForecast();
        Assert.NotEqual("", tile.Rows.Single().ForecastText);

        tile.Density = TileDensity.Mini;
        Assert.Equal("", tile.Rows.Single().ForecastText);

        tile.Density = TileDensity.Full;
        Assert.NotEqual("", tile.Rows.Single().ForecastText);
    }

    [Fact]
    public async Task TheSettingsAgeTextIsRaisedWhenItsSecondTurnsOverSoItKeepsMoving()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok) with { DataTimestamp = DateTimeOffset.Now.AddSeconds(-12) }, Now);
        var raised = new List<string?>();
        tile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await Task.Delay(1100); // below a minute the age text shows seconds
        tile.TickCountdowns(Now);

        Assert.Contains(nameof(ProviderTileViewModel.LastSuccessAgeText), raised);
        Assert.Equal(AgeText.Describe(tile.LastSuccessAt), tile.LastSuccessAgeText);
        Assert.NotEqual(LocalizationService.Instance["Settings.NeverUpdated"], tile.LastSuccessAgeText);
    }

    [Fact]
    public void Two_ticks_within_the_same_minute_raise_the_age_texts_only_once()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok) with { DataTimestamp = DateTimeOffset.Now.AddMinutes(-12).AddSeconds(-20) }, Now);
        tile.TickCountdowns(Now);
        var raised = new List<string?>();
        tile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tile.TickCountdowns(Now.AddSeconds(1));

        Assert.DoesNotContain(nameof(ProviderTileViewModel.LastUpdatedText), raised);
        Assert.DoesNotContain(nameof(ProviderTileViewModel.LastSuccessAgeText), raised);
    }

    [Fact]
    public void An_unchanged_silent_state_is_not_raised_again_on_the_next_tick()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);
        tile.TickCountdowns(Now.AddHours(25));
        Assert.True(tile.IsSilent);
        var raised = new List<string?>();
        tile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tile.TickCountdowns(Now.AddHours(25).AddSeconds(1));

        Assert.DoesNotContain(nameof(ProviderTileViewModel.IsSilent), raised);
        Assert.DoesNotContain(nameof(ProviderTileViewModel.SilentReasonText), raised);
    }

    [Fact]
    public void A_silent_state_that_changes_is_raised()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);
        var raised = new List<string?>();
        tile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tile.TickCountdowns(Now.AddHours(25));

        Assert.Contains(nameof(ProviderTileViewModel.IsSilent), raised);
        Assert.Contains(nameof(ProviderTileViewModel.SilentReasonText), raised);
    }

    [Fact]
    public void OkWebSessionSnapshotShowsTheSignOutButtonOnly()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };
        tile.Apply(Snapshot(ProviderStatus.Ok, sourceKind: SourceKind.WebSession), Now);

        Assert.False(tile.ShowSignIn);
        Assert.True(tile.ShowSignOut);
    }

    [Fact]
    public void AProviderWithoutInAppSignInSupportNeverShowsEitherButton()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = false };
        tile.Apply(Snapshot(ProviderStatus.NotSignedIn), Now);

        Assert.False(tile.ShowSignIn);
        Assert.False(tile.ShowSignOut);
    }

    [Fact]
    public void AProviderWithInAppSignInSupportShowsTheSignInButtonWhenSignedOut()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true };
        tile.Apply(Snapshot(ProviderStatus.NotSignedIn), Now);

        Assert.True(tile.ShowSignIn);
        Assert.False(tile.ShowSignOut);
    }

    [Fact]
    public void OkLocalFileSnapshotBeforeAnyWebSessionLeavesBothButtonsHidden()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, sourceKind: SourceKind.LocalFile), Now);

        Assert.Equal(SignInState.Unknown, tile.SignInState);
        Assert.False(tile.ShowSignIn);
        Assert.False(tile.ShowSignOut);
    }

    [Fact]
    public void RuntimeMissingExposesDownloadAction()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.RuntimeMissing), Now);

        Assert.True(tile.HasAction);
        Assert.Equal("Downloadseite öffnen", tile.ActionLabelText);
    }

    [Fact]
    public void OkStatusHasNoAction()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);

        Assert.False(tile.HasAction);
    }

    [Fact]
    public void StaleStatusKeepsItsBarsAndAddsAnAgeNotice()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Stale, percent: 40), Now);

        Assert.True(tile.IsStale);
        Assert.True(tile.ShowRows);
        Assert.True(tile.ShowDiagram);
        Assert.True(tile.ShowStaleNotice);
        Assert.False(tile.ShowPlaceholder);
        Assert.Single(tile.Rows);
    }

    [Fact]
    public void OkStatusShowsNoAgeNotice()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);

        Assert.False(tile.IsStale);
        Assert.False(tile.ShowStaleNotice);
    }

    [Fact]
    public void MiniStaleShowsTheBarsRatherThanTheHeadline()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { Density = TileDensity.Mini };
        tile.Apply(Snapshot(ProviderStatus.Stale, percent: 40), Now);

        Assert.True(tile.ShowMiniRows);
        Assert.False(tile.ShowMiniHeadline);
        Assert.False(tile.ShowRows);
        Assert.False(tile.ShowStaleNotice);
    }

    [Fact]
    public void MiniWithoutDataShowsTheHeadlineRatherThanBars()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { Density = TileDensity.Mini };
        tile.Apply(Snapshot(ProviderStatus.NoLocalData), Now);

        Assert.False(tile.ShowMiniRows);
        Assert.True(tile.ShowMiniHeadline);
    }

    [Fact]
    public void StaleReasonExplainsWhatStaleMeansNotTheAgeAgain()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var snapshot = Snapshot(ProviderStatus.Stale) with { DataTimestamp = Now - TimeSpan.FromHours(3) };
        tile.Apply(snapshot, Now);

        Assert.Equal("Die Zahlen stammen aus einem Fenster, das inzwischen abgelaufen ist.", tile.ReasonText);
        Assert.NotEqual(tile.LastUpdatedText, tile.ReasonText);
    }

    [Fact]
    public void StaleReasonTextDiffersFromLastUpdatedText()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var snapshot = Snapshot(ProviderStatus.Stale) with { DataTimestamp = Now - TimeSpan.FromHours(5) };
        tile.Apply(snapshot, Now);

        Assert.NotEqual(tile.ReasonText, tile.LastUpdatedText);
        Assert.NotEmpty(tile.ReasonText);
        Assert.NotEmpty(tile.LastUpdatedText);
    }

    // LastUpdatedText re-derives its age against the real wall clock (DateTimeOffset.Now), not the
    // snapshot's own "now" - the same reason TickCountdowns re-evaluates it every second rather than
    // only when a fetch lands (see its own doc comment) - so these two anchor DataTimestamp off the
    // real clock too, in minute-bucketed offsets: CountdownFormatter.FormatElapsed rounds down to a
    // whole minute below an hour, which absorbs the handful of milliseconds test execution itself
    // takes without the assertion ever landing on a bucket boundary.
    [Fact]
    public void LastUpdatedTextStaysOrdinaryAndTheToolTipNamesTheDataAgeOnceItIsOlderThanTheRefreshInterval()
    {
        var tile = new ProviderTileViewModel("codex", "Codex");
        var now = DateTimeOffset.Now;
        var snapshot = Snapshot(ProviderStatus.Ok) with { FetchedAt = now, DataTimestamp = now - TimeSpan.FromMinutes(5) };

        tile.Apply(snapshot, Now, refreshInterval: TimeSpan.FromMinutes(1));

        var ordinary = LocalizationService.Instance.Format("State.Stale.Reason", CountdownFormatter.FormatElapsed(TimeSpan.Zero));
        Assert.Equal(ordinary, tile.LastUpdatedText);
        var expected = LocalizationService.Instance.Format("Tile.SnapshotAge", CountdownFormatter.FormatElapsed(TimeSpan.FromMinutes(5)));
        Assert.Equal(expected, tile.LastUpdatedToolTip);
    }

    [Fact]
    public void LastUpdatedTextCountsFromTheFetchWhileTheToolTipCountsFromTheData()
    {
        var tile = new ProviderTileViewModel("codex", "Codex");
        var now = DateTimeOffset.Now;
        var snapshot = Snapshot(ProviderStatus.Ok) with { FetchedAt = now - TimeSpan.FromSeconds(1), DataTimestamp = now - TimeSpan.FromMinutes(5) };

        tile.Apply(snapshot, Now, refreshInterval: TimeSpan.FromMinutes(1));

        Assert.Equal(LocalizationService.Instance.Format("State.Stale.Reason", CountdownFormatter.FormatElapsed(TimeSpan.FromSeconds(1))), tile.LastUpdatedText);
        Assert.Equal(LocalizationService.Instance.Format("Tile.SnapshotAge", CountdownFormatter.FormatElapsed(TimeSpan.FromMinutes(5))), tile.LastUpdatedToolTip);
    }

    [Fact]
    public void LastUpdatedToolTipIsEmptyWithinTheRefreshInterval()
    {
        var tile = new ProviderTileViewModel("codex", "Codex");
        var snapshot = Snapshot(ProviderStatus.Ok) with { DataTimestamp = DateTimeOffset.Now - TimeSpan.FromMinutes(2) };

        tile.Apply(snapshot, Now, refreshInterval: TimeSpan.FromMinutes(5));

        Assert.Equal("", tile.LastUpdatedToolTip);
    }

    [Fact]
    public void LastUpdatedTextKeepsItsOrdinaryWordingWithinTheRefreshInterval()
    {
        var tile = new ProviderTileViewModel("codex", "Codex");
        var at = DateTimeOffset.Now - TimeSpan.FromMinutes(2);
        var snapshot = Snapshot(ProviderStatus.Ok) with { FetchedAt = at, DataTimestamp = at };

        tile.Apply(snapshot, Now, refreshInterval: TimeSpan.FromMinutes(5));

        var expected = LocalizationService.Instance.Format("State.Stale.Reason", CountdownFormatter.FormatElapsed(TimeSpan.FromMinutes(2)));
        Assert.Equal(expected, tile.LastUpdatedText);
    }

    [Theory]
    [InlineData("plus", "Plus")]
    [InlineData("MAX", "Max")]
    [InlineData("default_claude_max_20x", "Max 20x")]
    [InlineData("Google AI Pro", "Pro")]
    public void PlanTextShowsTheProductNameOfTheTier(string raw, string expected)
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok) with { PlanType = raw }, Now);

        Assert.Equal(expected, tile.PlanText);
    }

    [Fact]
    public void PlanTextIsEmptyForATierNobodyKnows()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok) with { PlanType = "mystery" }, Now);

        Assert.Null(tile.PlanText);
    }

    [Fact]
    public void PlanTextSurvivesATickThatDoesNotKnowTheTierButNotASignOut()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok) with { PlanType = "max" }, Now);

        tile.Apply(Snapshot(ProviderStatus.Ok) with { PlanType = null }, Now);
        Assert.Equal("Max", tile.PlanText);

        tile.Apply(Snapshot(ProviderStatus.NotSignedIn) with { PlanType = null }, Now);
        Assert.Null(tile.PlanText);
    }

    [Fact]
    public void ProviderSpecificErrorReasonOverridesTheDefault()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Failed, error: new ProviderError("Custom_Reason_Key")), Now);

        // Unknown key resolves to empty via StatusTextMap rather than throwing or showing a raw key.
        Assert.Equal("", tile.ReasonText);
    }

    [Theory]
    [InlineData(TileDensity.Full, true, true, true)]
    [InlineData(TileDensity.Mini, false, false, false)]
    public void DensityDrivesWhichSectionsShow(TileDensity density, bool showHeader, bool showRows, bool showDiagram)
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { Density = density };
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);

        Assert.Equal(showHeader, tile.ShowHeader);
        Assert.Equal(showRows, tile.ShowRows);
        Assert.Equal(showDiagram, tile.ShowDiagram);
        Assert.Equal(density == TileDensity.Mini, tile.IsMini);
    }

    [Fact]
    public void MiniPlaceholderStateHidesBothBarsAndFullPlaceholder()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { Density = TileDensity.Mini };
        tile.Apply(Snapshot(ProviderStatus.Failed), Now);

        Assert.True(tile.IsMini);
        Assert.False(tile.ShowRows);
        Assert.False(tile.ShowPlaceholder); // Mini folds the message into MiniRow instead
        Assert.True(tile.IsNotOk);
    }

    [Fact]
    public void TickCountdownsRefreshesEveryRowsCountdownText()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);
        var before = tile.Rows[0].CountdownText;

        tile.TickCountdowns(Now.AddHours(1));

        Assert.NotEqual(before, tile.Rows[0].CountdownText);
    }

    [Fact]
    public void ToggleVisibilityActionTextNamesTheProviderAndTheOppositeState()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        // The shared LocalizationService.Instance may be German or English depending on execution
        // order of other tests in this run (same convention as NotSignedInExposesSignInAction below) -
        // this test only cares that the two states differ and both name the provider.
        var beforeHide = tile.ToggleVisibilityActionText;
        Assert.StartsWith("Claude: ", beforeHide);

        tile.IsHidden = true;

        Assert.StartsWith("Claude: ", tile.ToggleVisibilityActionText);
        Assert.NotEqual(beforeHide, tile.ToggleVisibilityActionText);
    }

    [Fact]
    public void HideCommandRaisesHideRequested()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var raised = false;
        tile.HideRequested += (_, _) => raised = true;

        tile.HideCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void SignOutCommandRaisesSignOutRequestedExactlyOnce()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var raisedCount = 0;
        tile.SignOutRequested += (_, _) => raisedCount++;

        tile.SignOutCommand.Execute(null);

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void ToggleDetailsWorksEvenWhileTheProviderIsOk()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10) with { Diagnostics = ["still worth explaining"] }, Now);

        Assert.True(tile.HasDiagnostics);
        Assert.False(tile.DetailsExpanded);
        Assert.Equal(LocalizationService.Instance["Tile.Menu.Details"], tile.DetailsMenuHeader);

        tile.ToggleDetailsCommand.Execute(null);

        Assert.True(tile.DetailsExpanded);
        Assert.Equal(LocalizationService.Instance["Tile.Menu.HideDetails"], tile.DetailsMenuHeader);
    }

    /// <summary>The context menu's details entry used to do nothing for a healthy tile: it was always
    /// visible, but the panel it toggled only ever showed once <see cref="ProviderTileViewModel.HasDiagnostics"/>
    /// was true, so clicking it on a tile with no diagnostics had no visible effect at all. The
    /// <c>Visibility</c> binding in ProviderTile.xaml now keys off the same flag - this pins the
    /// underlying condition, since the XAML binding itself is not something a unit test can drive.</summary>
    [Fact]
    public void HasDiagnosticsIsFalseWithoutAnyDiagnosticLines()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10) with { Diagnostics = [] }, Now);

        Assert.False(tile.HasDiagnostics);
    }

    [Fact]
    public void OpenUsagePageIsOnlyEnabledForAProviderWithAKnownUsagePage()
    {
        var known = new ProviderTileViewModel("claude", "Claude");
        var unknown = new ProviderTileViewModel("not-a-real-provider", "Mystery");

        Assert.True(known.OpenUsagePageCommand.CanExecute(null));
        Assert.False(unknown.OpenUsagePageCommand.CanExecute(null));
    }

    [Fact]
    public void DetailsStayClosedUntilAskedForAndCopyHandsOverEveryLine()
    {
        var copied = "";
        var viewModel = new ProviderTileViewModel("claude", "Claude") { CopyToClipboard = text => copied = text };
        var snapshot = Snapshot(ProviderStatus.NoLocalData) with
        {
            Windows = [],
            Diagnostics = ["Gesucht in: <user>\\.claude", "Nichts gefunden."],
        };

        viewModel.Apply(snapshot, Now);

        Assert.True(viewModel.HasDiagnostics);
        Assert.False(viewModel.DetailsExpanded);

        viewModel.ToggleDetailsCommand.Execute(null);
        viewModel.CopyDetailsCommand.Execute(null);

        Assert.True(viewModel.DetailsExpanded);
        Assert.Equal("Gesucht in: <user>\\.claude" + Environment.NewLine + "Nichts gefunden.", copied);
    }

    [Fact]
    public void CopyDetailsRetriesOnceWhenTheClipboardIsMomentarilyLocked()
    {
        var attempts = 0;
        var copied = "";
        var viewModel = new ProviderTileViewModel("claude", "Claude")
        {
            CopyToClipboard = text =>
            {
                attempts++;
                if (attempts == 1)
                    throw new System.Runtime.InteropServices.COMException("clipboard busy");
                copied = text;
            },
        };
        viewModel.Apply(Snapshot(ProviderStatus.NoLocalData) with { Windows = [], Diagnostics = ["one line"] }, Now);

        viewModel.CopyDetailsCommand.Execute(null);

        Assert.Equal(2, attempts);
        Assert.Equal("one line", copied);
    }

    [Fact]
    public void CopyDetailsNeverThrowsWhenTheClipboardStaysLocked()
    {
        var viewModel = new ProviderTileViewModel("claude", "Claude")
        {
            CopyToClipboard = _ => throw new System.Runtime.InteropServices.COMException("clipboard busy"),
        };
        viewModel.Apply(Snapshot(ProviderStatus.NoLocalData) with { Windows = [], Diagnostics = ["one line"] }, Now);

        var exception = Record.Exception(() => viewModel.CopyDetailsCommand.Execute(null));

        Assert.Null(exception);
    }

    [Fact]
    public void A_failed_fetch_still_shows_the_age_of_the_last_good_number()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);
        var lastUpdatedAfterOk = tile.LastUpdatedText;

        var failed = Snapshot(ProviderStatus.Failed) with { DataTimestamp = null };
        tile.Apply(failed, Now.AddMinutes(5));

        Assert.True(tile.ShowLastUpdated);
        Assert.Equal(lastUpdatedAfterOk, tile.LastUpdatedText); // still the Ok snapshot's timestamp, not reset to "now" or cleared
    }

    [Fact]
    public void A_provider_that_answers_nothing_usable_for_24_hours_reports_it()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);
        tile.Apply(Snapshot(ProviderStatus.Failed), Now);

        Assert.False(tile.IsSilent); // not yet - the failure just started

        tile.TickCountdowns(Now.AddHours(25));

        Assert.True(tile.IsSilent);
        Assert.False(string.IsNullOrEmpty(tile.SilentReasonText));
    }

    [Fact]
    public void A_successful_snapshot_in_between_resets_the_silent_clock()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);
        tile.Apply(Snapshot(ProviderStatus.Failed), Now.AddHours(20));
        tile.Apply(Snapshot(ProviderStatus.Stale), Now.AddHours(23)); // still answering, just resets the clock

        tile.TickCountdowns(Now.AddHours(25)); // only 2h past the reset, well under 24h

        Assert.False(tile.IsSilent);
        Assert.Equal("", tile.SilentReasonText);
    }

    [Fact]
    public void RefreshLocalizedTextDoesNotAdvanceTheSilentClock()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);

        tile.RefreshLocalizedText(); // a language switch, re-applying the same snapshot, no fetch involved

        tile.TickCountdowns(Now.AddHours(25));

        Assert.True(tile.IsSilent); // the clock is still anchored at the original snapshot, not pushed to "now" by the re-apply
    }

    [Fact]
    public void ARealSnapshotStillAdvancesTheSilentClock()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok), Now);
        tile.Apply(Snapshot(ProviderStatus.Ok), Now.AddHours(20)); // a fresh snapshot arriving resets the clock

        tile.TickCountdowns(Now.AddHours(25)); // only 5h past the real reset

        Assert.False(tile.IsSilent);
    }

    private static ProviderSnapshot TwoWindowSnapshot(double fiveHourPercent, double weeklyPercent) =>
        Snapshot(ProviderStatus.Ok) with
        {
            Windows =
            [
                new UsageWindow("Window_FiveHour", WindowKind.FiveHour, fiveHourPercent, Now.AddHours(2), 300),
                new UsageWindow("Window_Weekly", WindowKind.Weekly, weeklyPercent, Now.AddDays(3), 300),
            ],
        };

    [Fact]
    public void SameWindowKindsUpdateTheExistingRowInstancesInPlace()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(TwoWindowSnapshot(10, 20), Now);
        var rowsBefore = tile.Rows.ToArray();

        tile.Apply(TwoWindowSnapshot(30, 40), Now);

        Assert.Same(rowsBefore[0], tile.Rows[0]);
        Assert.Same(rowsBefore[1], tile.Rows[1]);
        Assert.Equal(30, tile.Rows[0].UsedPercent);
        Assert.Equal(40, tile.Rows[1].UsedPercent);
    }

    [Fact]
    public void AWeeklyWindowDeliveredFirstStillEndsUpAfterTheFiveHourRow()
    {
        var tile = new ProviderTileViewModel("gemini", "Gemini");
        var snapshot = Snapshot(ProviderStatus.Ok) with
        {
            Windows =
            [
                new UsageWindow("Window_Weekly", WindowKind.Weekly, 20, Now.AddDays(3), 300),
                new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 10, Now.AddHours(2), 300),
            ],
        };

        tile.Apply(snapshot, Now);

        Assert.Equal(WindowKind.FiveHour, tile.Rows[0].Kind);
        Assert.Equal(WindowKind.Weekly, tile.Rows[1].Kind);
    }

    [Fact]
    public void HidingTheFiveHourWindowLeavesOnlyTheWeeklyRow()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { ShowFiveHour = false };

        tile.Apply(TwoWindowSnapshot(10, 20), Now);

        Assert.Single(tile.Rows);
        Assert.Equal(WindowKind.Weekly, tile.Rows[0].Kind);
    }

    [Fact]
    public void HidingBothWindowsLeavesTheTileWithNoRowsAtAll()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { ShowFiveHour = false, ShowWeekly = false };

        tile.Apply(TwoWindowSnapshot(10, 20), Now);

        Assert.Empty(tile.Rows);
    }

    [Fact]
    public void TogglingShowFiveHourReFiltersTheAlreadyDisplayedRowsImmediately()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(TwoWindowSnapshot(10, 20), Now);
        Assert.Equal(2, tile.Rows.Count);

        tile.ShowFiveHour = false;

        Assert.Single(tile.Rows);
        Assert.Equal(WindowKind.Weekly, tile.Rows[0].Kind);
    }

    private static ProviderSnapshot ThreeWindowSnapshot(double fiveHourPercent, double cursorModelsPercent, double otherModelsPercent) =>
        Snapshot(ProviderStatus.Ok) with
        {
            Windows =
            [
                new UsageWindow("Window_FiveHour", WindowKind.FiveHour, fiveHourPercent, Now.AddHours(2), 300),
                new UsageWindow("Window_CursorModels", WindowKind.Other, cursorModelsPercent, Now.AddDays(3), 300),
                new UsageWindow("Window_OtherModels", WindowKind.Other, otherModelsPercent, Now.AddDays(3), 300),
            ],
        };

    [Fact]
    public void EveryDistinctRowGetsItsOwnWindowToggleInTileOrder()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor");

        tile.Apply(ThreeWindowSnapshot(10, 20, 30), Now);

        Assert.Equal(3, tile.WindowToggles.Count);
        Assert.Equal(["Window_FiveHour", "Window_CursorModels", "Window_OtherModels"], tile.WindowToggles.Select(t => t.Label));
        Assert.All(tile.WindowToggles, t => Assert.True(t.IsVisible));
    }

    [Fact]
    public void HidingOneOtherRowByItsOwnToggleLeavesTheOtherOtherRowVisible()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor");
        tile.Apply(ThreeWindowSnapshot(10, 20, 30), Now);

        tile.WindowToggles.Single(t => t.Label == "Window_CursorModels").IsVisible = false;

        Assert.Equal(["Window_FiveHour", "Window_OtherModels"], tile.Rows.Select(r => r.Label));
        Assert.Contains("Window_CursorModels", tile.HiddenWindows);
    }

    [Fact]
    public void UntickingTheFiveHourToggleFlipsShowFiveHourAndReFilters()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor");
        tile.Apply(ThreeWindowSnapshot(10, 20, 30), Now);

        tile.WindowToggles.Single(t => t.Kind == WindowKind.FiveHour).IsVisible = false;

        Assert.False(tile.ShowFiveHour);
        Assert.DoesNotContain(tile.Rows, r => r.Kind == WindowKind.FiveHour);
    }

    [Fact]
    public void UntickingAnOtherRowRaisesOtherWindowVisibilityChanged()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor");
        tile.Apply(ThreeWindowSnapshot(10, 20, 30), Now);
        var raised = false;
        tile.OtherWindowVisibilityChanged += (_, _) => raised = true;

        tile.WindowToggles.Single(t => t.Label == "Window_OtherModels").IsVisible = false;

        Assert.True(raised);
    }

    [Fact]
    public void APreviouslyHiddenOtherRowStaysHiddenAcrossARestart()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor") { HiddenWindows = ["Window_OtherModels"] };

        tile.Apply(ThreeWindowSnapshot(10, 20, 30), Now);

        Assert.Equal(["Window_FiveHour", "Window_CursorModels"], tile.Rows.Select(r => r.Label));
        Assert.False(tile.WindowToggles.Single(t => t.Label == "Window_OtherModels").IsVisible);
    }

    [Fact]
    public void ADifferentWindowCountRebuildsTheRowCollection()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(TwoWindowSnapshot(10, 20), Now);
        var rowsBefore = tile.Rows.ToArray();

        tile.Apply(Snapshot(ProviderStatus.Ok, 50), Now); // single-window snapshot

        Assert.Single(tile.Rows);
        Assert.NotSame(rowsBefore[0], tile.Rows[0]);
    }

    [Fact]
    public void ASnapshotWithNothingToExplainClosesAndEmptiesTheDetails()
    {
        var viewModel = new ProviderTileViewModel("claude", "Claude");
        viewModel.Apply(Snapshot(ProviderStatus.NoLocalData) with { Windows = [], Diagnostics = ["one line"] }, Now);
        viewModel.ToggleDetailsCommand.Execute(null);

        viewModel.Apply(Snapshot(ProviderStatus.Ok, 10), Now);

        Assert.False(viewModel.HasDiagnostics);
        Assert.False(viewModel.DetailsExpanded);
        Assert.Equal("", viewModel.DiagnosticsText);
    }

    [Fact]
    public void SettingsRowTextJoinsAKnownAccountAndTheSourcePhrase()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10) with { AccountLabel = "octocat", SourceKind = SourceKind.LocalFile }, Now);

        Assert.Equal("octocat · " + LocalizationService.Instance["Settings.Via.ClaudeFiles"], tile.SettingsRowText);
    }

    [Fact]
    public void SettingsRowTextIsJustThePhraseWithoutAnAccount()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10) with { SourceKind = SourceKind.LocalFile }, Now);

        Assert.Equal(LocalizationService.Instance["Settings.Via.ClaudeFiles"], tile.SettingsRowText);
    }

    [Fact]
    public void SettingsRowTextSaysNotConnectedBeforeAnySourceDelivered()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        Assert.Equal(LocalizationService.Instance["Settings.Via.None"], tile.SettingsRowText);
    }

    [Theory]
    [InlineData("claude", SourceKind.WebSession, "Settings.Via.WebSession")]
    [InlineData("codex", SourceKind.WebSession, "Settings.Via.WebSession")]
    [InlineData("gemini", SourceKind.WebSession, "Settings.Via.WebSession")]
    [InlineData("claude", SourceKind.LocalLogin, "Settings.Via.ClaudeCode")]
    [InlineData("gemini", SourceKind.LocalLogin, "Settings.Via.Antigravity")]
    [InlineData("gemini", SourceKind.LocalFile, "Settings.Via.Antigravity")]
    [InlineData("gemini", SourceKind.LocalDatabase, "Settings.Via.Antigravity")]
    [InlineData("copilot", SourceKind.LocalLogin, "Settings.Via.GitHubCli")]
    [InlineData("codex", SourceKind.LocalFile, "Settings.Via.CodexFiles")]
    [InlineData("claude", SourceKind.LocalFile, "Settings.Via.ClaudeFiles")]
    [InlineData("claude#2", SourceKind.LocalFile, "Settings.Via.ClaudeFiles")]
    public void SettingsRowTextMapsEveryProviderAndSourcePair(string providerId, SourceKind source, string key)
    {
        var tile = new ProviderTileViewModel(providerId, providerId);
        tile.Apply(Snapshot(ProviderStatus.Ok, 10) with { SourceKind = source }, Now);

        Assert.Equal(LocalizationService.Instance[key], tile.SettingsRowText);
    }

    [Fact]
    public void SettingsRowTextFallsBackToTheSourceBadgeForAnyOtherPair()
    {
        var tile = new ProviderTileViewModel("codex", "Codex");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10) with { SourceKind = SourceKind.LocalLogin }, Now);

        Assert.Equal(StatusTextMap.SourceBadge(SourceKind.LocalLogin), tile.SettingsRowText);
    }

    [Fact]
    public void TheSourcePhraseSurvivesAFailedRead()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10) with { SourceKind = SourceKind.LocalFile }, Now);
        tile.Apply(Snapshot(ProviderStatus.Failed) with { Windows = [], SourceKind = SourceKind.None }, Now);

        Assert.Equal(LocalizationService.Instance["Settings.Via.ClaudeFiles"], tile.SettingsRowText);
    }

    [Fact]
    public void TheSourcePhraseIsForgottenOnSignOut()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok, 10) with { SourceKind = SourceKind.WebSession }, Now);
        tile.Apply(Snapshot(ProviderStatus.NotSignedIn) with { Windows = [], SourceKind = SourceKind.None }, Now);

        Assert.Equal(LocalizationService.Instance["Settings.Via.None"], tile.SettingsRowText);
    }

    [Fact]
    public void HeaderDisplayNameIsThePlainNameForAFirstAccount()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        Assert.Equal("Claude", tile.HeaderDisplayName);
    }

    [Fact]
    public void HeaderDisplayNameNamesTheSecondAccountByItsSuffix()
    {
        var tile = new ProviderTileViewModel("claude#2", "Claude", realProviderId: "claude");

        Assert.Equal("Claude (2)", tile.HeaderDisplayName);
    }

    [Fact]
    public void HeaderDisplayNameNamesTheThirdAccountByItsSuffix()
    {
        var tile = new ProviderTileViewModel("claude#3", "Claude", realProviderId: "claude");

        Assert.Equal("Claude (3)", tile.HeaderDisplayName);
    }

    [Fact]
    public void ApplyWithAWaitingSnapshotAndTheSettingOnMarksTheTile()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.Apply(Snapshot(ProviderStatus.Ok) with { IsWaitingForUser = true, WaitingSince = Now }, Now, showAttentionMark: true);

        Assert.True(tile.IsWaitingForUser);
    }

    [Fact]
    public void ApplyWithAWaitingSnapshotAndTheSettingOffNeverMarksTheTile()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.Apply(Snapshot(ProviderStatus.Ok) with { IsWaitingForUser = true, WaitingSince = Now }, Now, showAttentionMark: false);

        Assert.False(tile.IsWaitingForUser);
    }

    [Fact]
    public void ApplyShowAttentionMarkTogglesTheMarkerFromTheLastSnapshotImmediately()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok) with { IsWaitingForUser = true, WaitingSince = Now }, Now, showAttentionMark: true);
        Assert.True(tile.IsWaitingForUser);

        tile.ApplyShowAttentionMark(false);
        Assert.False(tile.IsWaitingForUser);

        tile.ApplyShowAttentionMark(true);
        Assert.True(tile.IsWaitingForUser);
    }

    [Fact]
    public void ATileWithItsOwnAttentionMarkSwitchedOffIsNeverMarkedWhileAnotherIs()
    {
        var waiting = Snapshot(ProviderStatus.Ok) with { IsWaitingForUser = true, WaitingSince = Now };
        var claude = new ProviderTileViewModel("claude", "Claude") { AttentionDisabled = true };
        var codex = new ProviderTileViewModel("codex", "Codex");

        claude.Apply(waiting, Now, showAttentionMark: true);
        codex.Apply(waiting, Now, showAttentionMark: true);

        Assert.False(claude.IsWaitingForUser);
        Assert.True(codex.IsWaitingForUser);
    }

    [Fact]
    public void SwitchingAnAttentionMarkOffClearsAnAlreadyShownMarkAndOnBringsItBack()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok) with { IsWaitingForUser = true, WaitingSince = Now }, Now, showAttentionMark: true);
        Assert.True(tile.IsWaitingForUser);

        tile.AttentionEnabled = false;
        Assert.False(tile.IsWaitingForUser);

        tile.AttentionEnabled = true;
        Assert.True(tile.IsWaitingForUser);
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("gemini", false)]
    [InlineData("copilot", false)]
    [InlineData("cursor", false)]
    public void OnlyProvidersThatReadASessionFileOfferTheAttentionSwitch(string providerId, bool supported)
    {
        Assert.Equal(supported, new ProviderTileViewModel(providerId, providerId).SupportsAttention);
        Assert.Equal(supported, new ProviderTileViewModel(providerId + "#2", providerId, providerId).SupportsAttention);
    }

    [Fact]
    public void GeminiAndCopilotNeverMarkTheTileEvenWithTheSettingOn()
    {
        // Neither provider ever sets IsWaitingForUser on its own snapshot - they have no session
        // file to read one from - so the tile stays unmarked whatever the setting says.
        var tile = new ProviderTileViewModel("gemini", "Gemini");

        tile.Apply(Snapshot(ProviderStatus.Ok), Now, showAttentionMark: true);

        Assert.False(tile.IsWaitingForUser);
    }

    [Fact]
    public void DismissWaitingClearsTheCurrentlyShownWait()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok) with { IsWaitingForUser = true, WaitingSince = Now }, Now, showAttentionMark: true);
        Assert.True(tile.IsWaitingForUser);

        tile.DismissWaitingCommand.Execute(null);

        Assert.False(tile.IsWaitingForUser);
    }

    [Fact]
    public void ASecondSnapshotWithTheSameWaitingSinceStaysDismissed()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok) with { IsWaitingForUser = true, WaitingSince = Now }, Now, showAttentionMark: true);
        tile.DismissWaitingCommand.Execute(null);
        Assert.False(tile.IsWaitingForUser);

        tile.Apply(Snapshot(ProviderStatus.Ok) with { IsWaitingForUser = true, WaitingSince = Now }, Now, showAttentionMark: true);

        Assert.False(tile.IsWaitingForUser);
    }

    [Fact]
    public void ASnapshotWithANewerWaitingSinceMarksTheTileAgain()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(ProviderStatus.Ok) with { IsWaitingForUser = true, WaitingSince = Now }, Now, showAttentionMark: true);
        tile.DismissWaitingCommand.Execute(null);
        Assert.False(tile.IsWaitingForUser);

        var later = Now.AddMinutes(5);
        tile.Apply(Snapshot(ProviderStatus.Ok) with { IsWaitingForUser = true, WaitingSince = later }, later, showAttentionMark: true);

        Assert.True(tile.IsWaitingForUser);
    }

    private static ProviderSnapshot WeeklySnapshot() =>
        Snapshot(ProviderStatus.Ok) with
        {
            Windows =
            [
                new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 10, Now.AddHours(2), 300),
                new UsageWindow("Window_Weekly", WindowKind.Weekly, 20, Now.AddDays(3), 10080),
            ],
        };

    [Fact]
    public void WeekTokensReachOnlyTheWeeklyRowAndStayEmptyWithoutAValue()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { Density = TileDensity.Full };
        tile.Apply(WeeklySnapshot(), Now);
        var weekly = tile.Rows.Single(r => r.Kind == WindowKind.Weekly);
        var fiveHour = tile.Rows.Single(r => r.Kind == WindowKind.FiveHour);

        Assert.Null(tile.WeekTokens);
        Assert.Equal("", weekly.InlineTokensText);

        tile.WeekTokens = 12345;

        Assert.NotEqual("", weekly.InlineTokensText);
        Assert.Equal("", fiveHour.InlineTokensText);
    }

    [Fact]
    public void WeekTokensSurviveRebuiltAndUpdatedRows()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { Density = TileDensity.Full };
        tile.WeekTokens = 12345;

        tile.Apply(WeeklySnapshot(), Now); // rows built after the value landed
        var weekly = tile.Rows.Single(r => r.Kind == WindowKind.Weekly);
        Assert.NotEqual("", weekly.InlineTokensText);

        tile.Apply(WeeklySnapshot(), Now.AddMinutes(1)); // rows updated in place
        Assert.Same(weekly, tile.Rows.Single(r => r.Kind == WindowKind.Weekly));
        Assert.NotEqual("", weekly.InlineTokensText);

        tile.Apply(Snapshot(ProviderStatus.Ok), Now.AddMinutes(2)); // weekly row gone, then back
        tile.Apply(WeeklySnapshot(), Now.AddMinutes(3));
        Assert.NotEqual("", tile.Rows.Single(r => r.Kind == WindowKind.Weekly).InlineTokensText);
    }

    [Fact]
    public void WeekTokensShortenLikeTheStatsFigures()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { Density = TileDensity.Full };
        var loc = LocalizationService.Instance;
        tile.Apply(WeeklySnapshot(), Now);

        tile.WeekTokens = 123_123_123_123;

        var weekly = tile.Rows.Single(r => r.Kind == WindowKind.Weekly);
        var expected = AiUsage.Stats.StatsAggregator.ShortenTokenCount(123_123_123_123, loc["Number.Thousand"], loc["Number.Million"], loc["Number.Billion"]);
        Assert.Equal(loc.Format("Window.TokensInline", expected), weekly.InlineTokensText);
        Assert.Contains(loc["Number.Billion"], weekly.InlineTokensText);
        Assert.DoesNotContain(123_123_123_123L.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), weekly.InlineTokensText);
    }

    // A list item whose template carries no name of its own falls back to the bound object's
    // ToString(). Without an override that is the type name, and a screen reader announced
    // "AiUsage.ViewModels.ProviderTileViewModel" for every tile, every threshold row and every
    // entry in the settings sidebar.
    [Fact]
    public void TheBoundRowsReadAsTheirLabelNotAsTheirTypeName()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var thresholdRow = new ThresholdRowViewModel("claude", "Claude", new ThresholdSettings(), () => { });
        var choice = new Choice<string>("Settings.Section.Display", "Display");

        foreach (var text in new[] { tile.ToString(), thresholdRow.ToString(), choice.ToString() })
        {
            Assert.DoesNotContain("AiUsage.ViewModels", text, StringComparison.Ordinal);
            Assert.NotEmpty(text);
        }

        Assert.Equal("Claude", tile.ToString());
        Assert.Equal("Claude", thresholdRow.ToString());
        Assert.Equal(choice.Label, choice.ToString());
    }
    // The placeholder's "refresh now" on an offline read asked for a sign-in window before, because
    // every action went through ActionRequested; a retry must only ask for the data again.
    [Theory]
    [InlineData(ProviderStatus.Failed, "Action_Retry", true)]
    [InlineData(ProviderStatus.NotSignedIn, null, false)]
    public void ThePlaceholderActionRefreshesOnARetryAndSignsInOtherwise(ProviderStatus status, string? actionKey, bool expectRefresh)
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var refreshed = 0;
        var actions = 0;
        tile.RefreshRequested += (_, _) => refreshed++;
        tile.ActionRequested += (_, _) => actions++;
        var error = actionKey is null ? null : new ProviderError("Status_Offline_Reason", actionKey);

        tile.Apply(Snapshot(status, error: error) with { Windows = [] }, Now);
        tile.RunPlaceholderActionCommand.Execute(null);

        Assert.Equal(expectRefresh ? 1 : 0, refreshed);
        Assert.Equal(expectRefresh ? 0 : 1, actions);
    }

    [Fact]
    public void ThePlaceholderButtonUsesTheRoutingCommand()
    {
        var text = File.ReadAllText(ProviderTileXamlPath());

        Assert.Contains("Content=\"{Binding ActionLabelText}\" Command=\"{Binding RunPlaceholderActionCommand}\"", text);
    }
    private static ProviderSnapshot TwoModelSnapshot(string firstLabel, double firstPercent, string secondLabel, double secondPercent) =>
        new("cursor",
            [
                new UsageWindow(firstLabel, WindowKind.Other, firstPercent, Now.AddDays(3), null),
                new UsageWindow(secondLabel, WindowKind.Other, secondPercent, Now.AddDays(3), null),
            ],
            null, SourceKind.WebSession, Now, Now, ProviderStatus.Ok, null);

    // The tray memo keyed only on the tooltip, which names the five-hour and weekly windows: a
    // change in a model row left the tray icon at its old number.
    [Fact]
    public void TheTrayIconRedrawsWhenOnlyAModelRowChanges()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor");
        var memo = new TrayTooltipMemo();
        tile.Apply(TwoModelSnapshot("Model A", 20, "Model B", 30), Now);
        Assert.True(AiUsage.Views.MainWindow.TryComputeTraySummary([tile], memo, out _, out _, out var before));

        tile.Apply(TwoModelSnapshot("Model A", 20, "Model B", 95), Now);
        var changed = AiUsage.Views.MainWindow.TryComputeTraySummary([tile], memo, out _, out _, out var after);

        Assert.Equal(30, before);
        Assert.True(changed);
        Assert.Equal(95, after);
    }

    [Fact]
    public void ARowKeptAcrossARefreshFollowsItsNewLabel()
    {
        var tile = new ProviderTileViewModel("cursor", "Cursor");
        tile.Apply(TwoModelSnapshot("Model A", 20, "Model B", 30), Now);

        tile.Apply(TwoModelSnapshot("Model B", 30, "Model A", 20), Now);

        Assert.Equal(["Model B", "Model A"], tile.Rows.Select(r => r.Label));
    }
}
