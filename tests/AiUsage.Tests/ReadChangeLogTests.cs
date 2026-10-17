using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>The shared change line: one WARN per distinct note and provider, in one fixed shape.</summary>
public class ReadChangeLogTests
{
    [Fact]
    public void A_note_becomes_one_line_with_the_fixed_prefix()
    {
        using var capture = ReadChangeLog.BeginCapture();

        ReadChangeLog.Report("Example", "the answer names an unknown field 'foo'.");

        var line = Assert.Single(capture.Lines);
        Assert.Equal(
            "CHANGE DETECTED (Example usage read needs adapting): the answer names an unknown field 'foo'.", line);
    }

    [Fact]
    public void The_same_note_is_reported_once_per_run()
    {
        using var capture = ReadChangeLog.BeginCapture();

        ReadChangeLog.Report("Example", "same");
        ReadChangeLog.Report("Example", "same");
        ReadChangeLog.Report("Example", "other");
        ReadChangeLog.Report("Another", "same");

        Assert.Equal(3, capture.Lines.Count);
    }

    [Fact]
    public async Task Concurrent_reports_of_one_note_give_one_line()
    {
        using var capture = ReadChangeLog.BeginCapture();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ReadChangeLog.Report("Example", "race"))));

        Assert.Single(capture.Lines);
    }

    [Fact]
    public async Task A_capture_follows_its_flow_into_started_tasks()
    {
        using var capture = ReadChangeLog.BeginCapture();
        await Task.Run(() => ReadChangeLog.Report("Example", "inside"));

        Assert.Single(capture.Lines);
    }
}
