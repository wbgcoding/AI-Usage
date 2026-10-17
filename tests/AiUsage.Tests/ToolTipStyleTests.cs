using System.Xml.Linq;

namespace AiUsage.Tests;

/// <summary>A tooltip never grows wider than a readable column: its style caps the width and every
/// text inside, including the text a plain string content produces, wraps.</summary>
public class ToolTipStyleTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XElement ToolTipStyle() =>
        XDocument.Load(Path.Combine(RepoRoot.Find(), "src", "AiUsage", "Themes", "Controls.xaml"))
            .Descendants(Wpf + "Style")
            .Single(style => (string?)style.Attribute("TargetType") == "ToolTip");

    [Fact]
    public void The_tooltip_style_caps_the_width()
    {
        var cap = ToolTipStyle().Elements(Wpf + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == "MaxWidth");

        Assert.Equal("420", (string?)cap.Attribute("Value"));
    }

    [Fact]
    public void Text_inside_a_tooltip_wraps()
    {
        var wrapped = ToolTipStyle().Descendants(Wpf + "Style")
            .Where(style => (string?)style.Attribute("TargetType") == "TextBlock")
            .SelectMany(style => style.Elements(Wpf + "Setter"))
            .Any(setter => (string?)setter.Attribute("Property") == "TextWrapping" && (string?)setter.Attribute("Value") == "Wrap");

        Assert.True(wrapped);
    }
}
