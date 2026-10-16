using AiUsage.Providers.Parsing;
using AiUsage.Stats;

namespace AiUsage.Tests;

public class StatsAntigravityRootsTests
{
    [Fact]
    public void The_stats_walk_reads_the_same_antigravity_folders_as_the_waiting_mark()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-agy-roots");

        var indexer = new StatsIndexer(new StatsStore(dataDir));

        Assert.Equal(AntigravityTurnReader.DefaultRoots(), indexer.AntigravityRoots);
    }
}
