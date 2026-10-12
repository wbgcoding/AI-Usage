using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiUsage.Io;

/// <summary>
/// A folder made for one run of an installer that asks for elevation. A program loads libraries from
/// its own folder, so the file that runs must sit alone in a folder nobody else could have prepared:
/// a new name under a root that is not a link, empty when created, and still holding nothing but the
/// file right before it runs. Both installers that run a downloaded file share it.
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

    /// <summary>True when the held <paramref name="lease"/> really is the file at <paramref name="path"/>:
    /// the handle's final path (every link resolved) equals the path's own final path, and the path is
    /// not a link. Opening a file follows a link, so one swapped in after an earlier check would be
    /// read and locked at its target while the path still names the link.</summary>
    internal static bool LeaseIsAt(FileStream lease, string path)
    {
        try
        {
            using var named = CreateFileW(path, 0, 7, IntPtr.Zero, 3, FileFlagBackupSemantics, IntPtr.Zero);
            if (named.IsInvalid)
                return false;

            var expected = FinalPath(named);
            var actual = FinalPath(lease.SafeFileHandle);
            return expected is not null && actual is not null
                && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                && !IsLink(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsLink(string path)
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

    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint pathLength, uint flags);

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length > buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length > buffer.Length)
            return null;

        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}
