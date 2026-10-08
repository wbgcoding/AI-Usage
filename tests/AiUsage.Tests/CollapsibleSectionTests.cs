using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsage.Services;
using AiUsage.Views.Controls;
using Xunit;
using Path = System.Windows.Shapes.Path;

namespace AiUsage.Tests;

/// <summary>
/// Builds the real <see cref="CollapsibleSection"/> off screen (same STA-thread construction
/// <see cref="StatsWindowTests"/> already documents, never shown) and proves its own acceptance:
/// <see cref="CollapsibleSection.IsExpanded"/> false hides the content and shows the
/// summary, true the reverse; the chevron follows the same state; and the header's own accessible
/// name carries the title. Every WPF object the worker thread touches is read into a plain record
/// before the thread ends - the same reasoning <see cref="AboutWindowTests"/> documents for why (WPF
/// objects are thread-affine).
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class CollapsibleSectionTests
{
    private sealed record State(
        bool ContentVisible, bool SummaryVisible, bool ChevronPointsUp, string AutomationName,
        object? RootToolTip, object? HeadingToolTip, PlacementMode HeadingToolTipPlacement, string HelpText);

    [Fact]
    public void Expanded_shows_the_content_and_hides_the_summary_with_the_chevron_pointing_up()
    {
        var state = Build(isExpanded: true);

        Assert.True(state.ContentVisible);
        Assert.False(state.SummaryVisible);
        Assert.True(state.ChevronPointsUp);
    }

    [Fact]
    public void Collapsed_hides_the_content_and_shows_the_summary_with_the_chevron_pointing_down()
    {
        var state = Build(isExpanded: false);

        Assert.False(state.ContentVisible);
        Assert.True(state.SummaryVisible);
        Assert.False(state.ChevronPointsUp);
    }

    [Fact]
    public void The_header_button_carries_the_title_as_its_accessible_name()
    {
        var state = Build(isExpanded: true);

        Assert.Equal("Per day", state.AutomationName);
    }

    /// <summary>The bug this covers: a ToolTip set on the whole <see cref="CollapsibleSection"/>
    /// used to cover its entire card (header AND content), opening no matter where the pointer
    /// hovered - <see cref="CollapsibleSection.HeaderToolTip"/> now reaches only the heading text
    /// itself, directly below it, while the root carries no <c>FrameworkElement.ToolTip</c> at all;
    /// <see cref="AutomationProperties.HelpText"/> on the control keeps the same text reachable for
    /// a screen reader regardless of where the mouse-only tooltip lives.</summary>
    [Fact]
    public void The_tooltip_lives_on_the_heading_text_alone_never_on_the_whole_card()
    {
        var state = Build(isExpanded: true, headerToolTip: "What this section shows");

        Assert.Null(state.RootToolTip);
        Assert.Equal("What this section shows", state.HeadingToolTip);
        Assert.Equal(PlacementMode.Bottom, state.HeadingToolTipPlacement);
        Assert.Equal("What this section shows", state.HelpText);
    }

    [Fact]
    public void An_empty_header_tool_tip_shows_no_tooltip_at_all()
    {
        var state = Build(isExpanded: true, headerToolTip: "");

        Assert.Null(state.HeadingToolTip);
    }

    // The summary is the part that gives way when the header is narrow: the title keeps its width and
    // the summary ends in an ellipsis instead of pushing the title out of the header.
    [Fact]
    public void A_narrow_collapsed_header_keeps_the_title_and_trims_the_summary()
    {
        Exception? failure = null;
        (double TitleActual, double TitleNatural, double TitleRight, double SummaryRight, double SummaryActual, double SummaryNatural) widths = default;
        var worker = new Thread(() =>
        {
            try
            {
                widths = MeasureNarrowHeader();
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
        Assert.Null(failure);

        Assert.True(widths.TitleActual > 0);
        Assert.Equal(widths.TitleNatural, widths.TitleActual, 1);
        Assert.True(widths.SummaryActual > 0);
        Assert.True(widths.SummaryActual < widths.SummaryNatural);
        Assert.True(widths.SummaryRight <= 300.5, $"summary ends at {widths.SummaryRight}");
    }

    private static (double, double, double, double, double, double) MeasureNarrowHeader()
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();
        try
        {
            ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);
            Application.Current!.Resources.MergedDictionaries.Add(LoadFromProductionAssembly(new Uri("Views/Controls/CollapsibleSection.xaml", UriKind.Relative)));

            const string title = "Aufschlüsselung";
            const string summary = "Claude 1,2 Mio. Tokens, Codex 800 Tsd. Tokens, Gemini 300 Tsd. Tokens";
            var section = new CollapsibleSection { Title = title, Summary = summary, IsExpanded = false, Content = new TextBlock { Text = "body" } };
            section.ApplyTemplate();
            section.Measure(new Size(300, 400));
            section.Arrange(new Rect(0, 0, 300, 400));
            section.UpdateLayout();

            var heading = FindTextBlock(section, title) ?? throw new InvalidOperationException("heading not found");
            var summaryText = FindTextBlock(section, summary) ?? throw new InvalidOperationException("summary not found");
            var headingActual = heading.ActualWidth;
            var summaryActual = summaryText.ActualWidth;
            var headingRight = heading.TranslatePoint(new Point(headingActual, 0), section).X;
            var summaryRight = summaryText.TranslatePoint(new Point(summaryActual, 0), section).X;
            heading.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var headingNatural = heading.DesiredSize.Width;
            summaryText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return (headingActual, headingNatural, headingRight, summaryRight, summaryActual, summaryText.DesiredSize.Width);
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            ResetApplicationCurrent();
        }
    }

    // Builds a fresh Application and CollapsibleSection per call, off the calling thread - the same
    // STA-worker-thread shape StatsWindowTests already documents.
    private static State Build(bool isExpanded, string headerToolTip = "")
    {
        Exception? failure = null;
        State? result = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = BuildAndRead(isExpanded, headerToolTip);
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

        if (!worker.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("CollapsibleSection build did not finish");
        if (failure is not null)
            throw new InvalidOperationException("CollapsibleSection build failed.", failure);

        return result!;
    }

    private static State BuildAndRead(bool isExpanded, string headerToolTip)
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);

        var app = new AiUsage.App();
        app.InitializeComponent();

        try
        {
            ThemeService.Apply(AppTheme.Nebula, Application.Current!.Resources.MergedDictionaries, LoadFromProductionAssembly, () => false);
            // CollapsibleSection is a lookless control - its own Style/ControlTemplate lives in a
            // plain ResourceDictionary (see CollapsibleSection.xaml.cs), merged by StatsWindow.xaml
            // in production; a bare instance needs the same merge to get a visual tree at all.
            Application.Current!.Resources.MergedDictionaries.Add(LoadFromProductionAssembly(new Uri("Views/Controls/CollapsibleSection.xaml", UriKind.Relative)));

            var section = new CollapsibleSection
            {
                Title = "Per day",
                Summary = "1.2M tokens",
                IsExpanded = isExpanded,
                HeaderToolTip = headerToolTip,
                Content = new TextBlock { Text = "section body" },
            };

            section.ApplyTemplate();
            section.Measure(new Size(400, 400));
            section.Arrange(new Rect(0, 0, 400, 400));
            section.UpdateLayout();

            // Located by template part name, not by walking the visual tree for the first match of a
            // type: the header Button's own template presents its Grid content through a
            // ContentPresenter of its own, so a plain type search would find that one instead of the
            // section's own PART_ContentHost.
            var contentPresenter = (ContentPresenter)section.Template.FindName("PART_ContentHost", section)!;
            var headerButton = (Button)section.Template.FindName("PART_HeaderButton", section)!;
            var chevron = FindFirst<Path>(section) ?? throw new InvalidOperationException("chevron not found");
            var summaryText = FindTextBlock(section, "1.2M tokens") ?? throw new InvalidOperationException("summary text not found");
            var headingText = FindTextBlock(section, "Per day") ?? throw new InvalidOperationException("heading text not found");

            var chevronUp = (Geometry)Application.Current!.FindResource("Icon.ChevronUp");
            var chevronPointsUp = ReferenceEquals(chevron.Data, chevronUp)
                || chevron.Data.ToString(CultureInfo.InvariantCulture) == chevronUp.ToString(CultureInfo.InvariantCulture);

            return new State(
                contentPresenter.Visibility == Visibility.Visible,
                summaryText.Visibility == Visibility.Visible,
                chevronPointsUp,
                AutomationProperties.GetName(headerButton),
                section.ToolTip,
                headingText.ToolTip,
                ToolTipService.GetPlacement(headingText),
                AutomationProperties.GetHelpText(section));
        }
        finally
        {
            Application.Current?.Shutdown();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            ResetApplicationCurrent();
        }
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            if (FindFirst<T>(child) is { } found)
                return found;
        }
        return null;
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock textBlock && textBlock.Text == text)
                return textBlock;

            if (FindTextBlock(child, text) is { } found)
                return found;
        }
        return null;
    }

    private static ResourceDictionary LoadFromProductionAssembly(Uri relativeUri) =>
        new() { Source = new Uri("pack://application:,,,/AI-Usage;component/" + relativeUri.OriginalString, UriKind.Absolute) };

    private static void ResetApplicationCurrent() =>
        typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
}
