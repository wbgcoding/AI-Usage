using System.Diagnostics;
using System.Text;
using AiUsage.Io;

namespace AiUsage.Tests;

public class ReverseLineReaderTests : IDisposable
{
    [Fact]
    public void ReadLinesReversed_drops_a_utf8_byte_order_mark_from_the_first_line()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("first\nsecond\n")]);
        try
        {
            Assert.Equal(["second", "first"], ReverseLineReader.ReadLinesReversed(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinesReversed_with_a_length_ignores_everything_appended_past_it()
    {
        var path = WriteTempFile("first\nsecond\nthird\n");
        try
        {
            Assert.Equal(["second", "first"], ReverseLineReader.ReadLinesReversed(path, upToLength: "first\nsecond\n".Length));
            Assert.Equal(["sec", "first"], ReverseLineReader.ReadLinesReversed(path, upToLength: "first\nsec".Length));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinesReversed_returns_lines_last_line_first()
    {
        var path = WriteTempFile("first\nsecond\nthird\n");
        try
        {
            Assert.Equal(["third", "second", "first"], ReverseLineReader.ReadLinesReversed(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinesReversed_strips_carriage_returns_from_crlf_endings()
    {
        var path = WriteTempFile("first\r\nsecond\r\n");
        try
        {
            Assert.Equal(["second", "first"], ReverseLineReader.ReadLinesReversed(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinesReversed_keeps_an_incomplete_final_line()
    {
        var path = WriteTempFile("complete\nincomplete-tail-no-newline");
        try
        {
            Assert.Equal(["incomplete-tail-no-newline", "complete"], ReverseLineReader.ReadLinesReversed(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinesReversed_returns_nothing_for_an_empty_file()
    {
        var path = WriteTempFile(string.Empty);
        try
        {
            Assert.Empty(ReverseLineReader.ReadLinesReversed(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinesReversed_finds_the_last_line_of_a_multi_megabyte_file_without_reading_it_whole()
    {
        var path = WriteTempFile(BuildFiveMegabyteContent(out var expectedLastLine));
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var firstYielded = ReverseLineReader.ReadLinesReversed(path).First();
            stopwatch.Stop();

            Assert.Equal(expectedLastLine, firstYielded);
            // The design target is "under 50 ms" for a 5 MB file; this assertion
            // uses a wider margin so it does not flake on a loaded CI/VM box while still catching a
            // regression to a full-file scan (which would take far longer than either bound).
            Assert.True(stopwatch.ElapsedMilliseconds < 1000,
                $"Expected the last line to be found in well under 1000 ms, took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinesReversed_returns_no_line_for_a_20_megabyte_file_with_a_single_oversized_line()
    {
        var path = WriteTempFile(new string('x', 20 * 1024 * 1024));
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var lines = ReverseLineReader.ReadLinesReversed(path).ToList();
            stopwatch.Stop();

            Assert.Empty(lines);
            // Recombining the tail in full on every 64 KB block would be an O(n^2) copy that takes
            // well over 10 seconds for this file; the reader drops an oversized line once it passes
            // the 4 MB cap instead of continuing to copy it.
            Assert.True(stopwatch.ElapsedMilliseconds < 1000,
                $"Expected the oversized-line file to be rejected in well under 1000 ms, took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinesReversed_reassembles_a_normal_line_that_spans_multiple_64kb_blocks()
    {
        // 200 KB is comfortably under the 4 MB cap but spans more than three 64 KB read blocks, so
        // this exercises the cross-block reassembly path the O(n^2) fix rewrote.
        var longLine = new string('y', 200 * 1024);
        var path = WriteTempFile($"before\n{longLine}\nafter\n");
        try
        {
            Assert.Equal(["after", longLine, "before"], ReverseLineReader.ReadLinesReversed(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinesReversed_skips_an_oversized_line_but_still_returns_the_normal_lines_around_it()
    {
        var oversizedLine = new string('z', 5 * 1024 * 1024);
        var path = WriteTempFile($"before\n{oversizedLine}\nafter\n");
        try
        {
            Assert.Equal(["after", "before"], ReverseLineReader.ReadLinesReversed(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string BuildFiveMegabyteContent(out string expectedLastLine)
    {
        var builder = new StringBuilder(5 * 1024 * 1024 + 1024);
        var filler = new string('x', 200);
        while (builder.Length < 5 * 1024 * 1024)
            builder.Append(filler).Append('\n');

        expectedLastLine = "the-known-last-line-12345";
        builder.Append(expectedLastLine).Append('\n');
        return builder.ToString();
    }

    private string WriteTempFile(string content)
    {
        var path = TestPaths.GetPath("ai-usage-reverse-line-reader", ".tmp");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _tempFiles.Add(path);
        return path;
    }

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
