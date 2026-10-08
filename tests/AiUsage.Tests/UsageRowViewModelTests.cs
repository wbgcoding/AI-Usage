using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using AiUsage.Views.Controls;

namespace AiUsage.Tests;

/// <summary>The optional token segment on the right-hand label.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class UsageRowViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T12:00:00Z");

    [Fact]
    public void RightLabelText_has_no_token_segment_when_the_window_carries_none()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now);

        Assert.Null(row.TokenCount);
        Assert.DoesNotContain("·", row.RightLabelText);
        Assert.Equal("", row.InlineTokensText);
    }

    [Fact]
    public void RightLabelText_never_carries_the_token_count_even_when_the_window_has_one()
    {
        var window = new UsageWindow(
            "Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300,
            tokens: new TokenUsage(51505));

        var row = new UsageRowViewModel(window, Now);

        Assert.Equal(51505, row.TokenCount);
        Assert.DoesNotContain(51505L.ToString("N0", CultureInfo.CurrentCulture), row.RightLabelText);
    }

    [Fact]
    public void InlineTokensText_carries_the_token_count_when_the_window_carries_one()
    {
        var window = new UsageWindow(
            "Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300,
            tokens: new TokenUsage(1_823_457));
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            LocalizationService.Instance.SetLanguage("en");
            var english = new UsageRowViewModel(window, Now);
            Assert.Equal("1.82 M tokens", english.InlineTokensText);
            Assert.Equal("1,823,457 tokens in this window", english.InlineTokensToolTip);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            LocalizationService.Instance.SetLanguage("de");
            var german = new UsageRowViewModel(window, Now);
            Assert.Equal("1,82 Mio Token", german.InlineTokensText);
            Assert.Equal("1.823.457 Token in diesem Zeitfenster", german.InlineTokensToolTip);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void InlineTokensToolTip_is_empty_without_a_token_count()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now);
        row.WeekTokens = 5_000_000;

        Assert.Equal("", row.InlineTokensToolTip);
        Assert.Equal("", row.InlineTokensText);
    }

    [Fact]
    public void WeekTokens_feed_only_a_weekly_row_without_a_window_count()
    {
        var weekly = new UsageWindow("Window_Weekly", WindowKind.Weekly, 42.0, resetsAt: null, windowMinutes: 10080);
        var other = new UsageWindow("Window_Weekly", WindowKind.Other, 42.0, resetsAt: null, windowMinutes: 10080);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            LocalizationService.Instance.SetLanguage("en");
            var row = new UsageRowViewModel(weekly, Now);
            Assert.Equal("", row.InlineTokensText);

            row.WeekTokens = 1_823_457;

            Assert.Equal("1.82 M tokens", row.InlineTokensText);
            Assert.Equal("1,823,457 tokens this week", row.InlineTokensToolTip);

            var notWeekly = new UsageRowViewModel(other, Now) { WeekTokens = 1_823_457 };
            Assert.Equal("", notWeekly.InlineTokensText);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void RightLabelText_never_carries_the_warning_glyph_at_Crit()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 90.0, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now);

        Assert.Equal(UsageLevel.Crit, row.Level);
        Assert.DoesNotContain("⚠", row.RightLabelText);
    }

    [Fact]
    public void The_forecast_stays_off_the_right_label_and_off_the_inline_tokens()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 50.0, resetsAt: null, windowMinutes: 300);
        var row = new UsageRowViewModel(window, Now);

        row.ForecastText = "full in about 2h 5m";

        Assert.DoesNotContain("full in about 2h 5m", row.RightLabelText);
        Assert.DoesNotContain("full in about 2h 5m", row.InlineTokensText);
    }

    /// <summary>A window already at its limit has no "full in" left to forecast: the history still
    /// rises, which read as "full in about 0s" under a bar that says the limit is reached.</summary>
    [Fact]
    public void A_full_window_shows_no_time_to_full_forecast()
    {
        var window = new UsageWindow(
            "Window_FiveHour", WindowKind.FiveHour, 100.0, resetsAt: Now.AddHours(1), windowMinutes: 300);
        var row = new UsageRowViewModel(window, Now);
        HistoryChart.ChartPoint[] rising =
        [
            new(Now.AddMinutes(-90), 40), new(Now.AddMinutes(-75), 52), new(Now.AddMinutes(-60), 64),
            new(Now.AddMinutes(-45), 76), new(Now.AddMinutes(-30), 88), new(Now.AddMinutes(-15), 99),
            new(Now.AddMinutes(-1), 100),
        ];
        Assert.NotNull(UsageForecast.TimeToFull(rising, Now, window.ResetsAt));

        row.UpdateForecast(rising, Now, lookback: null);

        Assert.Equal("", row.ForecastText);
    }

    [Fact]
    public void ARowThatAlreadyReadsLimitReachedShowsNoForecast()
    {
        var window = new UsageWindow(
            "Window_FiveHour", WindowKind.FiveHour, 99.6, resetsAt: Now.AddHours(2), windowMinutes: 300);
        var row = new UsageRowViewModel(window, Now);
        HistoryChart.ChartPoint[] rising =
        [
            new(Now.AddMinutes(-90), 90), new(Now.AddMinutes(-75), 92), new(Now.AddMinutes(-60), 94),
            new(Now.AddMinutes(-45), 96), new(Now.AddMinutes(-30), 97.5), new(Now.AddMinutes(-15), 98.8),
            new(Now.AddMinutes(-1), 99.6),
        ];
        Assert.NotNull(UsageForecast.TimeToFull(rising, Now, window.ResetsAt));

        row.UpdateForecast(rising, Now, lookback: null);

        Assert.Equal("", row.ForecastText);
    }

    [Fact]
    public void RightLabelText_is_the_plain_percent_at_Crit()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 90.0, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now);

        Assert.Equal("90 %", row.RightLabelText);
    }

    [Fact]
    public void ClockText_is_populated_at_Full_density()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: Now.AddHours(2), windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now, TileDensity.Full);

        Assert.NotEqual("", row.ClockText);
    }

    [Fact]
    public void ClockText_is_populated_at_Mini_density()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: Now.AddHours(2), windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now, TileDensity.Mini);

        Assert.NotEqual("", row.ClockText);
    }

    [Fact]
    public void ClockText_stays_empty_without_a_known_reset_instant_at_every_density()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);

        Assert.Equal("", new UsageRowViewModel(window, Now, TileDensity.Full).ClockText);
        Assert.Equal("", new UsageRowViewModel(window, Now, TileDensity.Mini).ClockText);
    }

    /// <summary>At the limit the whole row reads "100 % · Limit erreicht · 2h 0m · 21:15": the state,
    /// the bare time left and the clock time of the reset, each separated by the same dot, and no
    /// sentence wrapped around the duration that would only cost width.</summary>
    [Fact]
    public void AtLimitWithAKnownResetShowsTheStateTheBareDurationAndTheClock()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 100.0, resetsAt: Now.AddHours(2), windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now, TileDensity.Full);

        Assert.Equal($"{LocalizationService.Instance["State.LimitReached"]} · 2h", row.CountdownText);
        Assert.NotEqual("", row.ClockText);
        Assert.StartsWith("100 % · ", row.RightLabelText, StringComparison.Ordinal);
        Assert.EndsWith(row.ClockText, row.RightLabelText, StringComparison.Ordinal);
    }

    [Fact]
    public void AtLimitWithoutAKnownResetShowsOnlyTheLimitWords()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 100.0, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now, TileDensity.Full);

        Assert.DoesNotContain("·", row.CountdownText);
        Assert.Equal("", row.ClockText);
    }

    [Fact]
    public void BelowTheLimitClockAndCountdownAreUnchangedByTheLimitLogic()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: Now.AddHours(2), windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now, TileDensity.Full);

        Assert.DoesNotContain("·", row.CountdownText);
        Assert.NotEqual("", row.ClockText);
    }

    [Fact]
    public void ThresholdMarkerShowsAtFullDensityWhenAThresholdIsSet()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now, TileDensity.Full, thresholdPercent: 85);

        Assert.True(row.ShowThresholdMarker);
    }

    [Fact]
    public void ThresholdMarkerIsHiddenWhenThatWindowsNotificationIsDisabled()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now, TileDensity.Full, thresholdPercent: null);

        Assert.False(row.ShowThresholdMarker);
    }

    [Fact]
    public void ThresholdMarkerIsHiddenOutsideFullDensityEvenWithAThresholdSet()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now, TileDensity.Mini, thresholdPercent: 85);

        Assert.False(row.ShowThresholdMarker);
    }

    [Fact]
    public void RefreshCountdown_from_Mini_to_Full_shows_the_marker_and_raises_it()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);
        var row = new UsageRowViewModel(window, Now, TileDensity.Mini, thresholdPercent: 85);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.RefreshCountdown(Now, TileDensity.Full);

        Assert.True(row.ShowThresholdMarker);
        Assert.Contains(nameof(UsageRowViewModel.ShowThresholdMarker), raised);
        Assert.Contains(nameof(UsageRowViewModel.ThresholdMarkerTooltip), raised);
    }

    [Fact]
    public void RefreshCountdown_from_Full_to_Mini_hides_the_marker_and_raises_it()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);
        var row = new UsageRowViewModel(window, Now, TileDensity.Full, thresholdPercent: 85);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.RefreshCountdown(Now, TileDensity.Mini);

        Assert.False(row.ShowThresholdMarker);
        Assert.Contains(nameof(UsageRowViewModel.ShowThresholdMarker), raised);
        Assert.Contains(nameof(UsageRowViewModel.ThresholdMarkerTooltip), raised);
    }

    [Fact]
    public void DetailTextCarriesCountdownAndClockWithoutThePercent()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: Now.AddHours(2), windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now, TileDensity.Full);

        Assert.NotEqual("", row.CountdownText);
        Assert.NotEqual("", row.ClockText);
        Assert.Equal($"{row.CountdownText} · {row.ClockText}", row.DetailText);
        Assert.DoesNotContain("%", row.DetailText);
        Assert.Equal($"{row.PercentText} · {row.DetailText}", row.RightLabelText);

        var bare = new UsageRowViewModel(
            new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300), Now);
        Assert.Equal("", bare.DetailText);
        Assert.Equal(bare.PercentText, bare.RightLabelText);
    }

    [Fact]
    public void RightLabelTextShowsTheBarePercent()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);
        try
        {
            LocalizationService.Instance.SetLanguage("en");
            Assert.Equal("42%", new UsageRowViewModel(window, Now).RightLabelText);

            LocalizationService.Instance.SetLanguage("de");
            Assert.Equal("42 %", new UsageRowViewModel(window, Now).RightLabelText);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void MiniPercentText_rounds_the_same_way_as_RightLabelText()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 62.5, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now);

        Assert.Equal("63 %", row.MiniPercentText);
        Assert.StartsWith("63 %", row.RightLabelText);
    }

    [Fact]
    public void MiniPercentText_updates_when_UsedPercent_changes()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);
        var row = new UsageRowViewModel(window, Now);

        row.UsedPercent = 62.5;

        Assert.Equal("63 %", row.MiniPercentText);
    }

    [Theory]
    [InlineData(50.0, false, "")]
    [InlineData(70.0, true, "fast aufgebraucht")]
    [InlineData(90.0, false, "fast am Limit")]
    public void WarnLevelShowsTheAlertIcon(double percent, bool alertIcon, string levelName)
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, percent, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now);

        Assert.Equal(alertIcon, row.ShowAlertIcon);
        Assert.Equal(levelName, row.LevelName);
    }

    [Fact]
    public void TheAccessibleNamesAppendTheLevelNameOnlyOnceItIsNotOk()
    {
        var ok = new UsageRowViewModel(new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 50.0, resetsAt: null, windowMinutes: 300), Now);
        var warn = new UsageRowViewModel(new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 70.0, resetsAt: null, windowMinutes: 300), Now);

        Assert.Equal(ok.LabelText, ok.BarAccessibleName);
        Assert.Equal($"{ok.LabelText}, 50 % genutzt", ok.MiniAccessibleName);
        Assert.Equal($"{warn.LabelText}, fast aufgebraucht", warn.BarAccessibleName);
        Assert.Equal($"{warn.LabelText}, 70 % genutzt, fast aufgebraucht", warn.MiniAccessibleName);
    }

    [Fact]
    public void TheLevelNameFollowsALevelChangeOnAnUpdate()
    {
        var row = new UsageRowViewModel(new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 50.0, resetsAt: null, windowMinutes: 300), Now);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Update(new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 90.0, resetsAt: null, windowMinutes: 300), Now, TileDensity.Full, null);

        Assert.Equal("fast am Limit", row.LevelName);
        Assert.False(row.ShowAlertIcon);
        Assert.Contains(nameof(UsageRowViewModel.BarAccessibleName), changed);
    }

    [Fact]
    public void ThePercentTextFollowsALanguageSwitchWithAnUnchangedValue()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);
        try
        {
            LocalizationService.Instance.SetLanguage("de");
            var row = new UsageRowViewModel(window, Now);
            var changed = new List<string?>();
            row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            LocalizationService.Instance.SetLanguage("en");
            row.Update(window, Now, TileDensity.Full, null);

            Assert.Equal("42%", row.PercentText);
            Assert.Contains(nameof(UsageRowViewModel.PercentText), changed);
            Assert.Contains(nameof(UsageRowViewModel.MiniPercentText), changed);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void Classify_turns_Warn_only_strictly_above_WarnFrom()
    {
        Assert.Equal(UsageLevel.Ok, UsageRowViewModel.Classify(UsageRowViewModel.WarnFrom));
        Assert.Equal(UsageLevel.Warn, UsageRowViewModel.Classify(UsageRowViewModel.WarnFrom + 0.1));
    }

    [Fact]
    public void AllowanceText_names_both_numbers_and_the_unit_when_an_allowance_exists()
    {
        var window = new UsageWindow(
            "Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300,
            allowance: new UsageAllowance(210, 500, "Window.Other"));

        var row = new UsageRowViewModel(window, Now);

        Assert.Contains("210", row.AllowanceText);
        Assert.Contains("500", row.AllowanceText);
        Assert.Contains("42", row.AllowanceText);
    }

    [Fact]
    public void AllowanceText_is_empty_when_the_window_carries_no_allowance()
    {
        var window = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 42.0, resetsAt: null, windowMinutes: 300);

        var row = new UsageRowViewModel(window, Now);

        Assert.Equal("", row.AllowanceText);
    }
}
