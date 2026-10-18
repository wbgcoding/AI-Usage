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
/// As a last safety net, e-mail addresses and bearer or access tokens that slipped into a message
/// are masked too.
/// </summary>
public static class PathSanitizer
{
    private static readonly Lazy<string> RealProfile = new(() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static readonly Lazy<string?> RealShortProfile = new(() => ShortPathOf(RealProfile.Value));

    private static readonly Lazy<string> RealUserName = new(() => Environment.UserName);

    private static readonly Lazy<Regex?> RealNames = new(() =>
        NamesPattern(RealProfile.Value, RealShortProfile.Value, RealUserName.Value));

    private static readonly Regex EmailAddress = new(
        @"(?<![\p{L}\p{N}._%+\-])[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}\-]+(?:\.[\p{L}\p{N}\-]+)+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BearerToken = new(
        @"(?<=\bBearer\s+)[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Also matches the JSON-escaped spelling (\"access_token\":\"...\") a nested payload has in a log line.
    // The value runs to the closing quote; an escape inside it (\/ or +) is part of the token.
    private static readonly Regex AccessTokenField = new(
        @"(?<key>\\?""(?:access|refresh|id)_token\\?""\s*:\s*\\?"")(?:[^""\\]|\\(?!""))*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Sanitize(string text, string? userProfile = null, string? userName = null, string? shortProfile = null)
    {
        var real = userProfile is null && userName is null && shortProfile is null;
        if (userProfile is null)
        {
            userProfile = RealProfile.Value;
            shortProfile ??= RealShortProfile.Value;
        }
        userName ??= RealUserName.Value;

        text = EmailAddress.Replace(text, "{email}");
        text = BearerToken.Replace(text, "{token}");
        text = AccessTokenField.Replace(text, "${key}{token}");

        foreach (var profile in new[] { userProfile, shortProfile })
        {
            if (string.IsNullOrEmpty(profile))
                continue;

            var plain = profile.TrimEnd('\\', '/');
            foreach (var spelling in new[] { plain.Replace("\\", "\\\\", StringComparison.Ordinal), plain, plain.Replace('\\', '/') })
                if (spelling.Length > 0)
                    text = text.Replace(spelling, "<user>", StringComparison.OrdinalIgnoreCase);
        }

        var names = real ? RealNames.Value : NamesPattern(userProfile, shortProfile, userName);
        return names?.Replace(text, "<user>") ?? text;
    }

    /// <summary>One whole-word pattern for the account name and the profile folder names (the long
    /// and the short form), or null when none is longer than two characters.</summary>
    private static Regex? NamesPattern(string? userProfile, string? shortProfile, string? userName)
    {
        var names = new List<string>();
        foreach (var profile in new[] { userProfile, shortProfile })
            if (!string.IsNullOrEmpty(profile))
                names.Add(Path.GetFileName(profile.TrimEnd('\\', '/').Replace('/', '\\')));
        names.Add(userName ?? "");

        var distinct = names.Where(name => name.Length > 2).Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape).ToList();
        if (distinct.Count == 0)
            return null;

        return new Regex(
            $@"(?<![\p{{L}}\p{{N}}_])(?:{string.Join('|', distinct)})(?![\p{{L}}\p{{N}}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
