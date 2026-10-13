using AiUsage.Models;
using AiUsage.Providers;

namespace AiUsage.Tests;

public class ExpirePastWindowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_expired_window_loses_its_old_tokens_and_used_allowance_but_keeps_the_total()
    {
        var expired = new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 80, Now.AddMinutes(-1), 300,
            new TokenUsage(5000), new UsageAllowance(40, 50, "Unit_Requests"));
        var running = new UsageWindow("Window_Weekly", WindowKind.Weekly, 30, Now.AddDays(2), 10080,
            new TokenUsage(900), new UsageAllowance(3, 10, "Unit_Requests"));
        var snapshot = new ProviderSnapshot("claude", [expired, running], null, SourceKind.LocalFile, Now, Now, ProviderStatus.Ok, null);

        var result = ProviderSnapshots.ExpirePastWindows(snapshot, Now);

        var first = result.Windows[0];
        Assert.Equal(0, first.UsedPercent);
        Assert.Null(first.ResetsAt);
        Assert.Null(first.Tokens);
        Assert.Equal(new UsageAllowance(0, 50, "Unit_Requests"), first.Allowance);
        Assert.Same(running, result.Windows[1]);
    }
}
