using System.Windows;
using System.Windows.Media;

namespace AiUsage.Views.Controls;

/// <summary>The one tile template for every provider. Almost no code-behind logic -
/// everything is bound through the inherited DataContext, a ViewModels.ProviderTileViewModel.</summary>
public partial class ProviderTile : UserControl
{
    public ProviderTile()
    {
        InitializeComponent();
    }

    /// <summary>The chart's own ClipToBounds is a square, so the weekly fill would poke out past the
    /// rounded frame around it. The clip follows the frame's inner corner instead: the 8 px card
    /// radius less the frame's 1 px border.</summary>
    private void HistoryChart_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ((FrameworkElement)sender).Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
}
