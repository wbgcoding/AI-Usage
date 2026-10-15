using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using AiUsage.Views.Controls;

namespace AiUsage.Tests;

/// <summary>The title bar buttons draw their focus ring only for keyboard focus and re-test the hover
/// state against the real cursor when the window comes back from minimized or hidden.</summary>
[Collection(SharedStateTestsCollection.Name)]
public class TitleBarFocusTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Src(params string[] parts) => Path.Combine([RepoRoot.Find(), "src", "AiUsage", .. parts]);

    [Fact]
    public void The_button_style_has_a_focus_visual_style_and_no_keyboard_focus_trigger()
    {
        var style = XDocument.Load(Src("Views", "Controls", "TitleBar.xaml")).Descendants(Wpf + "Style")
            .Single(s => (string?)s.Attribute(X + "Key") == "TitleBarButton");

        Assert.DoesNotContain(style.Descendants(Wpf + "Trigger"), t => (string?)t.Attribute("Property") == "IsKeyboardFocused");
        var visual = style.Elements(Wpf + "Setter").Single(s => (string?)s.Attribute("Property") == "FocusVisualStyle");
        Assert.Contains("FocusRing.TitleBar", (string?)visual.Attribute("Value"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_title_bar_focus_ring_keeps_the_old_inset_radius_and_accent()
    {
        var style = XDocument.Load(Src("Themes", "Controls.xaml")).Descendants(Wpf + "Style")
            .Single(s => (string?)s.Attribute(X + "Key") == "FocusRing.TitleBar");
        var ring = style.Descendants(Wpf + "Border").Single();

        Assert.Equal("4", (string?)ring.Attribute("Margin"));
        Assert.Equal("1", (string?)ring.Attribute("BorderThickness"));
        Assert.Contains("Radius.Control", (string?)ring.Attribute("CornerRadius"), StringComparison.Ordinal);
        Assert.Contains("Accent", (string?)ring.Attribute("BorderBrush"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_bar_re_syncs_the_mouse_when_its_window_activates_until_it_detaches()
    {
        var (watching, queuedWhileAttached, syncsOnRun, watchingAfterDetach, queuedAfterDetach) = StaTestRunner.Run(() =>
        {
            var syncs = 0;
            var queue = new List<Action>();
            var bar = new TitleBar { SynchronizeMouse = () => syncs++, PostAtInputPriority = queue.Add };
            var window = new Window();
            bar.AttachToWindow(window);
            var watchingWhileAttached = bar.IsWatchingWindow;

            Activate(window);
            var queued = queue.Count;
            queue.ForEach(work => work());
            var ran = syncs;

            bar.DetachFromWindow();
            var watchingAfter = bar.IsWatchingWindow;
            queue.Clear();
            Activate(window);
            return (watchingWhileAttached, queued, ran, watchingAfter, queue.Count);
        });

        Assert.True(watching);
        Assert.Equal(1, queuedWhileAttached);
        Assert.Equal(1, syncsOnRun);
        Assert.False(watchingAfterDetach);
        Assert.Equal(0, queuedAfterDetach);
    }

    private static void Activate(Window window) =>
        typeof(Window).GetMethod("OnActivated", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, [EventArgs.Empty]);
}
