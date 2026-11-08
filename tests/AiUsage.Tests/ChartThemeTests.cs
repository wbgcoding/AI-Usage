using System.Threading;
using System.Windows;
using System.Windows.Media;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

/// <summary>A chart brush nobody set follows the theme resources of its role.</summary>
[Collection(SharedStateTestsCollection.Name)]
public sealed class ChartThemeTests
{
    private static readonly SolidColorBrush Marker = Freeze(Colors.HotPink);
    private static readonly SolidColorBrush Other = Freeze(Colors.Teal);

    private static SolidColorBrush Freeze(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    [Fact]
    public void An_unset_chart_brush_resolves_from_the_theme_resource_of_its_role()
    {
        OnSta(() =>
        {
            var ring = new StatsRingChart();
            ring.Resources["Text.Secondary"] = Marker;
            ring.Resources["Accent"] = Marker;
            ring.Resources["Bg.Popup"] = Marker;
            Assert.Same(Marker, ring.TextBrush);
            Assert.Same(Marker, ring.CenterTextBrush);
            Assert.Same(Marker, ring.FocusBrush);
            Assert.Same(Marker, ring.TooltipBackground);

            var heat = new StatsHeatmap();
            heat.Resources["Track"] = Marker;
            heat.Resources["Accent"] = Marker;
            Assert.Same(Marker, heat.EmptyCellBrush);
            Assert.Same(Marker, heat.AccentBrush);

            var bars = new StatsHorizontalBarChart();
            bars.Resources["Text.Muted"] = Marker;
            bars.Resources["Border"] = Marker;
            Assert.Same(Marker, bars.MutedBrush);
            Assert.Same(Marker, bars.TooltipBorderBrush);

            var month = new StatsMonthGrid();
            month.Resources["Text.Muted"] = Marker;
            Assert.Same(Marker, month.EmptyTextBrush);
            return 0;
        });
    }

    [Fact]
    public void A_brush_set_on_the_chart_wins_over_the_theme()
    {
        OnSta(() =>
        {
            var ring = new StatsRingChart { TextBrush = Other };
            ring.Resources["Text.Secondary"] = Marker;
            Assert.Same(Other, ring.TextBrush);
            return 0;
        });
    }

    [Fact]
    public void Without_any_theme_resource_the_chart_falls_back_to_a_system_brush()
    {
        OnSta(() =>
        {
            var ring = new StatsRingChart();
            Assert.NotNull(ring.TextBrush);
            Assert.NotSame(Brushes.Black, ring.TextBrush);
            Assert.Equal(SystemColors.ControlTextColor, ((SolidColorBrush)ring.TextBrush).Color);
            return 0;
        });
    }

    [Fact]
    public void A_font_size_token_of_the_theme_replaces_the_fixed_size()
    {
        OnSta(() =>
        {
            var bars = new StatsBarChart();
            Assert.Equal(12.0, ChartTheme.FontSize(bars, "Font.Size.Body", 12));
            bars.Resources["Font.Size.Body"] = 15.0;
            Assert.Equal(15.0, ChartTheme.FontSize(bars, "Font.Size.Body", 12));
            return 0;
        });
    }

    private static T OnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "work did not finish");
        if (failure is not null)
            throw new InvalidOperationException("STA work failed.", failure);
        return result!;
    }
}
