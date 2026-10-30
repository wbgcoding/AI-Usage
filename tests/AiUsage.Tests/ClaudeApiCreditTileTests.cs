using System.Globalization;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class ClaudeApiCreditTileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static ProviderSnapshot Snapshot(ClaudeApiCredit? credit, ProviderStatus status = ProviderStatus.Ok) => new(
        ProviderId: "claude",
        Windows: [new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 40, Now.AddHours(2), 300)],
        PlanType: "Pro", SourceKind: SourceKind.WebSession, FetchedAt: Now, DataTimestamp: Now, Status: status, Error: null)
    {
        ApiCredit = credit,
    };

    [Theory]
    [InlineData("en-US", "USD", 12.34, "$12.34")]
    [InlineData("de-DE", "USD", 12.34, "12,34 $")]
    [InlineData("en-US", "EUR", 5, "€5.00")]
    [InlineData("de-DE", "EUR", 1234.5, "1.234,50 €")]
    [InlineData("en-US", "CHF", 7.5, "7.50 CHF")]
    public void The_balance_is_written_the_way_the_culture_writes_money(string culture, string currency, double amount, string expected)
    {
        var text = new ClaudeApiCredit((decimal)amount, currency).FormatAmount(CultureInfo.GetCultureInfo(culture));

        Assert.Equal(expected.Replace(' ', ' '), text.Replace(' ', ' '));
    }

    [Fact]
    public void A_positive_balance_shows_as_a_label_and_value_line()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.Apply(Snapshot(new ClaudeApiCredit(18.40m, "USD")), Now);

        Assert.True(tile.ShowApiCredit);
        var loc = LocalizationService.Instance;
        Assert.Contains(loc["Tile.ApiCredit.Label"], tile.ApiCreditText, StringComparison.Ordinal);
        Assert.Contains(new ClaudeApiCredit(18.40m, "USD").FormatAmount(loc.DisplayCulture), tile.ApiCreditText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3.5)]
    public void A_balance_of_zero_or_less_shows_nothing(double amount)
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.Apply(Snapshot(new ClaudeApiCredit((decimal)amount, "USD")), Now);

        Assert.False(tile.ShowApiCredit);
        Assert.Equal("", tile.ApiCreditText);
    }

    [Fact]
    public void No_balance_shows_nothing_and_offers_the_console_sign_in()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.Apply(Snapshot(null), Now);

        Assert.False(tile.ShowApiCredit);
        Assert.True(tile.ShowConsoleSignIn);
    }

    [Fact]
    public void A_balance_that_was_read_hides_the_console_sign_in_even_when_it_is_zero()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.Apply(Snapshot(new ClaudeApiCredit(0m, "USD")), Now);

        Assert.False(tile.ShowConsoleSignIn);
    }

    [Fact]
    public void A_local_only_account_shows_neither_the_balance_nor_the_sign_in()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { LocalOnly = true };
        tile.SetApiCredit(new ClaudeApiCredit(5m, "USD"));

        Assert.False(tile.ShowApiCredit);
        Assert.False(tile.ShowConsoleSignIn);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("cursor")]
    public void Other_providers_never_offer_the_console_sign_in(string providerId)
    {
        var tile = new ProviderTileViewModel(providerId, providerId);

        Assert.False(tile.ShowConsoleSignIn);
    }

    [Fact]
    public void The_sign_in_offer_follows_the_extra_claude_account_too()
    {
        var tile = new ProviderTileViewModel("claude#2", "Claude", "claude");

        Assert.True(tile.ShowConsoleSignIn);
    }

    [Fact]
    public void A_failed_read_keeps_showing_the_balance_the_snapshot_carries()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(new ClaudeApiCredit(18.40m, "USD")), Now);

        tile.Apply(Snapshot(new ClaudeApiCredit(18.40m, "USD"), ProviderStatus.Failed) with { Windows = [] }, Now);

        Assert.NotEqual("", tile.ApiCreditText);
    }

    [Fact]
    public void A_balance_set_between_reads_raises_the_changes_the_tile_binds_to()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(Snapshot(null), Now);
        var raised = new List<string?>();
        tile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tile.SetApiCredit(new ClaudeApiCredit(9m, "USD"));

        Assert.Contains(nameof(ProviderTileViewModel.ApiCreditText), raised);
        Assert.Contains(nameof(ProviderTileViewModel.ShowApiCredit), raised);
        Assert.Contains(nameof(ProviderTileViewModel.ShowConsoleSignIn), raised);
        Assert.True(tile.ShowApiCredit);
    }
}
