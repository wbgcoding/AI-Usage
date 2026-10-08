using AiUsage.ViewModels;

namespace AiUsage.Views.Controls;

/// <summary>
/// Code-behind for the day-grid widget tile. Almost none of its own logic - everything is bound
/// through the inherited DataContext, a <see cref="DayGridTileViewModel"/>; the two exceptions are
/// the two things a plain view model has no other way to learn: this tile's own rendered width (fed
/// into <see cref="DayGridTileViewModel.AvailableWidth"/> so the density math can decide how many of
/// the most recent weeks fit) and the embedded <see cref="StatsMonthGrid"/>'s own click event (routed
/// to the view model's command instead of handled here).
/// </summary>
public partial class DayGridTile : UserControl
{
    public DayGridTile() => InitializeComponent();

    private void TileRoot_Loaded(object sender, RoutedEventArgs e) => UpdateAvailableWidth();

    private void TileRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAvailableWidth();

    private void UpdateAvailableWidth()
    {
        var width = InnerWidth(TileRoot.ActualWidth, TileRoot.Padding, TileRoot.BorderThickness);
        MonthGrid.AvailableWidth = width;
        if (DataContext is DayGridTileViewModel tile)
            tile.AvailableWidth = width;
    }

    /// <summary>The width the grid really gets inside the tile: the outer width minus padding and
    /// border on both sides. Planning the weeks against the outer width puts one week too many into
    /// the grid, and the tile then cuts off the current week and the end of the legend.</summary>
    internal static double InnerWidth(double outerWidth, Thickness padding, Thickness border) =>
        Math.Max(0, outerWidth - padding.Left - padding.Right - border.Left - border.Right);

    private void MonthGrid_DaySelected(object? sender, DateOnly day)
    {
        if (DataContext is DayGridTileViewModel tile)
            tile.SelectDayCommand.Execute(day);
    }
}
