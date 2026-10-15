using System.Xml.Linq;
using Xunit;

namespace AiUsage.Tests;

public class SettingsRefreshPlacementTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load() =>
        XDocument.Load(Path.Combine(RepoRoot.Find(), "src", "AiUsage", "Views", "SettingsWindow.xaml"));

    // The category a panel belongs to: the value of the DataTrigger in its own Style.
    private static string? CategoryOf(XElement element) =>
        element.Ancestors(Wpf + "StackPanel")
            .Select(panel => (string?)panel.Element(Wpf + "StackPanel.Style")?.Descendants(Wpf + "DataTrigger").FirstOrDefault()?.Attribute("Value"))
            .FirstOrDefault(value => value is not null);

    [Fact]
    public void The_per_account_refresh_picker_lives_in_the_refresh_category_once_per_account_and_not_in_the_provider_card()
    {
        var pickers = Load().Descendants(Wpf + "ComboBox")
            .Where(box => (string?)box.Attribute("ItemsSource") == "{Binding RefreshChoices}")
            .ToList();

        var picker = Assert.Single(pickers);
        Assert.Equal("Updates", CategoryOf(picker));
        var list = picker.Ancestors(Wpf + "ItemsControl").First();
        Assert.Equal("ProviderRefreshList", (string?)list.Attribute(X + "Name"));
        Assert.Equal("{Binding Main.Tiles}", (string?)list.Attribute("ItemsSource"));
        Assert.Equal("{Binding SelectedRefreshChoice, Mode=TwoWay}", (string?)picker.Attribute("SelectedItem"));
        Assert.Contains(list.Descendants(Wpf + "WrapPanel"), _ => true);
    }
}
