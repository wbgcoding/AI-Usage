using System.Windows.Controls;
using System.Xml.Linq;
using AiUsage.Views.Controls;

namespace AiUsage.Tests;

/// <summary>The right-click menus carry no separator lines and list related entries together.</summary>
public class ContextMenuOrderTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string Src(params string[] parts) => Path.Combine([RepoRoot.Find(), "src", "AiUsage", .. parts]);

    [Fact]
    public void Only_the_layout_popup_keeps_a_separator_and_no_context_menu_builds_one()
    {
        var hits = Directory.EnumerateFiles(Src(), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select(line => (File: Path.GetFileName(f), Line: line)))
            .Where(x => x.Line.Contains("<Separator", StringComparison.Ordinal) || x.Line.Contains("new Separator", StringComparison.Ordinal))
            .Select(x => x.File)
            .ToList();

        Assert.Equal(["TitleBar.xaml"], hits);
    }

    [Fact]
    public void The_title_bar_menu_lists_refresh_window_level_collapse_minimize_close()
    {
        var items = StaTestRunner.Run(() =>
        {
            var titleBar = new TitleBar { ShowWindowMenu = true };
            return titleBar.WindowMenu.Items.Cast<object>().Select(item => item switch
            {
                MenuItem { Items.Count: 3 } => "level",
                MenuItem menuItem => (menuItem.Name ?? "") + "|" + (menuItem.Header as string ?? ""),
                _ => item.GetType().Name,
            }).ToList();
        });

        Assert.Equal(5, items.Count);
        Assert.StartsWith("RefreshMenuItem|", items[0], StringComparison.Ordinal);
        Assert.Equal("level", items[1]);
        Assert.StartsWith("CollapseMenuItem|", items[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_tile_menu_puts_copy_details_before_the_chart_toggle()
    {
        var document = XDocument.Load(Src("Views", "Controls", "ProviderTile.xaml"));
        var menu = document.Descendants(Presentation + "ContextMenu").Single();

        var commands = menu.Elements(Presentation + "MenuItem")
            .Select(item => (string?)item.Attribute("Command") ?? "(click)")
            .ToList();

        Assert.Equal(
        [
            "{Binding RefreshCommand}", "{Binding OpenUsagePageCommand}", "{Binding ToggleDetailsCommand}",
            "{Binding CopyDetailsCommand}", "{Binding ToggleChartCommand}", "(click)",
            "{Binding HideCommand}", "{Binding SignOutCommand}",
        ], commands);
    }
}
