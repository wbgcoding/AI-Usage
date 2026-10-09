using System.Threading;
using System.Windows.Media;
using AiUsage.Views.Controls;

namespace AiUsage.Tests;

public class ChartFontsTests
{
    // WPF objects are thread-affine, so each case builds and reads them on one STA thread.
    private static T OnSta<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                result = work();
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
            throw new InvalidOperationException("STA work failed.", failure);
        return result!;
    }

    [Fact]
    public void Ui_returns_the_theme_font_when_the_element_can_reach_it()
    {
        var source = OnSta(() =>
        {
            var chart = new StatsBarChart();
            chart.Resources["Font.Ui"] = new FontFamily("Cascadia Mono, Consolas");
            return ChartFonts.Ui(chart).Source;
        });
        Assert.Equal("Cascadia Mono, Consolas", source);
    }

    [Fact]
    public void Ui_always_yields_a_font_even_without_a_theme_resource()
    {
        // The result is Segoe UI, or the app's theme font when an earlier test left the app
        // resources loaded; either way never empty.
        var source = OnSta(() => ChartFonts.UiTypeface(new StatsRingChart()).FontFamily.Source);
        Assert.False(string.IsNullOrWhiteSpace(source));
    }
}
