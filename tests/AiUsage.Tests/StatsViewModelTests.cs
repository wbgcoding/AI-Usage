using System.Globalization;
using System.Windows.Media;
using AiUsage.Services;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// <see cref="StatsViewModel"/>'s own logic, as opposed to <see cref="StatsWindowTests"/> (the
/// window's visual tree) or <see cref="StatsAggregatorTests"/> (the pure grouping math). First test
/// class for this view model - later items in Batch BC add to it rather than creating a second one.
/// </summary>
public class StatsViewModelTests
{
    // a provider without local token counts (Gemini/Antigravity, Copilot) is dropped entirely
    // from the provider row list instead of showing an explanatory "no local data" sentence.
    [Fact]
    public void A_provider_without_local_token_counts_creates_no_row()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-no-local-data");
        var store = new StatsStore(dataDir);
        store.AddDelta([
            new StatsRecord("claude", DateOnly.FromDateTime(DateTime.UtcNow), "modelA", "projA", 1000, 500, 200, 100),
        ]);

        var viewModel = StatsVm.Create(store);

        Assert.DoesNotContain(viewModel.ProviderRows, row => row.DisplayName.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(viewModel.ProviderRows, row => row.DisplayName.Contains("Copilot", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProviderCoverage.IndexedProviderIds.Count, viewModel.ProviderRows.Count);
    }

    // The "share per provider" donut names only providers this machine can count tokens for, an idle
    // one included; Gemini and Copilot keep no local token count and are not listed.
    [Fact]
    public void ProviderShareSlicesListOnlyCountableProvidersEvenAtZero()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-provider-share-all");
        var store = new StatsStore(dataDir);
        store.AddDelta([
            new StatsRecord("claude", DateOnly.FromDateTime(DateTime.UtcNow), "modelA", "projA", 1000, 500, 0, 0),
        ]);

        var viewModel = StatsVm.Create(store);

        Assert.Equal(["claude", "codex"], viewModel.ProviderShareSlices.Select(slice => slice.ProviderId).ToArray());
        Assert.All(viewModel.ProviderShareSlices, slice => Assert.Null(slice.LegendText));
    }

    /// <summary>"The whole history" has no lower bound, and the by-day chart fills every empty day
    /// in its range with a zero bar. Taken literally that is one bar per day since the year 1, about
    /// 740,000 of them - the window stopped answering while it built them and then gave up. The
    /// first day actually stored is the first one worth a bar.</summary>
    [Fact]
    public void TheWholeHistoryStartsAtTheEarliestStoredDayRatherThanAtTheStartOfTheCalendar()
    {
        var today = new DateOnly(2026, 9, 19);
        var earliest = new DateOnly(2026, 9, 1);
        var records = new List<StatsRecord>
        {
            new("claude", earliest, "model", "project", 1, 1, 0, 0),
            new("claude", today, "model", "project", 1, 1, 0, 0),
        };

        var start = StatsViewModel.GapFillStart(StatsViewModel.RangeStart("All", today), records, today);

        Assert.Equal(earliest, start);
    }

    [Fact]
    public void AnEmptyWholeHistoryFallsBackToTodayRatherThanToTheStartOfTheCalendar()
    {
        var today = new DateOnly(2026, 9, 19);

        var start = StatsViewModel.GapFillStart(StatsViewModel.RangeStart("All", today), [], today);

        Assert.Equal(today, start);
    }

    [Fact]
    public void ANamedPeriodKeepsItsOwnStartSoAnEmptyWeekStillDrawsSevenBars()
    {
        var today = new DateOnly(2026, 9, 19);
        var from = StatsViewModel.RangeStart("Week", today);

        var start = StatsViewModel.GapFillStart(from, [], today);

        Assert.Equal(from, start);
    }

    // The fifth figure card shows an em dash rather than a division by zero when the selected
    // period has no active day at all.
    [Fact]
    public void PerActiveDayFigureTextShowsAnEmDashWhenThePeriodHasNoActiveDay()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-no-active-day");
        var store = new StatsStore(dataDir);

        var viewModel = StatsVm.Create(store);

