using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AiUsage.Services;

/// <summary>
/// Strips the current user's home directory and account name out of free text before it reaches
/// the log file or the crash dialog (no user paths, no credentials in
/// diagnostic output). The profile path goes first, in its backslash, slash and JSON-escaped
/// spellings; the bare account name and the profile's own folder name (they differ after an account
/// rename) are replaced only as whole words, so "Maximum" survives a user called "Max". The 8.3
/// short form of the profile path, which temp paths can carry, is treated the same way. The real values are looked up lazily so a test can inject fakes instead.
/// </summary>
public static class PathSanitizer
{
    private static readonly Lazy<string?> RealShortProfile = new(() =>
        ShortPathOf(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    public static string Sanitize(string text, string? userProfile = null, string? userName = null, string? shortProfile = null)
    {
        if (userProfile is null)
        {
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            shortProfile ??= RealShortProfile.Value;
        }
        userName ??= Environment.UserName;

        var names = new List<string>();
        foreach (var profile in new[] { userProfile, shortProfile })
        {
            if (string.IsNullOrEmpty(profile))
                continue;

            var plain = profile.TrimEnd('\\', '/');
            foreach (var spelling in new[] { plain.Replace("\\", "\\\\", StringComparison.Ordinal), plain, plain.Replace('\\', '/') })
                if (spelling.Length > 0)
                    text = text.Replace(spelling, "<user>", StringComparison.OrdinalIgnoreCase);

            names.Add(Path.GetFileName(plain.Replace('/', '\\')));
        }
        names.Add(userName);

        foreach (var name in names.Where(name => !string.IsNullOrEmpty(name) && name.Length > 2).Distinct(StringComparer.OrdinalIgnoreCase))
            text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])", "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return text;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern unsafe uint GetShortPathNameW(string longPath, char* shortPath, uint bufferLength);

    /// <summary>The 8.3 spelling of <paramref name="path"/>, or null when it has none of its own.</summary>
    private static unsafe string? ShortPathOf(string path)
    {
        if (string.IsNullOrEmpty(path) || !OperatingSystem.IsWindows())
            return null;

        try
        {
            var buffer = new char[path.Length + 1];
            uint length;
            fixed (char* pointer = buffer)
                length = GetShortPathNameW(path, pointer, (uint)buffer.Length);
            if (length == 0 || length >= buffer.Length)
                return null;

            var shortPath = new string(buffer, 0, (int)length);
            return string.Equals(shortPath, path, StringComparison.OrdinalIgnoreCase) ? null : shortPath;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
