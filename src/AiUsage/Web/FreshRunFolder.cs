namespace AiUsage.Web;

/// <summary>
/// A folder made for one run of an installer that asks for elevation. A program loads libraries from
/// its own folder, so the file that runs must sit alone in a folder nobody else could have prepared:
/// a new name under a root that is not a link, empty when created, and still holding nothing but the
/// file right before it runs.
/// </summary>
internal static class FreshRunFolder
{
    /// <summary>A new, empty subfolder of <paramref name="root"/> under a random name; null when the
    /// root cannot be trusted (it or its parent is a link), the name is taken, or something is already
    /// inside the new folder.</summary>
    internal static string? TryCreate(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            if (IsLink(root) || (Path.GetDirectoryName(root) is { Length: > 0 } parent && IsLink(parent)))
                return null;

            var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(folder) || File.Exists(folder))
                return null;

            Directory.CreateDirectory(folder);
            if (IsLink(folder) || Directory.EnumerateFileSystemEntries(folder).Any())
            {
                TryRemoveEmpty(folder);
                return null;
            }

            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="folder"/> contains exactly <paramref name="file"/>: no other
    /// file, no subfolder.</summary>
    internal static bool HoldsOnly(string folder, string file)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(folder).Take(2).ToList();
            return entries.Count == 1 && string.Equals(entries[0], file, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes the folder when it is empty; anything else in it stays.</summary>
    internal static void TryRemoveEmpty(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty or in use: left for the next sweep of the temp folder.
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
