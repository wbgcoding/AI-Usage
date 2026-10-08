using System.Text;

namespace AiUsage.Io;

/// <summary>
/// Reads a text file back to front without loading it whole - built for multi-megabyte session logs
/// where only the last few lines matter. Reads in 64 KB blocks from the end, tolerates an incomplete
/// trailing line (a file caught mid-write) and both "\n" and "\r\n" endings. A single line longer than
/// <see cref="MaxLineBytes"/> - a shape none of this project's parsers recognise anyway - is skipped
/// instead of being copied block by block, which is what made a pathological file (one giant line, or
/// none at all) cost quadratic time and unbounded memory.
/// </summary>
public static class ReverseLineReader
{
    private const int BlockSize = 64 * 1024;
    private const int MaxLineBytes = 4 * 1024 * 1024;

    /// <summary>Yields physical lines starting with the LAST line in the file and ending with the first.
    /// With <paramref name="upToLength"/> the file counts as that long, so a caller that already
    /// measured it never sees bytes another writer appended since.</summary>
    public static IEnumerable<string> ReadLinesReversed(string path, Encoding? encoding = null, long? upToLength = null)
    {
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var length = upToLength is { } limit ? Math.Min(limit, stream.Length) : stream.Length;
        var position = length;

        // A file written line-by-line ends with a terminator, not with a trailing blank line - drop
        // exactly one trailing "\n" up front so the loop below never invents an empty last line.
        if (length > 0)
        {
            stream.Seek(length - 1, SeekOrigin.Begin);
            if (stream.ReadByte() == '\n')
                position = length - 1;
        }

        // Bytes of the line currently being assembled that lie before the earliest newline found so
        // far, oldest block last (blocks are read back to front, so each new one is chronologically
        // earlier than the last). Concatenated in reverse when a line closes, or at end of file.
        var pending = new List<byte[]>();
        var pendingBytes = 0;
        var pendingTooLong = false;
        var block = new byte[BlockSize];

        while (position > 0)
        {
            var readSize = (int)Math.Min(BlockSize, position);
            position -= readSize;
            stream.Seek(position, SeekOrigin.Begin);

            var offset = 0;
            while (offset < readSize)
            {
                var read = stream.Read(block, offset, readSize - offset);
                if (read == 0)
                    break; // the file shrank under us; work with what was actually read
                offset += read;
            }

            var searchEnd = offset;
            for (var i = offset - 1; i >= 0; i--)
            {
                if (block[i] != (byte)'\n')
                    continue;

                var sliceStart = i + 1;
                var sliceLen = searchEnd - sliceStart;

                if (pending.Count == 0 && !pendingTooLong)
                {
                    // Common case: both ends of the line live in this one block - no pending tail to
                    // stitch in, so decode straight out of it.
                    yield return DecodeLine(block, sliceStart, sliceLen, encoding);
                }
                else
                {
                    pendingBytes += sliceLen;
                    if (!pendingTooLong && pendingBytes <= MaxLineBytes)
                        yield return DecodeLine(AssembleLine(block, sliceStart, sliceLen, pending, pendingBytes), 0, pendingBytes, encoding);
                    // else: over the cap, or already flagged - this line is unparseable either way,
                    // drop it silently rather than paying to assemble bytes nothing will use.
                    pending.Clear();
                    pendingBytes = 0;
                    pendingTooLong = false;
                }

                searchEnd = i;
            }

            if (searchEnd > 0)
            {
                pendingBytes += searchEnd;
                if (pendingTooLong || pendingBytes > MaxLineBytes)
                    pendingTooLong = true;
                else
                {
                    var chunk = new byte[searchEnd];
                    Buffer.BlockCopy(block, 0, chunk, 0, searchEnd);
                    pending.Add(chunk);
                }
            }
        }

        // Whatever is left is the file's first line - or, for a file truncated mid-write, its one
        // and only (incomplete) line. Either way it is handed on rather than dropped silently, unless
        // it never closed and blew past the cap (an unparseable file with no newline at all).
        if (pending.Count > 0 && !pendingTooLong)
        {
            var lineBytes = new byte[pendingBytes];
            var written = 0;
            for (var p = pending.Count - 1; p >= 0; p--)
            {
                var chunk = pending[p];
                Buffer.BlockCopy(chunk, 0, lineBytes, written, chunk.Length);
                written += chunk.Length;
            }

            // The first line starts at file offset 0 - skip a UTF-8 byte order mark there, or it
            // decodes as U+FEFF and that line never parses.
            var bomLength = lineBytes.Length >= 3 && lineBytes[0] == 0xEF && lineBytes[1] == 0xBB && lineBytes[2] == 0xBF ? 3 : 0;
            yield return DecodeLine(lineBytes, bomLength, lineBytes.Length - bomLength, encoding);
        }
    }

    /// <summary>Builds the full line once, from a fresh block slice (the line's earliest bytes) followed
    /// by the accumulated pending chunks in file order (they were appended oldest-block-last).</summary>
    private static byte[] AssembleLine(byte[] block, int sliceStart, int sliceLen, List<byte[]> pending, int totalLength)
    {
        var lineBytes = new byte[totalLength];
        Buffer.BlockCopy(block, sliceStart, lineBytes, 0, sliceLen);
        var written = sliceLen;
        for (var p = pending.Count - 1; p >= 0; p--)
        {
            var chunk = pending[p];
            Buffer.BlockCopy(chunk, 0, lineBytes, written, chunk.Length);
            written += chunk.Length;
        }

        return lineBytes;
    }

    private static string DecodeLine(byte[] buffer, int offset, int count, Encoding encoding)
    {
        var text = encoding.GetString(buffer, offset, count);
        return text.EndsWith('\r') ? text[..^1] : text;
    }
}