        Assert.Equal("–", viewModel.PerActiveDayFigureText);
    }

    [Fact]
    public async Task PerActiveDayCardHidesWhenEveryDayWasActive()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-all-days-active");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        store.AddDelta([
            new StatsRecord("claude", today.AddDays(-1), "modelA", "projA", 100, 50, 0, 0),
            new StatsRecord("claude", today, "modelA", "projA", 100, 50, 0, 0),
        ]);
        var viewModel = StatsVm.Create(store);

        await viewModel.RecomputeAsync();

        Assert.False(viewModel.ShowPerActiveDay);
        Assert.Equal(4, viewModel.FigureCardColumns);
    }

    [Fact]
    public async Task PerActiveDayCardShowsWithAnIdleDay()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-idle-day");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        store.AddDelta([
            new StatsRecord("claude", today.AddDays(-2), "modelA", "projA", 100, 50, 0, 0),
            new StatsRecord("claude", today, "modelA", "projA", 100, 50, 0, 0),
        ]);
        var viewModel = StatsVm.Create(store);

        await viewModel.RecomputeAsync();

        Assert.True(viewModel.ShowPerActiveDay);
        Assert.Equal(5, viewModel.FigureCardColumns);
    }

    // The loading hint hangs off IsLoading rather than a window-side call before/after
    // RecomputeAsync, and the bound collections it guards must each settle exactly once per run -
    // not flicker through an empty state on the way - or the chart and table would visibly jump
    // twice for one period change instead of updating once.
    [Fact]
    public async Task RecomputeAsyncSetsEachBoundCollectionExactlyOnceWithoutFlashingTheLoadingState()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-recompute-async");
        var store = new StatsStore(dataDir);
        store.AddDelta([
            new StatsRecord("claude", DateOnly.FromDateTime(DateTime.UtcNow), "modelA", "projA", 100, 50, 0, 0),
        ]);
        var viewModel = StatsVm.Create(store);

        var loadingStates = new List<bool>();
        var barsChanges = 0;
        var perDayBarsChanges = 0;
        var rowsChanges = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(StatsViewModel.IsLoading):
                    loadingStates.Add(viewModel.IsLoading);
                    break;
                case nameof(StatsViewModel.Bars):
                    barsChanges++;
                    break;
                case nameof(StatsViewModel.PerDayBars):
                    perDayBarsChanges++;
                    break;
                case nameof(StatsViewModel.Rows):
                    rowsChanges++;
                    break;
            }
        };

        await viewModel.RecomputeAsync();

        Assert.Empty(loadingStates); // a quick run never shows the loading state
        Assert.False(viewModel.IsLoading);
        Assert.Equal(1, barsChanges);
        Assert.Equal(1, perDayBarsChanges);
        Assert.Equal(1, rowsChanges);
    }

    [Fact]
    public async Task ASlowRecomputeShowsTheLoadingStateOnlyAfterTheDelayAndClearsItAtTheEnd()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-slow-run");
        var viewModel = StatsVm.Create(new StatsStore(dataDir), vm => vm.LoadingDelay = TimeSpan.FromMilliseconds(20));
        viewModel.LoadRecords = () =>
        {
            Thread.Sleep(400);
            return [];
        };
        var loadingStates = new List<bool>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StatsViewModel.IsLoading))
                lock (loadingStates)
                    loadingStates.Add(viewModel.IsLoading);
        };

        await viewModel.RecomputeAsync();

        Assert.Equal([true, false], loadingStates);
    }

    [Fact]
    public async Task OfTwoOverlappingRunsTheNewestOneKeepsItsResult()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-overlap");
        var viewModel = StatsVm.Create(new StatsStore(dataDir));
        using var releaseFirst = new ManualResetEventSlim();
        var calls = 0;
        viewModel.LoadRecords = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
                return [];
            }

            return [new StatsRecord("claude", DateOnly.FromDateTime(DateTime.UtcNow), "modelA", "projA", 100, 50, 0, 0)];
        };

        var first = viewModel.RecomputeAsync();
        while (Volatile.Read(ref calls) < 1)
            await Task.Delay(5);
        await viewModel.RecomputeAsync();
        var rowsAfterSecond = viewModel.Rows.Count;
        releaseFirst.Set();
        await first;

        Assert.True(rowsAfterSecond > 0);
        Assert.Equal(rowsAfterSecond, viewModel.Rows.Count); // the older, empty result did not overwrite it
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task ProjectGroupingBundlesAllButTheTenLargestProjectsIntoOneBarButKeepsEveryTableRow()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-project-bundle");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta(Enumerable.Range(1, 25)
            .Select(i => new StatsRecord("claude", today, "modelA", $"proj{i:00}", i * 100, 0, 0, 0))
            .ToList());
        var viewModel = StatsVm.Create(store);

        await viewModel.SetGroupingCommand.ExecuteAsync(StatsGrouping.Project);

        Assert.Equal(11, viewModel.Bars.Count);
        var other = viewModel.Bars[^1];
        Assert.Equal(StatsViewModel.OtherProjectsColorKey, other.ColorProviderId);
        Assert.Equal(Enumerable.Range(1, 15).Sum(i => i * 100L), other.StackedValues.Sum());
        Assert.Contains("15", other.ExtraTooltipLine);
        Assert.All(viewModel.Bars.Take(10), bar => Assert.Null(bar.ExtraTooltipLine));
        Assert.Equal(25, viewModel.Rows.Count);
    }

    [Fact]
    public async Task ProjectGroupingWithElevenProjectsKeepsEveryBarOnItsOwn()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-project-eleven");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta(Enumerable.Range(1, 11)
            .Select(i => new StatsRecord("claude", today, "modelA", $"proj{i:00}", i * 100, 0, 0, 0))
            .ToList());
        var viewModel = StatsVm.Create(store);

        await viewModel.SetGroupingCommand.ExecuteAsync(StatsGrouping.Project);

        Assert.Equal(11, viewModel.Bars.Count);
        Assert.DoesNotContain(viewModel.Bars, bar => bar.ColorProviderId == StatsViewModel.OtherProjectsColorKey);
    }

    /// <summary>The month grid's own day list stacks every provider that wrote a record for a given
    /// day, not just whichever one happens to lead it - a day Codex actually out-used Claude on
    /// names Codex as the leader, and both providers' own totals still show up in
    /// <see cref="Views.Controls.StatsMonthGrid.DayValue.ByProvider"/> for the tooltip.</summary>
    [Fact]
    public void MonthGridDaysCarryEveryProviderThatWroteARecordThatDayNotJustTheLeader()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-monthgrid-multi-provider");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var yesterday = today.AddDays(-1);
        store.AddDelta([
            new StatsRecord("claude", yesterday, "modelA", "projA", 100, 0, 0, 0),
            new StatsRecord("codex", yesterday, "modelB", "projB", 900, 0, 0, 0),
            new StatsRecord("claude", today, "modelA", "projA", 50, 0, 0, 0),
        ]);

        var viewModel = StatsVm.Create(store);

        var yesterdayValue = Assert.Single(viewModel.MonthGridDays, day => day.Day == yesterday);
        Assert.Equal("codex", yesterdayValue.ByProvider![0].ProviderId); // Codex out-used Claude that day, so it sorts first
        Assert.Equal("codex", yesterdayValue.LeaderProviderId);
        Assert.Equal(1000, yesterdayValue.Total);
        Assert.NotNull(yesterdayValue.ByProvider);
        Assert.Equal(2, yesterdayValue.ByProvider!.Count);
        Assert.Contains(yesterdayValue.ByProvider, entry => entry.ProviderId == "claude" && entry.Total == 100);
        Assert.Contains(yesterdayValue.ByProvider, entry => entry.ProviderId == "codex" && entry.Total == 900);

        var todayValue = Assert.Single(viewModel.MonthGridDays, day => day.Day == today);
        Assert.Equal("claude", todayValue.ByProvider![0].ProviderId);
        Assert.Equal("claude", todayValue.LeaderProviderId);
        Assert.Single(todayValue.ByProvider!);
    }

    // The grid always covers the last twelve months up to today, regardless of which range the
    // header selector is set to - only the range-filtered charts and table follow that selector.
    [Fact]
    public void MonthGridSpansTheLastTwelveMonthsUpToToday()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-monthgrid-year-span");
        var store = new StatsStore(dataDir);
        var viewModel = StatsVm.Create(store, vm => vm.SelectedRange = "Month");
        viewModel.Recompute();

        var today = DateOnly.FromDateTime(DateTime.Now);
        var expectedStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        Assert.Equal(expectedStart, viewModel.MonthGridRangeStart);
        Assert.Equal(today, viewModel.MonthGridRangeEnd);
        Assert.Equal(today.DayNumber - expectedStart.DayNumber + 1, viewModel.MonthGridDays.Count);
    }

    private static StatsViewModel MonthGridVm(string name, out DateOnly oldRecordDay)
    {
        var dataDir = TestPaths.CreateDisposableDirectory(name);
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        oldRecordDay = today.AddDays(-900);
        var records = new List<StatsRecord> { new("claude", oldRecordDay, "m", "p", 5_000, 0, 0, 0) };
        for (var i = 0; i < 40; i++)
            records.Add(new StatsRecord("claude", today.AddDays(-i), "m", "p", 100 + i * 10, 0, 0, 0));
        store.AddDelta(records);
        return StatsVm.Create(store);
    }

    [Fact]
    public void MonthGridReachesBeforeTheTwelveMonthsOnAWideWindowAndNotOnANarrowOne()
    {
        var viewModel = MonthGridVm("stats-viewmodel-monthgrid-wide", out var old);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var twelveMonths = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);

        viewModel.MonthGridAvailableWidth = 400;
        Assert.Equal(twelveMonths, viewModel.MonthGridRangeStart);

        viewModel.MonthGridAvailableWidth = 22 + 150 * 14 - 3;
        Assert.True(viewModel.MonthGridRangeStart < twelveMonths);
        Assert.Equal(StatsAggregator.WeekStart(today).AddDays(-149 * 7), viewModel.MonthGridRangeStart);
        Assert.Equal(today, viewModel.MonthGridRangeEnd);
        Assert.Equal(viewModel.MonthGridRangeEnd.DayNumber - viewModel.MonthGridRangeStart.DayNumber + 1, viewModel.MonthGridDays.Count);
        Assert.Contains(viewModel.MonthGridDays, day => day.Day == old && day.Total == 5_000);

        viewModel.MonthGridAvailableWidth = 400;
        Assert.Equal(twelveMonths, viewModel.MonthGridRangeStart);
    }

    [Fact]
    public void MonthGridColorScaleDoesNotShiftWhenTheWindowIsResized()
    {
        var viewModel = MonthGridVm("stats-viewmodel-monthgrid-scale", out _);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fixedDayTotal = 100 + 5 * 10;

        viewModel.MonthGridAvailableWidth = 400;
        var narrowScale = viewModel.MonthGridColorScale;
        var narrowStep = AiUsage.Views.Controls.StatsMonthGrid.QuantileStep(fixedDayTotal, narrowScale);
        viewModel.MonthGridAvailableWidth = 22 + 150 * 14 - 3;
        var wideScale = viewModel.MonthGridColorScale;

        Assert.Equal(41, wideScale.Count); // 40 recent days plus the old one, whole history
        Assert.Equal(narrowScale, wideScale);
        Assert.Equal(narrowStep, AiUsage.Views.Controls.StatsMonthGrid.QuantileStep(fixedDayTotal, wideScale));
        Assert.Equal(today, viewModel.MonthGridToday);
    }

    // A record from outside the selected range (here: the first day of the twelve-month span,
    // months before the selected 30-day "Month" range starts) is still part of the grid, since the
    // grid ignores the range selector entirely.
    [Fact]
    public void MonthGridIncludesARecordFromOutsideTheSelectedRangeAsLongAsItIsWithinTheTwelveMonths()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-monthgrid-outside-range");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var spanStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        store.AddDelta([new StatsRecord("claude", spanStart, "modelA", "projA", 500, 0, 0, 0)]);

        var viewModel = StatsVm.Create(store, vm => vm.SelectedRange = "Month");
        viewModel.Recompute();

        var firstDay = Assert.Single(viewModel.MonthGridDays, day => day.Day == spanStart);
        Assert.Equal(500, firstDay.Total);
        Assert.Equal("claude", firstDay.LeaderProviderId);
    }

    [Theory]
    [InlineData("Week", -6)]
    [InlineData("Month", -29)]
    public void MonthGridHighlightFollowsTheSelectedPeriod(string range, int startOffsetDays)
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-monthgrid-highlight-" + range);
        var viewModel = StatsVm.Create(new StatsStore(dataDir), vm => vm.SelectedRange = range);
        viewModel.Recompute();

        var today = DateOnly.FromDateTime(DateTime.Now);
        Assert.Equal(today.AddDays(startOffsetDays), viewModel.MonthGridHighlightStart);
        Assert.Equal(today, viewModel.MonthGridHighlightEnd);
    }

    [Fact]
    public void MonthGridHighlightIsNoneForTheWholeHistory()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-monthgrid-highlight-all");
        var viewModel = StatsVm.Create(new StatsStore(dataDir), vm => vm.SelectedRange = "All");
        viewModel.Recompute();

        Assert.Null(viewModel.MonthGridHighlightStart);
        Assert.Null(viewModel.MonthGridHighlightEnd);
    }

    [Fact]
    public void MonthGridHighlightIsNoneWhenThePeriodCoversTheWholeGrid()
    {
        var gridStart = new DateOnly(2026, 2, 1);
        var today = new DateOnly(2027, 1, 3);

        Assert.Equal((null, null), StatsViewModel.MonthGridHighlight(gridStart, today, gridStart, today));
        Assert.Equal((null, null), StatsViewModel.MonthGridHighlight(gridStart.AddDays(-5), today, gridStart, today));
        Assert.Equal((gridStart.AddDays(5), today), StatsViewModel.MonthGridHighlight(gridStart.AddDays(5), today, gridStart, today));
    }

    [Fact]
    public void ClosingTheDayDetailClearsTheSelection()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-close-day-detail");
        var viewModel = StatsVm.Create(new StatsStore(dataDir));
        viewModel.Recompute();
        viewModel.ToggleSelectedDay(DateOnly.FromDateTime(DateTime.Now));
        Assert.True(viewModel.HasSelectedDay);
        Assert.Contains(AiUsage.Services.LocalizationService.Instance["Stats.DayDetail.Title"].Split("{0}")[0], viewModel.SelectedDayHeadText);

        viewModel.CloseDayDetailCommand.Execute(null);

        Assert.Null(viewModel.SelectedDay);
        Assert.False(viewModel.HasSelectedDay);
        Assert.Equal("", viewModel.SelectedDayHeadText);
    }

    private static StatsViewModel ViewModelWithRecordFrom(string dataDirName, int daysAgo, string range)
    {
        var dataDir = TestPaths.CreateDisposableDirectory(dataDirName);
        var store = new StatsStore(dataDir);
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-daysAgo);
        store.AddDelta([new StatsRecord("claude", day, "modelA", "projA", 100, 0, 0, 0)]);
        var viewModel = StatsVm.Create(store, vm => vm.SelectedRange = range);
        viewModel.Recompute();
        return viewModel;
    }

    [Fact]
    public void YearRangeAveragesOverTheDaysSinceTheFirstRecordedDay()
    {
        var viewModel = ViewModelWithRecordFrom("stats-vm-year-daycount", 94, "Year");

        Assert.Equal(95, viewModel.PeriodDayCountRaw);
        Assert.Equal("1", viewModel.PerDayFigureText);
    }

    [Fact]
    public void WeekAndMonthRangesKeepTheirFixedLengthWhenDataIsOlderThanThePeriod()
    {
        Assert.Equal(7, ViewModelWithRecordFrom("stats-vm-week-daycount", 94, "Week").PeriodDayCountRaw);
        Assert.Equal(30, ViewModelWithRecordFrom("stats-vm-month-daycount", 94, "Month").PeriodDayCountRaw);
    }

    [Fact]
    public void ARangeLongerThanTheRecordedHistoryIsShortenedToIt()
    {
        Assert.Equal(4, ViewModelWithRecordFrom("stats-vm-week-short", 3, "Week").PeriodDayCountRaw);
    }

    [Fact]
    public async Task WeekGroupingKeepsEmptyWeeksBetweenUsedOnes()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-vm-week-grouping-gaps");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        store.AddDelta([
            new StatsRecord("claude", today.AddDays(-28), "modelA", "projA", 100, 0, 0, 0),
            new StatsRecord("claude", today, "modelA", "projA", 100, 0, 0, 0),
        ]);
        var viewModel = StatsVm.Create(store, vm => vm.SelectedRange = "Month");

        await viewModel.SetGroupingCommand.ExecuteAsync(StatsGrouping.Week);

        Assert.InRange(viewModel.Bars.Count, 5, 6);
        Assert.Contains(viewModel.Bars, bar => bar.StackedValues.Sum() == 0);
        Assert.Equal(200, viewModel.Bars.Sum(bar => bar.StackedValues.Sum()));
    }

    [Fact]
    public void YearRangeDrawsOneDailyBarPerDayOfTheWholeYear()
    {
        var viewModel = ViewModelWithRecordFrom("stats-vm-year-daily-bars", 94, "Year");

        Assert.False(viewModel.IsWeeklyPerDay);
        Assert.Equal(YearDayCount(), viewModel.PerDayBars.Count);
        Assert.DoesNotContain("-W", viewModel.PerDayBars[0].Label);
        Assert.Equal(100, viewModel.PerDayBars.Sum(bar => bar.StackedValues.Sum()));
        Assert.Contains(viewModel.PerDayBars, bar => bar.StackedValues.Sum() == 0);
    }

    [Fact]
    public void ChangingTheRangeRefreshesThePerDayPanelChart()
    {
        var viewModel = ViewModelWithRecordFrom("stats-vm-perday-refresh", 94, "Week");
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.SelectedRange = "Year";
        viewModel.Recompute();

        Assert.Contains(nameof(StatsViewModel.PerDayViewBars), raised);
        Assert.Same(viewModel.PerDayBars, viewModel.PerDayViewBars);
        Assert.Equal(YearDayCount(), viewModel.PerDayViewBars.Count);
    }

    [Fact]
    public void YearRangeKeepsOneBarAndOneTableRowPerDay()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-vm-year-table-days");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        store.AddDelta([new StatsRecord("claude", today.AddDays(-200), "modelA", "projA", 100, 0, 0, 0)]);
        var viewModel = StatsVm.Create(store, vm => vm.SelectedRange = "Year");
        viewModel.Recompute();

        Assert.Equal(YearDayCount(), viewModel.Bars.Count);
        Assert.Equal(YearDayCount(), viewModel.Rows.Count);
    }

    [Fact]
    public void PerDayChoiceReadsPerWeekOnlyWhileTheChartBundlesByWeek()
    {
        var loc = AiUsage.Services.LocalizationService.Instance;
        var viewModel = ViewModelWithRecordFrom("stats-vm-choice-label", 400, "All");
        Assert.Equal(loc["Stats.Chart.PerWeek"], viewModel.PerDayViewChoices[0].Label);
        Assert.Equal(loc["Tip.Stats.Chart.PerWeek"], viewModel.PerDayViewTooltip);

        viewModel.SelectedRange = "Week";
        viewModel.Recompute();
        Assert.False(viewModel.IsWeeklyPerDay);
        Assert.Equal(loc["Stats.Chart.PerDay"], viewModel.PerDayViewChoices[0].Label);
        Assert.Equal(loc["Tip.Stats.Chart.PerDay"], viewModel.PerDayViewTooltip);
        Assert.True(viewModel.PerDayViewChoices[0].IsSelected);
    }

    [Fact]
    public void AllRangeSwitchesToWeeklyBarsOnlyBeyondAYear()
    {
        Assert.False(ViewModelWithRecordFrom("stats-vm-all-short", 10, "All").IsWeeklyPerDay);
        Assert.False(ViewModelWithRecordFrom("stats-vm-all-365", 364, "All").IsWeeklyPerDay);
        Assert.False(ViewModelWithRecordFrom("stats-vm-all-366", 365, "All").IsWeeklyPerDay);
        Assert.True(ViewModelWithRecordFrom("stats-vm-all-long", 400, "All").IsWeeklyPerDay);
    }

    [Fact]
    public void AllRangeDailySeriesCoversTheWholePeriodSum()
    {
        var viewModel = ViewModelWithRecordFrom("stats-vm-series-all", 9, "All");

        Assert.Equal(10, viewModel.DailyTotalsSeries.Count);
        Assert.Equal(viewModel.PeriodTotalRaw, viewModel.DailyTotalsSeries.Sum());
        Assert.Equal(100, viewModel.DailyTotalsSeries.Sum());
    }

    [Fact]
    public void YearRangeDailySeriesCoversTheWholePeriodSum()
    {
        var viewModel = ViewModelWithRecordFrom("stats-vm-series-year", 94, "Year");

        Assert.Equal(95, viewModel.DailyTotalsSeries.Count);
        Assert.Equal(viewModel.PeriodTotalRaw, viewModel.DailyTotalsSeries.Sum());
        Assert.Equal(100, viewModel.DailyTotalsSeries.Sum());
    }

    [Fact]
    public void MiniChartBundlesALongSeriesIntoDailyAveragesThatFitTheWidth()
    {
        var values = Enumerable.Repeat(4L, 365).ToList();

        var bundled = AiUsage.Views.Controls.StatsFigureMiniChart.BundleDailyAverages(values, 150);

        Assert.InRange(bundled.Count, 1, 75);
        Assert.All(bundled, point => Assert.Equal(4.0, point, 6));
    }

    [Fact]
    public void MiniChartBundleAveragesInsteadOfSummingAndKeepsAShortSeriesAsIs()
    {
        var values = new long[] { 2, 4, 6, 8 };

        Assert.Equal(new[] { 2.0, 4.0, 6.0, 8.0 }, AiUsage.Views.Controls.StatsFigureMiniChart.BundleDailyAverages(values, 100));
        Assert.Equal(new[] { 3.0, 7.0 }, AiUsage.Views.Controls.StatsFigureMiniChart.BundleDailyAverages(values, 4));
    }

    [Fact]
    public void MiniChartAccentsTheBundleHoldingTheBusiestDayNotTheHighestAverage()
    {
        // 4 days per point at width 4 (two points): the spike day 0 sits in a bundle averaging 28,
        // the second bundle averages 40 - yet the busiest day is in the first.
        var values = new long[] { 100, 4, 4, 4, 40, 40, 40, 40 };

        Assert.Equal(0, AiUsage.Views.Controls.StatsFigureMiniChart.BusiestPointIndex(values, 4));
        Assert.Equal(new[] { 28.0, 40.0 }, AiUsage.Views.Controls.StatsFigureMiniChart.BundleDailyAverages(values, 4));
    }

    // MonthGridToday names the grid's own future-day cutoff - every day after it draws faded and
    // ignores clicks, per StatsMonthGrid.BuildColumns.
    [Fact]
    public void MonthGridTodayIsTodaysOwnLocalDate()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-monthgrid-today");
        var viewModel = StatsVm.Create(new StatsStore(dataDir));

        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), viewModel.MonthGridToday);
    }

    // The day-detail panel feeds four charts instead of four plain lists - the provider
    // breakdown a stacked share bar (raw ids + values, largest first, brushes resolved by the code
    // behind), model/project a "top projects"-style horizontal bar chart, and the hour breakdown a
    // real 24-column bar chart in chronological order (not sorted by size, unlike the other three).
    [Fact]
    public void ToggleSelectedDay_builds_the_provider_bar_largest_first()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-daydetail-provider");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta([
            new StatsRecord("claude", today, "modelA", "projA", 100, 0, 0, 0),
            new StatsRecord("codex", today, "modelB", "projB", 900, 0, 0, 0),
        ]);
        var viewModel = StatsVm.Create(store);

        viewModel.ToggleSelectedDay(today);

        Assert.Equal(["codex", "claude"], viewModel.SelectedDayDetail.ProviderIds);
        Assert.Equal([900L, 100L], viewModel.SelectedDayDetail.ProviderValues);
    }

    [Fact]
    public void A_day_picked_from_the_widget_survives_the_rebuild_that_follows()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-requested-day");
        var store = new StatsStore(dataDir);
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-3);
        store.AddDelta([new StatsRecord("claude", day, "modelA", "projA", 500, 0, 0, 0)]);
        var viewModel = StatsVm.Create(store);

        viewModel.SelectDay(day);
        viewModel.Recompute();

        Assert.Equal(day, viewModel.SelectedDay);
        Assert.Equal([500L], viewModel.SelectedDayDetail.ProviderValues);

        viewModel.ToggleSelectedDay(day);
        viewModel.Recompute();

        Assert.Null(viewModel.SelectedDay);
    }

    [Fact]
    public void A_clicked_day_survives_an_index_refresh_and_shows_the_new_records()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-refresh-keeps-day");
        var store = new StatsStore(dataDir);
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-2);
        store.AddDelta([new StatsRecord("claude", day, "modelA", "projA", 500, 0, 0, 0)]);
        var viewModel = StatsVm.Create(store);
        viewModel.ToggleSelectedDay(day);

        store.AddDelta([new StatsRecord("codex", day, "modelB", "projB", 250, 0, 0, 0)]);
        viewModel.Recompute();

        Assert.Equal(day, viewModel.SelectedDay);
        Assert.Equal(750L, viewModel.SelectedDayDetail.ProviderValues.Sum());
    }

    [Fact]
    public async Task Changing_the_period_clears_the_selected_day()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-range-clears-day");
        var store = new StatsStore(dataDir);
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-2);
        store.AddDelta([new StatsRecord("claude", day, "modelA", "projA", 500, 0, 0, 0)]);
        var viewModel = StatsVm.Create(store);
        viewModel.ToggleSelectedDay(day);

        await viewModel.SetRangeCommand.ExecuteAsync("Year");

        Assert.Null(viewModel.SelectedDay);
    }

    [Fact]
    public void ToggleSelectedDay_finds_a_day_outside_the_selected_period()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-daydetail-outside");
        var store = new StatsStore(dataDir);
        var longAgo = DateOnly.FromDateTime(DateTime.Now).AddDays(-60);
        store.AddDelta([new StatsRecord("claude", longAgo, "modelA", "projA", 500, 0, 0, 0)]);
        var viewModel = StatsVm.Create(store);

        viewModel.ToggleSelectedDay(longAgo);

        Assert.Equal([500L], viewModel.SelectedDayDetail.ProviderValues);
        Assert.True(viewModel.SelectedDayHasUsage);
        Assert.False(viewModel.SelectedDayIsEmpty);
    }

    [Fact]
    public void ToggleSelectedDay_on_a_day_without_usage_shows_the_empty_line_instead_of_charts()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-daydetail-empty");
        var viewModel = StatsVm.Create(new StatsStore(dataDir));

        viewModel.ToggleSelectedDay(DateOnly.FromDateTime(DateTime.Now));

        Assert.False(viewModel.SelectedDayHasUsage);
        Assert.True(viewModel.SelectedDayIsEmpty);
    }

    [Fact]
    public void ToggleSelectedDay_builds_the_hour_chart_in_chronological_order_with_empty_hours_zero()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-daydetail-hour");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta([
            new StatsRecord("claude", today, "modelA", "projA", 100, 0, 0, 0) { Hour = 23 },
            new StatsRecord("claude", today, "modelA", "projA", 50, 0, 0, 0) { Hour = 5 },
        ]);
        var viewModel = StatsVm.Create(store);

        viewModel.ToggleSelectedDay(today);

        var hourBars = viewModel.SelectedDayDetail.HourBars;
        Assert.Equal(24, hourBars.Count);
        Assert.Equal(0L, hourBars[0].StackedValues.Sum()); // hour 0 saw nothing
        Assert.Equal(50L, hourBars[5].StackedValues.Sum());
        Assert.Equal(100L, hourBars[23].StackedValues.Sum());
    }

    [Fact]
    public void DayDetailBarsCarryTheirOwnColours()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-daydetail-colours");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta([
            new StatsRecord("claude", today, "modelA", "projA", 300, 0, 0, 0),
            new StatsRecord("claude", today, "modelB", "projB", 200, 0, 0, 0),
            new StatsRecord("codex", today, "modelC", "projB", 100, 0, 0, 0),
        ]);
        var viewModel = StatsVm.Create(store);

        viewModel.ToggleSelectedDay(today);

        // Models: the model ring's colour, lighter by rank within the provider.
        var models = viewModel.SelectedDayDetail.ModelRows;
        Assert.Equal(3, models.Count);
        Assert.Equal(ChartPalette.ForModel("claude", 0), models[0].Color);
        Assert.Equal(ChartPalette.ForModel("claude", 1), models[1].Color);
        Assert.Equal(ChartPalette.ForModel("codex", 0), models[2].Color);
        Assert.NotEqual(models[0].Color, models[1].Color);

        // Projects: a colour each, none left on the default grey.
        var projects = viewModel.SelectedDayDetail.ProjectRows;
        Assert.Equal(2, projects.Count);
        Assert.All(projects, row => Assert.NotEqual(default, row.Color));
    }

    [Fact]
    public void DayDetailNamesTheDominantProviderOfEachModel()
    {
        var day = new DateOnly(2026, 9, 3);
        var detail = StatsAggregator.DayDetail([
            new StatsRecord("claude", day, "modelA", "projA", 300, 0, 0, 0),
            new StatsRecord("codex", day, "modelA", "projA", 100, 0, 0, 0),
            new StatsRecord("codex", day, "modelC", "projA", 50, 0, 0, 0),
        ], day);

        Assert.Equal(["claude", "codex"], detail.ByModel.Select(slice => slice.ProviderId));
    }

    [Fact]
    public void PlainRowBrushUsesTheRowColourAndOtherwiseTheFallback()
    {
        var fallback = Brushes.Gray;
        var plain = new StatsProjectRow("a", "a", 1, 100, new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 3));
        var coloured = plain with { Color = Color.FromRgb(10, 20, 30) };

        Assert.Same(fallback, Views.Controls.StatsHorizontalBarChart.PlainRowBrush(plain, fallback));
        Assert.Equal(Color.FromRgb(10, 20, 30), ((SolidColorBrush)Views.Controls.StatsHorizontalBarChart.PlainRowBrush(coloured, fallback)).Color);
    }

    [Fact]
    public void ToggleSelectedDay_off_again_clears_every_day_detail_collection()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-daydetail-clear");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta([new StatsRecord("claude", today, "modelA", "projA", 100, 0, 0, 0)]);
        var viewModel = StatsVm.Create(store);

        viewModel.ToggleSelectedDay(today);
        viewModel.ToggleSelectedDay(today); // same day again: clears the selection

        Assert.Null(viewModel.SelectedDay);
        Assert.Empty(viewModel.SelectedDayDetail.ProviderIds);
        Assert.Empty(viewModel.SelectedDayDetail.ModelRows);
        Assert.Empty(viewModel.SelectedDayDetail.ProjectRows);
        Assert.Empty(viewModel.SelectedDayDetail.HourBars);
    }

    // The breakdown chart used to draw every model-grouped bar in the same flat theme accent,
    // naming no model in particular - each bar now carries the same provider/rank identity <see
    // cref="ChartPalette.ForModel"/> already colors that model's ring slice through, so the two never
    // disagree on what color a given model is.
    [Fact]
    public async Task ModelGroupedBarColorMatchesTheModelRingSliceColor()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-model-bar-color");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta([
            new StatsRecord("claude", today, "modelA", "projA", 1000, 0, 0, 0),
            new StatsRecord("claude", today, "modelB", "projA", 400, 0, 0, 0),
            new StatsRecord("codex", today, "modelC", "projB", 300, 0, 0, 0),
        ]);
        var viewModel = StatsVm.Create(store);

        await viewModel.SetGroupingCommand.ExecuteAsync(StatsGrouping.Model);

        // The same rank-by-provider counting the ring's own brush refresh uses, over the ring's
        // slices rather than the bars - the two are meant to land on identical colors, not just be
        // built from the same formula.
        var rankByProvider = new Dictionary<string, int>();
        var ringColorByLabel = new Dictionary<string, Color>();
        foreach (var slice in viewModel.ModelShareSlices)
        {
            if (string.IsNullOrEmpty(slice.ProviderId))
                continue;
            var rank = rankByProvider.GetValueOrDefault(slice.ProviderId);
            rankByProvider[slice.ProviderId] = rank + 1;
            ringColorByLabel[slice.Label] = ChartPalette.ForModel(slice.ProviderId, rank);
        }

        Assert.NotEmpty(viewModel.Bars);
        foreach (var bar in viewModel.Bars)
        {
            Assert.True(ringColorByLabel.TryGetValue(bar.Label, out var ringColor), $"no ring slice for bar '{bar.Label}'");
            Assert.Equal(ringColor, ChartPalette.ForModel(bar.ColorProviderId, bar.ColorRank));
        }
    }

    // The "Per day" panel used to be three always-visible sections (by day, by weekday, by hour);
    // it is now one chart multiplexed by SelectedPerDayView, with all three sources kept computed
    // regardless of which one is on screen.
    [Fact]
    public void SetPerDayViewSwitchesTheChartSourceWithoutTouchingTheOthers()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-per-day-view-switch");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        store.AddDelta([new StatsRecord("claude", today, "modelA", "projA", 1000, 0, 0, 0)]);
        var viewModel = StatsVm.Create(store);

        Assert.Equal(StatsPerDayView.Day, viewModel.SelectedPerDayView);
        Assert.Same(viewModel.PerDayBars, viewModel.PerDayViewBars);
        Assert.True(viewModel.IsPerDayViewStackedByProvider);

        viewModel.SetPerDayViewCommand.Execute(StatsPerDayView.Weekday);
        Assert.Equal(StatsPerDayView.Weekday, viewModel.SelectedPerDayView);
        Assert.Same(viewModel.WeekdayBars, viewModel.PerDayViewBars);
        Assert.False(viewModel.IsPerDayViewStackedByProvider);
        Assert.Equal(StatsPerDayView.Weekday, viewModel.SelectedPerDayViewChoice?.Value);

        viewModel.SetPerDayViewCommand.Execute(StatsPerDayView.Hour);
        Assert.Equal(StatsPerDayView.Hour, viewModel.SelectedPerDayView);
        Assert.Same(viewModel.HourBars, viewModel.PerDayViewBars);
        Assert.False(viewModel.IsPerDayViewStackedByProvider);
        Assert.Equal(8, viewModel.PerDayViewMaxAxisLabels);
    }

    [Fact]
    public void SelectedPerDayViewChoiceSetterRoutesThroughTheCommand()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-viewmodel-per-day-view-choice-setter");
        var store = new StatsStore(dataDir);
        var viewModel = StatsVm.Create(store);

        viewModel.SelectedPerDayViewChoice = viewModel.PerDayViewChoices.Single(choice => choice.Value == StatsPerDayView.Hour);

        Assert.Equal(StatsPerDayView.Hour, viewModel.SelectedPerDayView);
        Assert.True(viewModel.PerDayViewChoices.Single(choice => choice.Value == StatsPerDayView.Hour).IsSelected);
    }

    // The twelve-month range follows the calendar, so it holds 366 days when it spans a leap day.
    private static int YearDayCount()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return today.DayNumber - StatsViewModel.RangeStart("Year", today).DayNumber + 1;
    }

    [Fact]
    public void TwelveMonthsStartTheDayAfterTheSameDateOneYearBack()
    {
        Assert.Equal(new DateOnly(2027, 3, 2), StatsViewModel.RangeStart("Year", new DateOnly(2028, 3, 1)));
        Assert.Equal(new DateOnly(2025, 10, 5), StatsViewModel.RangeStart("Year", new DateOnly(2026, 10, 4)));
        Assert.Equal(new DateOnly(2027, 3, 1), StatsViewModel.RangeStart("Year", new DateOnly(2028, 2, 29)));
    }

    [Fact]
    public async Task ChangingPeriodOrGroupingRebuildsFromTheLoadedRecordsWithoutReadingTheStoreAgain()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-vm-no-reload");
        var viewModel = new StatsViewModel(new StatsStore(dataDir));
        var reads = 0;
        var today = DateOnly.FromDateTime(DateTime.Now);
        viewModel.LoadRecords = () =>
        {
            reads++;
            return
            [
                new StatsRecord("claude", today, "modelA", "projA", 100, 50, 0, 0),
                new StatsRecord("claude", today.AddDays(-200), "modelB", "projB", 70, 30, 0, 0),
            ];
        };
        await viewModel.RecomputeAsync();
        var weekRows = viewModel.Rows.Count;

        await viewModel.SetRangeCommand.ExecuteAsync("All");
        await viewModel.SetGroupingCommand.ExecuteAsync(StatsGrouping.Model);

        Assert.Equal(1, reads);
        Assert.Equal(2, viewModel.Rows.Count); // both models, so the older record was taken into account
        Assert.NotEqual(weekRows, viewModel.Rows.Count);

        await viewModel.RecomputeAsync(); // what an index update does
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task ConstructingTheViewModelReadsNothingAndTheFirstLoadFillsTheRows()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-vm-lazy-load");
        var store = new StatsStore(dataDir);
        store.AddDelta([new StatsRecord("claude", DateOnly.FromDateTime(DateTime.Now), "modelA", "projA", 100, 0, 0, 0)]);
        var reads = 0;

        var viewModel = new StatsViewModel(store);
        var realLoad = viewModel.LoadRecords;
        viewModel.LoadRecords = () =>
        {
            reads++;
            return realLoad();
        };

        Assert.Equal(0, reads);
        Assert.True(viewModel.IsLoading);
        Assert.Empty(viewModel.Rows);

        await viewModel.RecomputeAsync();

        Assert.Equal(1, reads);
        Assert.False(viewModel.IsLoading);
        Assert.NotEmpty(viewModel.Rows);
    }

    // The view model reads nothing on construction, so the window's Loaded handler is the one place
    // the first load starts; losing that call would leave the window on its loading state for good.
    [Fact]
    public void TheWindowStartsTheFirstLoadFromItsLoadedHandler()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var source = File.ReadAllText(Path.Combine(dir.FullName, "src", "AiUsage", "Views", "StatsWindow.xaml.cs"));
        var start = source.IndexOf("private void StatsWindow_Loaded", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private void StatsWindow_Closed", start, StringComparison.Ordinal);

        Assert.Contains("RecomputeLoggedAsync()", source[start..end], StringComparison.Ordinal);
        Assert.Contains("await _viewModel.RecomputeAsync()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeadlineLabelsAreComposedFromTheSharedLabelValueText()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-vm-label-value");
        var viewModel = StatsVm.Create(new StatsStore(dataDir));
        var loc = AiUsage.Services.LocalizationService.Instance;

        Assert.StartsWith(loc.Format("Stats.LabelValue", loc["Stats.Input"], ""), viewModel.InputText, StringComparison.Ordinal);
    }
}


