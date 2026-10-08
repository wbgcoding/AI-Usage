using AiUsage.Views;

namespace AiUsage.Tests;

public class MainWindowReplaceAfterDragTests
{
    [Fact]
    public void A_request_without_a_drag_runs_at_once()
    {
        var gate = new MainWindow.ReplaceAfterDrag();

        Assert.True(gate.Request());
        Assert.False(gate.DragEnded());
    }

    [Fact]
    public void A_request_during_a_drag_waits_for_the_drop_and_runs_once()
    {
        var gate = new MainWindow.ReplaceAfterDrag();
        gate.DragStarted();

        Assert.False(gate.Request());
        Assert.False(gate.Request());
        Assert.True(gate.DragEnded());
        Assert.False(gate.DragEnded());
        Assert.True(gate.Request());
    }

    [Fact]
    public void A_drag_without_a_request_asks_for_nothing_at_its_end()
    {
        var gate = new MainWindow.ReplaceAfterDrag();
        gate.DragStarted();

        Assert.False(gate.DragEnded());
    }
}
