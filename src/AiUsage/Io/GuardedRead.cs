using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AiUsage.Io;

/// <summary>Reads of files a foreign project or a pending request names: never from a network share,
/// never through a link, never more than a stated size.</summary>
internal static class GuardedRead
{
    /// <summary>True for a fully qualified path rooted at a drive letter (<c>X:\</c> or <c>X:/</c>). Every
    /// other spelling is refused, the UNC ones (<c>\\host\share</c>, <c>//host/share</c>,
    /// <c>\\?\UNC\...</c>, <c>\\.\</c>) and the NT object forms (<c>\??\UNC\...</c>, <c>\??\C:\...</c>)
    /// alike. Decided by the text alone, so nothing is sent to a share to find out.</summary>
    internal static bool IsLocalPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        try
        {
            return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
                && Path.IsPathFullyQualified(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>True for a fully qualified path on a local drive: <see cref="IsLocalPath"/>, rooted at a
    /// drive letter, and not a mapped network drive.</summary>
    internal static bool IsLocalDrivePath(string? path)
    {
        if (!IsLocalPath(path))
            return false;

        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || !char.IsAsciiLetter(root[0]) || root[1] != ':')
                return false;

            return new DriveInfo(root[..1]).DriveType is not (DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when the path is a link (symbolic link or junction), or cannot be inspected.</summary>
    internal static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>True when <paramref name="path"/> lies below <paramref name="root"/> and no folder between
    /// them is a link (the root itself and the leaf are not looked at). A linked folder in the middle
    /// would lead a read out of the project, possibly to a network share.</summary>
    internal static bool IsReachableWithoutLinks(string root, string path)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;

            for (var dir = Path.GetDirectoryName(full); dir is { Length: > 0 } && dir.Length > fullRoot.Length; dir = Path.GetDirectoryName(dir))
                if (IsLink(dir))
                    return false;

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary><see cref="TryReadSmallText"/> for a file below <paramref name="root"/>: also refused when
    /// it lies outside the root or a folder between the root and the file is a link.</summary>
    internal static bool TryReadSmallTextUnder(string root, string path, int maxBytes, out string text)
    {
        text = "";
        return IsReachableWithoutLinks(root, path) && TryReadSmallText(path, maxBytes, out text);
    }

    /// <summary>True when the file behind <paramref name="opened"/> is the one <paramref name="fullPath"/>
    /// names: on a local drive, in the folder the path's parent really is (a junction above the file is
    /// resolved, a link at the file itself or a file swapped in is not accepted) and under the same name.</summary>
    internal static bool OpenedFileIsTheChecked(SafeFileHandle opened, string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        var openedPath = FinalPath(opened);
        if (string.IsNullOrEmpty(parent) || openedPath is null || !IsLocalDrivePath(openedPath))
            return false;

        using var folder = CreateFileW(parent, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (folder.IsInvalid)
            return false;

        var realParent = FinalPath(folder);
        if (realParent is null || !IsLocalDrivePath(realParent))
            return false;

        return string.Equals(openedPath, Path.Combine(realParent, Path.GetFileName(fullPath)), StringComparison.OrdinalIgnoreCase);
    }

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint pathLength, uint flags);

    /// <summary>The path the handle really points at, without the <c>\\?\</c> prefix (a network path
    /// comes back as <c>\\host\share\...</c>), or null when it cannot be told.</summary>
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

    /// <summary>The text of a regular file of at most <paramref name="maxBytes"/> bytes. False when the
    /// path is not local, the file is a link, larger than the limit (also when it grows while being
    /// read) or cannot be read.</summary>
    internal static bool TryReadSmallText(string path, int maxBytes, out string text)
    {
        text = "";
        try
        {
            if (!IsLocalDrivePath(path))
                return false;

            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > maxBytes)
                return false;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // A link swapped in between the check above and this open is followed by the open: the
            // handle itself says which file it ended up on.
            if (!OpenedFileIsTheChecked(stream.SafeFileHandle, Path.GetFullPath(path)))
                return false;

            var buffer = new byte[maxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                total += read;
            if (total > maxBytes)
                return false;

            using var reader = new StreamReader(new MemoryStream(buffer, 0, total), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
