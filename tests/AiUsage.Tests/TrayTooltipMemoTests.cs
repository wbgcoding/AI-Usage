using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class TrayTooltipMemoTests
{
    [Fact]
    public void Sixty_ticks_with_unchanged_text_report_changed_exactly_once()
    {
        var memo = new TrayTooltipMemo();
        var assignments = 0;

        for (var i = 0; i < 60; i++)
        {
            if (memo.HasChanged("Codex 42% - Claude 10%"))
                assignments++;
        }

        Assert.Equal(1, assignments);
    }

    [Fact]
    public void A_changed_value_is_reported_again()
    {
        var memo = new TrayTooltipMemo();
        Assert.True(memo.HasChanged("Codex 42%"));
        Assert.False(memo.HasChanged("Codex 42%"));

        Assert.True(memo.HasChanged("Codex 43%"));
        Assert.False(memo.HasChanged("Codex 43%"));
    }
}
