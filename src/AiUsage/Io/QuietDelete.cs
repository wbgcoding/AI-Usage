namespace AiUsage.Io;

/// <summary>Deletes files on a best-effort basis: one that is still in use, read-only or already gone
/// stays, and the caller carries on (whatever is left is picked up by a later cleanup).</summary>
internal static class QuietDelete
{
    internal static void Delete(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Stays behind; see the summary.
            }
        }
    }
}
