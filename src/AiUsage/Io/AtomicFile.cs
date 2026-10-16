namespace AiUsage.Io;

/// <summary>Replaces a file in one step: the bytes go to a temp file beside it first, are flushed to
/// the device, and the temp file is then moved over the target, so a reader sees the old content or
/// the new, never half of it, and a power cut right after the move cannot leave an empty file.</summary>
public static class AtomicFile
{
    /// <summary>The temp file name carries this process's id so two copies running side by side never
    /// write through the same temp file. One left behind by a process killed before the move is
    /// swept up by <see cref="Storage.AppPaths.CleanUpLeftoverTempFiles()"/> at the next start. A failed
    /// move leaves the temp file in place and throws. A file other programs read can make the move
    /// fail for a few milliseconds (Windows refuses to replace a file that is open without delete
    /// sharing): <paramref name="moveAttempts"/> above 1 tries the swap again, <paramref
    /// name="retryDelay"/> apart, before giving up, and swaps an existing file with <see
    /// cref="File.Replace(string, string, string?)"/>, which a reader's open handle blocks less often
    /// than a move over it.</summary>
    public static void WriteAllBytes(string path, byte[] bytes, int moveAttempts = 1, TimeSpan? retryDelay = null)
    {
        var tempPath = $"{path}.{Environment.ProcessId}.tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            // Flush(true) asks the device itself to commit; a plain flush leaves the data in the cache.
            stream.Flush(flushToDisk: true);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (moveAttempts > 1 && File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < moveAttempts)
            {
                Thread.Sleep(retryDelay ?? TimeSpan.FromMilliseconds(10));
            }
        }
    }
}
