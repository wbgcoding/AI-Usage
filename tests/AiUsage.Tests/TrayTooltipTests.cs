using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class TrayTooltipTests
{
    [Fact]
    public void NoProvidersShowsAppName()
    {
        Assert.Equal("AI-Usage", TrayTooltipBuilder.Build([]));
    }

    [Fact]
    public void OneProviderFormatsFiveHourAndWeekly()
    {
        var text = TrayTooltipBuilder.Build([new TrayTooltipBuilder.ProviderLine("Claude", 42, 63)]);

        Assert.Equal("Claude 5 Std. 42 % · 7 T. 63 %", text);
    }

    [Fact]
    public void TheWindowTokensFollowTheLanguage()
    {
        LocalizationService.Instance.SetLanguage("en");
        try
        {
            var text = TrayTooltipBuilder.Build([new TrayTooltipBuilder.ProviderLine("Claude", 42, 63)]);

            Assert.Equal("Claude 5h 42% · 7d 63%", text);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("de");
        }
    }

    [Fact]
    public void SeveralProvidersEachGetTheirOwnLine()
    {
        var lines = new[]
        {
            new TrayTooltipBuilder.ProviderLine("Codex", 10, 20),
            new TrayTooltipBuilder.ProviderLine("Claude", 30, 40),
        };

        var text = TrayTooltipBuilder.Build(lines);

        Assert.Equal(2, text.Split('\n').Length);
        Assert.True(text.Length <= TrayTooltipBuilder.MaxLength);
    }

    [Fact]
    public void LongNamesAreShortenedInsteadOfLosingALine()
    {
        var lines = new[]
        {
            new TrayTooltipBuilder.ProviderLine("Copilot-Enterprise-Long-Name", 99, 99),
            new TrayTooltipBuilder.ProviderLine("Claude-Extended-Name-Too", 88, 88),
            new TrayTooltipBuilder.ProviderLine("Gemini-Advanced-Ultra-Pro", 77, 77),
            new TrayTooltipBuilder.ProviderLine("Codex-Business-Plus-Plan", 66, 66),
        };

        var text = TrayTooltipBuilder.Build(lines);

        Assert.True(text.Length <= TrayTooltipBuilder.MaxLength, $"tooltip was {text.Length} chars: {text}");
        Assert.Equal(4, text.Split('\n').Length);
    }

    [Fact]
    public void ShortenedNamesThatEndUpAlikeAreNumbered()
    {
        var lines = new[]
        {
            new TrayTooltipBuilder.ProviderLine("Claude work account", 10, 20),
            new TrayTooltipBuilder.ProviderLine("Claude private account", 30, 40),
            new TrayTooltipBuilder.ProviderLine("Codex enterprise account", 50, 60),
            new TrayTooltipBuilder.ProviderLine("Gemini advanced account", 70, 80),
        };

        var text = TrayTooltipBuilder.Build(lines);
        var rows = text.Split('\n');

        Assert.StartsWith("Cla1 ", rows[0]);
        Assert.StartsWith("Cla2 ", rows[1]);
        Assert.StartsWith("Cod ", rows[2]);
        Assert.StartsWith("Gem ", rows[3]);
    }

    [Fact]
    public void WholeTrailingLinesAreDroppedInsteadOfSlicingTheText()
    {
        var lines = Enumerable.Range(0, 20)
            .Select(i => new TrayTooltipBuilder.ProviderLine($"Provider{i}", 99, 99))
            .ToArray();

        var rows = TrayTooltipBuilder.Build(lines).Split('\n');

        Assert.True(rows.Length < 20);
        Assert.True(rows.Length >= 1);
        Assert.All(rows, row => Assert.Matches(@"^\S+ .+ 99 ?% · .+ 99 ?%$", row)); // every kept line is complete
        Assert.StartsWith("Pro", rows[0]);
    }

    [Fact]
    public void AMissingWindowShowsADashNotAnInventedZeroPercent()
    {
        var text = TrayTooltipBuilder.Build([new TrayTooltipBuilder.ProviderLine("Claude", null, 63)]);

        Assert.Equal("Claude 5 Std. – · 7 T. 63 %", text);
    }

    [Fact]
    public void EvenShortenedNamesAreHardCappedAsALastResort()
    {
        var lines = Enumerable.Range(0, 20)
            .Select(i => new TrayTooltipBuilder.ProviderLine($"Provider{i}", 99, 99))
            .ToArray();

        var text = TrayTooltipBuilder.Build(lines);

        Assert.True(text.Length <= TrayTooltipBuilder.MaxLength);
    }
}
