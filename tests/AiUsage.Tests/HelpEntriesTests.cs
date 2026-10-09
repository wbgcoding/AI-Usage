using System.Xml.Linq;
using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>The help entry points are markup: the About section link and the tile menu item that only
/// shows when a tile has a problem, plus the two README answers they lead to.</summary>
public class HelpEntriesTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load(params string[] parts) =>
        XDocument.Load(Path.Combine([RepoRoot.Find(), .. parts]));

    [Fact]
    public void The_about_section_links_to_the_faq()
    {
        var document = Load("src", "AiUsage", "Views", "SettingsWindow.xaml");

        var button = document.Descendants(Presentation + "Button")
            .Single(b => (string?)b.Attribute(Xaml + "Name") == "AboutHelpButton");
        Assert.Contains("About.Help", (string?)button.Attribute("Content"), StringComparison.Ordinal);
        Assert.Equal("https://github.com/wbgcoding/AI-Usage#faq", AppInfo.HelpUrl);
    }

    [Fact]
    public void The_tile_menu_offers_help_only_for_failed_blocked_and_unavailable_tiles()
    {
        var document = Load("src", "AiUsage", "Views", "Controls", "ProviderTile.xaml");

        var item = document.Descendants(Presentation + "MenuItem")
            .Single(m => (string?)m.Attribute(Xaml + "Name") == "HelpMenuItem");
        Assert.Contains("Tile.Menu.Help", (string?)item.Attribute("Header"), StringComparison.Ordinal);
        var shownFor = item.Descendants(Presentation + "DataTrigger")
            .Where(t => (string?)t.Attribute("Binding") == "{Binding Status}")
            .Select(t => (string?)t.Attribute("Value"))
            .Order()
            .ToList();
        Assert.Equal(["Blocked", "Failed", "SourceUnavailable"], shownFor);
    }

    [Fact]
    public void The_readme_faq_answers_both_questions()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot.Find(), "README.md"));

        Assert.Contains("**A tile says an agent is not answering or having problems.**", readme, StringComparison.Ordinal);
        Assert.Contains("**Numbers update less often on battery.**", readme, StringComparison.Ordinal);
    }
}
