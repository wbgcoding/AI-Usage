using AiUsage.Services;
using AiUsage.Stats;
using Xunit;

namespace AiUsage.Tests;

/// <summary>What the statistics view model does when the language changes under an open window.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class StatsViewModelLanguageTests
{
    [Fact]
    public void ALanguageSwitchRebuildsTheChoiceLabelsAndTheTextsTheViewModelComposes()
    {
        var loc = LocalizationService.Instance;
        // The suite's ambient language is German; exactly that is put back afterwards.
        loc.SetLanguage("en");
        try
        {
            using var dataDir = TestPaths.CreateDisposableDirectory("stats-vm-language");
            var store = new StatsStore(dataDir);
            var day = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
            store.AddDelta([new StatsRecord("claude", day, "modelA", "projA", 500, 0, 0, 0)]);
            var viewModel = StatsVm.Create(store);
            viewModel.ToggleSelectedDay(day);
            var rangeBefore = viewModel.RangeChoices[0].Label;
            var headBefore = viewModel.SelectedDayHeadText;

            loc.SetLanguage("de");
            viewModel.RefreshLanguage();

            Assert.NotEqual(rangeBefore, viewModel.RangeChoices[0].Label);
            Assert.NotEqual(headBefore, viewModel.SelectedDayHeadText);
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }
}
