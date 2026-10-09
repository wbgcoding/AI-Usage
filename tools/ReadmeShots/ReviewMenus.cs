// Menus and popups for the review set. A context menu or a popup cannot open without a window on
// screen, so each one is taken from the real object (the title bar, a tile, the statistics window,
// the tray menu factory) and drawn as an ordinary element in a host that looks like the window
// around it.
//
// WPF refuses a ContextMenu as the child of another element ("cannot have a logical or visual
// parent"), so the menu's own items (real MenuItem objects with their bindings, commands and the
// app's MenuItem style) are moved into a panel inside a border that carries the same tokens as the
// ContextMenu template in Controls.xaml. The layout popup's content is detached from its popup and
// hosted directly.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Views;
using AiUsage.Views.Controls;

namespace ReadmeShots;

internal static class ReviewMenus
{
    /// <summary>The title bar's window menu (refresh, minimize, collapse, window level, close).</summary>
    internal static void RenderTitleBarMenu(SurfaceContext context, string id)
    {
        using var main = OpenMain(context);
        var titleBar = new TitleBar { ShowWindowMenu = true, ShowStats = true, EyeSource = main.ViewModel, DataContext = main.ViewModel };
        titleBar.SetWindowLayer(WindowLayers.OnTop);
        RenderMenu(titleBar.WindowMenu, main.ViewModel, context.OutputPath(id));
    }

    /// <summary>The layout popup behind the title bar's eye button: one row per tile.</summary>
    internal static void RenderLayoutPopup(SurfaceContext context, string id)
    {
        using var main = OpenMain(context);
        var titleBar = new TitleBar { ShowWindowMenu = true, ShowStats = true, EyeSource = main.ViewModel, DataContext = main.ViewModel };
        var content = (FrameworkElement)titleBar.EyePopup.Child;
        titleBar.EyePopup.Child = null;
        content.DataContext = main.ViewModel;
        RenderHosted(content, context.OutputPath(id));
    }

    /// <summary>The tray icon's menu, from the same factory the tray service builds it with.</summary>
    internal static void RenderTrayMenu(SurfaceContext context, string id) =>
        RenderMenu(TrayService.BuildMenu(windowLayer: WindowLayers.OnTop, clickThrough: false, hotkeyShortcutText: null), null, context.OutputPath(id));

    /// <summary>The right-click menu of a provider tile that is signed in and has detail lines, so
    /// every entry shows.</summary>
    internal static void RenderTileMenu(SurfaceContext context, string id)
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true, Density = TileDensity.Full };
        var claude = context.Samples.Single(sample => sample.Id == "claude").Snapshot;
        tile.Apply(claude with { SourceKind = SourceKind.WebSession, Diagnostics = ["Sample detail line"] }, context.Now);
        var view = new ProviderTile { DataContext = tile };
        TakeMenu(view.TileRoot, tile, context.OutputPath(id));
    }

    /// <summary>The right-click menu of a tile whose read failed: the one with the help entry.</summary>
    internal static void RenderFailedTileMenu(SurfaceContext context, string id)
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { SupportsInAppSignIn = true, Density = TileDensity.Full };
        tile.Apply(new ProviderSnapshot("claude", [], null, SourceKind.None, context.Now, null, ProviderStatus.Failed,
            new ProviderError("Status_Failed_Reason", "Action_Retry", "State.Failed.Detail.Http", "502")), context.Now);
        var view = new ProviderTile { DataContext = tile };
        TakeMenu(view.TileRoot, tile, context.OutputPath(id));
    }

    /// <summary>The single-entry menu of the usage-per-day tile.</summary>
    internal static void RenderDayTileMenu(SurfaceContext context, string id)
    {
        var store = new StatsStore(context.NextDataFolder("menu-daytile"));
        var tile = new DayGridTileViewModel(store);
        var view = new DayGridTile { DataContext = tile };
        TakeMenu(view.TileRoot, tile, context.OutputPath(id));
    }

    /// <summary>The menu of the first visible section heading in the statistics window.</summary>
    internal static void RenderStatsSectionMenu(SurfaceContext context, string id)
    {
        var window = Program.BuildStatisticsWindow(context.NextDataFolder("menu-stats"), context.Records, context.BusyDay);
        var section = FindFirstVisibleSection(window.SectionHost)
            ?? throw new InvalidOperationException("The statistics window has no visible section.");
        window.RefreshContextMenu(section);
        RenderMenu(window.SectionMenu(section), window.DataContext, context.OutputPath(id));
    }

    private static void TakeMenu(FrameworkElement root, object dataContext, string outputPath)
    {
        var menu = root.ContextMenu ?? throw new InvalidOperationException("The tile has no context menu.");
        RenderMenu(menu, dataContext, outputPath);
    }

    /// <summary>Draws a context menu's entries in a panel that looks like the open menu.</summary>
    private static void RenderMenu(ContextMenu menu, object? dataContext, string outputPath)
    {
        var panel = new StackPanel();
        foreach (var item in menu.Items.Cast<object>().ToList())
        {
            menu.Items.Remove(item);
            panel.Children.Add((UIElement)item);
        }

        var surface = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Child = panel,
            DataContext = dataContext,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "Bg.Surface");
        surface.SetResourceReference(Border.BorderBrushProperty, "Border");
        surface.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        RenderHosted(surface, outputPath);
    }

    private static CollapsibleSection? FindFirstVisibleSection(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is CollapsibleSection { Visibility: Visibility.Visible } section)
                return section;
            if (FindFirstVisibleSection(child) is { } nested)
                return nested;
        }
        return null;
    }

    /// <summary>Puts an element in a host with the window's own background and draws it at its
    /// natural size.</summary>
    private static void RenderHosted(FrameworkElement content, string outputPath)
    {
        content.HorizontalAlignment = HorizontalAlignment.Left;
        content.VerticalAlignment = VerticalAlignment.Top;
        var host = new Border();
        host.SetResourceReference(FrameworkElement.StyleProperty, "WindowChrome");
        host.Padding = new Thickness(12);
        host.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        host.Child = content;

        for (var pass = 0; pass < 3; pass++)
        {
            Program.PumpUntil(() => false, TimeSpan.FromMilliseconds(300), settle: TimeSpan.Zero);
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
        }
        var width = Math.Ceiling(host.DesiredSize.Width);
        var height = Math.Ceiling(host.DesiredSize.Height);
        Program.RenderToPng(host, width, height, outputPath);
    }

    private static MainHolder OpenMain(SurfaceContext context) => new(context);

    /// <summary>A main view model built from the review samples, disposed with the holder.</summary>
    private sealed class MainHolder : IDisposable
    {
        internal MainHolder(SurfaceContext context)
        {
            var settings = new AppSettings();
            var settingsStore = new SettingsStore(context.NextDataFolder("menu-main"));
            var historyStore = new HistoryStore(context.NextDataFolder("menu-history"), () => context.Now);
            var providers = context.Samples.Select(sample => (IUsageProvider)new SampleProviders.Provider(sample)).ToList();
            ViewModel = new MainViewModel(settingsStore, settings, providers, historyStore);
            foreach (var tile in ViewModel.Tiles)
            {
                var sample = context.Samples.Single(candidate => candidate.Id == tile.ProviderId);
                tile.Apply(sample.Snapshot, context.Now);
            }
        }

        internal MainViewModel ViewModel { get; }

        public void Dispose() => ViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
