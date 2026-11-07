using System.Text;

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