[Collection(SharedStateTestsCollection.Name)]
public class StatsViewModelCaptionTests
{
    [Fact]
    public void VsPreviousCaptionNamesThePreviousTotal()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-vm-vs-previous");
        var store = new StatsStore(dataDir);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = StatsViewModel.RangeStart("Week", today);
        store.AddDelta([
            new StatsRecord("claude", today, "modelA", "projA", 500_000, 0, 0, 0),
            new StatsRecord("claude", from.AddDays(-1), "modelA", "projA", 1_000_000, 0, 0, 0),
            new StatsRecord("claude", from.AddDays(-2), "modelA", "projA", 1_000_000, 0, 0, 0),
        ]);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            LocalizationService.Instance.SetLanguage("en");
            var viewModel = StatsVm.Create(store, vm => vm.SelectedRange = "Week");
            viewModel.Recompute();

            Assert.True(viewModel.HasPreviousPeriod);
            Assert.Equal("vs. 2.00 M in the previous period", viewModel.VsPreviousCaption);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void BreakdownFiguresAreShortWithTheExactValueAsTooltip()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-vm-breakdown-short");
        var store = new StatsStore(dataDir);
        store.AddDelta([new StatsRecord("claude", DateOnly.FromDateTime(DateTime.Now), "modelA", "projA", 945_804_386, 12_345, 1_000, 2_000)]);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            LocalizationService.Instance.SetLanguage("en");
            var viewModel = StatsVm.Create(store, vm => vm.SelectedRange = "Week");
            viewModel.Recompute();

