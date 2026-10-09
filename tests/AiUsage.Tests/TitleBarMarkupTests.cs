using System.Xml.Linq;

namespace AiUsage.Tests;

/// <summary>Scans the title bar markup as text: it carries no logic worth instantiating a window for.</summary>
public class TitleBarMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_title_bar_has_no_refresh_button_and_the_window_menu_leads_with_refresh()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot.Find(), "src", "AiUsage", "Views", "Controls", "TitleBar.xaml"));

        Assert.DoesNotContain(document.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute(Xaml + "Name") == "RefreshButton");

        var menu = document.Descendants(Presentation + "ContextMenu").Single(m => (string?)m.Attribute(Xaml + "Name") == "WindowMenu");
        var first = menu.Elements().First();
        Assert.Equal(Presentation + "MenuItem", first.Name);
        Assert.Equal("RefreshMenuItem", (string?)first.Attribute(Xaml + "Name"));
        Assert.Equal("F5", (string?)first.Attribute("InputGestureText"));
        Assert.Contains("Action.RefreshNow", (string?)first.Attribute("Header"), StringComparison.Ordinal);
        Assert.Equal(Presentation + "Separator", menu.Elements().Skip(1).First().Name);
    }

    [Fact]
    public void The_title_bar_keeps_the_eye_stats_settings_minimize_and_close_buttons()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot.Find(), "src", "AiUsage", "Views", "Controls", "TitleBar.xaml"));

        var names = document.Descendants(Presentation + "Button")
            .Select(button => (string?)button.Attribute(Xaml + "Name"))
            .Where(name => name is not null && name.EndsWith("Button", StringComparison.Ordinal) && name != "RefreshButton")
            .ToList();
        Assert.Equal(["EyeButton", "StatsButton", "SettingsButton", "MinimizeButton", "CloseButton"], names);
    }
}
