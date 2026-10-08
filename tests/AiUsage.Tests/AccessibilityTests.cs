using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Windows.Input;
using AiUsage.Views;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// Every <c>Button</c>/<c>RadioButton</c>/<c>CheckBox</c>/
/// <c>ToggleButton</c>/<c>RepeatButton</c>/<c>Slider</c> needs a non-empty automation name, either
/// explicit (<c>AutomationProperties.Name</c>) or via WPF's own runtime fallback (a plain or bound
/// string <c>Content</c> - <c>Slider</c> has no <c>Content</c> property, so it always needs the
/// explicit one). Implemented as a static scan of the XAML source, the same proven, hang-free
/// technique <see cref="LocalizationParityTests"/> already uses - an actual live visual-tree walk
/// was tried first and dropped: constructing real Window/UserControl instances (even unshown) needs
/// an STA thread plus a bootstrapped <c>Application</c> for style/StaticResource lookups, and that
/// combination reliably left a hung `dotnet test` process behind - a hung test process is a worse
/// outcome than a slightly less exhaustive static check.
///
/// <see cref="RenderedTextContrastTests"/> later revisited exactly this and made it safe: the whole
/// walk runs on one thread the test itself creates, marked background and joined with a timeout, so
/// a stuck call fails the test instead of hanging the process. This static scan stays in place
/// regardless, since it also covers <see cref="SignInWindow"/>, which that later test still leaves
/// out (a real WebView2 control starts a real browser process the moment it is constructed - the
/// likely actual cause of the original hang).
/// </summary>
public class AccessibilityTests
{
    private static readonly string SrcDir = FindSrcDir();

