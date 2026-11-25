using System.Windows;
using System.Windows.Media;
using AiUsage.Services;

namespace AiUsage.Views.Controls;

/// <summary>The one tile template for every provider. Almost no code-behind logic -
/// everything is bound through the inherited DataContext, a ViewModels.ProviderTileViewModel.</summary>
public partial class ProviderTile : UserControl
{
    public ProviderTile()
    {
        InitializeComponent();
    }

    /// <summary>Opens the project's FAQ in the browser; with no default browser there is nothing to recover into.</summary>
    private void HelpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShellOpen.TryOpen(AppInfo.HelpUrl);
    }

    /// <summary>The chart's own ClipToBounds is a square, so the weekly fill would poke out past the
    /// rounded frame around it. The clip follows the frame's inner corner instead: the card radius
    /// less the frame's border.</summary>
    private void HistoryChart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var card = ((CornerRadius)FindResource("Radius.Card")).TopLeft;
        var inner = Math.Max(0, card - DiagramPlaceholder.BorderThickness.Left);
        ((FrameworkElement)sender).Clip = new RectangleGeometry(new Rect(e.NewSize), inner, inner);
    }
}
