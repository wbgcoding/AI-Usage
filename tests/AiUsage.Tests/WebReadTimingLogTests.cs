using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

public class WebReadTimingLogTests
{
    private const long TwoHundredMegabytes = 200L * 1024 * 1024;

    [Fact]
    public void A_read_with_the_browser_size_logs_milliseconds_and_megabytes()
    {
        var log = new WebReadTimingLog();

        Assert.Equal("web read claude: 142 ms, browser 200 MB", log.NextLine("claude", 142, TwoHundredMegabytes));
    }

    [Fact]
    public void Without_the_browser_size_the_megabyte_part_is_left_out()
    {
        var log = new WebReadTimingLog();

        Assert.Equal("web read codex: 87 ms", log.NextLine("codex", 87, null));
    }

    [Fact]
    public void Only_the_first_ten_reads_of_a_provider_log_a_line()
    {
        var log = new WebReadTimingLog();

        for (var i = 0; i < WebReadTimingLog.LoggedReadsPerProvider; i++)
            Assert.NotNull(log.NextLine("claude", 100 + i, TwoHundredMegabytes));

        Assert.Null(log.NextLine("claude", 100, TwoHundredMegabytes));
        Assert.Null(log.NextLine("claude", 100, null));
    }

    [Fact]
    public void Every_provider_counts_its_own_reads()
    {
        var log = new WebReadTimingLog();
        for (var i = 0; i < WebReadTimingLog.LoggedReadsPerProvider; i++)
            log.NextLine("claude", 100, null);

        Assert.Null(log.NextLine("claude", 100, null));
        Assert.Equal("web read cursor: 5 ms", log.NextLine("cursor", 5, null));
    }
}
