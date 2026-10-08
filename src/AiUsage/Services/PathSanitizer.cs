using System.Text.RegularExpressions;

namespace AiUsage.Services;

/// <summary>
/// Strips the current user's home directory and account name out of free text before it reaches
/// the log file or the crash dialog (no user paths, no credentials in
/// diagnostic output). The profile path goes first, in its backslash, slash and JSON-escaped
/// spellings; the bare account name and the profile's own folder name (they differ after an account
/// rename) are replaced only as whole words, so "Maximum" survives a user called "Max". The real
/// values are looked up lazily so a test can inject fakes instead.
/// </summary>
public static class PathSanitizer
{
    public static string Sanitize(string text, string? userProfile = null, string? userName = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        userName ??= Environment.UserName;

        var names = new List<string>();
        if (!string.IsNullOrEmpty(userProfile))
        {
            var plain = userProfile.TrimEnd('\\', '/');
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
}
