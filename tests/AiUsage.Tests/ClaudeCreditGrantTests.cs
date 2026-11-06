using System.Globalization;
using AiUsage.Models;
using AiUsage.Providers.Parsing;
using AiUsage.Services;
using AiUsage.ViewModels;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

public class ClaudeCreditGrantTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static string Credits(string tranches) => "{\"amount\":2540,\"currency\":\"USD\",\"promo_tranches\":" + tranches + "}";

    private static string Tranche(int granted, int remaining, string? expires) =>
        "{\"granted_amount_minor_units\":" + granted + ",\"remaining_amount_minor_units\":" + remaining
        + (expires is null ? "" : ",\"expires_at\":\"" + expires + "\"") + "}";

    [Fact]
    public void Two_overlapping_monthly_grants_add_up()
    {
        var body = Credits("[" + Tranche(10000, 2000, "2026-11-05T00:00:00Z") + "," + Tranche(10000, 10000, "2026-12-03T00:00:00Z") + "]");

        var grant = ClaudeConsoleCreditParser.Parse(body, Now).Credit!.Grant!;

        Assert.Equal(200m, grant.Granted);
        Assert.Equal(120m, grant.Remaining);
        Assert.Equal(80m, grant.Used);
        Assert.Equal(0.4, grant.UsedFraction, 9);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), grant.NextExpiry);
    }

    [Theory]
    [InlineData(9971, 9970, 99.71)]   // a cent apart: one figure on the tile
    [InlineData(9970, 9971, 99.70)]
    [InlineData(9975, 9970, 99.70)]   // more than a cent: real paid credit, left as read
    public void A_grant_remainder_a_cent_from_the_balance_takes_the_balance_figure(int balanceCents, int remainingCents, double expected)
    {
        var body = "{\"amount\":" + balanceCents + ",\"currency\":\"USD\",\"promo_tranches\":["
            + Tranche(10000, remainingCents, "2026-12-03T00:00:00Z") + "]}";

        var grant = ClaudeConsoleCreditParser.Parse(body, Now).Credit!.Grant!;

        Assert.Equal((decimal)expected, grant.Remaining);
        Assert.Equal(100m, grant.Granted);
    }

    [Fact]
    public void An_expired_grant_no_longer_counts()
    {
        var body = Credits("[" + Tranche(10000, 0, "2026-11-01T00:00:00Z") + "," + Tranche(10000, 7000, "2026-12-01T00:00:00Z") + "]");

        var grant = ClaudeConsoleCreditParser.Parse(body, Now).Credit!.Grant!;

        Assert.Equal(100m, grant.Granted);
        Assert.Equal(70m, grant.Remaining);
    }

    [Fact]
    public void A_grant_without_an_end_counts_and_has_no_expiry()
    {
        var grant = ClaudeConsoleCreditParser.Parse(Credits("[" + Tranche(500, 500, null) + "]"), Now).Credit!.Grant!;

        Assert.Equal(5m, grant.Granted);
        Assert.Null(grant.NextExpiry);
    }

    [Theory]
    [InlineData("{\"amount\":2540}")]
    [InlineData("{\"amount\":2540,\"promo_tranches\":null}")]
    [InlineData("{\"amount\":2540,\"promo_tranches\":[]}")]
    public void No_grants_listed_means_no_grant_but_the_balance_reads(string body)
    {
        var credit = ClaudeConsoleCreditParser.Parse(body, Now).Credit!;

        Assert.Equal(25.40m, credit.Amount);
        Assert.Null(credit.Grant);
        Assert.Empty(ClaudeConsoleCreditParser.ChangeNotes(null, body));
    }

    [Theory]
    [InlineData("{\"granted_amount_minor_units\":100,\"remaining_amount_minor_units\":200}")]
    [InlineData("{\"granted_amount_minor_units\":0,\"remaining_amount_minor_units\":0}")]
    [InlineData("{\"granted_amount_minor_units\":\"100\",\"remaining_amount_minor_units\":50}")]
    [InlineData("{\"remaining_amount_minor_units\":50}")]
    [InlineData("{\"granted_amount_minor_units\":100,\"remaining_amount_minor_units\":50,\"expires_at\":\"soon\"}")]
    public void A_grant_that_does_not_hold_together_shows_nothing_and_is_reported(string tranche)
    {
        var body = Credits("[" + Tranche(10000, 5000, "2026-12-01T00:00:00Z") + "," + tranche + "]");

        var credit = ClaudeConsoleCreditParser.Parse(body, Now).Credit!;

        Assert.Equal(25.40m, credit.Amount);
        Assert.Null(credit.Grant);
        Assert.Contains(ClaudeConsoleCreditParser.ChangeNotes(null, body), note => note.Contains("promo_tranches"));
    }

    [Fact]
    public void The_tile_shows_the_free_credit_as_a_bar_row_with_what_is_left_above_it()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var grant = new ClaudeCreditGrant(200m, 120m, DateTimeOffset.Now.AddDays(12));
        var credit = new ClaudeApiCredit(25.40m, "USD", grant);

        tile.SetApiCredit(credit);

        var loc = LocalizationService.Instance;
        var row = tile.ApiGrantRow!;
        Assert.Equal(loc["Tile.ApiGrant.Label"], row.LabelText);
        Assert.Equal(loc.Format("Tile.ApiGrant.Left", credit.FormatMoney(120m, loc.DisplayCulture)), row.InlineTokensText);
        Assert.Equal("", row.InlineTokensToolTip);
        Assert.Equal(40, row.UsedPercent, 9);
        Assert.Contains(credit.FormatMoney(80m, loc.DisplayCulture), row.BarTooltipText);
        Assert.Contains(credit.FormatMoney(200m, loc.DisplayCulture), row.BarTooltipText);
        Assert.NotEqual("", row.CountdownText);
        Assert.Null(row.PacePercent);
        Assert.False(row.ShowThresholdMarker);

        // A later read moves the same row instead of building a new one.
        tile.SetApiCredit(credit with { Grant = grant with { Remaining = 20m } });
        Assert.Same(row, tile.ApiGrantRow);
        Assert.Equal(90, row.UsedPercent, 9);
        Assert.Contains(credit.FormatMoney(20m, loc.DisplayCulture), row.InlineTokensText);
        Assert.Equal(UsageLevel.Crit, row.Level);

        tile.SetApiCredit(credit with { Grant = null });
        Assert.Null(tile.ApiGrantRow);
        Assert.False(tile.ShowApiGrant);
    }

    [Fact]
    public void The_free_credit_row_says_when_it_runs_out_from_its_own_readings()
    {
        var now = DateTimeOffset.Now;
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.SetApiCredit(new ClaudeApiCredit(0m, "USD", new ClaudeCreditGrant(200m, 110m, now.AddDays(20))));
        // 10 % to 45 % over seven hours: 5 % an hour, so the rest lasts about eleven hours.
        var points = Enumerable.Range(0, 8)
            .Select(i => new HistoryChart.ChartPoint(now.AddHours(i - 7), 10 + 5 * i))
            .ToList();

        tile.UpdateApiGrantHistory(points, now);

        var row = tile.ApiGrantRow!;
        Assert.NotEqual("", row.ForecastText);
        Assert.DoesNotContain(LocalizationService.Instance["Window.ForecastFull"].Replace("{0}", "", StringComparison.Ordinal).Trim(), row.ForecastText);

        // Lasting past the end of the grant is no warning.
        tile.SetApiCredit(new ClaudeApiCredit(0m, "USD", new ClaudeCreditGrant(200m, 110m, now.AddHours(2))));
        tile.UpdateApiGrantHistory(points, now);
        Assert.Equal("", tile.ApiGrantRow!.ForecastText);
    }

    [Fact]
    public void The_free_credit_history_has_its_own_id_per_account()
    {
        Assert.Equal("claude#credit", MainViewModel.ApiGrantHistoryId("claude"));
        Assert.Equal("claude#2#credit", MainViewModel.ApiGrantHistoryId("claude#2"));
        Assert.NotEqual(
            AiUsage.Storage.AppPaths.SanitizeAccountKeyForFileName(MainViewModel.ApiGrantHistoryId("claude#2")),
            AiUsage.Storage.AppPaths.SanitizeAccountKeyForFileName("claude#2"));
    }
}
