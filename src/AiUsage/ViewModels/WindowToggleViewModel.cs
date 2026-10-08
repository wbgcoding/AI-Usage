using AiUsage.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiUsage.ViewModels;

/// <summary>One row's own visibility switch in the Settings window's Providers card (see <see
/// cref="ProviderTileViewModel.WindowToggles"/>) - one instance per distinct row a tile's latest
/// snapshot carries, in the order those rows appear on the tile itself.</summary>
public sealed partial class WindowToggleViewModel : ObservableObject
{
    /// <summary>The row's raw identity (<see cref="Models.UsageWindow.Label"/>, a resource key or a
    /// literal provider-supplied name) - never resolved through the active locale, so it stays a
    /// stable key across a language switch and a settings round trip alike.</summary>
    public string Label { get; }

    public WindowKind Kind { get; }

    /// <summary>What the switch shows - <see cref="Label"/> resolved through the active locale,
    /// re-assigned on every <see cref="ProviderTileViewModel.RefreshLocalizedText"/> call.</summary>
    [ObservableProperty]
    private string displayText;

    /// <summary>True unless this row is currently hidden - a <see cref="WindowKind.FiveHour"/> or
    /// <see cref="WindowKind.Weekly"/> row mirrors the tile's own <see
    /// cref="ProviderTileViewModel.ShowFiveHour"/>/<see cref="ProviderTileViewModel.ShowWeekly"/>,
    /// any other row mirrors whether its <see cref="Label"/> is listed in <see
    /// cref="Models.ProviderSettings.HiddenWindows"/>.</summary>
    [ObservableProperty]
    private bool isVisible;

    public WindowToggleViewModel(string label, WindowKind kind, string displayText, bool isVisible)
    {
        Label = label;
        Kind = kind;
        this.displayText = displayText;
        this.isVisible = isVisible;
    }
}
