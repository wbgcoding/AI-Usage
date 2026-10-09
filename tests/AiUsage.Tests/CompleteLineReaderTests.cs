using AiUsage.Io;

namespace AiUsage.Tests;

public class CompleteLineReaderTests
{
    private const int Mib = 1024 * 1024;

    [Fact]
    public void An_unterminated_line_past_the_cap_is_discarded_while_it_streams_in()
    {
        using var dir = TestPaths.CreateDisposableDirectory("clr-huge");
        var path = Path.Combine(dir, "huge.jsonl");
        File.WriteAllText(path, new string('x', 10 * Mib));
        var oversized = 0;
        var lines = new List<string>();

        long consumed;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            consumed = CompleteLineReader.Read(stream, 0, false, Mib, () => oversized++, lines.Add, CancellationToken.None);

        Assert.Equal(1, oversized);
        Assert.Equal(0, consumed);
        Assert.Empty(lines);

        // The newline arrives later: the whole record is consumed, the next line is read normally.
        File.AppendAllText(path, "\nok\n");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            consumed = CompleteLineReader.Read(stream, 0, false, Mib, () => oversized++, lines.Add, CancellationToken.None);

        Assert.Equal(2, oversized);
        Assert.Equal(new FileInfo(path).Length, consumed);
        Assert.Equal(["ok"], lines);
    }

    [Fact]
    public void A_discarded_unterminated_tail_is_not_emitted_even_when_trailing_lines_are_accepted()
    {
        using var dir = TestPaths.CreateDisposableDirectory("clr-tail");
        var path = Path.Combine(dir, "tail.jsonl");
        File.WriteAllText(path, "first\n" + new string('x', 3 * Mib));
        var oversized = 0;
        var lines = new List<string>();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var consumed = CompleteLineReader.Read(stream, 0, true, Mib, () => oversized++, lines.Add, CancellationToken.None);

        Assert.Equal(1, oversized);
        Assert.Equal(["first"], lines);
        Assert.Equal(new FileInfo(path).Length, consumed); // the discarded tail is consumed too, so it is not met again
    }

    [Fact]
    public void An_oversized_unterminated_tail_is_consumed_once_trailing_lines_are_accepted()
    {
        using var dir = TestPaths.CreateDisposableDirectory("clr-tail-consumed");
        var path = Path.Combine(dir, "tail.jsonl");
        File.WriteAllText(path, "first\n" + new string('x', 3 * Mib));
        var length = new FileInfo(path).Length;

        using var settling = new FileStream(path, FileMode.Open, FileAccess.Read);
        var whileSettling = CompleteLineReader.Read(settling, 0, false, Mib, () => { }, _ => { }, CancellationToken.None);
        using var settled = new FileStream(path, FileMode.Open, FileAccess.Read);
        var afterSettling = CompleteLineReader.Read(settled, 0, true, Mib, () => { }, _ => { }, CancellationToken.None);

        Assert.Equal("first\n".Length, whileSettling);
        Assert.Equal(length, afterSettling);
    }

    private static long ReadFiltered(
        string path, bool acceptTrailing, CompleteLineReader.LinePrefilter prefilter, List<string> lines, ref int filtered)
    {
        var count = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var consumed = CompleteLineReader.Read(
            stream, 0, acceptTrailing, Mib, () => { }, lines.Add, CancellationToken.None, prefilter, () => count++);
        filtered += count;
        return consumed;
    }

    [Fact]
    public void A_prefilter_drops_lines_before_decoding_but_still_consumes_them()
    {
        using var dir = TestPaths.CreateDisposableDirectory("clr-filter");
        var path = Path.Combine(dir, "f.jsonl");
        File.WriteAllText(path, "keep one\ndrop\nkeep two\r\ndrop too\n");
        var lines = new List<string>();
        var filtered = 0;

        var consumed = ReadFiltered(path, false, line => line.IndexOf("keep"u8) >= 0, lines, ref filtered);

        Assert.Equal(["keep one", "keep two"], lines);
        Assert.Equal(2, filtered);
        Assert.Equal(new FileInfo(path).Length, consumed);
    }

    [Fact]
    public void A_line_longer_than_one_chunk_is_assembled_before_the_prefilter_sees_it()
    {
        using var dir = TestPaths.CreateDisposableDirectory("clr-long");
        var path = Path.Combine(dir, "f.jsonl");
        var wanted = "wanted " + new string('a', 200_000) + " end";
        var unwanted = "other " + new string('b', 150_000) + " end";
        File.WriteAllText(path, unwanted + "\n" + wanted + "\nlast wanted\n");
        var lines = new List<string>();
        var filtered = 0;

        var consumed = ReadFiltered(path, false, line => line.IndexOf("wanted"u8) >= 0, lines, ref filtered);

        Assert.Equal([wanted, "last wanted"], lines);
        Assert.Equal(1, filtered);
        Assert.Equal(new FileInfo(path).Length, consumed);
    }

    [Fact]
    public void A_trailing_line_without_newline_goes_through_the_prefilter_too()
    {
        using var dir = TestPaths.CreateDisposableDirectory("clr-tailfilter");
        var path = Path.Combine(dir, "f.jsonl");
        File.WriteAllText(path, "keep\ndrop");
        var lines = new List<string>();
        var filtered = 0;

        var consumed = ReadFiltered(path, true, line => line.IndexOf("keep"u8) >= 0, lines, ref filtered);

        Assert.Equal(["keep"], lines);
        Assert.Equal(1, filtered);
        Assert.Equal(new FileInfo(path).Length, consumed);
    }
}