            Assert.Equal("Input: 946 M", viewModel.InputText);
            Assert.Equal("945,804,386", viewModel.InputExact);
            Assert.Equal("Output: 12.3 K", viewModel.OutputText);
            Assert.Equal("12,345", viewModel.OutputExact);
            Assert.Equal("Cached: 3.00 K", viewModel.CachedText);
            Assert.Equal("3,000", viewModel.CachedExact);
            var claude = Assert.Single(viewModel.ProviderRows, row => row.DisplayName.Contains("Claude", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("946 M", claude.ValueText);
            Assert.Equal("945,819,731", claude.ExactText);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void VsPreviousCaptionFallsBackToTheShortWordingWithoutAPreviousPeriod()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-vm-vs-previous-none");
        var store = new StatsStore(dataDir);
        store.AddDelta([new StatsRecord("claude", DateOnly.FromDateTime(DateTime.Now), "modelA", "projA", 100, 0, 0, 0)]);
        try
        {
            LocalizationService.Instance.SetLanguage("en");
            var viewModel = StatsVm.Create(store, vm => vm.SelectedRange = "Week");
            viewModel.Recompute();

            Assert.False(viewModel.HasPreviousPeriod);
            Assert.Equal("vs. previous period", viewModel.VsPreviousCaption);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }
}
