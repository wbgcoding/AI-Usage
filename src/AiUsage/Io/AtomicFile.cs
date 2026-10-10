namespace AiUsage.Io;

/// <summary>Replaces a file in one step: the bytes go to a temp file beside it first and the temp
/// file is then moved over the target, so a reader sees the old content or the new, never half of it.</summary>
public static class AtomicFile
{
    /// <summary>The temp file name carries this process's id so two copies running side by side never
    /// write through the same temp file. One left behind by a process killed before the move is
    /// swept up by <see cref="Storage.AppPaths.CleanUpLeftoverTempFiles()"/> at the next start. A failed
    /// move leaves the temp file in place and throws.</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var tempPath = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }
}
