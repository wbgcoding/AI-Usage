using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using AiUsage.Services;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Keyboard focus is visible on every focusable button, check box and radio button style, the
/// window chrome buttons are named for a screen reader, and the other accessibility fixes of the same
/// pass. The XAML is read as text or XML (no window needed).</summary>
[Collection(SharedStateTestsCollection.Name)]
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
    [InlineData("Views", "SettingsWindow.xaml")]
    public void Every_focusable_button_style_draws_its_ring_as_a_focus_visual_style_not_a_trigger(params string[] parts)
    {
        var styles = FocusableStyles(Src(parts)).ToList();

        foreach (var style in styles)
        {
            var hasTrigger = style.Descendants(Wpf + "Trigger")
                .Any(trigger => (string?)trigger.Attribute("Property") == "IsKeyboardFocused");
            var visual = style.Elements(Wpf + "Setter")
                .SingleOrDefault(setter => (string?)setter.Attribute("Property") == "FocusVisualStyle");
            Assert.False(hasTrigger, $"{Name(style)} still draws its ring from an IsKeyboardFocused trigger");
            Assert.True(visual is not null && ((string?)visual.Attribute("Value") ?? "").Contains("FocusRing.", StringComparison.Ordinal),
                $"{Name(style)} has no FocusRing focus visual style");
        }
    }

    [Fact]
    public void Only_text_inputs_keep_an_IsKeyboardFocused_trigger()
    {
        var offenders = Directory.EnumerateFiles(Src(), "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => XDocument.Load(file).Descendants(Wpf + "Trigger")
                .Where(trigger => (string?)trigger.Attribute("Property") == "IsKeyboardFocused")
                .Select(trigger => (File: Path.GetFileName(file), Target: (string?)trigger.Ancestors(Wpf + "Style").First().Attribute("TargetType"))))
            .Where(hit => hit.Target != "TextBox")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_check_box_and_radio_rings_sit_outside_the_glyph_so_a_checked_one_still_shows_it()
    {
        var doc = XDocument.Load(Src("Themes", "Controls.xaml"));
        foreach (var key in new[] { "FocusRing.CheckBox", "FocusRing.RadioButton" })
        {
            var style = doc.Descendants(Wpf + "Style").Single(s => (string?)s.Attribute(X + "Key") == key);
            var ring = style.Descendants(Wpf + "ControlTemplate").Single().Elements().Single();
            Assert.Equal("-3,0,0,0", (string?)ring.Attribute("Margin"));
            Assert.Equal("22", (string?)ring.Attribute("Width"));
            Assert.Equal("22", (string?)ring.Attribute("Height"));
        }
    }

    [Fact]
    public void The_focus_visual_styles_of_the_controls_build_a_ring_in_the_theme_accent()
    {
        var rings = StaTestRunner.Run(() =>
        {
            ThemeService.Apply(AppTheme.Dark, Application.Current!.Resources.MergedDictionaries,
                uri => new ResourceDictionary { Source = new Uri("pack://application:,,,/AI-Usage;component/" + uri.OriginalString, UriKind.Absolute) },
                () => false);
            var owners = new object[] { typeof(CheckBox), typeof(RadioButton), typeof(Slider), typeof(ComboBox), "AiUsageButton" };
            return owners.Select(key =>
            {
                var owner = (Style)Application.Current.FindResource(key);
                var visual = owner.Setters.OfType<Setter>().Single(setter => setter.Property == FrameworkElement.FocusVisualStyleProperty);
                var ring = new Control { Style = (Style)visual.Value };
                ring.Measure(new Size(30, 30));
                ring.ApplyTemplate();
                var root = (FrameworkElement)VisualTreeHelper.GetChild(ring, 0);
                var brush = root switch
                {
                    Border border => border.BorderBrush,
                    System.Windows.Shapes.Shape shape => shape.Stroke,
                    _ => null,
                };
                return (Name: key.ToString()!, Accent: (brush as SolidColorBrush)?.Color);
            }).ToList();
        });

        Assert.Equal(5, rings.Count);
        Assert.All(rings, ring => Assert.True(ring.Accent is not null, $"{ring.Name} focus ring has no accent brush"));
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
