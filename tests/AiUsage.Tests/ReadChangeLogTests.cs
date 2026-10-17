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

    [Fact]
    public void A_read_scope_sees_a_write_also_when_the_line_was_already_logged()
    {
        using var capture = ReadChangeLog.BeginCapture();
        ReadChangeLog.Report("Example", "again");

        using var scope = ReadChangeLog.BeginReadScope();
        Assert.False(scope.Noted);
        ReadChangeLog.Report("Example", "again");

        Assert.True(scope.Noted);
        Assert.Single(capture.Lines);
    }

    [Fact]
    public void A_general_note_a_provider_note_already_covers_still_counts_for_the_scope()
    {
        using var capture = ReadChangeLog.BeginCapture();
        ReadChangeLog.Report("Example", "specific");

        using var scope = ReadChangeLog.BeginReadScope();
        ReadChangeLog.ReportGeneral("Example", "general");

        Assert.True(scope.Noted);
    }

    [Fact]
    public void Without_a_read_scope_a_write_breaks_nothing()
    {
        using var capture = ReadChangeLog.BeginCapture();

        ReadChangeLog.Report("Example", "no scope");

        Assert.Single(capture.Lines);
    }

    [Fact]
    public async Task A_read_scope_follows_its_flow_into_started_tasks_and_stays_clean_without_a_write()
    {
        using var scope = ReadChangeLog.BeginReadScope();
        await Task.Run(() => { });
        Assert.False(scope.Noted);

        await Task.Run(() => ReadChangeLog.Report("Example", "inside a task"));

        Assert.True(scope.Noted);
    }

    [Fact]
    public async Task Parallel_flows_with_their_own_scopes_do_not_leak_into_each_other()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            using var scope = ReadChangeLog.BeginReadScope();
            await Task.Yield();
            if (i % 2 == 0)
                ReadChangeLog.Report("Example", "flow " + i);
            await Task.Yield();
            return scope.Noted;
        })));

        Assert.Equal([true, false, true, false, true, false, true, false], results);
    }

    [Fact]
    public void An_inner_scope_gives_the_outer_one_back_when_it_ends()
    {
        using var outer = ReadChangeLog.BeginReadScope();
        using (var inner = ReadChangeLog.BeginReadScope())
        {
            ReadChangeLog.Report("Example", "inner");
            Assert.True(inner.Noted);
        }

        Assert.False(outer.Noted);
        ReadChangeLog.Report("Example", "outer");
        Assert.True(outer.Noted);
    }
}
