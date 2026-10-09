using System.ComponentModel;
using System.Diagnostics;
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

    /// <summary>Opens the project's FAQ in the browser; with no default browser there is nothing to recover into.</summary>
    private void HelpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AiUsage.Services.AppInfo.HelpUrl) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
        }
    }

    /// <summary>The chart's own ClipToBounds is a square, so the weekly fill would poke out past the
    /// rounded frame around it. The clip follows the frame's inner corner instead: the 8 px card
    /// radius less the frame's 1 px border.</summary>
    private void HistoryChart_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ((FrameworkElement)sender).Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
}
