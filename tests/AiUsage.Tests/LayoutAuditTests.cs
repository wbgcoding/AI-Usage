using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AiUsage.Views;
using AiUsage.Views.Controls;
using ReadmeShots;

namespace AiUsage.Tests;

/// <summary>The review tool's layout audit on element trees built offscreen with the app's own
/// resources: each check fires on a planted defect and stays silent on the clean version of it.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class LayoutAuditTests
{
    [Fact]
    public void A_clean_themed_surface_has_no_findings()
    {
        var findings = Audit(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Weekly" });
            panel.Children.Add(new TextBlock { Text = "Daily" });
            var grid = new Grid();
            grid.Children.Add(Themed(new Border(), "Bg.Surface"));
            grid.Children.Add(new TextBlock { Text = "Layered in one cell" });
            panel.Children.Add(grid);
            return Surface(panel);
        });

        Assert.Empty(findings);
    }

    [Fact]
    public void A_text_narrower_than_its_content_is_clipped()
    {
        var findings = Audit(() => Surface(new StackPanel
        {
            Children = { new TextBlock { Text = "A label that cannot fit", Width = 30, HorizontalAlignment = HorizontalAlignment.Left } },
        }));

        var finding = Assert.Single(findings);
        Assert.Equal(AuditKind.Clipped, finding.Kind);
        Assert.Contains("\"A label that cannot fit\"", finding.Element);
        Assert.Contains("has 30 px", finding.Detail);
    }

    [Fact]
    public void A_text_a_panel_gives_no_room_at_all_is_hidden_not_clipped()
    {
        var findings = Audit(() => Surface(new FitOrHidePanel
        {
            Children =
            {
                new TextBlock { Text = "A label that fills the whole row and more", TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = "1.82 M tokens" },
            },
        }));

        // Only the first, trimming label: the panel leaves no room for the figure, so it does not show.
        var finding = Assert.Single(findings);
        Assert.StartsWith("TextBlock \"A label that fills", finding.Element);
        Assert.EndsWith(", trimmed", finding.Detail);
    }

    [Fact]
    public void A_caption_with_an_access_key_cut_by_its_container_is_clipped()
    {
        // The row gives its caption all the width it asks for and is itself cut to 60 px, the way a
        // control cuts the caption inside it.
        var findings = Audit(() => Surface(new Border
        {
            Width = 60,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { new AccessText { Text = "_Start with Windows" } } },
        }));

        var finding = Assert.Single(findings);
        Assert.Equal(AuditKind.Clipped, finding.Kind);
        Assert.StartsWith("TextBlock \"Start with Windows\"", finding.Element);
    }

    [Fact]
    public void A_clip_mark_silences_the_text_and_everything_inside()
    {
        var findings = Audit(() =>
        {
            var panel = new StackPanel
            {
                Children = { new TextBlock { Text = "A label that cannot fit", Width = 30, HorizontalAlignment = HorizontalAlignment.Left } },
            };
            AuditMark.SetClip(panel, "test: trimmed on purpose");
            return Surface(panel);
        });

        Assert.Empty(findings);
    }

    [Fact]
    public void Stacked_siblings_pulled_onto_each_other_overlap()
    {
        var findings = Audit(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new Border { Height = 20 });
            panel.Children.Add(new Border { Height = 20, Margin = new Thickness(0, -8, 0, 0) });
            return Surface(panel);
        });

        var finding = Assert.Single(findings);
        Assert.Equal(AuditKind.Overlap, finding.Kind);
        Assert.Contains("by 200 x 8 px in StackPanel", finding.Detail);
    }

    [Fact]
    public void Grid_children_in_different_rows_overlap_but_children_in_one_cell_do_not()
    {
        var findings = Audit(() =>
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            var top = new Border();
            var bottom = new Border { Margin = new Thickness(0, -10, 0, 0) };
            Grid.SetRow(bottom, 1);
            var layeredOnTop = new Border();
            grid.Children.Add(top);
            grid.Children.Add(bottom);
            grid.Children.Add(layeredOnTop);
            return Surface(grid);
        });

        // bottom against top and against the layer sharing top's cell; never top against its layer.
        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(AuditKind.Overlap, finding.Kind));
    }

    [Fact]
    public void Canvas_children_and_marked_siblings_may_overlap()
    {
        var findings = Audit(() =>
        {
            var canvas = new Canvas { Height = 40 };
            canvas.Children.Add(new Border { Width = 50, Height = 20 });
            canvas.Children.Add(new Border { Width = 50, Height = 20 });
            var panel = new StackPanel();
            panel.Children.Add(canvas);
            panel.Children.Add(new Border { Height = 20 });
            var badge = new Border { Height = 20, Margin = new Thickness(0, -8, 0, 0) };
            AuditMark.SetOverlap(badge, "test: sits on the row above on purpose");
            panel.Children.Add(badge);
            return Surface(panel);
        });

        Assert.Empty(findings);
    }

    [Fact]
    public void A_text_with_no_themed_ancestor_has_the_system_default_foreground()
    {
        var findings = Audit(() =>
        {
            var root = new Border { Width = 200 };
            root.SetResourceReference(Border.BackgroundProperty, "Bg.Surface");
            root.Child = new TextBlock { Text = "Unthemed" };
            return root;
        });

        var finding = Assert.Single(findings);
        Assert.Equal(AuditKind.OffTheme, finding.Kind);
        Assert.StartsWith("Foreground #FF000000 (system default)", finding.Detail);
    }

    [Fact]
    public void A_stock_control_without_an_app_style_is_off_theme()
    {
        var findings = Audit(() => Surface(new StackPanel { Children = { new Button { Content = "OK" } } }));

        Assert.NotEmpty(findings);
        Assert.All(findings, finding => Assert.Equal(AuditKind.OffTheme, finding.Kind));
        // The stock template paints the stock grey, and the caption keeps the stock black text.
        Assert.Contains(findings, finding => finding.Element.StartsWith("Border#border in Button") && finding.Detail == "Background #FFDDDDDD (not a theme colour)");
        Assert.Contains(findings, finding => finding.Element.StartsWith("TextBlock \"OK\"") && finding.Detail == "Foreground #FF000000 (system default from Button)");
    }

    [Fact]
    public void A_literal_colour_is_off_theme_and_a_theme_colour_at_any_opacity_is_not()
    {
        var findings = Audit(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new Border { Height = 10, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56)) });
            var accent = ((SolidColorBrush)Application.Current.FindResource("Accent")).Color;
            panel.Children.Add(new Border { Height = 10, Background = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)) });
            return Surface(panel);
        });

        var finding = Assert.Single(findings);
        Assert.Equal(AuditKind.OffTheme, finding.Kind);
        Assert.Equal("Background #FF123456 (not a theme colour)", finding.Detail);
    }

    [Fact]
    public void An_off_theme_mark_silences_the_element_and_everything_inside()
    {
        var findings = Audit(() =>
        {
            var panel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56)) };
            panel.Children.Add(new Button { Content = "OK" });
            AuditMark.SetOffTheme(panel, "test: a brand colour on purpose");
            return Surface(panel);
        });

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("widget.full-Dark-de-min.png", "widget.full", "min", "de", "Dark")]
    [InlineData("stats.window-HighContrast-pt-BR-default.png", "stats.window", "default", "pt-BR", "HighContrast")]
    [InlineData("settings.general-Light-zh-Hans-default.png", "settings.general", "default", "zh-Hans", "Light")]
    [InlineData("settings.system-moving-Nebula-pt-BR-min.png", "settings.system-moving", "min", "pt-BR", "Nebula")]
    public void A_line_names_surface_size_locale_and_theme_from_the_picture(string file, string surface, string size, string locale, string theme)
    {
        var picture = LayoutAudit.Picture.FromFile(@"C:\out\" + file, ["Dark", "Light", "Nebula", "Terminal", "HighContrast"]);
        var line = LayoutAudit.Format(picture, new AuditFinding(AuditKind.Overlap, "Border", "overlaps Border"));

        Assert.Equal($"OVERLAP | {surface} | {size} | {locale} | {theme} | Border | overlaps Border", line);
        Assert.Equal(surface.StartsWith("settings."), picture.IsSettings);
    }

    [Fact]
    public void A_setting_without_any_tooltip_is_reported_on_settings_surfaces_only()
    {
        StackPanel Build() => new()
        {
            Children = { new TextBlock { Text = "Refresh" }, new ComboBox() },
        };

        var onSettings = Audit(() => Surface(Build()), settings: true);
        var elsewhere = Audit(() => Surface(Build()));

        var finding = Assert.Single(onSettings);
        Assert.Equal(AuditKind.NoTooltip, finding.Kind);
        Assert.Equal("ComboBox for \"Refresh\"", finding.Element);
        Assert.Empty(elsewhere);
    }

    [Fact]
    public void A_tooltip_on_the_setting_its_label_or_its_row_counts()
    {
        var findings = Audit(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new CheckBox { Content = "Start with Windows", ToolTip = "Opens the widget at sign-in." });
            panel.Children.Add(new StackPanel
            {
                Children = { new TextBlock { Text = "Layout", ToolTip = "How the tiles are arranged." }, new ComboBox() },
            });
            panel.Children.Add(new StackPanel
            {
                ToolTip = "How often the numbers are read.",
                Children = { new TextBlock { Text = "Refresh" }, new ComboBox() },
            });
            var marked = new CheckBox { Content = "Beta" };
            AuditMark.SetTooltip(marked, "test: explained by the page heading");
            panel.Children.Add(marked);
            return Surface(panel);
        }, settings: true);

        Assert.Empty(findings);
    }

    [Fact]
    public void An_empty_or_disabled_tooltip_does_not_count()
    {
        var findings = Audit(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new CheckBox { Content = "Sounds", ToolTip = "" });
            var disabled = new CheckBox { Content = "Badges", ToolTip = "Shows a badge." };
            ToolTipService.SetIsEnabled(disabled, false);
            panel.Children.Add(disabled);
            return Surface(panel);
        }, settings: true);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(AuditKind.NoTooltip, finding.Kind));
    }

    [Fact]
    public void A_read_only_text_box_is_no_setting()
    {
        var findings = Audit(() => Surface(new StackPanel
        {
            Children = { new TextBox { Text = "https://example.org/archive", IsReadOnly = true } },
        }), settings: true);

        Assert.Empty(findings);
    }

    [Fact]
    public void A_label_longer_than_forty_characters_is_reported_and_a_short_one_is_not()
    {
        const string sentence = "Show a notification when a limit is almost used up";
        var findings = Audit(() =>
        {
            var panel = new StackPanel { Width = 600 };
            panel.Children.Add(new CheckBox { Content = sentence, ToolTip = "Warns before the limit." });
            panel.Children.Add(new CheckBox { Content = "Warn near a limit", ToolTip = "Warns before the limit." });
            var marked = new CheckBox { Content = sentence + " (marked)", ToolTip = "Warns before the limit." };
            AuditMark.SetLabel(marked, "test: a legal notice that must be read whole");
            panel.Children.Add(marked);
            var root = Surface(panel);
            root.Width = 600;
            return root;
        }, settings: true);

        var finding = Assert.Single(findings);
        Assert.Equal(AuditKind.LongLabel, finding.Kind);
        Assert.StartsWith("TextBlock \"" + sentence[..40], finding.Element);
        Assert.Equal($"{sentence.Length} characters, at most 40", finding.Detail);
    }

    /// <summary>A root that draws the theme surface and hands the theme text colour down, the way
    /// every window of the app does.</summary>
    private static Border Surface(UIElement child)
    {
        var root = Themed(new Border { Width = 200, Child = child }, "Bg.Surface");
        root.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        return root;
    }

    private static Border Themed(Border border, string backgroundKey)
    {
        border.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        return border;
    }

    private static IReadOnlyList<AuditFinding> Audit(Func<FrameworkElement> build, bool settings = false) =>
        StaTestRunner.Run(() =>
        {
            var root = build();
            root.Measure(new Size(root.Width, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();
            return LayoutAudit.Run(root, settings);
        });
}
