using AiUsage.Services;

namespace AiUsage.ViewModels;

/// <summary>
/// The handful of members MainWindow's density and reorder machinery needs from any row in <see
/// cref="MainViewModel.DisplayRows"/> - a real provider tile (<see cref="ProviderTileViewModel"/>) or
/// the widget's own day-grid pseudo tile (<see cref="DayGridTileViewModel"/>) - so the window's
/// height/density measurement and the eye popup's reorder arrows work the same way for both kinds
/// without branching on the concrete type.
/// </summary>
public interface ITileRow
{
    string ProviderId { get; }

    bool IsHidden { get; }

    TileDensity Density { get; set; }

    bool CanMoveUp { get; set; }

    bool CanMoveDown { get; set; }
}
