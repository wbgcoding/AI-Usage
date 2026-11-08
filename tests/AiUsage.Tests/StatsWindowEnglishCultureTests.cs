using System.Globalization;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Runs the statistics window walks under a culture whose week starts on Sunday: the window
/// threads these tests open pick the default culture up, so the layout code sees the same first day
/// of the week as on an English machine.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class StatsWindowEnglishCultureTests
{
    [Fact]
    public void TheStatsWindowBuildsUnderAWeekThatStartsOnSunday()
    {
        var previousCulture = CultureInfo.DefaultThreadCurrentCulture;
        var previousUi = CultureInfo.DefaultThreadCurrentUICulture;
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        try
        {
            var stats = new StatsWindowTests();
            stats.StatsWindow_builds_and_renders_without_throwing_across_every_theme();
            stats.StatsWindow_resizes_through_the_native_sizing_frame();
            stats.ChartIsNestedInsideTheBreakdownSectionsOwnContentHost();

            var chrome = new WindowChromeTests();
            chrome.TheStatisticsWindowScrollsItsWholeContentAndNotOnlyItsLowerHalf();
            chrome.NoStatsLabelReachesPastTheWindowEdge();
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = previousCulture;
            CultureInfo.DefaultThreadCurrentUICulture = previousUi;
        }
    }
}
