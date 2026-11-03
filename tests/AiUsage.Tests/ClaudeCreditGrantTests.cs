using System.Globalization;
using AiUsage.Models;
using AiUsage.Providers.Parsing;
using AiUsage.Services;
using AiUsage.ViewModels;
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
    public void The_tile_shows_used_of_granted_and_what_is_left_in_its_tooltip()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        var grant = new ClaudeCreditGrant(200m, 120m, new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero));
        var credit = new ClaudeApiCredit(25.40m, "USD", grant);

        tile.SetApiCredit(credit);

        var loc = LocalizationService.Instance;
        Assert.Contains(credit.FormatMoney(80m, loc.DisplayCulture), tile.ApiGrantText);
        Assert.Contains(credit.FormatMoney(200m, loc.DisplayCulture), tile.ApiGrantText);
        Assert.StartsWith(loc["Tile.ApiGrant.Label"], tile.ApiGrantText);
        Assert.Equal(0.4, tile.ApiGrantFraction, 9);
        Assert.Contains(credit.FormatMoney(120m, loc.DisplayCulture), tile.ApiGrantToolTip);

        tile.SetApiCredit(credit with { Grant = null });
        Assert.Equal("", tile.ApiGrantText);
        Assert.False(tile.ShowApiGrant);
    }
}
