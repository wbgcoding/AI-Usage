using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Xml.Linq;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Keyboard focus is visible on every focusable button, check box and radio button style, the
/// window chrome buttons are named for a screen reader, and the other accessibility fixes of the same
/// pass. The XAML is read as text or XML (no window needed).</summary>
public class KeyboardFocusTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Src(params string[] parts) => Path.Combine([RepoRoot.Find(), "src", "AiUsage", .. parts]);

    private static IEnumerable<XElement> FocusableStyles(string file)
    {
        var doc = XDocument.Load(file);
        var controlTypes = new[] { "Button", "CheckBox", "RadioButton" };
        return doc.Descendants(Wpf + "Style")
            .Where(style => controlTypes.Contains((string?)style.Attribute("TargetType")))
            .Where(style => style.Attribute("BasedOn") is null)
            .Where(style => !style.Elements(Wpf + "Setter").Any(setter =>
                (string?)setter.Attribute("Property") == "Focusable" && (string?)setter.Attribute("Value") == "False"));
    }

    private static string Name(XElement style) => (string?)style.Attribute(X + "Key") ?? (string?)style.Attribute("TargetType") ?? "?";

    [Theory]
    [InlineData("Themes", "Controls.xaml")]
    [InlineData("Views", "Controls", "TitleBar.xaml")]
    [InlineData("Views", "Controls", "CollapsibleSection.xaml")]
    [InlineData("Views", "Controls", "ProviderTile.xaml")]
    public void Every_focusable_button_style_shows_a_focus_ring_and_drops_the_default_dotted_one(params string[] parts)
    {
        var styles = FocusableStyles(Src(parts)).ToList();

        foreach (var style in styles)
        {
            var hasTrigger = style.Descendants(Wpf + "Trigger")
                .Any(trigger => (string?)trigger.Attribute("Property") == "IsKeyboardFocused");
            var dropsDefault = style.Elements(Wpf + "Setter").Any(setter =>
                (string?)setter.Attribute("Property") == "FocusVisualStyle" && (string?)setter.Attribute("Value") == "{x:Null}");
            Assert.True(hasTrigger, $"{Name(style)} has no IsKeyboardFocused trigger");
            Assert.True(dropsDefault, $"{Name(style)} keeps the default focus rectangle");
        }
    }

    [Fact]
    public void The_check_box_and_radio_rings_sit_outside_the_glyph_so_a_checked_one_still_shows_it()
    {
        var doc = XDocument.Load(Src("Themes", "Controls.xaml"));
        foreach (var type in new[] { "CheckBox", "RadioButton" })
        {
            var style = doc.Descendants(Wpf + "Style").Single(s => (string?)s.Attribute("TargetType") == type);
            var ring = style.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "Focus");
            Assert.Equal("-3", (string?)ring.Attribute("Margin"));
        }
    }

    [Fact]
    public void The_window_chrome_buttons_are_named_for_a_screen_reader_and_carry_no_tooltip()
    {
        var doc = XDocument.Load(Src("Views", "Controls", "TitleBar.xaml"));
        foreach (var name in new[] { "MinimizeButton", "CloseButton" })
        {
            var button = doc.Descendants(Wpf + "Button").Single(b => (string?)b.Attribute(X + "Name") == name);
            Assert.Contains(name == "MinimizeButton" ? "TitleBar.Minimize" : "TitleBar.Close", (string?)button.Attribute("AutomationProperties.Name"));
            Assert.Null(button.Attribute("ToolTip"));
            Assert.Null(button.Element(Wpf + "Button.ToolTip"));
        }
    }

    [Fact]
    public void The_two_quiet_hours_boxes_have_their_own_names()
    {
        var doc = XDocument.Load(Src("Views", "SettingsWindow.xaml"));
        string NameOf(string binding) => (string)doc.Descendants(Wpf + "TextBox")
            .Single(box => ((string?)box.Attribute("Text") ?? "").StartsWith("{Binding " + binding, StringComparison.Ordinal))
            .Attribute("AutomationProperties.Name")!;

        Assert.Contains("[Settings.QuietHours.From]", NameOf("QuietHoursStartText"));
        Assert.Contains("[Settings.QuietHours.Until]", NameOf("QuietHoursEndText"));
        Assert.NotEqual(LocalizationKeyValue("Settings.QuietHours.From"), LocalizationKeyValue("Settings.QuietHours.Until"));
    }

    private static string LocalizationKeyValue(string key) =>
        XDocument.Load(Src("Resources", "Strings.resx")).Descendants("data")
            .Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;

    [Fact]
    public void The_scroll_bar_track_pages_on_a_click_in_both_directions()
    {
        var xaml = File.ReadAllText(Src("Themes", "Controls.xaml"));

        foreach (var command in new[] { "PageUpCommand", "PageDownCommand", "PageLeftCommand", "PageRightCommand" })
            Assert.Contains($"ScrollBar.{command}", xaml);
    }

    [Fact]
    public void The_indeterminate_work_bar_keeps_its_track_and_sweeps_by_fractions_of_its_width()
    {
        var xaml = File.ReadAllText(Src("Views", "ConfirmWindow.xaml"));

        Assert.Contains("TargetName=\"PART_Indicator\" Property=\"Visibility\" Value=\"Hidden\"", xaml);
        Assert.DoesNotContain("TargetName=\"PART_Track\" Property=\"Visibility\"", xaml);
        Assert.DoesNotContain("To=\"320\"", xaml);
    }

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null)
            throw new InvalidOperationException("failed on the UI thread", failure);
        return result;
    }

    [Fact]
    public void A_collapsible_section_exposes_its_state_through_the_expand_collapse_pattern()
    {
        var states = OnSta(() =>
        {
            var section = new CollapsibleSection { IsExpanded = true };
            var peer = UIElementAutomationPeer.CreatePeerForElement(section);
            var provider = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse)!;
            var first = provider.ExpandCollapseState;
            provider.Collapse();
            var second = (provider.ExpandCollapseState, section.IsExpanded);
            provider.Expand();
            return (first, second, provider.ExpandCollapseState);
        });

        Assert.Equal(ExpandCollapseState.Expanded, states.first);
        Assert.Equal((ExpandCollapseState.Collapsed, false), states.second);
        Assert.Equal(ExpandCollapseState.Expanded, states.Item3);
    }
}