    private static string FindSrcDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        var repoRoot = Path.GetDirectoryName(testsDir)!;
        return Path.Combine(repoRoot, "src", "AiUsage");
    }

    private static readonly string[] NamedControlTags = ["Button", "RadioButton", "CheckBox", "ToggleButton", "RepeatButton", "Slider"];

    // One element's opening tag, attributes included, up to its first unescaped '>' - safe for this
    // codebase because no attribute value here ever contains a literal '>' (Binding/StaticResource
    // markup extensions use '=', never generic-style angle brackets).
    // (?=[\s/>]) rather than \b: a property-element tag like <RadioButton.Style> would otherwise
    // match too (\b matches right before the '.'), being mistaken for a RadioButton instance itself.
    private static readonly Regex ControlTag = new(
        $@"<({string.Join('|', NamedControlTags)})(?=[\s/>])([^>]*?)/?>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex StyleBlock = new(
        @"<Style\s+x:Key=""([^""]+)""[^>]*TargetType=""[^""]*""[\s\S]*?</Style>", RegexOptions.Compiled);

    private static bool StyleNamesItsContent(string styleBody) =>
        Regex.IsMatch(styleBody, @"Setter\s+Property=""(?:AutomationProperties\.Name|Content)""\s+Value=""[^""]+""") ||
        Regex.IsMatch(styleBody, @"AutomationProperties\.Name\s*=\s*""[^""]+""");

    [Fact]
    public void EveryButtonRadioCheckboxToggleAndSliderHasAnAutomationName()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SrcDir, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // Styles that already name their content via a Setter (e.g. ProviderTile.xaml's
            // EyeButtonStyle) apply to every element that references them by key.
            var namedStyleKeys = new HashSet<string>();
            foreach (Match style in StyleBlock.Matches(text))
                if (StyleNamesItsContent(style.Value))
                    namedStyleKeys.Add(style.Groups[1].Value);

            foreach (Match tag in ControlTag.Matches(text))
            {
                var elementType = tag.Groups[1].Value;
                var attrs = tag.Groups[2].Value;

                // A <Style TargetType="Slider"> block itself is not a control instance - only an
                // actual <Slider .../> element counts. ControlTag already only matches instances
                // (it requires "<Slider", never "<Style"), so no extra guard is needed there.
                if (attrs.Contains("AutomationProperties.Name="))
                    continue;
                if (elementType != "Slider" && attrs.Contains("Content="))
                    continue;

                var styleRef = Regex.Match(attrs, @"Style=""\{StaticResource ([^}]+)\}""");
                if (styleRef.Success && namedStyleKeys.Contains(styleRef.Groups[1].Value))
                    continue;

                offenders.Add($"{Path.GetFileName(file)}: <{elementType}{Truncate(attrs)}");
            }
        }

        Assert.True(offenders.Count == 0, "Control(s) without an automation name:\n" + string.Join("\n", offenders));
    }

    private static string Truncate(string attrs) => attrs.Length > 80 ? attrs[..80] + "…" : attrs;

    // Sanity check on the scanner itself, mirroring LocalizationParityTests' own "a silently-empty
    // set would make the real check trivially pass" guard.
    [Fact]
    public void TheScanFindsAtLeastTheKnownControlsSoAnEmptyResultIsNotASilentNoOp()
    {
        var total = 0;
        foreach (var file in Directory.EnumerateFiles(SrcDir, "*.xaml", SearchOption.AllDirectories))
            total += ControlTag.Matches(File.ReadAllText(file)).Count;

        Assert.True(total > 10, $"Expected well over 10 Button/RadioButton/CheckBox/Slider instances across the app, found {total}.");
    }

    // Every window draws its own title bar rather than letting Windows draw a native one; the close
    // button lives inside that shared control and carries its automation name there, so a window
    // that forgot to host the control would silently lose its only keyboard-reachable close button.
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("SettingsWindow.xaml")]
    [InlineData("CrashWindow.xaml")]
    [InlineData("SignInWindow.xaml")]
    public void EveryWindowHostsTheAppsOwnTitleBar(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(SrcDir, "Views", fileName));

        Assert.Contains("controls:TitleBar", text, StringComparison.Ordinal);
        Assert.Contains("WindowStyle=\"None\"", text, StringComparison.Ordinal);
    }

    // The eight resize grips sit along the window's edges. A Thumb with the system's own template
    // draws a light bevel, and eight of those read as a white border around the whole window - the
    // exact complaint this window's chrome was rebuilt to fix.
    [Fact]
    public void TheResizeGripsDrawNothingOfTheirOwn()
    {
        var text = File.ReadAllText(Path.Combine(SrcDir, "Views", "MainWindow.xaml"));
        var grips = Regex.Matches(text, @"<Thumb\b[^>]*>");

        Assert.Equal(8, grips.Count);
        Assert.All(grips, grip => Assert.Contains("Style=\"{StaticResource ResizeGrip}\"", grip.Value, StringComparison.Ordinal));
    }

    // The two grips that also carry the double click resetting the height are the only ones a user
    // has to hit twice in a row on the same pixel row, so they get the same, slightly larger target
    // as each other. The bottom one used to be one pixel shorter than the top one.
    [Fact]
    public void TheTwoHeightGripsAreTheSameHeight()
    {
        var text = File.ReadAllText(Path.Combine(SrcDir, "Views", "MainWindow.xaml"));
        var heights = Regex
            .Matches(text, "<Thumb\\b[^>]*MouseLeftButtonDown=\"ResizeHeightGrip_MouseLeftButtonDown\"[^>]*>")
            .Select(grip => Regex.Match(grip.Value, "Height=\"(\\d+)\"").Groups[1].Value)
            .ToList();

        Assert.Equal(2, heights.Count);
        Assert.Single(heights.Distinct());
    }

    // All eight grips share this one style, so keeping them out of Tab order and off the
    // accessibility keyboard path only needs proving once, here, rather than per element.
    [Fact]
    public void TheResizeGripStyleIsNotKeyboardReachable()
    {
        var text = File.ReadAllText(Path.Combine(SrcDir, "Themes", "Controls.xaml"));
        var style = Regex.Match(text, @"<Style\s+x:Key=""ResizeGrip""[\s\S]*?</Style>").Value;

        Assert.Contains("""<Setter Property="Focusable" Value="False"/>""", style, StringComparison.Ordinal);
        Assert.Contains("""<Setter Property="IsTabStop" Value="False"/>""", style, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTitleBars_close_button_is_named_for_a_screen_reader()
    {
        var text = File.ReadAllText(Path.Combine(SrcDir, "Views", "Controls", "TitleBar.xaml"));
        var closeButton = Regex.Match(text, @"<Button\s+x:Name=""CloseButton""[\s\S]*?/>");

        Assert.True(closeButton.Success, "TitleBar.xaml no longer has a CloseButton.");
        Assert.Contains("AutomationProperties.Name", closeButton.Value, StringComparison.Ordinal);
    }

    // Window-chrome buttons explain themselves by their glyph; a tooltip popping up over close
    // or minimize only gets in the way. Their screen-reader names stay.
    [Fact]
    public void Window_chrome_buttons_carry_no_tooltip()
    {
        var text = File.ReadAllText(Path.Combine(SrcDir, "Views", "Controls", "TitleBar.xaml"));
        foreach (var name in new[] { "CloseButton", "MinimizeButton" })
        {
            var button = Regex.Match(text, $@"<Button\s+x:Name=""{name}""[^>]*>");
            Assert.True(button.Success, $"TitleBar.xaml no longer has a {name}.");
            Assert.DoesNotContain("ToolTip", button.Value, StringComparison.Ordinal);
        }
    }

    // A plain ItemsControl's own generated item peer (ControlType.DataItem) falls back to the
    // bound record's ToString() for its automation name whenever neither the item's own DataObject
    // nor its realized container carries one - AutomationProperties.Name set on the ItemTemplate's
    // root element never reaches that peer (proven live against this exact window: setting it there
    // left the peer's name unchanged). Only a Setter on the generated ContentPresenter
    // (ItemsControl.ItemContainerStyle) does, so every list here either declares its own inline
    // ItemContainerStyle or references a named one that sets AutomationProperties.Name - the same
    // named-style tracking EveryButtonRadioCheckboxToggleAndSliderHasAnAutomationName already uses
    // for Style Setters.
    [Fact]
    public void EveryListItemInStatsWindowHasAnAutomationName()
    {
        var text = File.ReadAllText(Path.Combine(SrcDir, "Views", "StatsWindow.xaml"));

        var namedStyleKeys = new HashSet<string>();
        foreach (Match style in Regex.Matches(text, @"<Style\s+x:Key=""([^""]+)""[^>]*TargetType=""ContentPresenter""[\s\S]*?</Style>"))
            if (style.Value.Contains("AutomationProperties.Name"))
                namedStyleKeys.Add(style.Groups[1].Value);

        // Self-closing (<ItemsControl .../>) and full (<ItemsControl ...>...</ItemsControl>) forms
        // both occur in this file - the day-detail lists are self-closing, ProviderRows and the
        // grouped table are not.
        var items = Regex.Matches(text, @"<ItemsControl\b(?:[^>]*?/>|[^>]*?>[\s\S]*?</ItemsControl>)")
            .Where(m => m.Value.Contains("ItemsSource="))
            .ToList();

        Assert.True(items.Count > 0, "Expected at least one ItemsControl in StatsWindow.xaml.");

        var offenders = new List<string>();
        foreach (var ic in items)
        {
            if (ic.Value.Contains("AutomationProperties.Name"))
                continue;

            var styleRef = Regex.Match(ic.Value, @"ItemContainerStyle=""\{StaticResource ([^}]+)\}""");
            if (styleRef.Success && namedStyleKeys.Contains(styleRef.Groups[1].Value))
                continue;

            offenders.Add(ic.Value.Length > 100 ? ic.Value[..100] + "…" : ic.Value);
        }

        Assert.True(offenders.Count == 0, "List(s) without an automation name for their items:\n" + string.Join("\n", offenders));
    }

    // The sign-in window is the one window that must NOT be layered: it hosts a native browser
    // child, and a layered window cannot compose one, so the page would come up blank.
    [Fact]
    public void TheSignInWindowStaysUnlayeredSoTheBrowserCanDraw()
    {
        var text = File.ReadAllText(Path.Combine(SrcDir, "Views", "SignInWindow.xaml"));
        var withoutComments = Regex.Replace(text, "<!--[\\s\\S]*?-->", "");

        Assert.DoesNotContain("AllowsTransparency", withoutComments, StringComparison.Ordinal);
    }

    // These two plain static predicates are the entire condition behind MainWindow's F5/Ctrl+,
    // shortcuts - calling them needs no Window instance, so this is genuine,
    // hermetic coverage of the shortcut LOGIC. The full event-routing path (a real key press opening
    // the real Settings window) could not be proven end to end: several attempts at synthetic key
    // delivery (F5, Ctrl+,, Esc) had no visible effect on a real window, while every mouse-driven
    // interaction on the same window worked normally - disclosed as a test-tooling gap, not treated
    // as silently proven.
    [Theory]
    [InlineData(Key.F5, true)]
    [InlineData(Key.Enter, false)]
    [InlineData(Key.Escape, false)]
    public void RefreshShortcutIsOnlyF5(Key key, bool expected) =>
        Assert.Equal(expected, MainWindow.IsRefreshShortcut(key));

    [Theory]
    [InlineData(Key.OemComma, ModifierKeys.Control, true)]
    [InlineData(Key.OemComma, ModifierKeys.None, false)]
    [InlineData(Key.OemComma, ModifierKeys.Control | ModifierKeys.Shift, false)]
    [InlineData(Key.OemPeriod, ModifierKeys.Control, false)]
    public void SettingsShortcutIsOnlyCtrlComma(Key key, ModifierKeys modifiers, bool expected) =>
        Assert.Equal(expected, MainWindow.IsSettingsShortcut(key, modifiers));

    // A tooltip that only repeats the text already on screen adds noise. Where the visible text is
    // trimmed the tooltip shows the cut-off rest, so an element that trims its text is exempt.
    [Fact]
    public void No_tooltip_repeats_the_text_it_sits_on()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SrcDir, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var doc = XDocument.Load(file);
            foreach (var element in doc.Descendants())
            {
                var tip = BindingPath(element.Attribute("ToolTip")?.Value);
                if (tip is null)
                    continue;

                var texts = element.DescendantsAndSelf().Where(e => e.Name.LocalName == "TextBlock").ToList();
                if (element.Attribute("TextTrimming") is not null || texts.Any(t => t.Attribute("TextTrimming") is not null))
                    continue;

                var shown = new List<string?>
                {
                    BindingPath(element.Attribute("Text")?.Value),
                    BindingPath(element.Attribute("Content")?.Value),
                };
                shown.AddRange(texts.Select(t => BindingPath(t.Attribute("Text")?.Value)));
                if (shown.Contains(tip))
                    offenders.Add($"{Path.GetFileName(file)}: {element.Name.LocalName} ToolTip={tip}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    // The data path of a plain {Binding Path} on the view model; bindings to another source (the
    // localization lookups) have no comparable path here and read as null.
    private static string? BindingPath(string? value)
    {
        if (value is null || value.Contains("Source=", StringComparison.Ordinal) || value.Contains("RelativeSource", StringComparison.Ordinal))
            return null;

        var match = Regex.Match(value, @"^\{Binding\s+(?:Path=)?(?<path>[^,\s}=]+)\s*[,}]");
        return match.Success ? match.Groups["path"].Value : null;
    }
}
