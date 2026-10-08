using AiUsage.Stats;

namespace AiUsage.Tests;

/// <summary>Builds a view model the way the window ends up with it: constructed, then loaded once.</summary>
internal static class StatsVm
{
    public static StatsViewModel Create(StatsStore store, Action<StatsViewModel>? configure = null)
    {
        var viewModel = new StatsViewModel(store);
        viewModel.Recompute();
        configure?.Invoke(viewModel);
        return viewModel;
    }
}
