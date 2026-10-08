using System.Text;

namespace AiUsage.Io;

/// <summary>Chunked, memory-bounded line reader shared by the session log readers.</summary>
internal static class CompleteLineReader
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Decides from a complete line's raw bytes, before any UTF-8 decoding, whether the
    /// caller could use it at all. Must never reject a line the caller would have acted on.</summary>
    internal delegate bool LinePrefilter(ReadOnlySpan<byte> line);

    /// <summary>Reads every complete line between <paramref name="startOffset"/> and <paramref
    /// name="stream"/>'s length at the moment this call began, in fixed chunks so memory stays
    /// bounded regardless of file size. A final line with no trailing newline yet - the writer paused
    /// mid line - is normally left unread rather than guessed at: the returned offset lands exactly
    /// on the byte after the last newline actually seen, never mid line, and any growth appended
    /// after this call started is never looked at (that belongs to the next run). When <paramref
    /// name="acceptTrailingLineWithoutNewline"/> is true (the file's write time is already past
    /// the settling window), that final unterminated line is trusted to be complete instead and
    /// handed to <paramref name="onLine"/> like any other, with the returned offset advanced past it
    /// - otherwise a session file whose writer stopped appending to it forever would leave that last
    /// line uncounted for good. Checked once per line rather than once per file, so cancelling mid
    /// walk does not have to wait out one huge file's entire remaining read; a cancelled file earns
    /// this run's read nothing, since it never reaches the caller's own write of the new offset - the
    /// next run simply reads it again from where it last actually finished. A line the optional
    /// <paramref name="prefilter"/> rejects is consumed like any other but never decoded or
    /// delivered; <paramref name="onFiltered"/> is told about it instead.</summary>
    internal static long Read(
        FileStream stream, long startOffset, bool acceptTrailingLineWithoutNewline, int maxRecordBytes, Action onOversized,
        Action<string> onLine, CancellationToken cancellationToken, LinePrefilter? prefilter = null, Action? onFiltered = null)
    {
        var endOffset = stream.Length;
        if (startOffset < 0 || startOffset > endOffset)
            startOffset = 0;

        stream.Seek(startOffset, SeekOrigin.Begin);

        var consumedOffset = startOffset;
        // The part of the current line seen so far, when it started in an earlier chunk. A growable
        // buffer that is reused for every line.
        var carry = new byte[1024];
        var carryLength = 0;
        // Set while the current record is past the cap: its bytes are counted but not kept, up to the
        // next newline.
        var discarding = false;
        long discardedBytes = 0;
        var buffer = new byte[65536];
        var remaining = endOffset - startOffset;

        void Deliver(ReadOnlySpan<byte> lineBytes)
        {
            if (prefilter is not null && !prefilter(lineBytes))
            {
                onFiltered?.Invoke();
                return;
            }

            onLine(Utf8NoBom.GetString(lineBytes).TrimEnd('\r'));
        }

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
                break; // shrank mid read; whatever completed so far stands, the rest is left for next time

            remaining -= read;
            var position = 0;
            while (position < read)
            {
                var newline = buffer.AsSpan(position, read - position).IndexOf((byte)'\n');
                if (newline < 0)
                {
                    // No newline in the rest of this chunk: keep it for the next one.
                    var rest = read - position;
                    if (discarding)
                    {
                        discardedBytes += rest;
                    }
                    else
                    {
                        AppendToCarry(ref carry, ref carryLength, buffer.AsSpan(position, rest));
                        if (carryLength > maxRecordBytes)
                        {
                            // Past the cap with no newline yet: stop buffering, count the bytes only.
                            onOversized();
                            discarding = true;
                            discardedBytes += carryLength;
                            carryLength = 0;
                        }
                    }

                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (discarding || carryLength + newline > maxRecordBytes)
                {
                    if (!discarding)
                        onOversized();
                    consumedOffset += discardedBytes + carryLength + newline + 1;
                    discarding = false;
                    discardedBytes = 0;
                    carryLength = 0;
                    position += newline + 1;
                    continue;
                }

                int lineLength;
                if (carryLength > 0)
                {
                    AppendToCarry(ref carry, ref carryLength, buffer.AsSpan(position, newline));
                    lineLength = carryLength;
                    Deliver(carry.AsSpan(0, lineLength));
                    carryLength = 0;
                }
                else
                {
                    lineLength = newline;
                    Deliver(buffer.AsSpan(position, lineLength));
                }

                consumedOffset += lineLength + 1; // +1 for the newline itself
                position += newline + 1;
            }
        }

        if (acceptTrailingLineWithoutNewline && carryLength > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deliver(carry.AsSpan(0, carryLength));
            consumedOffset = endOffset;
        }

        return consumedOffset;
    }

    private static void AppendToCarry(ref byte[] carry, ref int carryLength, ReadOnlySpan<byte> bytes)
    {
        if (carryLength + bytes.Length > carry.Length)
            Array.Resize(ref carry, Math.Max(carry.Length * 2, carryLength + bytes.Length));
        bytes.CopyTo(carry.AsSpan(carryLength));
        carryLength += bytes.Length;
    }
}
